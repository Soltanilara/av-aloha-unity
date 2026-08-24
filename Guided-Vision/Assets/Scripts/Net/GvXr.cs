using UnityEngine;

/// <summary>
/// What this particular headset can actually do.
///
/// Kept in one place because the answers are needed in three (the session handshake,
/// the settings page, and the uplink's permission request) and because every one of
/// these calls can throw when there is no XR runtime -- which is the normal case in the
/// Editor, and would otherwise mean three separate try/catch blocks that each get it
/// slightly wrong.
/// </summary>
public static class GvXr
{
    private static int eyeTracking = -1;   // -1 unknown, 0 no, 1 yes

    /// <summary>
    /// True only where the hardware actually tracks eyes -- Quest Pro, not Quest 2/3.
    ///
    /// Foveation is gated on this rather than on the user's preference alone. Without
    /// gaze the crop can only sit in the middle of the frame, which spends the whole
    /// mechanism -- a sharp patch, a soft blend, a low-detail surround -- on making the
    /// centre of the picture sharp and the edges worse. A plain stream is strictly
    /// better on a headset that cannot say where you are looking.
    /// </summary>
    public static bool EyeTrackingAvailable
    {
        get
        {
            if (eyeTracking < 0)
                eyeTracking = Probe() ? 1 : 0;
            return eyeTracking == 1;
        }
    }

    private static bool Probe()
    {
#if UNITY_ANDROID
        if (Application.isEditor)
            return false;      // no runtime to ask, and guessing "yes" is the bad guess
        try
        {
            return OVRPlugin.eyeTrackingSupported;
        }
        catch (System.Exception e)
        {
            Debug.Log("GvXr: eye tracking unavailable (" + e.GetType().Name + ")");
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>Re-ask, after a permission grant for instance.</summary>
    public static void Forget() { eyeTracking = -1; }

    /// <summary>
    /// The transform to treat as "the viewer's head": the rig's centre-eye anchor.
    ///
    /// Not <c>Camera.main</c>. Meta's OVRCameraRig tags *both* CenterEyeAnchor and
    /// LeftEyeAnchor as MainCamera, and Camera.main only returns *enabled* cameras -- so
    /// in a scene using the legacy per-eye camera setup (the teleop scene does), it
    /// silently resolves to the LEFT EYE. Everything placed "in front of the head" then
    /// sits half an IPD off to one side, and every head-relative ray starts from the
    /// wrong origin. It looks almost right, which is why it survives review.
    /// </summary>
    public static Transform Head()
    {
        var rig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            return rig.centerEyeAnchor;
        var cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    /// <summary>
    /// Clip planes for every active camera.
    ///
    /// The default near plane is 0.1 m, and hands and controllers are *rendered meshes* --
    /// so anything you bring closer than a hand's width from your face is clipped away and
    /// simply vanishes. That is disconcerting in a way a number in an inspector does not
    /// convey, and it is scene-scoped by accident: the teleop scene happened to carry a
    /// 0.07 override on its per-eye cameras and the start scene did not, so the same app
    /// behaved differently in two places for no reason anyone chose.
    ///
    /// Set here rather than per scene so it cannot drift apart again. Nothing in either
    /// scene needs depth precision -- the video quad does not even write depth -- so the
    /// near plane can go well inside arm's reach, and the far plane comes in to pay for it.
    /// </summary>
    public static void ApplyClipPlanes(float near = 0.03f, float far = 100f)
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null)
                continue;
            cam.nearClipPlane = near;
            cam.farClipPlane = far;
        }
    }
}
