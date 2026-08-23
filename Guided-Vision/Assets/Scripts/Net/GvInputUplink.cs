using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Head, controllers and gaze, upstream at display rate. Mirror of
/// gvlink/protocol.py HeadsetInput (158 bytes, version 2).
///
/// UDP and last-wins, deliberately. A pose from two frames ago is worse than no pose,
/// so this must never queue or retransmit; anything that has to *arrive* -- a command,
/// a mode change -- belongs on the control channel instead.
///
/// Gaze goes out as normalised source-image coordinates rather than a direction,
/// because the headset is the only end that knows the quad geometry needed to do that
/// projection. The robot then needs to know nothing about the display in order to crop
/// for it.
/// </summary>
public class GvInputUplink : MonoBehaviour
{
    [Header("Tracking")]
    [Tooltip("Poses are sent relative to this. Leave empty to send world space.")]
    public Transform trackingOrigin;
    public Transform head;
    public Transform leftController;
    public Transform rightController;

    [Header("Gaze (Quest Pro)")]
    public OVREyeGaze leftEyeGaze;
    public OVREyeGaze rightEyeGaze;

    [Tooltip("Needed to project a gaze ray onto the video image. Found automatically " +
             "if left empty.")]
    public GvStereoDisplay display;

    [Header("Editor / simulator")]
    [Tooltip("With no eye tracker, steer the fovea with the mouse: the cursor's " +
             "position across the Game view maps straight to a point in the source " +
             "image. Takes precedence over OVREyeGaze, which reports a fixed " +
             "straight-ahead gaze in the Editor. Editor only.")]
    public bool simulateGazeWithMouse = true;

    [Header("Rate")]
    [Tooltip("Send rate. Matching the display refresh keeps gaze as fresh as the " +
             "frame it will steer.")]
    public float rateHz = 90f;

    private readonly byte[] packet = new byte[GvInputPacket.Size];
    private Socket socket;
    private EndPoint target;
    private uint seq;
    private float nextSend;
    private string boundHost;
    private int boundPort;
    private float nextResolve;

    public bool Running { get; private set; }
    public long Sent { get; private set; }
    public bool GazeAvailable { get; private set; }
    public bool GazeSimulated { get; private set; }

    /// <summary>Last gaze point sent, in source-image UV. Centre when unavailable.</summary>
    public Vector2 GazeUV { get; private set; } = new Vector2(0.5f, 0.5f);

    private static readonly System.Diagnostics.Stopwatch Clock =
        System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Find everything from the rig if it was not assigned. The component is meant to
    /// be dropped onto a scene and work: hand-wiring five transforms is five chances
    /// to wire one wrong, and a mis-wired controller is invisible until a robot moves
    /// the wrong way.
    /// </summary>
    private void AutoWire()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            if (trackingOrigin == null) trackingOrigin = rig.trackingSpace;
            if (head == null) head = rig.centerEyeAnchor;
            if (leftController == null) leftController = rig.leftHandAnchor;
            if (rightController == null) rightController = rig.rightHandAnchor;
        }
        if (head == null && Camera.main != null)
            head = Camera.main.transform;

        if (leftEyeGaze == null || rightEyeGaze == null)
        {
            foreach (var g in FindObjectsByType<OVREyeGaze>(FindObjectsInactive.Include))
            {
                if (g.Eye == OVREyeGaze.EyeId.Left && leftEyeGaze == null) leftEyeGaze = g;
                if (g.Eye == OVREyeGaze.EyeId.Right && rightEyeGaze == null) rightEyeGaze = g;
            }
        }

        if (display == null)
            display = FindAnyObjectByType<GvStereoDisplay>();
    }

    private void Start()
    {
        AutoWire();
        if (head == null)
            Debug.LogWarning("GvInputUplink: no head transform found; poses will be empty.");

        // Eye tracking is permission-gated and the user can refuse. Everything else
        // must keep working when they do -- foveation simply falls back to centre.
#if UNITY_ANDROID && !UNITY_EDITOR
        const string EyePermission = "com.oculus.permission.EYE_TRACKING";
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(EyePermission))
            UnityEngine.Android.Permission.RequestUserPermission(EyePermission);
#endif
        // The robot address is deliberately NOT read here. GvRobotSession.Profile is
        // only populated when GvStereoDisplay.Start() calls Connect(), and the order of
        // two components' Start() is undefined -- reading it here is a coin flip that,
        // when it loses, leaves the uplink silently idle for the whole session. Resolve
        // it from Update() instead, where it can also survive the profile changing.
    }

    private void Update()
    {
        if (Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + 1f / Mathf.Max(1f, rateHz);
        if (!EnsureTarget())
            return;

        Vector2 gl, gr;
        float confidence;
        bool gazeValid = TryGaze(out gl, out gr, out confidence);
        GazeAvailable = gazeValid;
        GazeUV = gazeValid ? (gl + gr) * 0.5f : new Vector2(0.5f, 0.5f);

        GvInputPacket.Pack(packet, ++seq, (ulong)(Clock.ElapsedTicks / 10L),
                           ReadPose(head),
                           ReadController(leftController, OVRInput.Controller.LTouch),
                           ReadController(rightController, OVRInput.Controller.RTouch),
                           gl, gr, confidence, gazeValid);
        try
        {
            socket.SendTo(packet, target);
            Sent++;
        }
        catch (Exception)
        {
            // Fire and forget by design: a failed datagram is a dropped sample and the
            // next is 11 ms away. Tearing down the session over it would be worse.
        }
    }

    /// <summary>
    /// Bind (or rebind) the socket to wherever the robot currently is.
    ///
    /// Cheap and idempotent once bound -- it compares the resolved host and port and
    /// returns immediately if nothing moved. Retried on a timer while idle so that a
    /// session established after this component started is picked up on its own.
    /// </summary>
    private bool EnsureTarget()
    {
        var profile = GvRobotSession.Instance.Profile;
        string host = profile != null ? profile.host : null;
        int port = profile != null ? profile.inputPort : GvInputPacket.DefaultPort;

        // Playing GvPassthroughScene directly, without the menu. The display already
        // carries the address for exactly this case; read its field rather than adding
        // a second one that can disagree with it.
        if (string.IsNullOrWhiteSpace(host) && display != null)
            host = display.fallbackHost;

        if (string.IsNullOrWhiteSpace(host))
        {
            if (Running)
            {
                Running = false;
                Debug.Log("GvInputUplink: no robot address; uplink idle.");
            }
            return false;
        }
        if (Running && host == boundHost && port == boundPort)
            return true;
        if (Time.unscaledTime < nextResolve)
            return false;
        nextResolve = Time.unscaledTime + 1f;

        try
        {
            IPAddress ip = null;
            foreach (var a in Dns.GetHostAddresses(host))
                if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            if (ip == null)
                throw new Exception("could not resolve '" + host + "'");

            if (socket == null)
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            target = new IPEndPoint(ip, port);
            boundHost = host;
            boundPort = port;
            Running = true;
            Debug.Log("GvInputUplink: sending to " + ip + ":" + port + " at " + rateHz.ToString("0") + " Hz");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("GvInputUplink: " + e.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ gathering

    private GvPose ReadPose(Transform t)
    {
        if (t == null)
            return GvPose.Invalid;
        var p = new GvPose { Valid = true };
        if (trackingOrigin != null)
        {
            p.Position = trackingOrigin.InverseTransformPoint(t.position);
            p.Rotation = Quaternion.Inverse(trackingOrigin.rotation) * t.rotation;
        }
        else
        {
            p.Position = t.position;
            p.Rotation = t.rotation;
        }
        return p;
    }

    private GvControllerState ReadController(Transform t, OVRInput.Controller which)
    {
        var s = new GvControllerState { Pose = ReadPose(t) };
        try
        {
            s.Stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, which);
            s.Trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, which);
            s.Grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, which);
            if (OVRInput.Get(OVRInput.Button.One, which)) s.Buttons |= GvInputPacket.ButtonOne;
            if (OVRInput.Get(OVRInput.Button.Two, which)) s.Buttons |= GvInputPacket.ButtonTwo;
            if (OVRInput.Get(OVRInput.Button.PrimaryThumbstick, which)) s.Buttons |= GvInputPacket.ButtonStick;
            if (OVRInput.Get(OVRInput.Button.Start, which)) s.Buttons |= GvInputPacket.ButtonMenu;
        }
        catch (Exception)
        {
            // No OVR runtime (plain Editor play mode). Poses still go out.
        }
        return s;
    }

    private bool TryGaze(out Vector2 left, out Vector2 right, out float confidence)
    {
        left = right = new Vector2(0.5f, 0.5f);
        confidence = 0f;
        if (display == null || head == null)
            return false;

        // The mouse wins in the Editor when it is enabled, rather than only filling in
        // when no eye tracker reports. OVREyeGaze can report EyeTrackingEnabled with a
        // fixed straight-ahead gaze under Link or the simulator, and that would quietly
        // pin the fovea to the centre and leave the mouse doing nothing -- which looks
        // exactly like the feature being broken.
        if (TrySimulatedGaze(out left, out right, out confidence))
            return true;

        bool any = false;
        float conf = 1f;
        bool haveLeft = leftEyeGaze != null && leftEyeGaze.EyeTrackingEnabled;
        bool haveRight = rightEyeGaze != null && rightEyeGaze.EyeTrackingEnabled;

        if (haveLeft)
        {
            left = display.DirectionToImageUV(
                head.InverseTransformDirection(leftEyeGaze.transform.forward), false);
            conf = Mathf.Min(conf, leftEyeGaze.Confidence);
            any = true;
        }
        if (haveRight)
        {
            right = display.DirectionToImageUV(
                head.InverseTransformDirection(rightEyeGaze.transform.forward), true);
            conf = Mathf.Min(conf, rightEyeGaze.Confidence);
            any = true;
        }
        if (!any)
            return false;

        // One eye tracking and not the other is common at the edges of the box. Using
        // the good eye for both beats letting the fovea snap back to centre-screen.
        if (!haveLeft) left = right;
        if (!haveRight) right = left;

        confidence = conf;
        GazeSimulated = false;
        return true;
    }

    /// <summary>
    /// Stand-in for an eye tracker: the cursor's position across the Game view maps
    /// straight to a point in the source image, so the fovea can be steered by hand.
    ///
    /// Deliberately NOT routed through <see cref="GvStereoDisplay.DirectionToImageUV"/>,
    /// which was the first attempt and did not work. Under XR that projection needs a
    /// ray built from the render camera, and the numbers do not line up: the mouse
    /// arrives in Game-view pixels (measured 1241x608) while the eye camera's
    /// ScreenPointToRay expects its own render target (1440x1584, aspect 0.91, 96 deg
    /// FOV against the quad's 55). Screen centre came out as a ray pointing 43 degrees
    /// down, which pinned the fovea to the bottom edge and looked exactly like the
    /// feature doing nothing.
    ///
    /// A linear map has none of that coupling and is what a steering control should do
    /// anyway: the cursor a quarter of the way across the view puts the patch a quarter
    /// of the way across the image, whatever the headset, FOV or render target. It is a
    /// control, not a simulated eyeball -- the real projection is covered by its own
    /// known-angle test and by real gaze rays on device.
    /// </summary>
    private bool TrySimulatedGaze(out Vector2 left, out Vector2 right, out float confidence)
    {
        left = right = new Vector2(0.5f, 0.5f);
        confidence = 0f;
        GazeSimulated = false;
        if (!simulateGazeWithMouse || !Application.isEditor)
            return false;
        if (Screen.width <= 0 || Screen.height <= 0)
            return false;

        Vector3 m = Input.mousePosition;
        float x = m.x / Screen.width;
        float y = m.y / Screen.height;
        if (x < 0f || x > 1f || y < 0f || y > 1f)
            return false;   // pointer outside the view: hold, do not snap to centre

        // Screen y counts up from the bottom; image v counts down from the top.
        left = right = new Vector2(x, 1f - y);
        confidence = 1f;
        GazeSimulated = true;
        return true;
    }

    private void OnDestroy()
    {
        Running = false;
        try { socket?.Close(); } catch { /* already gone */ }
        socket = null;
    }
}
