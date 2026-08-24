using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Session-scoped owner of the control channel.
///
/// Separate from the display because it is not a display concern: the link carries
/// robot I/O, and the video stream happens to be one of the things it negotiates. Any
/// script can reach it with <c>GvRobotSession.Instance</c> and start publishing or
/// subscribing without a scene reference.
///
///     var link = GvRobotSession.Instance.Link;
///     link.Subscribe("arm/state", d =&gt; { ... });
///     link.Publish("gripper/cmd", GvRobotSession.Map("width", 0.04f));
///
/// It survives scene loads, so a future in-session menu can drop back to the start
/// scene without dropping the robot connection.
/// </summary>
public class GvRobotSession : MonoBehaviour
{
    private static GvRobotSession instance;

    public static GvRobotSession Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("GvRobotSession");
                instance = go.AddComponent<GvRobotSession>();
                DontDestroyOnLoad(go);
            }
            return instance;
        }
    }

    public GvRobotLink Link { get; private set; }
    public GvRobotProfile Profile { get; private set; }

    private int lastCodec = -1;
    private bool lastFoveation;
    // Link.Disconnect() reports "down" synchronously, before Profile is cleared, so
    // without this a deliberate leave announces itself as a dropout -- and a Reconnect
    // announces a failure it is in the middle of fixing.
    private bool leaving;
    public bool Connected => Link != null && Link.Connected;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        Link = new GvRobotLink();
        // Connection state is the one thing the operator always needs to know and can
        // never infer: video simply stops, which looks identical to a robot that has
        // nothing to send. Announced from here rather than the display because the link
        // outlives any one scene.
        Link.ConnectionChanged += up =>
        {
            if (up)
                GvToast.Post("Connected to " + (Profile != null ? Profile.name : "robot"),
                             "info", 2f);
            else if (Profile != null && !leaving)
                GvToast.Post("Lost the robot - retrying", "warn", 4f);
        };
    }

    /// <summary>
    /// Connect to the robot in `profile` and tell it where to send video. Safe to call
    /// again with the same profile; the link ignores a second connect.
    /// </summary>
    public void Connect(GvRobotProfile profile, int codec, bool foveation)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.host))
        {
            Debug.Log("GvRobotSession: no robot address; control channel not started.");
            return;
        }
        Profile = profile;
        lastCodec = codec;
        lastFoveation = foveation;
        Link.Connect(profile.host, profile.controlPort, new Dictionary<string, object>
        {
            { "video", profile.videoPort },
            { "codec", codec },
            { "fovea", foveation },
            { "name", SystemInfo.deviceModel },
            // The stream's shape is chosen here, not on the robot: this is the end that
            // knows its own decoder and its own link. The robot clamps and falls back to
            // its own defaults for anything omitted.
            { "cw", profile.canvasWidth },
            { "ch", profile.canvasHeight },
            { "cs", profile.coarseScale },
            { "fs", profile.foveaScale },
        });
    }

    /// <summary>End the session on purpose. The link stays reusable.</summary>
    public void Disconnect()
    {
        leaving = true;
        try
        {
            if (Link != null)
                Link.Disconnect();
        }
        finally
        {
            leaving = false;
        }
        Profile = null;
        lastCodec = -1;
        lastFoveation = false;
    }

    /// <summary>
    /// Drop and re-establish with the same settings.
    ///
    /// Worth having as one operation rather than asking the operator to leave the
    /// session and come back: a stalled stream is the common case, and the fix should
    /// not cost them the robot list, the scene load and their place in the menu.
    /// </summary>
    public bool Reconnect()
    {
        var p = Profile;
        int codec = lastCodec;
        bool fovea = lastFoveation;
        if (p == null || codec < 0)
            return false;
        Disconnect();
        Connect(p, codec, fovea);
        GvToast.Post("Reconnecting to " + p.name, "info", 2f);
        return true;
    }

    private void Update()
    {
        // Every subscriber and reply callback fires from here, on the main thread.
        Link?.Pump();
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            Link?.Dispose();
            Link = null;
            instance = null;
        }
    }

    /// <summary>Shorthand for building a small message without the ceremony.</summary>
    public static Dictionary<string, object> Map(params object[] keysAndValues)
    {
        var m = new Dictionary<string, object>(keysAndValues.Length / 2);
        for (int i = 0; i + 1 < keysAndValues.Length; i += 2)
            m[keysAndValues[i] as string] = keysAndValues[i + 1];
        return m;
    }
}
