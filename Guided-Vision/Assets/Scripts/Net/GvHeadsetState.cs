using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What the device itself is doing, published on `hs/state` a couple of times a second.
///
/// The 90 Hz uplink answers "where are the operator's hands". This answers "is the
/// operator there at all, and is the headset in a fit state to be flown from" -- which
/// the robot currently has no way to ask.
///
/// The field that matters most is `mounted`. **A headset taken off is a safety event,
/// not telemetry.** The poses keep arriving from a device sitting on a desk, so a robot
/// with no other signal happily keeps tracking a wrist that nobody is attached to. It
/// should be treated exactly like a deadman release.
///
/// Everything else is here because it is cheap and because the answers are otherwise
/// unobtainable from the robot side: battery (a session that will die in ten minutes is
/// worth knowing about before the demonstration, not during), the render frame meter (so
/// a bad demonstration can be explained afterwards by judder rather than blamed on the
/// policy), hand-tracking confidence, and which input the operator actually has in their
/// hands. That last one belongs in the episode record: hands and controllers produce
/// measurably different demonstrations, and if it is not logged it becomes unexplained
/// variance in the data.
///
/// Slow on purpose. None of this needs to be fresh, and a topic that fires at frame rate
/// is a topic people switch off.
/// </summary>
public class GvHeadsetState : MonoBehaviour
{
    [Tooltip("Publishes per second. This is status, not control -- nothing here is worth " +
             "spending uplink on more often.")]
    public float rateHz = 2f;

    /// <summary>
    /// Bumped by every recentre. World-frame markers carry the epoch they were sent
    /// under, so a marker placed before a recentre can be dropped rather than silently
    /// relocating to a spot that no longer means anything.
    /// </summary>
    public int OriginEpoch { get; private set; }

    private GvStereoDisplay display;
    private GvInputUplink uplink;
    private GvHandTracking hands;
    private GvRobotSession session;
    private float next;
    private bool subscribed;
    private bool lastMounted = true;

    private void Start()
    {
        display = FindAnyObjectByType<GvStereoDisplay>(FindObjectsInactive.Include);
        uplink = FindAnyObjectByType<GvInputUplink>(FindObjectsInactive.Include);
        hands = FindAnyObjectByType<GvHandTracking>(FindObjectsInactive.Include);
        session = GvRobotSession.Instance;
    }

    private void Update()
    {
        if (session == null)
            session = GvRobotSession.Instance;
        var link = session != null ? session.Link : null;
        if (link == null || !link.Connected)
        {
            subscribed = false;
            return;
        }

        if (!subscribed)
        {
            // Re-subscribed on every reconnect: the link drops its handlers when it
            // disconnects, and a topic that quietly stops working after a reconnect is
            // a miserable thing to notice later.
            link.Subscribe("ui/recenter", _ => Recenter());
            subscribed = true;
        }

        // Taking the headset off is reported the moment it happens rather than waiting
        // for the next tick. Half a second is a long time for an arm that should have
        // stopped.
        bool mounted = Mounted();
        if (mounted != lastMounted)
        {
            lastMounted = mounted;
            next = 0f;
        }

        if (Time.unscaledTime < next)
            return;
        next = Time.unscaledTime + 1f / Mathf.Max(0.2f, rateHz);
        link.Publish("hs/state", Snapshot());
    }

    /// <summary>Re-origin the view, and invalidate anything placed in the old frame.</summary>
    public void Recenter()
    {
        if (OVRManager.display != null)
            OVRManager.display.RecenterPose();
        OriginEpoch++;
        if (display != null)
            display.ApplyHudDistance();
        GvToast.Post("View recentred", "info", 1.5f);
    }

    private static bool Mounted()
    {
        var m = OVRManager.instance;
        return m == null || m.isUserPresent;
    }

    private Dictionary<string, object> Snapshot()
    {
        var d = new Dictionary<string, object>
        {
            { "mounted", Mounted() },
            { "origin_epoch", OriginEpoch },
            { "eye_tracking", OVRPlugin.eyeTrackingEnabled },
        };

        // -1 on a platform that will not say. Passed through rather than clamped to 0,
        // which would read as a flat battery.
        float batt = SystemInfo.batteryLevel;
        d["batt"] = batt;

        if (uplink != null)
        {
            d["src"] = uplink.HandsActive ? "hands"
                     : (uplink.Running ? "controllers" : "none");
            d["uplink_hz"] = uplink.rateHz;
            d["sent"] = uplink.Sent;
            d["deadman"] = uplink.deadmanSource.ToString().ToLowerInvariant();
            d["deadman_held"] = uplink.DeadmanHeld;
            d["gaze"] = uplink.GazeAvailable;
        }

        if (display != null)
        {
            d["hz"] = display.DisplayHz;
            d["fps"] = display.Fps;
            d["missed"] = display.MissedFrames;
        }

        if (hands != null)
        {
            d["hand_conf"] = new Dictionary<string, object>
            {
                { "l", hands.Left.Tracked ? hands.Left.Confidence : 0f },
                { "r", hands.Right.Tracked ? hands.Right.Confidence : 0f },
            };
        }
        return d;
    }
}
