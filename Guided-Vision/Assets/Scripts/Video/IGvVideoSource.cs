using UnityEngine;

/// <summary>
/// One eye's decoded video, however it got here.
///
/// Two implementations: <see cref="GvVideoSource"/> (MediaCodec on device, the real
/// one) and <see cref="GvEditorVideoSource"/> (MJPEG decoded in C#, so the display
/// shader, the atlas geometry and the stereo layout can be looked at on a desktop or
/// in the Meta XR Simulator instead of only through a headset).
/// </summary>
public interface IGvVideoSource : System.IDisposable
{
    /// <summary>Null until the first frame has arrived.</summary>
    Texture2D Texture { get; }

    bool Foveated { get; }

    /// <summary>Foveal crop centre.xy and extent.zw, already in GL uv convention.</summary>
    Vector4 FoveaRect { get; }

    /// <summary>
    /// Each layer's stored size as a fraction of the canvas -- coarse u,v then fovea
    /// u,v. Shrinking the coarse layer is what makes the periphery genuinely
    /// low-resolution; the shader needs these to sample either band.
    /// </summary>
    Vector4 LayerSpans { get; }

    /// <summary>
    /// True when the texture holds sRGB-encoded bits but is declared linear, so the
    /// shader must convert. The MediaCodec path blits raw decoder output and needs
    /// this; the Editor path lets Unity's importer handle it and does not.
    /// </summary>
    bool NeedsSrgbDecode { get; }

    string DecoderName { get; }

    long FramesDecoded { get; }
    long FramesCompleted { get; }
    long FramesDropped { get; }
    long FragmentsLost { get; }
    long BytesReceived { get; }
    long FramesNoInputBuffer { get; }
    long DecodeErrors { get; }

    /// <summary>
    /// Capture stamp of the newest frame, in the *robot's* monotonic microseconds.
    ///
    /// Not comparable to a local clock in absolute terms -- the two epochs are
    /// unrelated -- so never present this as a latency. Differences are meaningful
    /// though, which is all the bitrate controller needs: it tracks the minimum of
    /// (local now - this) and reads anything above that minimum as queueing delay,
    /// and the unknown offset cancels in the subtraction.
    /// </summary>
    long CaptureTsUs { get; }

    /// <summary>Once per frame, on the main thread.</summary>
    void Update();

    /// <summary>Pull the counters across; called about once a second, not per frame.</summary>
    void RefreshStats();
}
