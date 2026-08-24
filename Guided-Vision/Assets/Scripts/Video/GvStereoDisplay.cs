using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays the two decoded eye streams as one head-locked quad per eye.
///
/// The stereo geometry, edge treatment and display configuration here are the
/// accumulated result of actually wearing the thing, and none of it depends on the
/// transport -- the pixels arrive from two GvVideoSource instances as a foveal atlas.
///
/// Nothing here queues video. Each source points at the newest decoded texture; if
/// frames arrive faster than Unity renders, the extra ones are overwritten rather
/// than buffered.
/// </summary>
public class GvStereoDisplay : MonoBehaviour
{
    [Header("Scene bindings")]
    public RawImage leftImage;
    public RawImage rightImage;
    public Canvas leftCanvas;
    public Canvas rightCanvas;
    public Transform headset;

    [Header("Transport")]
    [Tooltip("Robot address used only when no profile was chosen in the menu -- i.e. " +
             "when this scene is played directly. Leave empty and run the sender with " +
             "--host instead.")]
    public string fallbackHost = "";

    [Tooltip("UDP port both eyes arrive on. They are told apart by the packet " +
             "header's eye field, exactly as gvlink/protocol.py sends them.")]
    public int videoPort = 15552;

    [Tooltip("Transmitted canvas size per eye. Must match the sender's --canvas, and " +
             "must not change during a session -- the decoder is configured once.")]
    public int canvasWidth = 1024;
    public int canvasHeight = 1024;

    [Tooltip("The camera's own resolution, BEFORE atlas packing. This drives the quad " +
             "aspect ratio: the atlas canvas is square but the imagery is not, so " +
             "getting this wrong stretches the picture.")]
    public int sourceWidth = 1920;
    public int sourceHeight = 1200;

    [Header("Stereo display")]
    [Tooltip("How far in front of each eye the video quad sits, in metres. This does " +
             "NOT change how far away the imagery LOOKS: the quad is scaled with the " +
             "distance, so the angular image is identical at any value. Use " +
             "stereoSeparationDeg to change perceived depth.")]
    public float videoPlaneDistance = 1.0f;

    [Tooltip("Vertical field of view the video is displayed at, in degrees. Set this " +
             "to the camera's vertical FOV for 1:1 (undistorted) geometry.")]
    public float videoVFOV = 55f;

    [Tooltip("Extra magnification on top of videoVFOV. 1 = none.")]
    public float videoScale = 1.0f;

    [Tooltip("Angular separation between the two eye images, in degrees. Positive " +
             "pushes the scene further away; negative brings it closer. Keep it small.")]
    public float stereoSeparationDeg = 0f;

    [Tooltip("Vertical trim between the two eye images, in degrees. Should be 0 when " +
             "the sender rectifies.")]
    public float stereoVerticalTrimDeg = 0f;

    [Tooltip("Distance placed at zero disparity, in metres. With a known baseline this " +
             "replaces eyeballing the separation: objects at this range fuse on the " +
             "screen plane, nearer ones come forward. 0 disables. Needs camera params.")]
    public float convergenceDistance = 2.0f;

    [Tooltip("Ignore the robot's camera parameters and use the manual FOV above. For " +
             "a camera whose reported numbers are wrong.")]
    public bool ignoreCameraParams = false;

    [Header("Stereo comfort")]
    public float edgeFeather = 0.03f;
    public float outerEdgeMask = 0f;
    public float hudDistance = 2.5f;

    [Header("Foveation")]
    [Tooltip("Width of the cross-fade ring around the foveal patch, as a fraction of " +
             "it. A hard edge is more visible than the resolution difference itself.")]
    [Range(0.01f, 0.5f)] public float foveaFeather = 0.15f;

    [Tooltip("Draw a border around the high-resolution patch. Foveation is meant to be " +
             "invisible when it works, which makes a stuck crop look exactly like a " +
             "working one; this answers whether the patch is where you are looking.")]
    public bool foveaOutline = false;

    [Tooltip("Decode MJPEG in C# instead of using MediaCodec. For bring-up: it tells " +
             "you whether a black screen is the transport or the decoder bridge.")]
    public bool softwareVideo = false;

    [Header("Display quality")]
    [Tooltip("Turn off eye-buffer dynamic resolution. It saves GPU time by rendering " +
             "below native resolution, which visibly softens video.")]
    public bool disableDynamicResolution = true;

    [Tooltip("Request the highest refresh rate the headset supports, up to the ceiling " +
             "below. Each display frame is a hard floor on end-to-end latency.")]
    public bool useMaxDisplayFrequency = true;

    [Tooltip("Ceiling on the requested refresh rate. Raising the rate only helps if the " +
             "app can actually hit it: a Quest 2 offers 120 Hz, and an app that misses " +
             "frames at 120 is reprojected, which reads as judder on anything you track " +
             "with your eyes -- your own hands most of all. A made frame at 72 beats a " +
             "missed one at 120. 0 means no ceiling.")]
    public float maxDisplayFrequency = 90f;

    [Tooltip("How much telemetry to show. Off is a legitimate default once a link is " +
             "trusted: the centre of the view is where the work is, and the video is " +
             "the point.")]
    public GvHudMode statsMode = GvHudMode.Compact;

    // ------------------------------------------------------------------ per-eye state

    private sealed class Eye
    {
        public string name;
        public int index;
        public RawImage image;
        public Canvas canvas;
        public RectTransform canvasRect;
        public Camera camera;
        public Material material;
        public IGvVideoSource source;
        public float outerSign;
        public bool textureBound;
    }

    private readonly Eye left = new Eye { name = "L", index = 0, outerSign = -1f };
    private readonly Eye right = new Eye { name = "R", index = 1, outerSign = 1f };

    private GvVideoLink link;
    private GvRobotSession session;
    private GvInputUplink uplink;
    private GvCameraParams camera;
    private GvRobotProfile profile;
    private string robotStatus = "";
    private readonly System.Text.StringBuilder stats = new System.Text.StringBuilder(512);
    private readonly GvFrameStats frameStats = new GvFrameStats();
    // Refreshed on the stats tick rather than read per frame: it is a plugin call, and
    // the value changes only when something asks it to.
    private float cachedHz;
    private GvSceneCommands sceneCommands;
    private GvHud hud;
    private float statsTimer;
    private const float StatsInterval = 0.25f;
    private float reportTimer;
    private const float ReportInterval = 0.5f;
    private long lastLostL, lastLostR, lastAttemptL, lastAttemptR;
    private long lastBytesL, lastBytesR;
    private float lastRateSample;

    private void Start()
    {
        LoadProfile();
        BindEyeObjects();
        SetUpEyeMaterials();
        ConfigureDisplay();
        SetUpHud();
        ApplyStereoLayout();

        link = new GvVideoLink();
        link.Start(videoPort, canvasWidth, canvasHeight, softwareVideo);
        left.source = link.Left;
        right.source = link.Right;

        // The Editor cannot decode H.264, so it asks for MJPEG rather than requiring
        // the sender to be launched differently. One less thing to get wrong.
        int codec = link.IsSoftwarePath ? 1 : 0;

        var p = profile;
        if (p == null && !string.IsNullOrWhiteSpace(fallbackHost))
        {
            // Playing this scene directly, without going through the menu.
            p = new GvRobotProfile { name = "direct", host = fallbackHost,
                                     videoPort = videoPort, foveation = true };
        }
        session = GvRobotSession.Instance;
        uplink = FindAnyObjectByType<GvInputUplink>(FindObjectsInactive.Include);
        // Foveation needs gaze, and gaze needs an eye tracker. On a Quest 2 or 3 the
        // profile's preference cannot be honoured, and asking for it anyway would spend
        // the whole mechanism on a crop pinned to the middle of the frame -- a sharp
        // centre and a worse surround, which is the opposite of the point.
        bool fovea = p != null && p.foveation && GvXr.EyeTrackingAvailable;
        if (p != null && p.foveation && !fovea)
            Debug.Log("GvStereoDisplay: no eye tracking on this headset; "
                    + "requesting a plain stream.");
        session.Connect(p, codec, fovea);
        session.Link.ConnectionChanged += OnRobotConnection;

        // A worked example of the I/O interface. The mock robot publishes this at
        // 20 Hz; delete it once something real is subscribing.
        // The robot publishes these as soon as we connect, so the very first frame is
        // already placed correctly rather than snapping into place a moment later.
        session.Link.Subscribe("camera/params", data =>
        {
            var p = GvCameraParams.FromMap(data as System.Collections.Generic.Dictionary<string, object>);
            if (!p.Valid)
            {
                Debug.LogWarning("GvStereoDisplay: camera/params did not parse; keeping manual FOV.");
                return;
            }
            camera = p;
            if (!p.Rectified)
                Debug.LogWarning("GvStereoDisplay: robot reports UNRECTIFIED frames. This "
                               + "viewer does not undistort; fix it at the source.");
            Debug.Log("GvStereoDisplay: camera " + p);
            ApplyStereoLayout();
        });

        session.Link.Subscribe("arm/state", data =>
        {
            var m = data as System.Collections.Generic.Dictionary<string, object>;
            if (m == null)
                return;
            robotStatus = string.Format("arm grip {0:0.000} homed {1}",
                GvMsgPack.GetFloat(m, "grip"), GvMsgPack.GetBool(m, "homed"));
        });
    }

    /// <summary>
    /// Take everything from the profile the menu picked. Playing this scene directly
    /// leaves the inspector values in place, which is what makes it testable on its
    /// own.
    /// </summary>
    private void LoadProfile()
    {
        profile = GvConfig.Load().Last;
        if (profile == null)
        {
            Debug.Log("GvStereoDisplay: no profile selected; using inspector values.");
            return;
        }
        videoPort = profile.videoPort;
        canvasWidth = profile.canvasWidth;
        canvasHeight = profile.canvasHeight;
        sourceWidth = profile.sourceWidth;
        sourceHeight = profile.sourceHeight;
        videoPlaneDistance = profile.videoPlaneDistance;
        videoVFOV = profile.videoVFOV;
        videoScale = profile.videoScale;
        stereoSeparationDeg = profile.stereoSeparationDeg;
        stereoVerticalTrimDeg = profile.stereoVerticalTrimDeg;
        edgeFeather = profile.edgeFeather;
        outerEdgeMask = profile.outerEdgeMask;
        hudDistance = profile.hudDistance;
        foveaFeather = profile.foveaFeather;
        foveaOutline = profile.foveaOutline;
        softwareVideo = profile.softwareVideo;
        Debug.Log($"GvStereoDisplay: profile '{profile.name}' at {profile.host}");
    }

    private void BindEyeObjects()
    {
        left.image = leftImage;
        left.canvas = leftCanvas;
        right.image = rightImage;
        right.canvas = rightCanvas;

        foreach (var eye in new[] { left, right })
        {
            if (eye.canvas != null)
            {
                eye.canvasRect = eye.canvas.GetComponent<RectTransform>();
                eye.camera = eye.canvas.GetComponentInParent<Camera>(true);
            }
            if (eye.image != null)
                eye.image.raycastTarget = false;   // nothing raycasts the video quad
        }
    }

    private void SetUpEyeMaterials()
    {
        var shader = Resources.Load<Shader>("StereoEyeView");
        if (shader == null)
        {
            Debug.LogWarning("GvStereoDisplay: Resources/StereoEyeView shader not found; " +
                             "using the default UI material. No edge treatment or foveal composite.");
            return;
        }
        foreach (var eye in new[] { left, right })
        {
            if (eye.image == null)
                continue;
            eye.material = new Material(shader) { name = "StereoEyeView (" + eye.name + ")" };
            // The quad's own eye, tested per fragment. This is what keeps the left
            // image out of the right eye, and unlike per-eye cameras it works under
            // Single Pass Instanced, which is what this project renders with.
            eye.material.SetFloat("_EyeIndex", eye.index);
            eye.image.material = eye.material;
        }
        PushStaticMaterialParams();
    }

    private void PushStaticMaterialParams()
    {
        foreach (var eye in new[] { left, right })
        {
            if (eye.material == null)
                continue;
            eye.material.SetFloat("_EdgeFeather", Mathf.Clamp(edgeFeather, 0f, 0.5f));
            eye.material.SetFloat("_OuterMask", Mathf.Clamp(outerEdgeMask, 0f, 0.5f));
            eye.material.SetFloat("_OuterSign", eye.outerSign);
            eye.material.SetFloat("_FoveaFeather", foveaFeather);
        eye.material.SetFloat("_FoveaOutline", foveaOutline ? 1f : 0f);
            eye.material.SetVector("_SourceSize", new Vector4(sourceWidth, sourceHeight, 0f, 0f));
            // _SrgbDecode is per-source (the MediaCodec and Editor paths differ) and
            // is pushed in UpdateEye once a source exists.
            eye.material.SetFloat("_UndistortMode", 0f);
        }
    }

    private void ConfigureDisplay()
    {
        // Quest paces frames through the compositor; Unity's own vsync/target-rate
        // throttles only add latency on top of it.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // The eye buffer starts as opaque black. The video quad covers the view, so a
        // skybox behind it is pure waste -- and CenterEyeAnchor ships with Skybox set.
        // GvPassthroughBackdrop overrides this to transparent where the room should
        // show through; here it must not.
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null)
                continue;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
        }
        GvXr.ApplyClipPlanes();

        var manager = OVRManager.instance;
        if (manager != null && disableDynamicResolution)
        {
            manager.enableDynamicResolution = false;
            manager.minDynamicResolutionScale = 1.0f;
            manager.maxDynamicResolutionScale = 1.0f;
        }

        if (useMaxDisplayFrequency)
            ApplyDisplayFrequency();
    }

    /// <summary>The refresh rates this headset offers, ascending. Empty off-device.</summary>
    public float[] AvailableFrequencies
    {
        get
        {
            try
            {
                var a = OVRManager.display?.displayFrequenciesAvailable;
                if (a == null)
                    return new float[0];
                var copy = (float[])a.Clone();
                System.Array.Sort(copy);
                return copy;
            }
            catch (System.Exception)
            {
                return new float[0];
            }
        }
    }

    /// <summary>What the display is running at now, or 0 if it cannot be read.</summary>
    public float DisplayHz
    {
        get
        {
            try { return OVRManager.display != null ? OVRManager.display.displayFrequency : 0f; }
            catch (System.Exception) { return 0f; }
        }
    }

    /// <summary>
    /// Pick the highest offered rate that is not above the ceiling.
    ///
    /// Previously this took the highest rate on offer unconditionally. That is the right
    /// call for latency and the wrong one for comfort the moment the app cannot sustain
    /// it: the compositor reprojects the frames that do not arrive, and reprojection is
    /// exactly what makes tracked hands feel wrong to look at.
    /// </summary>
    public void ApplyDisplayFrequency()
    {
        var available = AvailableFrequencies;
        if (available.Length == 0)
            return;
        float ceiling = maxDisplayFrequency > 0f ? maxDisplayFrequency : float.MaxValue;
        float best = 0f;
        foreach (float f in available)
            if (f <= ceiling && f > best) best = f;
        // Every rate is above the ceiling -- take the lowest rather than leaving it alone.
        if (best <= 0f)
            best = available[0];
        try
        {
            OVRManager.display.displayFrequency = best;
            cachedHz = best;
            Debug.Log($"GvStereoDisplay: display frequency {best:0} Hz (ceiling {ceiling:0}).");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("GvStereoDisplay: could not set display frequency: " + e.Message);
        }
    }

    /// <summary>
    /// Size and place both quads from the current stereo parameters. The quad subtends
    /// videoVFOV vertically at videoPlaneDistance, which is why moving the plane changes
    /// nothing visible -- the angular image is distance-invariant by construction.
    /// </summary>
    public void ApplyStereoLayout()
    {
        float d = Mathf.Max(0.2f, videoPlaneDistance);
        float scale = Mathf.Max(0.05f, videoScale);
        float trimX = Mathf.Tan(Mathf.Clamp(stereoSeparationDeg, -20f, 20f) * 0.5f * Mathf.Deg2Rad) * d;
        float trimY = Mathf.Tan(Mathf.Clamp(stereoVerticalTrimDeg, -20f, 20f) * 0.5f * Mathf.Deg2Rad) * d;

        if (camera.Valid && !ignoreCameraParams)
        {
            // Convergence, from the baseline the robot reported. Shifting each eye by
            // b*d/(2*Z) puts distance Z at zero disparity -- note it does not depend on
            // focal length, so it is the same number whatever the lens. With no shift
            // at all, infinity sits on the screen plane and everything real is nearer
            // than it, which is exactly the arrangement that causes eye strain.
            float conv = (camera.BaselineM > 0f && convergenceDistance > 0.05f)
                       ? camera.BaselineM * d / (2f * convergenceDistance)
                       : 0f;
            LayoutFromIntrinsics(left, camera.Left, d, scale, -conv - trimX, -trimY);
            LayoutFromIntrinsics(right, camera.Right, d, scale, conv + trimX, trimY);
        }
        else
        {
            float halfHeight = Mathf.Tan(Mathf.Clamp(videoVFOV, 1f, 170f) * 0.5f * Mathf.Deg2Rad)
                               * d * scale;
            ApplyEyeLayout(left, -trimX, -trimY, halfHeight, d);
            ApplyEyeLayout(right, trimX, trimY, halfHeight, d);
        }
        PushStaticMaterialParams();
        ApplyHudDistance();
    }

    /// <summary>
    /// Stand up the telemetry readout.
    /// </summary>
    private void SetUpHud()
    {
        hud = FindAnyObjectByType<GvHud>(FindObjectsInactive.Include);
        if (hud == null)
            hud = gameObject.AddComponent<GvHud>();
        hud.Mode = statsMode;
        hud.distance = hudDistance;
    }

    /// <summary>
    /// Place one eye's quad so a pixel appears in the direction its camera ray pointed.
    ///
    /// The quad's angular size comes from the focal length, and its *offset* from the
    /// principal point: cx is generally not the image centre even after rectification,
    /// and that difference is a sideways shift of the picture relative to the optical
    /// axis. Ignoring it looks perfectly fine in either eye alone and makes the pair
    /// tiring to fuse, which is a miserable thing to debug by feel.
    /// </summary>
    private void LayoutFromIntrinsics(Eye eye, GvEyeIntrinsics k, float d, float scale,
                                      float extraX, float extraY)
    {
        if (eye.canvasRect == null)
            return;
        Vector2 size, centre;
        GvCameraParams.QuadFromIntrinsics(k, camera.Width, camera.Height, d, scale,
                                          out size, out centre);
        eye.canvasRect.sizeDelta = size;
        eye.canvasRect.localPosition = new Vector3(centre.x + extraX, centre.y + extraY, d);
    }

    private void ApplyEyeLayout(Eye eye, float shiftX, float shiftY, float halfHeight, float distance)
    {
        if (eye.canvasRect == null)
            return;
        // Aspect comes from the CAMERA's resolution, not the texture's: the atlas
        // canvas is square and carries a squashed full-field band, so using the
        // texture's own dimensions would stretch the picture.
        float aspect = sourceHeight > 0 ? (float)sourceWidth / sourceHeight : 16f / 9f;
        eye.canvasRect.sizeDelta = new Vector2(halfHeight * 2f * aspect, halfHeight * 2f);
        eye.canvasRect.localPosition = new Vector3(shiftX, shiftY, distance);
    }

    /// <summary>Cycle the readout: off, one line, everything.</summary>
    public GvHudMode CycleStats(int dir)
    {
        if (hud == null)
            return GvHudMode.Off;
        statsMode = hud.Cycle(dir);
        return statsMode;
    }

    public GvHudMode StatsMode => hud != null ? hud.Mode : statsMode;

    public void ApplyHudDistance()
    {
        if (hud == null)
            return;
        hud.distance = Mathf.Max(0.2f, hudDistance);
        hud.Recentre();
    }

    /// <summary>
    /// Project a direction in head space onto the video image, as normalised
    /// coordinates with y measured DOWN from the top -- the convention the sender's
    /// foveal crop uses.
    ///
    /// The quad is a virtual pinhole camera of half-angles (halfTanX, halfTanY), so
    /// this is just that camera's projection. Living here rather than in the uplink is
    /// deliberate: these are the same numbers that size the quad, and a gaze projected
    /// with a different FOV than the image is drawn at puts the sharp patch somewhere
    /// other than where the operator is looking.
    /// </summary>
    public Vector2 DirectionToImageUV(Vector3 dirInHeadSpace, bool rightEye = false)
    {
        if (dirInHeadSpace.z <= 1e-4f)
            return new Vector2(0.5f, 0.5f);

        // Intersect the gaze ray with the quad this eye is *actually* drawn on and read
        // off where it lands. Derived from the placed geometry rather than recomputed
        // from FOV, which matters: those were two independent formulas that happened to
        // agree, so any change to the layout -- intrinsics, convergence, magnification --
        // could move the picture without moving the crop, and the fovea would sharpen a
        // spot you are not looking at.
        var rt = (rightEye ? right : left).canvasRect;
        if (rt == null)
            return new Vector2(0.5f, 0.5f);

        return GvCameraParams.QuadUV(dirInHeadSpace, rt.localPosition, rt.sizeDelta);
    }

    private void OnRobotConnection(bool up)
    {
        robotStatus = up ? "robot connected" : "robot disconnected";
        Debug.Log("GvStereoDisplay: " + robotStatus);
    }

    private void Update()
    {
        // Sampled unconditionally, not only while the HUD is up: the interesting frames
        // are the ones during teleoperation, and gating the meter on the readout would
        // mean only ever measuring the app while it is showing a debug overlay.
        frameStats.Sample(Time.unscaledDeltaTime, cachedHz);

        UpdateEye(left);
        UpdateEye(right);

        reportTimer += Time.unscaledDeltaTime;
        if (reportTimer >= ReportInterval)
        {
            reportTimer = 0f;
            ReportToRobot();
        }

        if (hud == null || hud.Mode == GvHudMode.Off)
            return;
        statsTimer += Time.unscaledDeltaTime;
        if (statsTimer < StatsInterval)
            return;
        statsTimer = 0f;
        UpdateStats();
    }

    /// <summary>
    /// Tear the video path down and bring it back up, keeping the display geometry.
    ///
    /// The decoder is configured once per session, so a wedged stream cannot be fixed
    /// by waiting -- something has to close the socket and start again. Camera params
    /// are kept: they describe the robot's hardware, not this connection.
    /// </summary>
    public void RestartVideo()
    {
        Debug.Log("GvStereoDisplay: restarting video.");
        left.source = null;
        right.source = null;
        left.textureBound = false;
        right.textureBound = false;
        link?.Dispose();
        link = null;

        link = new GvVideoLink();
        if (!link.Start(videoPort, canvasWidth, canvasHeight, softwareVideo))
        {
            Debug.LogError("GvStereoDisplay: video restart failed.");
            return;
        }
        left.source = link.Left;
        right.source = link.Right;
    }

    /// <summary>Reconnect the control channel and the video together.</summary>
    public void RestartSession()
    {
        RestartVideo();
        if (session != null && !session.Reconnect())
            Debug.LogWarning("GvStereoDisplay: nothing to reconnect to.");
    }

    private void UpdateEye(Eye eye)
    {
        var src = eye.source;
        if (src == null)
            return;
        src.Update();

        if (!eye.textureBound && src.Texture != null && eye.image != null)
        {
            eye.image.texture = src.Texture;
            eye.textureBound = true;
            ApplyStereoLayout();
        }

        if (eye.material == null)
            return;
        eye.material.SetFloat("_Foveated", src.Foveated ? 1f : 0f);
        eye.material.SetVector("_FoveaRect", src.FoveaRect);
        eye.material.SetVector("_LayerSpans", src.LayerSpans);
        eye.material.SetFloat("_SrgbDecode", src.NeedsSrgbDecode ? 1f : 0f);
    }

    /// <summary>
    /// Tell the robot what actually arrived, so it can pick a bitrate for the link.
    ///
    /// The viewer is the only end that can see this: the sender knows what it put on
    /// the wire, not what survived. Two numbers are enough -- the fraction of fragments
    /// lost, and how late frames are running -- and they go out on the control channel
    /// that already exists rather than a socket of their own.
    ///
    /// The time figure is *not* a latency. The robot stamps frames with its own
    /// monotonic clock, whose epoch is unrelated to ours, so the absolute number is
    /// meaningless; only its changes carry information. The controller on the far end
    /// tracks the minimum and reads the excess as queueing delay, which makes the
    /// unknown offset cancel. Sending it raw and letting one end do that subtraction
    /// beats pretending we can measure something we cannot.
    /// </summary>
    private void ReportToRobot()
    {
        var rl = session != null ? session.Link : null;
        if (rl == null || !rl.Connected)
            return;
        if (left.source != null) left.source.RefreshStats();
        if (right.source != null) right.source.RefreshStats();

        long lost = (left.source?.FragmentsLost ?? 0) + (right.source?.FragmentsLost ?? 0);
        long done = (left.source?.FramesCompleted ?? 0) + (right.source?.FramesCompleted ?? 0);
        long dLost = lost - lastLostL;
        long dDone = done - lastAttemptL;
        lastLostL = lost;
        lastAttemptL = done;
        if (dDone <= 0)
            return;   // nothing arrived this window; reporting 0% would look like health

        // Fragments per frame is roughly canvas bytes / MTU, but the exact figure does
        // not matter: what the controller needs is a ratio that rises with congestion.
        float loss = Mathf.Clamp01((float)dLost / Mathf.Max(1f, dDone * 8f));

        // System.Math, not Mathf: these are microsecond stamps off a monotonic clock and
        // will not survive being narrowed to int.
        long capture = System.Math.Max(left.source != null ? left.source.CaptureTsUs : 0L,
                                       right.source != null ? right.source.CaptureTsUs : 0L);
        if (capture <= 0)
        {
            rl.Publish("viewer/stats", GvRobotSession.Map("loss", loss));
            return;
        }
        double relativeMs = (Stopwatch.ElapsedTicks / (double)System.TimeSpan.TicksPerMillisecond)
                          - capture / 1000.0;

        rl.Publish("viewer/stats", GvRobotSession.Map(
            "loss", loss,
            "lat", (float)relativeMs));
    }

    private static readonly System.Diagnostics.Stopwatch Stopwatch =
        System.Diagnostics.Stopwatch.StartNew();

    private void UpdateStats()
    {
        link?.RefreshStats();

        float now = Time.unscaledTime;
        float dt = Mathf.Max(1e-3f, now - lastRateSample);
        lastRateSample = now;
        long bl = left.source?.BytesReceived ?? 0;
        long br = right.source?.BytesReceived ?? 0;
        float mbps = (bl - lastBytesL + br - lastBytesR) * 8f / dt / 1e6f;
        lastBytesL = bl;
        lastBytesR = br;

        stats.Clear();
        frameStats.Flush();
        cachedHz = DisplayHz;
        float hz = cachedHz;
        var rlink = session != null ? session.Link : null;
        bool up = rlink != null && rlink.Connected;

        if (hud.Mode == GvHudMode.Compact)
        {
            // One line, and only what you would turn your head to check: is the robot
            // there, is the picture arriving, is the headset keeping up. Everything else
            // is a debugging question and lives in Full.
            stats.AppendFormat("{0} {1}   {2:0.0} Mbit/s   {3:0} fps",
                up ? "<color=#6FD48A>\u25CF</color>" : "<color=#F09A5A>\u25CF</color>",
                up ? "link" : "no link", mbps, frameStats.Fps);
            if (frameStats.Missed > 0)
                stats.AppendFormat("   <color=#F09A5A>{0} missed</color>", frameStats.Missed);
            hud.Set(stats.ToString());
            return;
        }
        stats.AppendFormat("{0:0.0} Mbit/s   render {1:0.0} fps / {2:0} Hz   worst {3:0.0} ms   " +
                           "missed {4}/{5}   {6}{7}\n",
            mbps, frameStats.Fps, hz, frameStats.WorstMs, frameStats.Missed, frameStats.Frames,
            UnityEngine.XR.XRSettings.stereoRenderingMode,
            link != null && link.IsSoftwarePath ? "   [mjpeg/software]" : "");
        if (rlink != null)
            stats.AppendFormat("link {0}  in {1} out {2}   {3}\n",
                up ? "up" : "down", rlink.MessagesIn, rlink.MessagesOut, robotStatus);
        if (sceneCommands == null)
            sceneCommands = FindAnyObjectByType<GvSceneCommands>(FindObjectsInactive.Include);
        if (sceneCommands != null && (sceneCommands.MarkersReceived > 0
                                      || sceneCommands.GuidesReceived > 0))
            stats.AppendFormat("ui  markers {0} rcvd / {1} live   guides {2} rcvd\n",
                sceneCommands.MarkersReceived, sceneCommands.MarkerCount,
                sceneCommands.GuidesReceived);
        AppendGazeStats();
        AppendEyeStats(left);
        AppendEyeStats(right);
        hud.Set(stats.ToString());
    }

    /// <summary>
    /// Gaze, and how far the *displayed* patch trails it. That offset is the whole
    /// foveation question made visible: it is the eye-to-fovea latency expressed in
    /// image units, it costs nothing to compute because both ends of it are already
    /// here, and a saccade shows up as a spike that decays as the crop catches up.
    /// </summary>
    private void AppendGazeStats()
    {
        if (uplink == null)
            return;
        Vector2 g = uplink.GazeUV;
        // "idle" and "none" are different failures and were worth telling apart: idle
        // means the uplink never found a robot address and is sending nothing at all,
        // which is what a directly-played scene with no fallbackHost looks like.
        string src = !uplink.Running ? "idle"
                   : !uplink.GazeAvailable ? "none"
                   : uplink.GazeSimulated ? "mouse" : "eyes";
        var s = left.source;
        if (s != null && s.Foveated)
        {
            Vector4 r = s.FoveaRect;
            // FoveaRect is in display UV, which is y-flipped from image UV.
            float lag = Vector2.Distance(g, new Vector2(r.x, 1f - r.y));
            stats.AppendFormat("gaze {0:0.00},{1:0.00} ({2}, {3} sent)  patch trails {4:0.000}\n",
                g.x, g.y, src, uplink.Sent, lag);
        }
        else
        {
            stats.AppendFormat("gaze {0:0.00},{1:0.00} ({2}, {3} sent)  patch off\n",
                g.x, g.y, src, uplink.Sent);
        }
    }

    private void AppendEyeStats(Eye eye)
    {
        var s = eye.source;
        if (s == null)
            return;
        long attempted = s.FramesCompleted + s.FramesDropped;
        float loss = attempted > 0 ? 100f * s.FramesDropped / attempted : 0f;
        stats.AppendFormat("{0}: dec {1}  drop {2} ({3:0.00}%)  fraglost {4}  noBuf {5}  err {6}  {7}{8}\n",
            eye.name, s.FramesDecoded, s.FramesDropped, loss, s.FragmentsLost,
            s.FramesNoInputBuffer, s.DecodeErrors,
            s.Foveated ? "fovea" : "plain",
            s.Texture == null ? "  [no texture]" : "");
    }

    private void OnDestroy()
    {
        if (session != null && session.Link != null)
            session.Link.ConnectionChanged -= OnRobotConnection;
        session = null;
        link?.Dispose();
        link = null;
        left.source = null;
        right.source = null;
    }
}
