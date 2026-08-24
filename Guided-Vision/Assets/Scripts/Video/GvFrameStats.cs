using UnityEngine;

/// <summary>
/// How well the app is actually keeping up with the display.
///
/// The HUD used to report `1 / Time.unscaledDeltaTime` sampled twice a second, which is
/// one frame's duration and tells you nothing: it reads a healthy number right through a
/// session that is dropping one frame in three. Judder is not a low average, it is a
/// *distribution with a tail*, and the tail is the part the eyes object to.
///
/// So this keeps the worst frame in each interval and counts the ones that overran the
/// display period. That distinction matters here because it separates two problems with
/// completely different fixes: a stereo geometry fault looks wrong while standing still,
/// while missed frames look fine until you move -- which is why hands and controllers,
/// the things you move and track with your eyes, are where it shows up first.
/// </summary>
public sealed class GvFrameStats
{
    /// <summary>A frame is counted as missed past this multiple of the display period.
    /// Not 1.0: a frame landing a hair late is measurement noise, and counting it would
    /// bury the real overruns in a number nobody can read.</summary>
    private const float MissThreshold = 1.5f;

    private int frames;
    private int missed;
    private float elapsed;
    private float worst;

    /// <summary>Mean frames per second over the last completed interval.</summary>
    public float Fps { get; private set; }

    /// <summary>The single longest frame in the last interval, in milliseconds.</summary>
    public float WorstMs { get; private set; }

    /// <summary>Frames in the last interval that overran the display period.</summary>
    public int Missed { get; private set; }

    /// <summary>Frames in the last interval, so Missed reads as a proportion.</summary>
    public int Frames { get; private set; }

    /// <summary>Call once per frame.</summary>
    public void Sample(float dt, float displayHz)
    {
        if (dt <= 0f)
            return;
        frames++;
        elapsed += dt;
        if (dt > worst)
            worst = dt;
        if (displayHz > 1f && dt > MissThreshold / displayHz)
            missed++;
    }

    /// <summary>Close the interval and expose its numbers. Call on the HUD's tick.</summary>
    public void Flush()
    {
        if (frames == 0 || elapsed <= 0f)
            return;
        Fps = frames / elapsed;
        WorstMs = worst * 1000f;
        Missed = missed;
        Frames = frames;
        frames = 0;
        missed = 0;
        elapsed = 0f;
        worst = 0f;
    }
}
