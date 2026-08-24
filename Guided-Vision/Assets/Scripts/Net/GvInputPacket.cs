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
/// One tracked hand. Joints are positions in the same frame as every other pose here.
///
/// Positions rather than joint rotations: they are what a visualiser draws and what a
/// retargeter solves against, and they let the robot know nothing about Meta's skeleton
/// -- no bone lengths, no parent table, no handedness convention.
/// </summary>
public struct GvHandState
{
    public bool Tracked;
    public float Confidence;          // 0..1
    public GvPose Wrist;
    public float[] Pinch;             // 5, thumb..pinky, 0..1
    public Vector3[] Joints;          // null or the tracked joint positions
    public int JointCount;
}

/// <summary>
/// The headset input packet, as a pure function. Mirror of gvlink/protocol.py
/// HeadsetInput (version 3): 158 fixed bytes, then an optional hand block, then an
/// optional msgpack tail.
///
/// Separate from <see cref="GvInputUplink"/> so the wire format can be exercised
/// without a MonoBehaviour, a socket or a running XR runtime -- which is the only way
/// a layout this fiddly gets checked against the other end at all. The Python side
/// learned this the hard way: an off-by-one in the field offsets put gaze inside a
/// controller and nothing complained.
/// </summary>
public static class GvInputPacket
{
    /// <summary>The fixed part. Hands and extras are appended after it.</summary>
    public const int Size = 158;

    /// <summary>Fixed part of one hand: tracked, confidence, count, pad, pose, pinch.</summary>
    public const int HandHeadSize = 4 + 7 * 4 + 5;

    public const byte Version = 3;

    /// <summary>Matches DEFAULT_PORTS["input"] in gvlink/protocol.py.</summary>
    public const int DefaultPort = 15553;

    public const int FlagGaze = 1 << 0;
    public const int FlagHead = 1 << 1;
    public const int FlagLeft = 1 << 2;
    public const int FlagRight = 1 << 3;
    public const int FlagExtras = 1 << 4;
    public const int FlagHands = 1 << 5;

    /// <summary>
    /// The operator is holding the deadman control.
    ///
    /// Deliberately a bit in this packet rather than a topic on the control channel. It
    /// has to be current, and it has to fail safe: if the uplink stops arriving the bit
    /// stops arriving with it, so a robot gating motion on "deadman set in a packet
    /// newer than 100 ms" halts on its own with no timeout logic to get wrong. A TCP
    /// "released" event is a message that can fail to be delivered, which is the one
    /// thing a stop signal must never be.
    ///
    /// When the operator has configured no deadman control, this is set on every packet
    /// and <c>hs/state</c> reports <c>deadman: "off"</c> -- so a robot can tell "held"
    /// from "not in use" without the stream going quiet meaning something different.
    /// </summary>
    public const int FlagDeadman = 1 << 6;

    public const int ButtonOne = 1 << 0;     // A / X
    public const int ButtonTwo = 1 << 1;     // B / Y
    public const int ButtonStick = 1 << 2;
    public const int ButtonMenu = 1 << 3;

    /// <summary>Bytes needed for a packet carrying these hands.</summary>
    public static int SizeWithHands(GvHandState l, GvHandState r) =>
        Size + HandHeadSize * 2 + 12 * (l.JointCount + r.JointCount);

    /// <summary>
    /// Fills `buf` and returns the number of bytes written.
    ///
    /// Hands are appended only when `hands` is true, so a controller session is exactly
    /// the 158 bytes it always was and pays nothing for a feature it is not using.
    /// </summary>
    public static int Pack(byte[] buf, uint seq, ulong tsUs,
                           GvPose head, GvControllerState left, GvControllerState right,
                           Vector2 gazeLeft, Vector2 gazeRight, float gazeConfidence,
                           bool gazeValid, bool deadman,
                           bool hands, GvHandState handLeft, GvHandState handRight)
    {
        int need = hands ? SizeWithHands(handLeft, handRight) : Size;
        if (buf == null || buf.Length < need)
            throw new ArgumentException($"GvInputPacket: buffer must be >= {need} bytes");
        int c = 0;
        buf[c++] = (byte)'G'; buf[c++] = (byte)'V'; buf[c++] = (byte)'I'; buf[c++] = (byte)'N';
        buf[c++] = Version;

        int flags = 0;
        if (gazeValid) flags |= FlagGaze;
        if (head.Valid) flags |= FlagHead;
        if (left.Pose.Valid) flags |= FlagLeft;
        if (right.Pose.Valid) flags |= FlagRight;
        if (hands) flags |= FlagHands;
        if (deadman) flags |= FlagDeadman;
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

        if (hands)
        {
            WriteHand(buf, ref c, handLeft);
            WriteHand(buf, ref c, handRight);
        }
        return c;
    }

    private static void WriteHand(byte[] b, ref int c, GvHandState h)
    {
        int n = Mathf.Clamp(h.JointCount, 0, 255);
        b[c++] = (byte)(h.Tracked ? 1 : 0);
        b[c++] = (byte)Mathf.Clamp(Mathf.RoundToInt(h.Confidence * 255f), 0, 255);
        b[c++] = (byte)n;
        b[c++] = 0;                    // pad; the Python struct has it too
        WritePose(b, ref c, h.Wrist);
        for (int i = 0; i < 5; i++)
        {
            float v = (h.Pinch != null && i < h.Pinch.Length) ? h.Pinch[i] : 0f;
            b[c++] = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }
        for (int i = 0; i < n; i++)
        {
            Vector3 p = h.Joints[i];
            F32(b, ref c, p.x); F32(b, ref c, p.y); F32(b, ref c, p.z);
        }
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
