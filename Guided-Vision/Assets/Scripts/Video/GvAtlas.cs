using UnityEngine;

/// <summary>
/// Atlas canvas arithmetic. Mirror of `AtlasLayout` in `gvlink/foveal.py`, which is the
/// spec — if the two ever disagree, that one is right.
///
/// Only one operation lives here: shrinking the canvas to what the two layers actually
/// occupy.
///
/// The canvas size and the layer scales are chosen independently, so the default pairing
/// leaves most of the canvas black — 1024x1024 carrying a 358x178 coarse layer and a
/// 512x256 fovea is **81% padding**. Both ends still pay for it: the sender's encoder
/// walks every one of those macroblocks and, with intra-refresh on, periodically codes
/// them; the headset's decoder then reconstructs them. Measured at 1920x1200 that is
/// about **2x the encode time and 1.3x the decode time**, spent on pixels that are black
/// by construction.
///
/// Bitrate is *not* part of the win, and it was right not to expect it to be: black
/// compresses to almost nothing, which is exactly why the padded layout was defensible
/// in the first place. What it costs is time, at both ends.
///
/// **This has to happen on the headset, not the robot.** The canvas is a property of
/// this end's decoder: it is what the native receiver allocates its destination texture
/// at, and what <see cref="GvVideoSource.LayerSpans"/> divides by. A robot that shrank
/// the canvas unilaterally would leave the texture the wrong size and every layer span
/// off by the ratio — a picture sampled from the wrong part of the atlas, which looks
/// like a display bug rather than a negotiation one. Requesting the tight canvas keeps
/// the requested size and the arriving size the same number.
///
/// Nothing else changes. Layer pixel sizes are preserved, the header still carries them,
/// and the shader works in canvas *fractions* with the band split at the midpoint — so
/// it cannot tell the difference. The robot applies the same rule as a safety net for
/// clients that do not, which is safe because the operation is idempotent: after it, one
/// of the two scales is 1.0, and a layout whose largest scale is 1.0 is returned
/// unchanged.
/// </summary>
public static class GvAtlas
{
    /// <summary>
    /// Smallest canvas either end will use, mirrored by MIN_CANVAS in mock_robot.py.
    ///
    /// The robot clamps whatever canvas it is asked for into a sane range. A clamp that
    /// raised the canvas above what this end allocated would be silent and ugly: the
    /// decoder texture would be one size, every layer span divided by another, and the
    /// shader would sample the wrong part of the atlas. Keeping the same floor on both
    /// ends means the clamp never fires on a canvas we produced.
    /// </summary>
    public const int MinCanvas = 128;

    /// <summary>Round down to an even count — yuv420p subsamples chroma 2x2.</summary>
    public static int Even(float x) => Mathf.Max(2, (int)x & ~1);

    /// <summary>Round up to an even count, where undershooting would crop a layer.</summary>
    public static int EvenUp(float x) => Mathf.Max(2, (Mathf.CeilToInt(x) + 1) & ~1);

    public static int CoarseW(int canvasW, float coarseScale) => Even(canvasW * coarseScale);
    public static int CoarseH(int canvasH, float coarseScale) => Even(canvasH / 2 * coarseScale);
    public static int FoveaW(int canvasW, float foveaScale) => Even(canvasW * foveaScale);
    public static int FoveaH(int canvasH, float foveaScale) => Even(canvasH / 2 * foveaScale);

    /// <summary>
    /// The smallest canvas holding the same two layers, and the scales that reproduce
    /// them in it.
    ///
    /// Scaling the canvas by the larger of the two scales, and dividing both scales by
    /// it, leaves every `scale * dimension` product where it was — which is why the
    /// layers come out the same size to within the even-rounding both ends already do.
    /// </summary>
    public static void Tighten(int canvasW, int canvasH, float coarseScale, float foveaScale,
                               out int outW, out int outH, out float outCoarse, out float outFovea)
    {
        outW = canvasW;
        outH = canvasH;
        outCoarse = coarseScale;
        outFovea = foveaScale;

        float k = Mathf.Max(coarseScale, foveaScale);
        if (k >= 1f || k <= 0f)
            return;

        int cw = CoarseW(canvasW, coarseScale), ch = CoarseH(canvasH, coarseScale);
        int fw = FoveaW(canvasW, foveaScale), fh = FoveaH(canvasH, foveaScale);

        // Exactly the layers, nothing around them. Both sizes are already even, so no
        // rounding is needed -- and rounding up here would be worse than untidy: it
        // would leave the larger layer at a scale just under 1.0, so tightening an
        // already-tight layout would find more to do.
        int w = Mathf.Max(cw, fw);
        int bh = Mathf.Max(ch, fh);

        // Growing up to the floor is always safe; it only leaves the layers more room
        // than they need. Both dimensions by the same factor, because a scale is one
        // number per layer: a canvas whose band aspect drifted could no longer express
        // the layer shapes. Only this end floors -- the shared rule in foveal.py does
        // not, because a floor and exact layer preservation cannot both hold for a
        // layer smaller than the floor.
        float grow = Mathf.Max(1f, Mathf.Max((float)MinCanvas / w, (MinCanvas / 2f) / bh));
        if (grow > 1f)
        {
            w = EvenUp(w * grow);
            bh = EvenUp(bh * grow);
        }

        outW = w;
        outH = 2 * bh;
        // Derived from the layer sizes, not from k: what has to be preserved is the
        // layer, and the scale is only how the layout expresses it.
        outCoarse = Mathf.Min(1f, cw / (float)w);
        outFovea = Mathf.Min(1f, fw / (float)w);
    }

    /// <summary>Fraction of the canvas that is black padding. 0 is a perfect fit.</summary>
    public static float Waste(int canvasW, int canvasH, float coarseScale, float foveaScale)
    {
        if (canvasW <= 0 || canvasH <= 0)
            return 0f;
        long used = (long)CoarseW(canvasW, coarseScale) * CoarseH(canvasH, coarseScale)
                  + (long)FoveaW(canvasW, foveaScale) * FoveaH(canvasH, foveaScale);
        return 1f - used / (float)((long)canvasW * canvasH);
    }
}
