using UnityEngine;

/// <summary>
/// Shows the room behind the menu instead of a black void.
///
/// Worth doing for the start screen specifically: that is the moment the user has just
/// put the headset on and has not yet been given anything to look at. A black background
/// there means standing blind in a room, which is both unpleasant and the point at which
/// people walk into furniture.
///
/// Everything is set up at runtime and every step is optional, because passthrough is a
/// capability that can be missing: an older device, a runtime that does not offer it, or
/// the Editor with no headset attached. When any of that is true the backdrop falls back
/// to an opaque colour, and the menu reads exactly as it did before.
/// </summary>
public class GvPassthroughBackdrop : MonoBehaviour
{
    [Tooltip("Used when passthrough is unavailable, so the menu never renders over an " +
             "undefined background.")]
    public Color fallback = new Color(0.02f, 0.03f, 0.04f, 1f);

    public bool Running { get; private set; }

    private OVRPassthroughLayer layer;

    /// <summary>Attach one if the scene has none. Nothing to wire, like the menu itself.</summary>
    public static GvPassthroughBackdrop Ensure(GameObject host)
    {
        var found = FindAnyObjectByType<GvPassthroughBackdrop>(FindObjectsInactive.Include);
        return found != null ? found : host.AddComponent<GvPassthroughBackdrop>();
    }

    private void Start()
    {
        if (!TryEnable())
            ApplyClear(fallback);
    }

    private bool TryEnable()
    {
        var manager = FindAnyObjectByType<OVRManager>(FindObjectsInactive.Include);
        if (manager == null)
            return false;

        // OVRPlugin throws rather than returning false when there is no runtime, which
        // is the normal case in the Editor.
        try
        {
            if (!OVRManager.IsInsightPassthroughSupported())
                return false;
        }
        catch (System.Exception)
        {
            return false;
        }

        manager.isInsightPassthroughEnabled = true;

        layer = manager.GetComponent<OVRPassthroughLayer>();
        if (layer == null)
            layer = manager.gameObject.AddComponent<OVRPassthroughLayer>();
        // Underlay: passthrough composites *behind* the eye buffer, so anything the app
        // draws stays on top of the room.
        layer.overlayType = OVROverlay.OverlayType.Underlay;
        layer.textureOpacity = 1f;
        layer.hidden = false;

        // The room only shows through where the app clears to transparent.
        ApplyClear(new Color(0f, 0f, 0f, 0f));
        Running = true;
        Debug.Log("GvPassthroughBackdrop: passthrough on.");
        return true;
    }

    private void ApplyClear(Color c)
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null)
                continue;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = c;
        }
    }

    private void OnDestroy()
    {
        // Leaving passthrough running into the next scene would show the room behind the
        // robot's video, which is not what the video is for.
        if (layer != null)
            layer.hidden = true;
        var manager = FindAnyObjectByType<OVRManager>(FindObjectsInactive.Include);
        if (manager != null && Running)
            manager.isInsightPassthroughEnabled = false;
    }
}
