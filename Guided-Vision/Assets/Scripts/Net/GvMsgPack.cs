using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// A small MessagePack codec: enough of the spec for a control channel, and nothing else.
///
/// Why hand-written rather than a package: Unity ships no MessagePack, and the
/// reflection/codegen serialisers that exist fight IL2CPP's AOT compilation on Android
/// in ways that surface at runtime on device rather than at build time. This is a few
/// hundred lines of well-specified format with no dependencies and no codegen step, and
/// it is validated byte-for-byte against Python's `msgpack` (see selftest.py).
///
/// Schema-less on purpose. Adding a new topic on either side means adding a key, not
/// declaring a type in two languages and keeping them in step.
///
/// Decoded types: null, bool, long, double, string, byte[],
/// List&lt;object&gt;, Dictionary&lt;string, object&gt;.
/// </summary>
public static class GvMsgPack
{
    // ------------------------------------------------------------------- writing

    public sealed class Writer
    {
        private byte[] buf;
        private int len;

        public Writer(int capacity = 256) { buf = new byte[Math.Max(16, capacity)]; }

        public int Length => len;

        public void Reset() { len = 0; }

        private void Need(int n)
        {
            if (len + n <= buf.Length)
                return;
            int cap = buf.Length;
            while (cap < len + n) cap *= 2;
            Array.Resize(ref buf, cap);
        }

        public void U8(byte b) { Need(1); buf[len++] = b; }

        public void Raw(byte[] src, int offset, int count)
        {
            Need(count);
            Buffer.BlockCopy(src, offset, buf, len, count);
            len += count;
        }

        // MessagePack is big-endian throughout.
        public void BE16(ushort v) { Need(2); buf[len++] = (byte)(v >> 8); buf[len++] = (byte)v; }

        public void BE32(uint v)
        {
            Need(4);
            buf[len++] = (byte)(v >> 24); buf[len++] = (byte)(v >> 16);
            buf[len++] = (byte)(v >> 8); buf[len++] = (byte)v;
        }

        public void BE64(ulong v)
        {
            Need(8);
            for (int i = 56; i >= 0; i -= 8) buf[len++] = (byte)(v >> i);
        }

        public byte[] ToArray()
        {
            var outp = new byte[len];
            Buffer.BlockCopy(buf, 0, outp, 0, len);
            return outp;
        }

        /// <summary>The backing buffer, valid for <see cref="Length"/> bytes. No copy.</summary>
        public byte[] Buffer_ => buf;
    }

    public static byte[] Encode(object value)
    {
        var w = new Writer();
        Encode(w, value);
        return w.ToArray();
    }

    public static void Encode(Writer w, object value)
    {
        switch (value)
        {
            case null: w.U8(0xc0); return;
            case bool b: w.U8(b ? (byte)0xc3 : (byte)0xc2); return;
            case string s: EncodeString(w, s); return;
            case byte[] bin: EncodeBinary(w, bin); return;
            case float f: w.U8(0xca); w.BE32(BitConverter.ToUInt32(BitConverter.GetBytes(f), 0)); return;
            case double d: w.U8(0xcb); w.BE64(BitConverter.ToUInt64(BitConverter.GetBytes(d), 0)); return;
            case sbyte v: EncodeLong(w, v); return;
            case byte v: EncodeLong(w, v); return;
            case short v: EncodeLong(w, v); return;
            case ushort v: EncodeLong(w, v); return;
            case int v: EncodeLong(w, v); return;
            case uint v: EncodeLong(w, v); return;
            case long v: EncodeLong(w, v); return;
        }

        if (value is IDictionary<string, object> map)
        {
            EncodeMapHeader(w, map.Count);
            foreach (var kv in map)
            {
                EncodeString(w, kv.Key);
                Encode(w, kv.Value);
            }
            return;
        }
        if (value is IList<object> list)
        {
            EncodeArrayHeader(w, list.Count);
            for (int i = 0; i < list.Count; i++) Encode(w, list[i]);
            return;
        }
        if (value is float[] fa)
        {
            // Poses and joint vectors are the common case; keeping them float32 halves
            // the bytes against the double the language would otherwise widen them to.
            EncodeArrayHeader(w, fa.Length);
            foreach (var f in fa) Encode(w, f);
            return;
        }

        throw new ArgumentException("GvMsgPack: cannot encode " + value.GetType());
    }

    private static void EncodeLong(Writer w, long v)
    {
        if (v >= 0)
        {
            if (v < 0x80) { w.U8((byte)v); }
            else if (v <= byte.MaxValue) { w.U8(0xcc); w.U8((byte)v); }
            else if (v <= ushort.MaxValue) { w.U8(0xcd); w.BE16((ushort)v); }
            else if (v <= uint.MaxValue) { w.U8(0xce); w.BE32((uint)v); }
            else { w.U8(0xcf); w.BE64((ulong)v); }
        }
        else
        {
            if (v >= -32) { w.U8((byte)(0xe0 | (v + 32))); }
            else if (v >= sbyte.MinValue) { w.U8(0xd0); w.U8((byte)(sbyte)v); }
            else if (v >= short.MinValue) { w.U8(0xd1); w.BE16((ushort)(short)v); }
            else if (v >= int.MinValue) { w.U8(0xd2); w.BE32((uint)(int)v); }
            else { w.U8(0xd3); w.BE64((ulong)v); }
        }
    }

    private static void EncodeString(Writer w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length < 32) w.U8((byte)(0xa0 | bytes.Length));
        else if (bytes.Length <= byte.MaxValue) { w.U8(0xd9); w.U8((byte)bytes.Length); }
        else if (bytes.Length <= ushort.MaxValue) { w.U8(0xda); w.BE16((ushort)bytes.Length); }
        else { w.U8(0xdb); w.BE32((uint)bytes.Length); }
        w.Raw(bytes, 0, bytes.Length);
    }

    private static void EncodeBinary(Writer w, byte[] b)
    {
        if (b.Length <= byte.MaxValue) { w.U8(0xc4); w.U8((byte)b.Length); }
        else if (b.Length <= ushort.MaxValue) { w.U8(0xc5); w.BE16((ushort)b.Length); }
        else { w.U8(0xc6); w.BE32((uint)b.Length); }
        w.Raw(b, 0, b.Length);
    }

    public static void EncodeArrayHeader(Writer w, int n)
    {
        if (n < 16) w.U8((byte)(0x90 | n));
        else if (n <= ushort.MaxValue) { w.U8(0xdc); w.BE16((ushort)n); }
        else { w.U8(0xdd); w.BE32((uint)n); }
    }

    public static void EncodeMapHeader(Writer w, int n)
    {
        if (n < 16) w.U8((byte)(0x80 | n));
        else if (n <= ushort.MaxValue) { w.U8(0xde); w.BE16((ushort)n); }
        else { w.U8(0xdf); w.BE32((uint)n); }
    }

    // ------------------------------------------------------------------- reading

    public static object Decode(byte[] data) { int o = 0; return Decode(data, ref o, data.Length); }

    public static object Decode(byte[] data, ref int o, int end)
    {
        if (o >= end) throw new FormatException("GvMsgPack: truncated");
        byte t = data[o++];

        if (t <= 0x7f) return (long)t;                       // positive fixint
        if (t >= 0xe0) return (long)(sbyte)t;                // negative fixint
        if ((t & 0xf0) == 0x80) return DecodeMap(data, ref o, end, t & 0x0f);
        if ((t & 0xf0) == 0x90) return DecodeArray(data, ref o, end, t & 0x0f);
        if ((t & 0xe0) == 0xa0) return DecodeString(data, ref o, end, t & 0x1f);

        switch (t)
        {
            case 0xc0: return null;
            case 0xc2: return false;
            case 0xc3: return true;
            case 0xc4: return DecodeBinary(data, ref o, end, (int)U8(data, ref o, end));
            case 0xc5: return DecodeBinary(data, ref o, end, BE16(data, ref o, end));
            case 0xc6: return DecodeBinary(data, ref o, end, (int)BE32(data, ref o, end));
            case 0xca: return (double)BitConverter.ToSingle(BitConverter.GetBytes(BE32(data, ref o, end)), 0);
            case 0xcb: return BitConverter.ToDouble(BitConverter.GetBytes(BE64(data, ref o, end)), 0);
            case 0xcc: return (long)U8(data, ref o, end);
            case 0xcd: return (long)BE16(data, ref o, end);
            case 0xce: return (long)BE32(data, ref o, end);
            case 0xcf: return (long)BE64(data, ref o, end);
            case 0xd0: return (long)(sbyte)U8(data, ref o, end);
            case 0xd1: return (long)(short)BE16(data, ref o, end);
            case 0xd2: return (long)(int)BE32(data, ref o, end);
            case 0xd3: return (long)BE64(data, ref o, end);
            case 0xd9: return DecodeString(data, ref o, end, U8(data, ref o, end));
            case 0xda: return DecodeString(data, ref o, end, BE16(data, ref o, end));
            case 0xdb: return DecodeString(data, ref o, end, (int)BE32(data, ref o, end));
            case 0xdc: return DecodeArray(data, ref o, end, BE16(data, ref o, end));
            case 0xdd: return DecodeArray(data, ref o, end, (int)BE32(data, ref o, end));
            case 0xde: return DecodeMap(data, ref o, end, BE16(data, ref o, end));
            case 0xdf: return DecodeMap(data, ref o, end, (int)BE32(data, ref o, end));
        }
        throw new FormatException($"GvMsgPack: unsupported type 0x{t:x2}");
    }

    private static byte U8(byte[] d, ref int o, int end)
    {
        if (o >= end) throw new FormatException("GvMsgPack: truncated");
        return d[o++];
    }

    private static ushort BE16(byte[] d, ref int o, int end)
    {
        if (o + 2 > end) throw new FormatException("GvMsgPack: truncated");
        ushort v = (ushort)((d[o] << 8) | d[o + 1]); o += 2; return v;
    }

    private static uint BE32(byte[] d, ref int o, int end)
    {
        if (o + 4 > end) throw new FormatException("GvMsgPack: truncated");
        uint v = ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
        o += 4; return v;
    }

    private static ulong BE64(byte[] d, ref int o, int end)
    {
        if (o + 8 > end) throw new FormatException("GvMsgPack: truncated");
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | d[o + i];
        o += 8; return v;
    }

    private static string DecodeString(byte[] d, ref int o, int end, int n)
    {
        if (o + n > end) throw new FormatException("GvMsgPack: truncated string");
        var s = Encoding.UTF8.GetString(d, o, n); o += n; return s;
    }

    private static byte[] DecodeBinary(byte[] d, ref int o, int end, int n)
    {
        if (o + n > end) throw new FormatException("GvMsgPack: truncated binary");
        var b = new byte[n];
        Buffer.BlockCopy(d, o, b, 0, n); o += n; return b;
    }

    private static List<object> DecodeArray(byte[] d, ref int o, int end, int n)
    {
        var list = new List<object>(n);
        for (int i = 0; i < n; i++) list.Add(Decode(d, ref o, end));
        return list;
    }

    private static Dictionary<string, object> DecodeMap(byte[] d, ref int o, int end, int n)
    {
        var map = new Dictionary<string, object>(n);
        for (int i = 0; i < n; i++)
        {
            // Keys are strings by convention here. Anything else is coerced rather than
            // rejected: a decoder that throws on an unexpected key type turns a
            // cosmetic mismatch into a dead control channel.
            object k = Decode(d, ref o, end);
            map[k as string ?? Convert.ToString(k)] = Decode(d, ref o, end);
        }
        return map;
    }

    // ------------------------------------------------------------- typed access

    public static double Num(object v, double fallback = 0)
    {
        switch (v)
        {
            case long l: return l;
            case double d: return d;
            case bool b: return b ? 1 : 0;
            default: return fallback;
        }
    }

    public static float GetFloat(IDictionary<string, object> m, string k, float def = 0f) =>
        m != null && m.TryGetValue(k, out var v) ? (float)Num(v, def) : def;

    public static long GetLong(IDictionary<string, object> m, string k, long def = 0) =>
        m != null && m.TryGetValue(k, out var v) ? (long)Num(v, def) : def;

    public static bool GetBool(IDictionary<string, object> m, string k, bool def = false) =>
        m != null && m.TryGetValue(k, out var v) ? (v is bool b ? b : Num(v, def ? 1 : 0) != 0) : def;

    public static string GetString(IDictionary<string, object> m, string k, string def = null) =>
        m != null && m.TryGetValue(k, out var v) ? (v as string ?? def) : def;

    public static Dictionary<string, object> GetMap(IDictionary<string, object> m, string k) =>
        m != null && m.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

    public static List<object> GetList(IDictionary<string, object> m, string k) =>
        m != null && m.TryGetValue(k, out var v) ? v as List<object> : null;

    public static float[] GetFloats(IDictionary<string, object> m, string k)
    {
        var list = GetList(m, k);
        if (list == null) return null;
        var outp = new float[list.Count];
        for (int i = 0; i < list.Count; i++) outp[i] = (float)Num(list[i]);
        return outp;
    }
}
