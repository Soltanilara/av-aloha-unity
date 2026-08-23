using System;
using UnityEngine;

/// <summary>One tracked pose, in whatever frame the caller chose.</summary>
public struct GvPose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Valid;

    public static GvPose Invalid => new GvPose { Rotation = Quaternion.identity };
}

/// <summary>One controller: where it is, and what is being done to it.</summary>
public struct GvControllerState
{
    public GvPose Pose;
    public Vector2 Stick;
    public float Trigger;      // index
    public float Grip;         // hand / middle
    public int Buttons;
}

/// <summary>
/// The headset input packet, as a pure function. Mirror of gvlink/protocol.py
/// HeadsetInput (158 bytes, version 2).
///
/// Separate from <see cref="GvInputUplink"/> so the wire format can be exercised
/// without a MonoBehaviour, a socket or a running XR runtime -- which is the only way
/// a layout this fiddly gets checked against the other end at all. The Python side
/// learned this the hard way: an off-by-one in the field offsets put gaze inside a
/// controller and nothing complained.
/// </summary>
public static class GvInputPacket
{
    public const int Size = 158;
    public const byte Version = 2;

    /// <summary>Matches DEFAULT_PORTS["input"] in gvlink/protocol.py.</summary>
    public const int DefaultPort = 15553;

    public const int FlagGaze = 1 << 0;
    public const int FlagHead = 1 << 1;
    public const int FlagLeft = 1 << 2;
    public const int FlagRight = 1 << 3;

    public const int ButtonOne = 1 << 0;     // A / X
    public const int ButtonTwo = 1 << 1;     // B / Y
    public const int ButtonStick = 1 << 2;
    public const int ButtonMenu = 1 << 3;

    /// <summary>Fills `buf` (must be at least <see cref="Size"/>) and returns the flags written.</summary>
    public static int Pack(byte[] buf, uint seq, ulong tsUs,
                           GvPose head, GvControllerState left, GvControllerState right,
                           Vector2 gazeLeft, Vector2 gazeRight, float gazeConfidence,
                           bool gazeValid)
    {
        if (buf == null || buf.Length < Size)
            throw new ArgumentException($"GvInputPacket: buffer must be >= {Size} bytes");

        int c = 0;
        buf[c++] = (byte)'G'; buf[c++] = (byte)'V'; buf[c++] = (byte)'I'; buf[c++] = (byte)'N';
        buf[c++] = Version;

        int flags = 0;
        if (gazeValid) flags |= FlagGaze;
        if (head.Valid) flags |= FlagHead;
        if (left.Pose.Valid) flags |= FlagLeft;
        if (right.Pose.Valid) flags |= FlagRight;
        buf[c++] = (byte)flags;

        U32(buf, ref c, seq);
        U64(buf, ref c, tsUs);

        WritePose(buf, ref c, head);
        WriteController(buf, ref c, left);
        WriteController(buf, ref c, right);

        F32(buf, ref c, gazeLeft.x); F32(buf, ref c, gazeLeft.y);
        F32(buf, ref c, gazeRight.x); F32(buf, ref c, gazeRight.y);
        F32(buf, ref c, gazeConfidence);

        if (c != Size)
            throw new InvalidOperationException($"GvInputPacket: wrote {c} bytes, expected {Size}");
        return flags;
    }

    private static void WritePose(byte[] b, ref int c, GvPose p)
    {
        F32(b, ref c, p.Position.x); F32(b, ref c, p.Position.y); F32(b, ref c, p.Position.z);
        F32(b, ref c, p.Rotation.x); F32(b, ref c, p.Rotation.y);
        F32(b, ref c, p.Rotation.z); F32(b, ref c, p.Rotation.w);
    }

    private static void WriteController(byte[] b, ref int c, GvControllerState s)
    {
        WritePose(b, ref c, s.Pose);
        F32(b, ref c, s.Stick.x); F32(b, ref c, s.Stick.y);
        F32(b, ref c, s.Trigger); F32(b, ref c, s.Grip);
        b[c++] = (byte)s.Buttons;
        b[c++] = 0;                    // pad; the Python struct has it too
    }

    // Big-endian throughout, matching Python's struct '!' prefix.
    private static void F32(byte[] b, ref int c, float v)
    {
        int bits = BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
        b[c++] = (byte)(bits >> 24); b[c++] = (byte)(bits >> 16);
        b[c++] = (byte)(bits >> 8); b[c++] = (byte)bits;
    }

    private static void U32(byte[] b, ref int c, uint v)
    {
        b[c++] = (byte)(v >> 24); b[c++] = (byte)(v >> 16);
        b[c++] = (byte)(v >> 8); b[c++] = (byte)v;
    }

    private static void U64(byte[] b, ref int c, ulong v)
    {
        for (int i = 56; i >= 0; i -= 8) b[c++] = (byte)(v >> i);
    }
}
