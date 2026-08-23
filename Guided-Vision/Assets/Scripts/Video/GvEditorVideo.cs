using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Editor-only receiver: the same wire protocol, decoded in C#.
///
/// MediaCodec is Android-only, so without this the display shader, the foveal atlas
/// geometry and the stereo layout could only ever be judged by building to a headset.
/// Run the sender with <c>--codec mjpeg</c> and this shows the real stream, with the
/// real fovea rect, in the Editor or the Meta XR Simulator.
///
/// It is a development tool, not a transport: MJPEG costs several times the bitrate of
/// H.264 for worse pictures, and decoding runs on the main thread. Use it on loopback
/// or a wired LAN.
/// </summary>
public sealed class GvEditorVideoReceiver : IDisposable
{
    private Socket socket;
    private Thread thread;
    private volatile bool running;

    public GvEditorVideoSource Left { get; } = new GvEditorVideoSource(0);
    public GvEditorVideoSource Right { get; } = new GvEditorVideoSource(1);

    public bool Start(int port)
    {
        if (running)
            return true;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ReceiveBufferSize = 8 << 20;
            socket.ReceiveTimeout = 200;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception e)
        {
            Debug.LogError($"GvEditorVideoReceiver: bind :{port} failed: {e.Message}");
            Dispose();
            return false;
        }
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "gv-editor-rx" };
        thread.Start();
        Debug.Log($"GvEditorVideoReceiver: listening on :{port} (run the sender with --codec mjpeg)");
        return true;
    }

    private void Loop()
    {
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int len;
            try
            {
                len = socket.ReceiveFrom(buf, ref any);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (Exception)
            {
                return;   // closed, or shutting down
            }
            if (len < GvVideoHeader.Size)
                continue;
            // Same demux as the Java receiver: the header's eye field, at offset 8.
            switch (buf[8])
            {
                case 0: Left.OnDatagram(buf, len); break;
                case 1: Right.OnDatagram(buf, len); break;
            }
        }
    }

    public void Dispose()
    {
        running = false;
        try { socket?.Close(); } catch { /* closing a closed socket is fine */ }
        if (thread != null && thread.IsAlive)
            thread.Join(500);
        thread = null;
        socket = null;
        Left.Dispose();
        Right.Dispose();
    }
}

/// <summary>One eye of <see cref="GvEditorVideoReceiver"/>.</summary>
public sealed class GvEditorVideoSource : IGvVideoSource
{
    private readonly int eye;
    private readonly GvReassembler asm = new GvReassembler();
    private readonly object gate = new object();

    private byte[] pending;
    private GvVideoHeader pendingHeader;
    private bool hasPending;

    private Texture2D texture;
    private GvVideoHeader pendingSpans;
    private long framesDecoded, decodeErrors, unsupportedCodec;

    public GvEditorVideoSource(int eye) { this.eye = eye; }

    public Texture2D Texture => texture;
    public bool Foveated { get; private set; }
    public Vector4 FoveaRect { get; private set; } = new Vector4(0.5f, 0.5f, 0f, 0f);
    public Vector4 LayerSpans { get; private set; } = new Vector4(1f, 0.5f, 1f, 0.5f);

    // LoadImage hands Unity an sRGB texture and it handles the conversion, so the
    // shader must not convert a second time.
    public bool NeedsSrgbDecode => false;

    public string DecoderName => "editor-mjpeg";

    public long FramesDecoded => framesDecoded;
    public long FramesCompleted => asm.FramesCompleted;
    public long FramesDropped => asm.FramesDropped;
    public long FragmentsLost => asm.FragmentsLost;
    public long BytesReceived => asm.BytesReceived;
    public long FramesNoInputBuffer => unsupportedCodec;
    public long DecodeErrors => decodeErrors;
    public long CaptureTsUs { get; private set; }

    /// <summary>Socket thread. Keeps only the newest complete frame -- never a queue.</summary>
    internal void OnDatagram(byte[] buf, int len)
    {
        if (!asm.Push(buf, len))
            return;
        // LoadImage takes a whole array, so this has to be exactly sized. An
        // allocation per frame is not something the device path would tolerate; in the
        // Editor it is cheaper than the JPEG decode that follows it.
        var frame = new byte[asm.FrameLength];
        Buffer.BlockCopy(asm.Frame, 0, frame, 0, asm.FrameLength);
        lock (gate)
        {
            pending = frame;
            pendingHeader = asm.Header;
            hasPending = true;
        }
    }

    public void Update()
    {
        byte[] frame;
        GvVideoHeader h;
        lock (gate)
        {
            if (!hasPending)
                return;
            frame = pending;
            h = pendingHeader;
            hasPending = false;
        }

        Foveated = h.Foveated;
        FoveaRect = h.FoveaRectUV;
        CaptureTsUs = h.CaptureTsUs;
        // Canvas size comes from the decoded texture rather than configuration, so a
        // sender that changes canvas mid-session still composites correctly here.
        if (texture != null && texture.width > 0)
            LayerSpans = h.LayerSpans(texture.width, texture.height);
        pendingSpans = h;

        if (h.Codec != GvVideoHeader.CodecMjpeg)
        {
            // H.264 in the Editor would need a managed decoder; there is deliberately
            // not one. This is the "you forgot --codec mjpeg" case.
            if (unsupportedCodec++ == 0)
                Debug.LogWarning($"GvEditorVideoSource: eye {eye} is receiving " +
                                 "H.264, which the Editor cannot decode. Restart the " +
                                 "sender with --codec mjpeg.");
            return;
        }

        if (texture == null)
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        if (texture.LoadImage(frame, false))
        {
            framesDecoded++;
            // LoadImage is what establishes the canvas size, so the spans can only be
            // resolved after the first successful decode.
            LayerSpans = pendingSpans.LayerSpans(texture.width, texture.height);
        }
        else
        {
            decodeErrors++;
        }
    }

    public void RefreshStats() { /* counters are read straight through */ }

    public void Dispose()
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
