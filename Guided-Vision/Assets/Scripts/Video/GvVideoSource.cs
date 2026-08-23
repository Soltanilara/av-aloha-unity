using System;
using UnityEngine;

/// <summary>
/// One eye's video feed: a Java-side UDP receiver and MediaCodec decoder, surfaced to
/// Unity as an ordinary <see cref="Texture2D"/>.
///
/// Nothing here touches video bytes. The socket, the reassembler and the decoder all
/// live in com.guidedvision.gv.GvVideoStream, and the decoded frame reaches Unity as a
/// GL texture that libgvnative fills on the render thread. See docs/PLAN.md section 4.3.
///
/// The texture is NOT sRGB-flagged: it holds sRGB-encoded bits in a linear texture, and
/// StereoEyeView decodes them in the shader (_SrgbDecode). That keeps the colour
/// decision explicit rather than depending on how a driver treats an external texture.
/// </summary>
public sealed class GvVideoSource : IGvVideoSource
{
    private const string NativeClass = "com.guidedvision.gv.GvNative";

    private const int ActionInit = 1;
    private const int ActionUpdate = 2;
    private const int ActionShutdown = 3;

    public readonly int Slot;
    public readonly int Eye;          // 0 left, 1 right -- matches gvlink.protocol
    public readonly int Width;
    public readonly int Height;

    private Texture2D texture;
    private bool started;

#if UNITY_ANDROID
    private AndroidJavaObject stream;
    private AndroidJavaClass native;
    private IntPtr eventFunc = IntPtr.Zero;
#endif

    /// <summary>Null until the first decoded frame has allocated the GL texture.</summary>
    public Texture2D Texture => texture;

    public bool Running => started;
    public bool Foveated { get; private set; }

    /// <summary>
    /// Centre.xy and extent.zw of the foveal crop, normalised into the source image,
    /// already flipped into GL uv convention (v = 0 at the bottom) so the shader can
    /// use it directly. The sender works in image-row convention; the flip happens
    /// here, once.
    /// </summary>
    public Vector4 FoveaRect { get; private set; } = new Vector4(0.5f, 0.5f, 0f, 0f);
    public Vector4 LayerSpans { get; private set; } = new Vector4(1f, 0.5f, 1f, 0.5f);

    public string DecoderName { get; private set; } = "-";

    // The decoder blits raw output into a linear-declared texture, so the shader owns
    // the sRGB conversion. See gvnative.c.
    public bool NeedsSrgbDecode => true;

    // Counters, refreshed by RefreshStats() rather than every frame.
    public long FramesDecoded { get; private set; }
    public long FramesCompleted { get; private set; }
    public long FramesDropped { get; private set; }
    public long FragmentsLost { get; private set; }
    public long BytesReceived { get; private set; }
    public long FramesNoInputBuffer { get; private set; }
    public long DecodeErrors { get; private set; }
    public long CaptureTsUs { get; private set; }

    public GvVideoSource(int slot, int eye, int width, int height)
    {
        Slot = slot;
        Eye = eye;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Adopt one eye's Java stream. The stream is owned by GvVideoLink, which shares a
    /// socket between the eyes; this only wires up the texture path on top of it.
    /// </summary>
    public bool Start(AndroidJavaObject javaStream)
    {
        if (started)
            return true;
        if (javaStream == null)
            return false;
        if (Application.isEditor)
            return false;      // the JNI calls below only mean anything on device
#if UNITY_ANDROID
        try
        {
            stream = javaStream;
            DecoderName = stream.Call<string>("getDecoderName");

            native = new AndroidJavaClass(NativeClass);
            using (var surfaceTexture = stream.Call<AndroidJavaObject>("getSurfaceTexture"))
            {
                if (!native.CallStatic<bool>("register", Slot, surfaceTexture, Width, Height))
                {
                    Debug.LogError($"GvVideoSource: native register failed for slot {Slot}");
                    Dispose();
                    return false;
                }
            }
            eventFunc = (IntPtr)native.CallStatic<long>("getRenderEventFunc");

            // GL objects can only be built on the render thread, so kick that off now
            // rather than waiting for the first frame -- otherwise a stream that never
            // connects also never reports a texture, and the two failures look alike.
            GL.IssuePluginEvent(eventFunc, EventId(ActionInit));

            started = true;
            Debug.Log($"GvVideoSource: eye {Eye}, {Width}x{Height}, decoder {DecoderName}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"GvVideoSource: eye {Eye} start threw: {e}");
            Dispose();
            return false;
        }
#else
        Debug.LogWarning("GvVideoSource: Android only; no video on this platform.");
        return false;
#endif
    }

    /// <summary>Call once per frame, from the display component.</summary>
    public void Update()
    {
        if (!started)
            return;
#if UNITY_ANDROID
        var state = stream.Call<float[]>("pollFrameState");
        if (state == null || state.Length < 10)
            return;

        Foveated = state[1] > 0.5f;
        int band = Height / 2;
        float cw = state[6] > 0f ? state[6] : Width;
        float chh = state[7] > 0f ? state[7] : band;
        float fw = state[8] > 0f ? state[8] : Width;
        float fh = state[9] > 0f ? state[9] : band;
        LayerSpans = new Vector4(cw / Width, chh / Height, fw / Width, fh / Height);
        // The sender measures the fovea centre from the top of the image; uv counts
        // from the bottom.
        FoveaRect = new Vector4(state[2], 1f - state[3], state[4], state[5]);

        if (state[0] > 0.5f)
            GL.IssuePluginEvent(eventFunc, EventId(ActionUpdate));

        if (texture == null)
            TryBindTexture();
#endif
    }

#if UNITY_ANDROID
    private void TryBindTexture()
    {
        // Non-zero only once the render thread has processed the init event.
        int name = native.CallStatic<int>("getDstTexture", Slot);
        if (name == 0)
            return;

        // linear:true -- the texture holds sRGB bits but we do the decode in the
        // shader, so Unity must not apply any conversion of its own.
        texture = Texture2D.CreateExternalTexture(
            Width, Height, TextureFormat.RGBA32, false, true, (IntPtr)name);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        Debug.Log($"GvVideoSource: eye {Eye} bound GL texture {name}");
    }
#endif

    public void RefreshStats()
    {
        if (!started)
            return;
#if UNITY_ANDROID
        var s = stream.Call<long[]>("getStats");
        if (s == null || s.Length < 8)
            return;
        FramesDecoded = s[0];
        FramesCompleted = s[1];
        FramesDropped = s[2];
        FragmentsLost = s[3];
        BytesReceived = s[4];
        FramesNoInputBuffer = s[5];
        DecodeErrors = s[6];
        CaptureTsUs = s[7];
#endif
    }

    private int EventId(int action) => (action << 8) | (Slot & 0xFF);

    public void Dispose()
    {
        started = false;
#if UNITY_ANDROID
        try
        {
            if (eventFunc != IntPtr.Zero)
                GL.IssuePluginEvent(eventFunc, EventId(ActionShutdown));
            native?.CallStatic("unregister", Slot);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GvVideoSource: dispose eye {Eye}: {e.Message}");
        }
        // stream is owned by GvVideoLink; only the class handle is ours.
        native?.Dispose();
        stream = null;
        native = null;
        eventFunc = IntPtr.Zero;
#endif
        if (texture != null)
        {
            // The GL name belongs to the native plugin, which deletes it on the
            // shutdown event; Unity must only drop its wrapper.
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
