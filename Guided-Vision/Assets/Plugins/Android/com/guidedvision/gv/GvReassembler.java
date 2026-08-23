package com.guidedvision.gv;

/**
 * Rebuilds encoded frames from UDP fragments. Mirror of gvlink/protocol.py --
 * if you change one, change the other.
 *
 * The policy is the whole latency argument (docs/PLAN.md 4.1): a frame is completed
 * or abandoned, never waited for. The moment a fragment of a newer frame arrives, any
 * older incomplete frame is dead.
 *
 * Allocation-free in steady state: buffers are sized once and reused, because a
 * per-frame allocation at 60 fps x 2 eyes is a GC pause waiting to happen.
 */
final class GvReassembler {

    static final int HEADER_SIZE = 40;
    static final int MAGIC = 0x47564944;      // 'GVID'
    static final int FLAG_KEYFRAME = 1;
    static final int FLAG_FOVEATED = 1 << 1;
    static final int FLAG_LAST_FRAGMENT = 1 << 2;

    private static final int MAX_FRAGMENTS = 2048;   // ~2.8 MB frame at a 1400 B MTU
    private static final int MAX_FRAGMENT = 1600;
    private static final int RESTART_GAP = 64;

    // ---- header of the frame currently being assembled ----
    int frameId, eye, flags;
    long captureTsUs;
    float foveaX, foveaY, foveaW, foveaH;
    int codec;
    // Pixels of its band each layer occupies, anchored top-left; 0 means it fills the
    // band. See gvlink/foveal.py.
    int coarsePxW, coarsePxH, foveaPxW, foveaPxH;

    // ---- completed frame, valid immediately after push() returns true ----
    byte[] frame = new byte[1 << 20];
    int frameLen;

    // ---- counters ----
    long framesCompleted, framesDropped, fragmentsReceived, fragmentsLost, bytesReceived;

    private long curId = -1;
    private int count, have;
    private final byte[][] parts = new byte[MAX_FRAGMENTS][];
    private final int[] partLen = new int[MAX_FRAGMENTS];
    private final boolean[] got = new boolean[MAX_FRAGMENTS];

    private static int u16(byte[] b, int o) {
        return ((b[o] & 0xFF) << 8) | (b[o + 1] & 0xFF);
    }

    private static long u32(byte[] b, int o) {
        return ((long) (b[o] & 0xFF) << 24) | ((b[o + 1] & 0xFF) << 16)
                | ((b[o + 2] & 0xFF) << 8) | (b[o + 3] & 0xFF);
    }

    private static long u64(byte[] b, int o) {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | (b[o + i] & 0xFF);
        return v;
    }

    /** True if frame id a is newer than b, tolerating u32 wraparound. */
    private static boolean newer(long a, long b) {
        return ((a - b) & 0xFFFFFFFFL) < 0x80000000L;
    }

    private void reset() {
        if (curId >= 0) java.util.Arrays.fill(got, 0, Math.min(count, MAX_FRAGMENTS), false);
        curId = -1;
        count = 0;
        have = 0;
    }

    private void abandon() {
        if (curId >= 0 && count > 0) {
            fragmentsLost += count - have;
            framesDropped++;
        }
        reset();
    }

    /** Feed one datagram. Returns true when {@link #frame} holds a complete frame. */
    boolean push(byte[] buf, int len) {
        if (len < HEADER_SIZE || (int) u32(buf, 0) != MAGIC) return false;
        fragmentsReceived++;
        bytesReceived += len;

        long fid = u32(buf, 4);
        int fragIdx = u16(buf, 10);
        int fragCount = u16(buf, 12);
        if (fragCount <= 0 || fragCount > MAX_FRAGMENTS || fragIdx >= fragCount) return false;

        if (curId >= 0 && fid != curId) {
            if (newer(fid, curId) || ((curId - fid) & 0xFFFFFFFFL) > RESTART_GAP) {
                abandon();
            } else {
                return false;               // straggler from a frame already given up on
            }
        }

        if (curId < 0) {
            curId = fid;
            count = fragCount;
            frameId = (int) fid;
            eye = buf[8] & 0xFF;
            flags = (buf[9] & 0xFF) & ~FLAG_LAST_FRAGMENT;
            captureTsUs = u64(buf, 14);
            foveaX = u16(buf, 22) / 65535.0f;
            foveaY = u16(buf, 24) / 65535.0f;
            foveaW = u16(buf, 26) / 65535.0f;
            foveaH = u16(buf, 28) / 65535.0f;
            codec = buf[30] & 0xFF;
            coarsePxW = u16(buf, 32);
            coarsePxH = u16(buf, 34);
            foveaPxW = u16(buf, 36);
            foveaPxH = u16(buf, 38);
        }

        if (got[fragIdx]) return false;
        int payload = len - HEADER_SIZE;
        if (payload > MAX_FRAGMENT) return false;
        if (parts[fragIdx] == null) parts[fragIdx] = new byte[MAX_FRAGMENT];
        System.arraycopy(buf, HEADER_SIZE, parts[fragIdx], 0, payload);
        partLen[fragIdx] = payload;
        got[fragIdx] = true;
        have++;

        if (have < count) return false;

        int total = 0;
        for (int i = 0; i < count; i++) total += partLen[i];
        if (total > frame.length) frame = new byte[Integer.highestOneBit(total) << 1];
        int off = 0;
        for (int i = 0; i < count; i++) {
            System.arraycopy(parts[i], 0, frame, off, partLen[i]);
            off += partLen[i];
        }
        frameLen = off;
        framesCompleted++;
        reset();
        return true;
    }
}
