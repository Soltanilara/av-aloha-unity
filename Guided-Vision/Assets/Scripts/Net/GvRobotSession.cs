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
    }

    /// <summary>
    /// Connect to the robot in `profile` and tell it where to send video. Safe to call
    /// again with the same profile; the link ignores a second connect.
    /// </summary>
    public void Connect(GvRobotProfile profile, int codec)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.host))
        {
            Debug.Log("GvRobotSession: no robot address; control channel not started.");
            return;
        }
        Profile = profile;
        Link.Connect(profile.host, profile.controlPort, new Dictionary<string, object>
        {
            { "video", profile.videoPort },
            { "codec", codec },
            { "fovea", profile.foveation },
            { "name", SystemInfo.deviceModel },
        });
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
