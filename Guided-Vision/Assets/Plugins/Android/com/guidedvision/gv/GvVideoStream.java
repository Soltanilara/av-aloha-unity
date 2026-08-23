package com.guidedvision.gv;

import android.graphics.SurfaceTexture;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.view.Surface;

import java.nio.ByteBuffer;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * One eye's video path: reassembly and hardware decode onto a SurfaceTexture.
 *
 * Datagrams are handed in by {@link GvVideoReceiver}, which owns the shared socket and
 * demuxes on the header's eye field. Video bytes never cross into managed memory --
 * the reassembler and MediaCodec both live here, and Unity only ever asks for the
 * texture and a handful of numbers. That is the point of doing this in Java at all.
 *
 * The SurfaceTexture is created detached from any GL context. The native plugin
 * attaches it to Unity's context on the render thread (see gvnative.c); calling
 * updateTexImage from any other thread is the classic source of a mysterious
 * one-frame stall.
 */
public final class GvVideoStream {

    private static final String TAG = "GvVideo";
    private static final String MIME = "video/avc";
    private static final int FOVEA_RING = 64;

    private final int eye;
    private final int width;
    private final int height;

    private SurfaceTexture surfaceTexture;
    private Surface surface;
    private MediaCodec codec;
    private HandlerThread callbackThread;
    private Thread drainThread;

    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicBoolean frameAvailable = new AtomicBoolean(false);
    private final GvReassembler asm = new GvReassembler();

    // Fovea metadata, parked by frame id until the decoder gives that frame back.
    // Keyed on presentationTimeUs, which we set to the frame id, so header and frame
    // are matched exactly rather than by assuming FIFO order.
    private final long[] ringId = new long[FOVEA_RING];
    private final float[] ringX = new float[FOVEA_RING];
    private final float[] ringY = new float[FOVEA_RING];
    private final float[] ringW = new float[FOVEA_RING];
    private final float[] ringH = new float[FOVEA_RING];
    private final boolean[] ringFov = new boolean[FOVEA_RING];
    private final long[] ringTs = new long[FOVEA_RING];
    private final int[] ringCoarseW = new int[FOVEA_RING];
    private final int[] ringCoarseH = new int[FOVEA_RING];
    private final int[] ringFovPxW = new int[FOVEA_RING];
    private final int[] ringFovPxH = new int[FOVEA_RING];

    private volatile float curFoveaX = 0.5f, curFoveaY = 0.5f;
    private volatile float curFoveaW = 0f, curFoveaH = 0f;
    private volatile int curCoarsePxW, curCoarsePxH, curFoveaPxW, curFoveaPxH;
    private volatile boolean curFoveated = false;
    private volatile long curCaptureTsUs = 0;

    private volatile long framesDecoded = 0;
    private volatile long framesNoInputBuffer = 0;
    private volatile long decodeErrors = 0;
    private volatile String decoderName = "?";

    public GvVideoStream(int eye, int width, int height) {
        this.eye = eye;
        this.width = width;
        this.height = height;
        java.util.Arrays.fill(ringId, -1L);
    }

    // ------------------------------------------------------------------ lifecycle

    public boolean start() {
        if (running.get()) return true;
        try {
            surfaceTexture = new SurfaceTexture(false);   // detached; native attaches
            surfaceTexture.setDefaultBufferSize(width, height);
            callbackThread = new HandlerThread("gv-st-" + eye);
            callbackThread.start();
            surfaceTexture.setOnFrameAvailableListener(
                    st -> frameAvailable.set(true), new Handler(callbackThread.getLooper()));
            surface = new Surface(surfaceTexture);

            MediaFormat fmt = MediaFormat.createVideoFormat(MIME, width, height);
            if (Build.VERSION.SDK_INT >= 30) {
                fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            }
            // Ask the codec to treat this as realtime work rather than throughput work.
            fmt.setInteger(MediaFormat.KEY_PRIORITY, 0);
            fmt.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE);

            codec = MediaCodec.createDecoderByType(MIME);
            codec.configure(fmt, surface, null, 0);
            codec.start();
            try {
                decoderName = codec.getName();
            } catch (Exception ignored) {
            }

            running.set(true);
            drainThread = new Thread(this::drainLoop, "gv-drain-" + eye);
            drainThread.setPriority(Thread.MAX_PRIORITY);
            drainThread.start();
            Log.i(TAG, "eye " + eye + " decoder=" + decoderName);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "start failed for eye " + eye, e);
            stop();
            return false;
        }
    }

    public void stop() {
        running.set(false);
        joinQuietly(drainThread);
        try { if (codec != null) { codec.stop(); codec.release(); } } catch (Exception ignored) {}
        try { if (surface != null) surface.release(); } catch (Exception ignored) {}
        // The SurfaceTexture is released by the native side, which owns its GL
        // attachment; releasing it here would race the render thread.
        if (callbackThread != null) callbackThread.quitSafely();
        codec = null;
        surface = null;
        drainThread = null;
        callbackThread = null;
    }

    private static void joinQuietly(Thread t) {
        if (t == null) return;
        try { t.join(500); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
    }

    // --------------------------------------------------------------------- threads

    /** Called from the receiver's socket thread, once per datagram for this eye. */
    void onDatagram(byte[] buf, int len) {
        if (!running.get()) return;
        if (!asm.push(buf, len)) return;
        submit(asm.frame, asm.frameLen, asm.frameId, asm.flags, asm.captureTsUs,
               asm.foveaX, asm.foveaY, asm.foveaW, asm.foveaH,
               asm.coarsePxW, asm.coarsePxH, asm.foveaPxW, asm.foveaPxH);
    }

    private void submit(byte[] data, int len, int frameId, int flags, long ts,
                        float fx, float fy, float fw, float fh,
                        int coarsePxW, int coarsePxH, int foveaPxW, int foveaPxH) {
        MediaCodec c = codec;
        if (c == null) return;
        try {
            // A short timeout, not an indefinite one: blocking here would stall the
            // socket read and turn decoder backpressure into UDP loss, which is much
            // harder to diagnose than a dropped frame we counted ourselves.
            int idx = c.dequeueInputBuffer(2000);
            if (idx < 0) {
                framesNoInputBuffer++;
                return;
            }
            ByteBuffer in = c.getInputBuffer(idx);
            if (in == null) return;
            in.clear();
            if (in.remaining() < len) {
                framesNoInputBuffer++;
                c.queueInputBuffer(idx, 0, 0, frameId, 0);
                return;
            }
            in.put(data, 0, len);

            int slot = frameId & (FOVEA_RING - 1);
            ringId[slot] = frameId;
            ringX[slot] = fx; ringY[slot] = fy; ringW[slot] = fw; ringH[slot] = fh;
            ringFov[slot] = (flags & GvReassembler.FLAG_FOVEATED) != 0;
            ringTs[slot] = ts;
            ringCoarseW[slot] = coarsePxW;
            ringCoarseH[slot] = coarsePxH;
            ringFovPxW[slot] = foveaPxW;
            ringFovPxH[slot] = foveaPxH;

            c.queueInputBuffer(idx, 0, len, frameId, 0);
        } catch (IllegalStateException e) {
            decodeErrors++;
        } catch (Exception e) {
            decodeErrors++;
            Log.w(TAG, "submit eye " + eye, e);
        }
    }

    private void drainLoop() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (running.get()) {
            MediaCodec c = codec;
            if (c == null) return;
            int idx;
            try {
                idx = c.dequeueOutputBuffer(info, 5000);
            } catch (IllegalStateException e) {
                decodeErrors++;
                continue;
            } catch (Exception e) {
                if (running.get()) Log.w(TAG, "drain eye " + eye, e);
                return;
            }
            if (idx < 0) continue;

            long fid = info.presentationTimeUs;
            int slot = (int) (fid & (FOVEA_RING - 1));
            if (ringId[slot] == fid) {
                curFoveaX = ringX[slot]; curFoveaY = ringY[slot];
                curFoveaW = ringW[slot]; curFoveaH = ringH[slot];
                curFoveated = ringFov[slot];
                curCaptureTsUs = ringTs[slot];
                curCoarsePxW = ringCoarseW[slot];
                curCoarsePxH = ringCoarseH[slot];
                curFoveaPxW = ringFovPxW[slot];
                curFoveaPxH = ringFovPxH[slot];
            }
            try {
                // true = render into the SurfaceTexture. Everything downstream of here
                // happens on the render thread via the native plugin.
                c.releaseOutputBuffer(idx, true);
                framesDecoded++;
            } catch (Exception e) {
                decodeErrors++;
            }
        }
    }

    // ----------------------------------------------------------------- Unity-facing

    public SurfaceTexture getSurfaceTexture() { return surfaceTexture; }

    /** True at most once per produced frame; clears the flag. */
    public boolean pollFrameAvailable() { return frameAvailable.getAndSet(false); }

    private final float[] frameState = new float[10];

    /**
     * Everything Unity needs per frame in a single JNI round trip:
     * {newFrame, foveated, foveaX, foveaY, foveaW, foveaH,
     *  coarsePxW, coarsePxH, foveaPxW, foveaPxH}.
     *
     * Twelve individual Call<T> invocations per eye per frame is not a lot of time,
     * but it is a lot of avoidable time in the one place that runs 72 times a second.
     */
    public float[] pollFrameState() {
        frameState[0] = frameAvailable.getAndSet(false) ? 1f : 0f;
        frameState[1] = curFoveated ? 1f : 0f;
        frameState[2] = curFoveaX;
        frameState[3] = curFoveaY;
        frameState[4] = curFoveaW;
        frameState[5] = curFoveaH;
        frameState[6] = curCoarsePxW;
        frameState[7] = curCoarsePxH;
        frameState[8] = curFoveaPxW;
        frameState[9] = curFoveaPxH;
        return frameState;
    }

    /**
     * Counters for the HUD, polled about once a second:
     * {decoded, completed, dropped, fragmentsLost, bytes, noInputBuffer, errors, captureTsUs}.
     */
    public long[] getStats() {
        return new long[] {
            framesDecoded, asm.framesCompleted, asm.framesDropped, asm.fragmentsLost,
            asm.bytesReceived, framesNoInputBuffer, decodeErrors, curCaptureTsUs,
        };
    }

    public boolean isFoveated()   { return curFoveated; }
    public float getFoveaX()      { return curFoveaX; }
    public float getFoveaY()      { return curFoveaY; }
    public float getFoveaW()      { return curFoveaW; }
    public float getFoveaH()      { return curFoveaH; }
    public long  getCaptureTsUs() { return curCaptureTsUs; }

    public long getFramesDecoded()    { return framesDecoded; }
    public long getFramesCompleted()  { return asm.framesCompleted; }
    public long getFramesDropped()    { return asm.framesDropped; }
    public long getFragmentsLost()    { return asm.fragmentsLost; }
    public long getBytesReceived()    { return asm.bytesReceived; }
    public long getFramesNoInput()    { return framesNoInputBuffer; }
    public long getDecodeErrors()     { return decodeErrors; }
    public String getDecoderName()    { return decoderName; }
    public boolean isRunning()        { return running.get(); }
}
