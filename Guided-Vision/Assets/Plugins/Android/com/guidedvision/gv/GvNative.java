package com.guidedvision.gv;

import android.graphics.SurfaceTexture;

/**
 * Thin front door to libgvnative. Loading the library from Java (rather than relying
 * on IL2CPP's DllImport resolution) is what gets JNI_OnLoad called, which is how the
 * native side gets a JavaVM.
 *
 * Everything except {@link #getRenderEventFunc()} is safe to call from any thread.
 * The returned pointer is fed to Unity's GL.IssuePluginEvent so the real GL work
 * lands on the render thread.
 */
public final class GvNative {

    static { System.loadLibrary("gvnative"); }

    private GvNative() { }

    public static final int ACTION_INIT     = 1;
    public static final int ACTION_UPDATE   = 2;
    public static final int ACTION_SHUTDOWN = 3;

    /** Event id for GL.IssuePluginEvent. */
    public static int event(int slot, int action) { return (action << 8) | (slot & 0xFF); }

    public static native boolean register(int slot, SurfaceTexture st, int width, int height);
    public static native void unregister(int slot);

    /** The ordinary GL_TEXTURE_2D that Unity samples. 0 until the init event has run. */
    public static native int getDstTexture(int slot);

    public static native long getUpdateCount(int slot);
    public static native long getUpdateErrors(int slot);
    public static native long getRenderEventFunc();
}
