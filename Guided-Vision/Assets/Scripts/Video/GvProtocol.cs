using UnityEngine;

/// <summary>
/// C# mirror of gvlink/protocol.py. Used by the Editor video path; on device the
/// equivalent lives in Java (GvReassembler.java) so video bytes never enter managed
/// memory. Three copies of this layout exist and all three must agree -- change one,
/// change all three, and run <c>uv run selftest.py</c>.
/// </summary>
public struct GvVideoHeader
{
    public const int Size = 40;
    public const uint Magic = 0x47564944;   // 'GVID'

    public const int FlagKeyframe = 1 << 0;
    public const int FlagFoveated = 1 << 1;
    public const int FlagLastFragment = 1 << 2;

    public const int CodecH264 = 0;
    public const int CodecMjpeg = 1;

    public uint FrameId;
    public int Eye;
    public int Flags;
    public int FragmentIdx;
    public int FragmentCount;
    public long CaptureTsUs;
    public float FoveaX, FoveaY, FoveaW, FoveaH;
    public int Codec;

    /// <summary>
    /// Pixels of its band each layer occupies, anchored top-left; 0 means it fills the
    /// band. Exact pixels rather than a scale fraction, because rounding a fraction
    /// makes the sampler read into the black padding beside the layer.
    /// </summary>
    public int CoarsePxW, CoarsePxH, FoveaPxW, FoveaPxH;

    public bool Foveated => (Flags & FlagFoveated) != 0;
    public bool Keyframe => (Flags & FlagKeyframe) != 0;

    private static uint U16(byte[] b, int o) => (uint)((b[o] << 8) | b[o + 1]);
    private static uint U32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static long U64(byte[] b, int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v;
    }

    public static bool TryParse(byte[] b, int len, out GvVideoHeader h)
    {
        h = default;
        if (len < Size || U32(b, 0) != Magic)
            return false;
        h.FrameId = U32(b, 4);
        h.Eye = b[8];
        h.Flags = b[9] & ~FlagLastFragment;   // that bit describes the datagram, not the frame
        h.FragmentIdx = (int)U16(b, 10);
        h.FragmentCount = (int)U16(b, 12);
        h.CaptureTsUs = U64(b, 14);
        h.FoveaX = U16(b, 22) / 65535f;
        h.FoveaY = U16(b, 24) / 65535f;
        h.FoveaW = U16(b, 26) / 65535f;
        h.FoveaH = U16(b, 28) / 65535f;
        h.Codec = b[30];
        // b[31] reserved
        h.CoarsePxW = (int)U16(b, 32);
        h.CoarsePxH = (int)U16(b, 34);
        h.FoveaPxW = (int)U16(b, 36);
        h.FoveaPxH = (int)U16(b, 38);
        return true;
    }

    /// <summary>
    /// Centre and extent of the foveal crop in GL uv convention, ready for the shader.
    /// The sender measures the centre from the top of the image; uv counts from the
    /// bottom, so the flip happens exactly once, here.
    /// </summary>
    public Vector4 FoveaRectUV => new Vector4(FoveaX, 1f - FoveaY, FoveaW, FoveaH);

    /// <summary>
    /// Each layer's stored size as a fraction of the canvas -- coarse u,v then fovea
    /// u,v -- which is what the shader needs to turn a source uv into an atlas uv.
    /// </summary>
    public Vector4 LayerSpans(int canvasW, int canvasH)
    {
        if (canvasW <= 0 || canvasH <= 0)
            return new Vector4(1f, 0.5f, 1f, 0.5f);
        int band = canvasH / 2;
        float cw = CoarsePxW > 0 ? CoarsePxW : canvasW;
        float chh = CoarsePxH > 0 ? CoarsePxH : band;
        float fw = FoveaPxW > 0 ? FoveaPxW : canvasW;
        float fh = FoveaPxH > 0 ? FoveaPxH : band;
        return new Vector4(cw / canvasW, chh / canvasH, fw / canvasW, fh / canvasH);
    }
}

/// <summary>
/// Rebuilds encoded frames from fragments for one eye. Port of the Python and Java
/// reassemblers, including the policy that matters: a frame is completed or abandoned,
/// never waited for.
/// </summary>
public sealed class GvReassembler
{
    private const int MaxFragments = 2048;
    private const int MaxFragment = 1600;
    private const int RestartGap = 64;

    public GvVideoHeader Header;
    public byte[] Frame = new byte[1 << 20];
    public int FrameLength;

    public long FramesCompleted, FramesDropped, FragmentsReceived, FragmentsLost, BytesReceived;

    private long curId = -1;
    private int count, have;
    private readonly byte[][] parts = new byte[MaxFragments][];
    private readonly int[] partLen = new int[MaxFragments];
    private readonly bool[] got = new bool[MaxFragments];

    private static bool Newer(long a, long b) => ((a - b) & 0xFFFFFFFFL) < 0x80000000L;

    /// <summary>
    /// Plain C#, deliberately: Push() runs on the socket thread and Mathf.NextPowerOfTwo
    /// is an internal engine call. Nothing in this file should reach into the engine --
    /// it is a wire-format parser, and the one Unity type it touches (Vector4) is a
    /// plain struct.
    /// </summary>
    private static int NextPowerOfTwo(int v)
    {
        if (v <= 0)
            return 1;
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16;
        return v + 1;
    }

    private void Reset()
    {
        if (curId >= 0)
            System.Array.Clear(got, 0, count < MaxFragments ? count : MaxFragments);
        curId = -1;
        count = 0;
        have = 0;
    }

    private void Abandon()
    {
        if (curId >= 0 && count > 0)
        {
            FragmentsLost += count - have;
            FramesDropped++;
        }
        Reset();
    }

    /// <summary>Feed one datagram. True when <see cref="Frame"/> holds a whole frame.</summary>
    public bool Push(byte[] buf, int len)
    {
        if (!GvVideoHeader.TryParse(buf, len, out var h))
            return false;
        FragmentsReceived++;
        BytesReceived += len;

        if (h.FragmentCount <= 0 || h.FragmentCount > MaxFragments ||
            h.FragmentIdx >= h.FragmentCount)
            return false;

        long fid = h.FrameId;
        if (curId >= 0 && fid != curId)
        {
            if (Newer(fid, curId) || ((curId - fid) & 0xFFFFFFFFL) > RestartGap)
                Abandon();
            else
                return false;
        }

        if (curId < 0)
        {
            curId = fid;
            count = h.FragmentCount;
            Header = h;
        }

        if (got[h.FragmentIdx])
            return false;
        int payload = len - GvVideoHeader.Size;
        if (payload > MaxFragment)
            return false;
        if (parts[h.FragmentIdx] == null)
            parts[h.FragmentIdx] = new byte[MaxFragment];
        System.Buffer.BlockCopy(buf, GvVideoHeader.Size, parts[h.FragmentIdx], 0, payload);
        partLen[h.FragmentIdx] = payload;
        got[h.FragmentIdx] = true;
        have++;

        if (have < count)
            return false;

        int total = 0;
        for (int i = 0; i < count; i++) total += partLen[i];
        if (total > Frame.Length)
            Frame = new byte[NextPowerOfTwo(total)];
        int off = 0;
        for (int i = 0; i < count; i++)
        {
            System.Buffer.BlockCopy(parts[i], 0, Frame, off, partLen[i]);
            off += partLen[i];
        }
        FrameLength = off;
        FramesCompleted++;
        Reset();
        return true;
    }
}
