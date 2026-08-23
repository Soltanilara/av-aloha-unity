using System;
using UnityEngine;

/// <summary>
/// The video half of the robot link: one UDP socket, two decoded eye textures.
///
/// Owns the Java-side receiver (socket + demux by the header's eye field) and pairs
/// each eye's stream with a <see cref="GvVideoSource"/> that turns it into a Unity
/// texture. Phase 4 grows the control channel alongside this; the video path does not
/// change when it does.
/// </summary>
public sealed class GvVideoLink : IDisposable
{
    private const string ReceiverClass = "com.guidedvision.gv.GvVideoReceiver";

    private AndroidJavaObject receiver;
    private GvEditorVideoReceiver editorReceiver;

    public IGvVideoSource Left { get; private set; }
    public IGvVideoSource Right { get; private set; }
    public bool Running { get; private set; }

    /// <summary>
    /// True when the C# MJPEG path is in use rather than MediaCodec -- either because
    /// this is the Editor, or because software decode was forced for bring-up.
    /// </summary>
    public bool IsSoftwarePath => editorReceiver != null;

    public bool Start(int port, int width, int height, bool forceSoftware = false)
    {
        if (Running)
            return true;

        if (Application.isEditor || forceSoftware)
        {
            // MediaCodec is Android-only, so the Editor gets the C# MJPEG stand-in.
            // Same socket, same protocol, same fovea rect -- everything above this
            // line behaves identically, which is the point.
            //
            // It is also selectable *on device*, which is what makes a first bring-up
            // tractable: MediaCodec, the native OES blit and the transport all fail the
            // same way, as a black screen. Forcing this path answers which half is
            // broken in one step, and if it works you at least have a picture to tune
            // the display geometry against while the decoder is sorted out.
            if (forceSoftware && !Application.isEditor)
                Debug.LogWarning("GvVideoLink: software MJPEG decode forced. Expect high "
                               + "bitrate and main-thread cost; this is a bring-up tool.");
            editorReceiver = new GvEditorVideoReceiver();
            Left = editorReceiver.Left;
            Right = editorReceiver.Right;
            Running = editorReceiver.Start(port);
            return Running;
        }

        var left = new GvVideoSource(0, 0, width, height);
        var right = new GvVideoSource(1, 1, width, height);
        Left = left;
        Right = right;

#if UNITY_ANDROID
        try
        {
            receiver = new AndroidJavaObject(ReceiverClass, port, width, height);
            if (!receiver.Call<bool>("start"))
            {
                Debug.LogError($"GvVideoLink: receiver failed to start on :{port}");
                Dispose();
                return false;
            }
            // The streams stay owned by the receiver -- these handles are borrowed.
            bool ok = left.Start(receiver.Call<AndroidJavaObject>("getStream", 0))
                    & right.Start(receiver.Call<AndroidJavaObject>("getStream", 1));
            if (!ok)
            {
                Dispose();
                return false;
            }
            Running = true;
            Debug.Log($"GvVideoLink: listening on :{port} at {width}x{height} per eye");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"GvVideoLink: start threw: {e}");
            Dispose();
            return false;
        }
#else
        Debug.LogWarning("GvVideoLink: Android only; no video on this platform.");
        return false;
#endif
    }

    public void Update()
    {
        Left?.Update();
        Right?.Update();
    }

    public void RefreshStats()
    {
        Left?.RefreshStats();
        Right?.RefreshStats();
    }

    public void Dispose()
    {
        Running = false;
        // Sources first: they issue the native shutdown event that detaches each
        // SurfaceTexture from the GL context, which must happen before the receiver
        // releases the streams that own them.
        Left?.Dispose();
        Right?.Dispose();
        Left = null;
        Right = null;
        editorReceiver?.Dispose();
        editorReceiver = null;
#if UNITY_ANDROID
        try { receiver?.Call("stop"); }
        catch (Exception e) { Debug.LogWarning("GvVideoLink: stop: " + e.Message); }
        receiver?.Dispose();
#endif
        receiver = null;
    }
}
