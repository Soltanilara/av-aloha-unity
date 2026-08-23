using System.Collections.Generic;
using UnityEngine;

/// <summary>One rectified camera's projection, in pixels.</summary>
public struct GvEyeIntrinsics
{
    public float fx, fy, cx, cy;

    public static GvEyeIntrinsics FromMap(Dictionary<string, object> m)
    {
        return new GvEyeIntrinsics
        {
            fx = GvMsgPack.GetFloat(m, "fx"),
            fy = GvMsgPack.GetFloat(m, "fy"),
            cx = GvMsgPack.GetFloat(m, "cx"),
            cy = GvMsgPack.GetFloat(m, "cy"),
        };
    }
}

/// <summary>
/// What the robot says about the cameras it is sending, so the images can be placed at
/// the angular size and position they were actually taken at.
///
/// Mirror of gvlink/camera.py. Everything describes the frames *as sent*, which is to
/// say after rectification -- the robot owns the calibration and has to resample anyway,
/// so it rectifies and the viewer never sees a distortion model. If one ever arrives
/// here, something upstream is wrong.
///
/// Without this the operator sets a field-of-view slider by eye, which is a guess made
/// from inside a headset where there is nothing to compare against. With it, a pixel
/// lands in the direction its camera ray actually pointed.
/// </summary>
public struct GvCameraParams
{
    public int Width, Height;
    public GvEyeIntrinsics Left, Right;

    /// <summary>Metres between optical centres. Zero when the robot did not say.</summary>
    public float BaselineM;

    public bool Rectified;
    public bool Valid;

    public static GvCameraParams FromMap(Dictionary<string, object> m)
    {
        var p = new GvCameraParams();
        if (m == null)
            return p;
        p.Width = (int)GvMsgPack.GetLong(m, "w");
        p.Height = (int)GvMsgPack.GetLong(m, "h");
        p.BaselineM = GvMsgPack.GetFloat(m, "b");
        p.Rectified = GvMsgPack.GetBool(m, "rect", true);
        var l = GvMsgPack.GetMap(m, "l");
        var r = GvMsgPack.GetMap(m, "r");
        if (p.Width <= 0 || p.Height <= 0 || l == null || r == null)
            return p;
        p.Left = GvEyeIntrinsics.FromMap(l);
        p.Right = GvEyeIntrinsics.FromMap(r);
        p.Valid = p.Left.fx > 1f && p.Left.fy > 1f && p.Right.fx > 1f && p.Right.fy > 1f;
        return p;
    }

    public float VFovDeg => Valid
        ? 2f * Mathf.Atan(Height / (2f * Mathf.Max(Left.fy, 1e-3f))) * Mathf.Rad2Deg
        : 0f;

    public float HFovDeg => Valid
        ? 2f * Mathf.Atan(Width / (2f * Mathf.Max(Left.fx, 1e-3f))) * Mathf.Rad2Deg
        : 0f;

    /// <summary>
    /// The quad for one eye: size in metres at plane distance `d`, and where its centre
    /// sits relative to the optical axis.
    ///
    /// Pure and static so the geometry can be tested without a scene. The offset is the
    /// part worth being careful about: cx is generally not the image centre even after
    /// rectification, so the picture is shifted sideways from the axis, and getting that
    /// sign wrong looks fine in one eye and is tiring to fuse in two.
    /// </summary>
    public static void QuadFromIntrinsics(GvEyeIntrinsics k, int width, int height,
                                          float distance, float scale,
                                          out Vector2 size, out Vector2 centre)
    {
        float fx = Mathf.Max(k.fx, 1e-3f), fy = Mathf.Max(k.fy, 1e-3f);
        size = new Vector2(width / fx * distance * scale,
                           height / fy * distance * scale);
        centre = new Vector2((width * 0.5f - k.cx) / fx * distance * scale,
                             -(height * 0.5f - k.cy) / fy * distance * scale);
    }

    /// <summary>
    /// Where a direction in head space lands on a quad, in image uv.
    ///
    /// The inverse of the placement above, and deliberately expressed in terms of the
    /// quad rather than the intrinsics: whatever put the quad there -- intrinsics,
    /// convergence, magnification, a manual trim -- this stays consistent with it.
    /// </summary>
    public static Vector2 QuadUV(Vector3 dirInHeadSpace, Vector3 quadPos, Vector2 quadSize)
    {
        if (dirInHeadSpace.z <= 1e-4f || quadPos.z <= 1e-4f
            || quadSize.x <= 1e-6f || quadSize.y <= 1e-6f)
            return new Vector2(0.5f, 0.5f);

        float x = dirInHeadSpace.x / dirInHeadSpace.z * quadPos.z;
        float y = dirInHeadSpace.y / dirInHeadSpace.z * quadPos.z;
        float u = (x - (quadPos.x - quadSize.x * 0.5f)) / quadSize.x;
        float v = 1f - (y - (quadPos.y - quadSize.y * 0.5f)) / quadSize.y;  // v counts down
        return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
    }

    public override string ToString() =>
        !Valid ? "no camera params"
               : string.Format("{0}x{1} {2:0}x{3:0} deg  base {4:0} mm{5}",
                               Width, Height, HFovDeg, VFovDeg, BaselineM * 1000f,
                               Rectified ? "" : "  UNRECTIFIED");
}
