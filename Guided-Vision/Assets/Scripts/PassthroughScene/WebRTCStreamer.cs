using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using Unity.WebRTC;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
using OVRSimpleJSON;
using Debug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;

[System.Serializable]
public class HeadsetData
{
    public Vector3 HPosition;
    public Quaternion HRotation;
    public Vector3 LPosition;
    public Quaternion LRotation;
    public Vector2 LThumbstick;
    public float LIndexTrigger;
    public float LHandTrigger;
    public bool LButtonOne;
    public bool LButtonTwo;
    public bool LButtonThumbstick;
    public Vector3 RPosition;
    public Quaternion RRotation;
    public Vector2 RThumbstick;
    public float RIndexTrigger;
    public float RHandTrigger;
    public bool RButtonOne;
    public bool RButtonTwo;
    public bool RButtonThumbstick;
    public Vector2 LEyePixel;
    public Vector2 REyePixel;
    public uint LeftTimestamp;
    public uint RightTimestamp;
}

/// <summary>
/// Receives two WebRTC video tracks (left eye, then right eye) plus a bidirectional
/// control data channel from the robot-side sender, and displays them as a stereo
/// pair -- one head-locked quad per eye.
///
/// See docs/VISION_PIPELINE.md for the full contract with the sender.
///
/// Latency policy: nothing in this component queues video. The WebRTC plugin keeps a
/// single "latest decoded frame" texture per track and uploads it once per Unity
/// frame; the RawImage points straight at that texture. If frames arrive faster than
/// Unity renders, the extra frames are simply overwritten -- never buffered.
/// </summary>
public class WebRTCStreamer : MonoBehaviour
{
    public RawImage leftImage;
    public RawImage rightImage;
    public Canvas leftCanvas;
    public Canvas rightCanvas;
    public Transform headset;
    public Transform leftController;
    public Transform rightController;
    public Transform leftEye;
    public Transform rightEye;
    public GameObject leftEyeMarker;
    public GameObject rightEyeMarker;
    public GameObject leftArmVisual;
    public GameObject rightArmVisual;
    public TextMeshProUGUI headWarningText;
    public TextMeshProUGUI infoText;
    public TextMeshProUGUI debugText;
    public float dataFrequency = 20f;

    [Tooltip("Unused. Kept so existing scene/prefab serialization stays valid. " +
             "Video is displayed at whatever rate it arrives -- never throttled.")]
    public float videoFrequency = 30f;

    [Header("Stereo display")]
    [Tooltip("How far in front of each eye the video quad sits, in metres. This does " +
             "NOT change how far away the imagery LOOKS: the quad is scaled with the " +
             "distance, so the angular image is identical at any value. Use " +
             "stereoSeparationDeg to change perceived depth.")]
    public float videoPlaneDistance = 1.0f;

    [Tooltip("Vertical field of view the video is displayed at, in degrees. Set this " +
             "to the *camera's* vertical FOV for 1:1 (undistorted) geometry. Larger " +
             "values magnify the image, which also makes everything look closer.")]
    public float videoVFOV = 105f;

    [Tooltip("Extra magnification on top of videoVFOV. 1 = none.")]
    public float videoScale = 1.0f;

    [Tooltip("Angular separation between the two eye images, in degrees. Positive " +
             "pulls the images apart, which pushes the whole scene FURTHER away; " +
             "negative brings it closer. This is the knob for 'the image is too " +
             "close to fuse'. Keep it small -- a couple of degrees is a lot.")]
    public float stereoSeparationDeg = 0f;

    [Tooltip("Vertical trim between the two eye images, in degrees. Corrects a stereo " +
             "pair that is not perfectly row-aligned. Should normally be 0 when the " +
             "sender rectifies.")]
    public float stereoVerticalTrimDeg = 0f;

    [Header("Stereo comfort")]
    [Tooltip("Softens the border of each eye's image, as a fraction of the image. A " +
             "hard rectangular edge is itself a depth cue and fights the imagery.")]
    public float edgeFeather = 0.03f;

    [Tooltip("Extra fade on each eye's OUTER edge (left edge of the left image, right " +
             "edge of the right image), as a fraction of the width. That strip is the " +
             "part of the scene only one camera can see, so there is nothing for the " +
             "other eye to fuse it with and it reads as flicker. 0.05-0.12 is typical " +
             "for a wide-angle pair.")]
    public float outerEdgeMask = 0f;

    [Tooltip("How far in front of the head the debug/warning HUD sits, in metres. The " +
             "canvas is scaled with the distance so it keeps the same apparent size -- " +
             "this only changes how hard your eyes have to converge to read it. Text at " +
             "the same depth as the video, or nearer, is tiring; further is more " +
             "relaxed.")]
    public float hudDistance = 2.5f;

    [Header("Lens correction (GPU)")]
    [Tooltip("Undistort and rectify per display pixel, from a calibration file, instead " +
             "of having the sender do it with cv2.remap. Off keeps the current " +
             "behaviour exactly. Requires a calibration file -- see " +
             "tools/export_calibration_for_unity.py.")]
    public bool gpuUndistort = false;

    [Tooltip("Calibration file name. Looked for in Application.persistentDataPath " +
             "first (adb-pushable, no rebuild), then in Resources.")]
    public string calibrationFile = StereoCalibration.DefaultFileName;

    [Header("Transport")]
    [Tooltip("Bytes of sender metadata appended to every encoded video frame. 0 = the " +
             "sender appends nothing (leave it at 0 unless the sender really does " +
             "append a 4-byte big-endian timestamp). A non-zero value here strips that " +
             "many bytes off EVERY frame before decoding -- if the sender does not " +
             "actually append them, the video stream is corrupted.")]
    public int metadataLength = 0;

    [Header("Diagnostics")]
    [Tooltip("Install a pass-through frame transform so arrival time and rate of each " +
             "encoded frame can be measured. Costs one managed callback per frame. " +
             "Turn off for the absolute lowest latency once tuning is done.")]
    public bool enableFrameStats = true;

    [Tooltip("Show the live latency/FPS readout in debugText.")]
    public bool showStats = true;

    [Tooltip("A stream with no new frame for this long is reported as STALE.")]
    public float staleFrameMs = 250f;

    [Header("Display quality")]
    [Tooltip("Turn off eye-buffer dynamic resolution. It saves GPU time by rendering " +
             "the eye buffers below native resolution, which visibly softens video.")]
    public bool disableDynamicResolution = true;

    [Tooltip("Request the highest refresh rate the headset supports. Each display " +
             "frame is a hard floor on end-to-end latency.")]
    public bool useMaxDisplayFrequency = true;

    [Tooltip("Render each eye's video quad only to that eye. Without this both eye " +
             "cameras draw both quads, which ghosts the periphery and doubles the " +
             "overdraw. Requires the LeftEyeOnly/RightEyeOnly layers.")]
    public bool isolateEyeLayers = true;

    // ---------------------------------------------------------------- eye state

    /// <summary>Everything that is per-eye. One instance for the left, one for the right.</summary>
    private sealed class EyeStream
    {
        public string name;
        public RawImage image;
        public Canvas canvas;
        public RectTransform canvasRect;
        public GameObject marker;
        public Transform gaze;
        public Camera eyeCamera;

        public string mid;
        public RTCRtpReceiver receiver;
        public RTCRtpTransform frameTransform;
        public Texture texture;
        public Material material;
        public float outerSign;             // -1 left eye, +1 right eye
        public StereoCalibration.EyeCalibration calibration;

        // canvas offset currently applied, in metres, in eye-anchor space
        public float shiftX;
        public float shiftY;

        // ---- written on the WebRTC worker thread, read on the main thread ----
        public volatile uint frameTimestamp;   // sender metadata, when enabled
        public long lastFrameTicks;            // Stopwatch ticks; Interlocked
        public int framesArrived;              // Interlocked

        // ---- main thread only ----
        public int framesArrivedAtLastSample;
        public int rendersSinceLastArrival;
        public float rxFps;
        public float frameAgeMs;
        public bool stale;
        public int staleEvents;
        public int coalescedFrames;            // arrived but never got their own render

        public uint frameWidth, frameHeight, framesDecoded, framesDropped, freezeCount;
        public double jitterBufferMs;
        public string decoder = "?";
        public ulong bytesReceived;
        public ulong bytesAtLastSample;
        public float kbps;

        // jitterBufferDelay and jitterBufferEmittedCount are cumulative for the whole
        // connection, so their plain ratio is a lifetime average that a bad first few
        // seconds pins high forever. Differencing between samples gives the delay the
        // buffer is running RIGHT NOW, which is the number worth tuning against.
        public double jitterDelayAtLastSample;
        public double jitterTargetAtLastSample;
        public ulong jitterFramesAtLastSample;
        public double jitterBufferTargetMs;

        public void NoteArrival(long ticks)
        {
            Interlocked.Exchange(ref lastFrameTicks, ticks);
            Interlocked.Increment(ref framesArrived);
        }
    }

    private readonly EyeStream left = new EyeStream { name = "L" };
    private readonly EyeStream right = new EyeStream { name = "R" };

    private static readonly Stopwatch clock = Stopwatch.StartNew();

    private StereoCalibration calibration = null;

    private RectTransform hudCanvasRect = null;
    private float hudScalePerMetre = 0f;

    private int videoTrackCount = 0;
    private int receiveStreamCount = 0;

    private HeadsetData headsetData;
    private float dataTimer = 0f;

    // create mutex lock for data channel receiving
    private object dataChannelReceiveLock = new object();
    private bool headOutOfSync = false;
    private bool leftOutOfSync = false;
    private Vector3 leftArmPosition = Vector3.zero;
    private Quaternion leftArmRotation = Quaternion.identity;
    private bool rightOutOfSync = false;
    private Vector3 rightArmPosition = Vector3.zero;
    private Quaternion rightArmRotation = Quaternion.identity;
    private string pendingInfoText = null;

    private RTCPeerConnection pc = null;
    private MediaStream receiveStream = null;
    private RTCDataChannel dataChannel = null;
    private string robotID = null;
    private string projectID = null;
    private string password = null;
    private string connectionState = "new";

    // The HUD rewrites debugText several times a second, so signalling progress and
    // errors have to live somewhere it will render them rather than being written to
    // debugText directly and immediately clobbered.
    private string signalingStatus = "signaling: fetching offer";

    // stereo tuning mode
    private bool tuningMode = false;
    private bool layoutDirty = true;
    private bool eyeLayersIsolated = false;

    private readonly System.Text.StringBuilder statsBuilder = new System.Text.StringBuilder(512);
    private float statsTextTimer = 0f;

    // Rewriting the HUD forces a TextMeshPro mesh rebuild, so it is not worth doing
    // every frame for numbers nobody can read that fast.
    private const float StatsTextInterval = 0.25f;

    // Start is called before the first frame update
    void Start()
    {
        // get robot ID from the player prefs
        robotID = PlayerPrefs.GetString("RobotID");
        projectID = PlayerPrefs.GetString("ProjectID");
        password = PlayerPrefs.GetString("Password");
        dataFrequency = PlayerPrefs.GetFloat("DataSendFrequency", 20f);
        videoFrequency = PlayerPrefs.GetFloat("VideoRenderFrequency", 30f);
        videoPlaneDistance = PlayerPrefs.GetFloat("VideoPlaneDistance", 1.0f);
        videoVFOV = PlayerPrefs.GetFloat("VideoVFOV", 55f);
        videoScale = PlayerPrefs.GetFloat("VideoScale", 1.0f);
        stereoSeparationDeg = PlayerPrefs.GetFloat("StereoSeparationDeg", 0f);
        stereoVerticalTrimDeg = PlayerPrefs.GetFloat("StereoVerticalTrimDeg", 0f);
        edgeFeather = PlayerPrefs.GetFloat("EdgeFeather", edgeFeather);
        outerEdgeMask = PlayerPrefs.GetFloat("OuterEdgeMask", outerEdgeMask);
        hudDistance = PlayerPrefs.GetFloat("HudDistance", hudDistance);

        BindEyeObjects();
        SetUpHud();
        SetUpEyeMaterials();
        ConfigureDisplay();
        ApplyStereoLayout();

        // create a new peer connection
        var configuration = GetSelectedSdpSemantics();
        pc = new RTCPeerConnection(ref configuration);

        receiveStream = new MediaStream();
        headsetData = new HeadsetData();

        receiveStream.OnAddTrack = e =>
        {
            if (!(e.Track is VideoStreamTrack track))
                return;

            // Track order is the contract: the sender adds the left eye first.
            EyeStream eye = videoTrackCount == 0 ? left : right;
            videoTrackCount++;

            // Fires once per decoded-texture (re)allocation, i.e. on connect and on
            // any resolution change -- not per frame. Just rebind; never allocate.
            track.OnVideoReceived += texture => AttachTexture(eye, texture);
        };

        pc.OnTrack = (RTCTrackEvent e) =>
        {
            if (e.Track.Kind != TrackKind.Video)
                return;

            // Add track to MediaStream for receiver.
            // This process triggers `OnAddTrack` event of `MediaStream`.
            receiveStream.AddTrack(e.Track);

            EyeStream eye = receiveStreamCount == 0 ? left : right;
            receiveStreamCount++;

            eye.receiver = e.Receiver;
            try { eye.mid = e.Transceiver?.Mid; } catch { eye.mid = null; }
            SetUpReceiverTransform(eye);
        };

        pc.OnIceCandidate = candidate =>
        {
            pc.AddIceCandidate(candidate);
            Debug.Log($"pc ICE candidate:\n {candidate.Candidate}");
        };

        pc.OnConnectionStateChange = state =>
        {
            connectionState = state.ToString();
            Debug.Log($"pc connection state: {connectionState}");
        };

        pc.OnDataChannel = channel =>
        {
            dataChannel = channel;
            dataChannel.OnMessage = bytes =>
            {
                try
                {
                    string message = System.Text.Encoding.UTF8.GetString(bytes);
                    JSONNode json = JSON.Parse(message);

                    bool headSync = json["headOutOfSync"].AsBool;
                    bool leftSync = json["leftOutOfSync"].AsBool;
                    bool rightSync = json["rightOutOfSync"].AsBool;
                    string info = json["info"];
                    Vector3 rightPosition = new Vector3(json["rightArmPosition"][0].AsFloat, json["rightArmPosition"][1].AsFloat, json["rightArmPosition"][2].AsFloat);
                    Quaternion rightRotation = new Quaternion(json["rightArmRotation"][0].AsFloat, json["rightArmRotation"][1].AsFloat, json["rightArmRotation"][2].AsFloat, json["rightArmRotation"][3].AsFloat);
                    Vector3 leftPosition = new Vector3(json["leftArmPosition"][0].AsFloat, json["leftArmPosition"][1].AsFloat, json["leftArmPosition"][2].AsFloat);
                    Quaternion leftRotation = new Quaternion(json["leftArmRotation"][0].AsFloat, json["leftArmRotation"][1].AsFloat, json["leftArmRotation"][2].AsFloat, json["leftArmRotation"][3].AsFloat);

                    lock (dataChannelReceiveLock)
                    {
                        headOutOfSync = headSync;
                        leftOutOfSync = leftSync;
                        rightOutOfSync = rightSync;
                        pendingInfoText = info;
                        leftArmPosition = leftPosition;
                        leftArmRotation = leftRotation;
                        rightArmPosition = rightPosition;
                        rightArmRotation = rightRotation;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError("Failed to parse the message: " + e.Message);
                }
            };
        };

        StartCoroutine(Answer());
        StartCoroutine(WebRTC.Update());
        if (showStats)
            StartCoroutine(StatsLoop());
    }

    // ------------------------------------------------------------------ display

    private void BindEyeObjects()
    {
        left.image = leftImage;
        left.canvas = leftCanvas;
        left.marker = leftEyeMarker;
        left.gaze = leftEye;
        right.image = rightImage;
        right.canvas = rightCanvas;
        right.marker = rightEyeMarker;
        right.gaze = rightEye;

        foreach (var eye in new[] { left, right })
        {
            if (eye.canvas != null)
            {
                eye.canvasRect = eye.canvas.GetComponent<RectTransform>();
                // The canvas is parented under its OVRCameraRig eye anchor, which is
                // also where that eye's Camera lives.
                eye.eyeCamera = eye.canvas.GetComponentInParent<Camera>(true);
            }
            if (eye.image != null)
            {
                // Nothing raycasts against the video quad; skip the graphic raycaster work.
                eye.image.raycastTarget = false;
            }
        }

        if (isolateEyeLayers)
            IsolateEyeLayers();
    }

    /// <summary>
    /// Put each eye's video quad on its own layer and cull it from the other eye's
    /// camera. Both OVRCameraRig eye cameras default to "render everything", so
    /// without this the left eye also draws the right eye's quad (offset by the IPD)
    /// and vice versa -- visible as a ghosted band at the edges, and twice the
    /// canvas overdraw for no benefit.
    /// </summary>
    private void IsolateEyeLayers()
    {
        int leftLayer = LayerMask.NameToLayer("LeftEyeOnly");
        int rightLayer = LayerMask.NameToLayer("RightEyeOnly");

        if (leftLayer < 0 || rightLayer < 0)
        {
            Debug.LogWarning("WebRTCStreamer: LeftEyeOnly/RightEyeOnly layers are missing; " +
                             "both eyes will draw both video quads. Add them in Project Settings > Tags and Layers.");
            return;
        }
        if (left.canvas == null || right.canvas == null || left.eyeCamera == null || right.eyeCamera == null)
        {
            Debug.LogWarning("WebRTCStreamer: could not resolve both eye canvases/cameras; skipping per-eye layer isolation.");
            return;
        }

        SetLayerRecursively(left.canvas.gameObject, leftLayer);
        SetLayerRecursively(right.canvas.gameObject, rightLayer);

        left.eyeCamera.cullingMask &= ~(1 << rightLayer);
        right.eyeCamera.cullingMask &= ~(1 << leftLayer);
        eyeLayersIsolated = true;

        // Per-eye culling masks need each eye to be rendered by its own camera pass.
        // That is what Multi Pass gives you; the project's OpenXR render mode is
        // surfaced in the HUD so a mismatch here is visible rather than mysterious.
        var mode = UnityEngine.XR.XRSettings.stereoRenderingMode;
        if (mode != UnityEngine.XR.XRSettings.StereoRenderingMode.MultiPass)
        {
            Debug.LogWarning($"WebRTCStreamer: stereo rendering mode is {mode}, not MultiPass. " +
                             "Per-eye cameras and per-eye culling masks are expected to work, but " +
                             "if both eyes show the same image, switch to Multi Pass.");
        }
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
    }

    /// <summary>
    /// Give each eye its own material instance so the two can carry different
    /// uniforms (outer-edge side, and per-eye intrinsics when GPU undistort is on).
    /// If the shader is missing the RawImages keep their default material and
    /// everything still displays -- just without edge treatment or lens correction.
    /// </summary>
    private void SetUpEyeMaterials()
    {
        left.outerSign = -1f;
        right.outerSign = 1f;

        if (gpuUndistort)
        {
            calibration = StereoCalibration.Load(calibrationFile);
            if (calibration == null)
            {
                Debug.LogWarning("WebRTCStreamer: gpuUndistort is on but no calibration file " +
                                 $"'{calibrationFile}' was found; falling back to raw display.");
                gpuUndistort = false;
            }
            else
            {
                left.calibration = calibration.left;
                right.calibration = calibration.right;
                Debug.Log($"WebRTCStreamer: loaded {calibration.model} calibration from {calibration.source}");
            }
        }

        var shader = Resources.Load<Shader>("StereoEyeView");
        if (shader == null)
        {
            Debug.LogWarning("WebRTCStreamer: Resources/StereoEyeView shader not found; " +
                             "using the default UI material.");
            return;
        }

        foreach (var eye in new[] { left, right })
        {
            if (eye.image == null)
                continue;
            eye.material = new Material(shader) { name = "StereoEyeView (" + eye.name + ")" };
            eye.image.material = eye.material;
        }

        PushEyeMaterialParams();
    }

    /// <summary>Uniforms that depend on the tunables. Called whenever the layout changes.</summary>
    private void PushEyeMaterialParams()
    {
        PushEyeMaterialParams(left);
        PushEyeMaterialParams(right);
    }

    private void PushEyeMaterialParams(EyeStream eye)
    {
        var material = eye.material;
        if (material == null)
            return;

        material.SetFloat("_EdgeFeather", Mathf.Clamp(edgeFeather, 0f, 0.5f));
        material.SetFloat("_OuterMask", Mathf.Clamp(outerEdgeMask, 0f, 0.5f));
        material.SetFloat("_OuterSign", eye.outerSign);

        if (!gpuUndistort || calibration == null)
        {
            material.SetFloat("_UndistortMode", 0f);
            return;
        }

        // The quad IS the virtual rectified camera: its half-angles are exactly what
        // videoVFOV/videoScale describe, so turning the FOV knob genuinely widens the
        // rendered view instead of just magnifying a fixed crop.
        float halfTanY = Mathf.Tan(Mathf.Clamp(videoVFOV, 1f, 170f) * 0.5f * Mathf.Deg2Rad)
                         * Mathf.Max(0.05f, videoScale);
        float aspect = EyeAspect(eye);

        material.SetFloat("_UndistortMode", (float)calibration.model);
        material.SetFloat("_HalfTanY", halfTanY);
        material.SetFloat("_HalfTanX", halfTanY * aspect);
        material.SetVector("_Intrinsics", eye.calibration.intrinsics);
        material.SetVector("_Dist", eye.calibration.dist);
        material.SetVector("_Tangential", eye.calibration.tangential);
        material.SetVector("_SourceSize", new Vector4(calibration.imageSize.x, calibration.imageSize.y, 0f, 0f));
        material.SetMatrix("_RectInv", eye.calibration.rectInv);
    }

    private float EyeAspect(EyeStream eye)
    {
        var texture = eye.texture;
        if (texture != null && texture.height > 0)
            return (float)texture.width / texture.height;
        if (calibration != null && calibration.imageSize.y > 0f)
            return calibration.imageSize.x / calibration.imageSize.y;
        return 16f / 9f;
    }

    /// <summary>
    /// The debug/warning HUD shares one head-locked canvas, authored at 1 m. Remember
    /// its scale-per-metre so the distance can be changed without changing how big the
    /// text looks.
    /// </summary>
    private void SetUpHud()
    {
        if (debugText == null)
            return;

        var canvas = debugText.canvas;
        if (canvas == null)
            return;

        hudCanvasRect = canvas.GetComponent<RectTransform>();
        if (hudCanvasRect == null)
            return;

        float authoredDistance = hudCanvasRect.localPosition.z;
        if (authoredDistance > 0.01f)
            hudScalePerMetre = hudCanvasRect.localScale.x / authoredDistance;

        ApplyHudDistance();
    }

    private void ApplyHudDistance()
    {
        if (hudCanvasRect == null || hudScalePerMetre <= 0f)
            return;

        float d = Mathf.Clamp(hudDistance, 0.5f, 10f);
        var position = hudCanvasRect.localPosition;
        hudCanvasRect.localPosition = new Vector3(position.x, position.y, d);

        // Scale with distance so the angular size -- and therefore legibility -- is
        // unchanged; only the vergence demand moves.
        float scale = hudScalePerMetre * d;
        hudCanvasRect.localScale = new Vector3(scale, scale, scale);
    }

    private void ConfigureDisplay()
    {
        // Quest paces frames through the compositor; Unity's own vsync/target-rate
        // throttles only add latency on top of it.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        var manager = OVRManager.instance;
        if (manager != null && disableDynamicResolution)
        {
            // Dynamic resolution renders the eye buffers below native res under load,
            // which softens a full-FOV video quad more than anything else in the scene.
            manager.enableDynamicResolution = false;
            manager.minDynamicResolutionScale = 1.0f;
            manager.maxDynamicResolutionScale = 1.0f;
        }

        if (useMaxDisplayFrequency)
        {
            try
            {
                var available = OVRManager.display?.displayFrequenciesAvailable;
                if (available != null && available.Length > 0)
                {
                    float best = 0f;
                    for (int i = 0; i < available.Length; i++)
                        if (available[i] > best) best = available[i];
                    if (best > 0f)
                        OVRManager.display.displayFrequency = best;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("WebRTCStreamer: could not raise display frequency: " + e.Message);
            }
        }
    }

    /// <summary>Point a RawImage straight at the plugin's decoded texture. No copy, no allocation.</summary>
    private void AttachTexture(EyeStream eye, Texture texture)
    {
        eye.texture = texture;
        if (texture != null)
        {
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
        }
        if (eye.image != null)
            eye.image.texture = texture;
        layoutDirty = true;
    }

    /// <summary>
    /// Size and place both video quads from the current stereo parameters.
    ///
    /// The quad is sized so it subtends videoVFOV vertically at videoPlaneDistance,
    /// which is why moving the plane changes nothing you can see -- the angular image
    /// is distance-invariant by construction. What DOES change perceived depth is the
    /// horizontal offset between the two quads (stereoSeparationDeg): pulling them
    /// apart makes the eyes' lines of sight less convergent, pushing the whole scene
    /// away from the viewer.
    /// </summary>
    private void ApplyStereoLayout()
    {
        float d = Mathf.Max(0.2f, videoPlaneDistance);
        float halfHeight = Mathf.Tan(Mathf.Clamp(videoVFOV, 1f, 170f) * 0.5f * Mathf.Deg2Rad)
                           * d * Mathf.Max(0.05f, videoScale);
        float shiftX = Mathf.Tan(Mathf.Clamp(stereoSeparationDeg, -20f, 20f) * 0.5f * Mathf.Deg2Rad) * d;
        float shiftY = Mathf.Tan(Mathf.Clamp(stereoVerticalTrimDeg, -20f, 20f) * 0.5f * Mathf.Deg2Rad) * d;

        ApplyEyeLayout(left, -shiftX, -shiftY, halfHeight, d);
        ApplyEyeLayout(right, shiftX, shiftY, halfHeight, d);
        PushEyeMaterialParams();
        ApplyHudDistance();
        layoutDirty = false;
    }

    private void ApplyEyeLayout(EyeStream eye, float shiftX, float shiftY, float halfHeight, float distance)
    {
        eye.shiftX = shiftX;
        eye.shiftY = shiftY;
        if (eye.canvasRect == null)
            return;

        float aspect = 16f / 9f;
        var tex = eye.texture;
        if (tex != null && tex.height > 0)
            aspect = (float)tex.width / tex.height;

        eye.canvasRect.sizeDelta = new Vector2(halfHeight * 2f * aspect, halfHeight * 2f);
        eye.canvasRect.localPosition = new Vector3(shiftX, shiftY, distance);
    }

    // ------------------------------------------------------------------ receive

    private void SetUpReceiverTransform(EyeStream eye)
    {
        // A script transform puts managed code in front of the decoder for every
        // encoded frame, so it is only installed when it has a job to do: stripping
        // sender metadata, or timing frame arrivals.
        if (metadataLength <= 0 && !enableFrameStats)
            return;

        eye.frameTransform = new RTCRtpScriptTransform(
            TrackKind.Video, e => OnReceiverTransform(eye, e));
        eye.receiver.Transform = eye.frameTransform;
    }

    /// <summary>
    /// Runs on a WebRTC worker thread, once per encoded frame, ahead of the decoder.
    /// Everything here is on the critical latency path: no logging, no allocation,
    /// no Unity API calls.
    /// </summary>
    private void OnReceiverTransform(EyeStream eye, RTCTransformEvent e)
    {
        long ticks = clock.ElapsedTicks;

        if (metadataLength > 0)
        {
            var data = e.Frame.GetData();
            int length = data.Length - metadataLength;
            if (length > 0)
            {
                // Big-endian, read before the frame is handed on: once it is written
                // to the sink the backing buffer may be recycled.
                uint timestamp = 0;
                int count = Mathf.Min(metadataLength, 4);
                for (int i = 0; i < count; i++)
                    timestamp = (timestamp << 8) | data[length + i];
                eye.frameTimestamp = timestamp;

                e.Frame.SetData(data, 0, length);
            }
        }

        eye.frameTransform.Write(e.Frame);
        eye.NoteArrival(ticks);
    }

    RTCConfiguration GetSelectedSdpSemantics()
    {
        RTCConfiguration config = default;

        string turnServerURL = PlayerPrefs.GetString("TurnServerURL");
        string turnServerUsername = PlayerPrefs.GetString("TurnServerUsername");
        string turnServerPassword = PlayerPrefs.GetString("TurnServerPassword");
        bool hasTurn = !string.IsNullOrEmpty(turnServerURL)
                       && !string.IsNullOrEmpty(turnServerUsername)
                       && !string.IsNullOrEmpty(turnServerPassword);

        var servers = new RTCIceServer[hasTurn ? 3 : 2];
        servers[0] = new RTCIceServer { urls = new string[] { "stun:stun1.l.google.com:19302" } };
        servers[1] = new RTCIceServer { urls = new string[] { "stun:stun2.l.google.com:19302" } };

        if (hasTurn)
        {
            // NOTE: this used to be `config.iceServers.Append(...)`, whose result was
            // discarded -- the TURN server was never actually used. Relays matter as
            // soon as the robot and headset are not on the same LAN.
            servers[2] = new RTCIceServer
            {
                urls = new string[] { turnServerURL },
                username = turnServerUsername,
                credential = turnServerPassword
            };
        }
        else
        {
            Debug.Log("No turn server found in the player prefs, not using turn server");
        }

        config.iceServers = servers;
        return config;
    }


    IEnumerator Answer()
    {
        // get the offer from the firestore
        string url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents/{password}/{robotID}";
        string offerSdp = null;

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            // yield, rather than spinning on isDone: a busy-wait here freezes the
            // render thread for the whole round trip and can trip an ANR on Quest.
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to get the offer from the firestore: " + www.error);
                signalingStatus = "signaling FAILED: could not read the offer document (" +
                                  www.error + "). Is the robot sender running?";
                yield break;
            }
            Debug.Log("Offer received from Firestore: " + www.downloadHandler.text);
            signalingStatus = "signaling: offer received";

            JSONNode json = JSON.Parse(www.downloadHandler.text);
            offerSdp = json["fields"]["sdp"]["stringValue"];
            string type = json["fields"]["type"]["stringValue"];
            if (type != "offer")
            {
                Debug.LogError("When reading the offer, the type is not offer: " + type);
                // The sender deletes its call document once it has our answer, so a
                // document holding anything but a fresh offer means the sender is not
                // currently waiting for us. Restart the sender and reconnect.
                signalingStatus = "signaling FAILED: document holds '" + type + "', not an offer. " +
                                  "Restart the robot sender, then reconnect.";
                yield break;
            }
        }

        // set the remote description
        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Offer;
        desc.sdp = offerSdp;
        var op1 = pc.SetRemoteDescription(ref desc);
        yield return op1;

        // create the answer
        var op2 = pc.CreateAnswer();
        yield return op2;

        // set the local description
        desc = op2.Desc;
        var op = pc.SetLocalDescription(ref desc);
        yield return op;

        // send the answer to the firestore
        // for sdp make sure to escape the new line characters
        string answerSdp = desc.sdp.Replace("\n", "\\n");
        string answerType = "answer";
        url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents:commit";
        string jsonData = @$"
        {{
            ""writes"": [
                {{
                ""update"": {{
                    ""name"": ""projects/{projectID}/databases/(default)/documents/{password}/{robotID}"",
                    ""fields"": {{
                    ""sdp"": {{""stringValue"": ""{answerSdp}""}},
                    ""type"": {{""stringValue"": ""{answerType}""}}
                    }}
                }}
                }}
            ]
        }}
        ";

        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to send the answer to the firestore: " + www.error);
                signalingStatus = "signaling FAILED: could not post the answer (" + www.error + ")";
                yield break;
            }
        }

        Debug.Log("Answer sent to Firestore successfully!");
        signalingStatus = null;   // from here on the connection state says everything
    }

    void OnDestroy()
    {
        // close all coroutine
        StopAllCoroutines();

        left.frameTransform?.Dispose();
        right.frameTransform?.Dispose();
        left.frameTransform = null;
        right.frameTransform = null;

        if (left.material != null) Destroy(left.material);
        if (right.material != null) Destroy(right.material);
        left.material = null;
        right.material = null;

        receiveStream?.Dispose();
        dataChannel?.Dispose();
        pc?.Close();

        receiveStream = null;
        dataChannel = null;
        pc = null;
    }

    // -------------------------------------------------------------- diagnostics

    /// <summary>
    /// Polls the peer connection's inbound-RTP stats once a second. This is where the
    /// numbers that actually explain end-to-end latency live -- above all the mean
    /// jitter buffer delay, which is usually the single largest contributor, and
    /// decoderImplementation, which tells you whether the Quest is using its hardware
    /// H.264 decoder or falling back to software.
    /// </summary>
    IEnumerator StatsLoop()
    {
        var wait = new WaitForSeconds(1f);
        while (true)
        {
            yield return wait;
            if (pc == null)
                continue;

            var op = pc.GetStats();
            yield return op;
            if (op.IsError || op.Value == null)
                continue;

            using (var report = op.Value)
            {
                foreach (var pair in report.Stats)
                {
                    if (!(pair.Value is RTCInboundRTPStreamStats inbound))
                        continue;
                    if (inbound.kind != "video")
                        continue;

                    EyeStream eye = MatchEye(inbound);
                    if (eye == null)
                        continue;

                    eye.frameWidth = inbound.frameWidth;
                    eye.frameHeight = inbound.frameHeight;
                    eye.framesDecoded = inbound.framesDecoded;
                    eye.framesDropped = inbound.framesDropped;
                    eye.freezeCount = inbound.freezeCount;
                    double delay = inbound.jitterBufferDelay;
                    double targetDelay = inbound.jitterBufferTargetDelay;
                    ulong emitted = inbound.jitterBufferEmittedCount;

                    ulong framesInWindow = emitted - eye.jitterFramesAtLastSample;
                    if (framesInWindow > 0 && delay >= eye.jitterDelayAtLastSample)
                    {
                        eye.jitterBufferMs = (delay - eye.jitterDelayAtLastSample) / framesInWindow * 1000.0;
                        eye.jitterBufferTargetMs =
                            (targetDelay - eye.jitterTargetAtLastSample) / framesInWindow * 1000.0;
                    }
                    else if (emitted > 0 && eye.jitterFramesAtLastSample == 0)
                    {
                        // First sample: nothing to difference against yet.
                        eye.jitterBufferMs = delay / emitted * 1000.0;
                        eye.jitterBufferTargetMs = targetDelay / emitted * 1000.0;
                    }
                    eye.jitterDelayAtLastSample = delay;
                    eye.jitterTargetAtLastSample = targetDelay;
                    eye.jitterFramesAtLastSample = emitted;
                    string impl = inbound.decoderImplementation;
                    if (!string.IsNullOrEmpty(impl))
                        eye.decoder = impl;

                    // StatsLoop ticks at 1 Hz, so the delta is already per second.
                    eye.bytesReceived = inbound.bytesReceived;
                    if (eye.bytesAtLastSample > 0 && eye.bytesReceived >= eye.bytesAtLastSample)
                        eye.kbps = (eye.bytesReceived - eye.bytesAtLastSample) * 8f / 1000f;
                    eye.bytesAtLastSample = eye.bytesReceived;
                }
            }
        }
    }

    private EyeStream MatchEye(RTCInboundRTPStreamStats inbound)
    {
        string mid = inbound.mid;
        if (!string.IsNullOrEmpty(mid))
        {
            if (mid == left.mid) return left;
            if (mid == right.mid) return right;
        }
        // Fall back to track identity when the mid is not reported.
        string id = inbound.trackIdentifier;
        if (!string.IsNullOrEmpty(id))
        {
            if (left.receiver?.Track != null && id == left.receiver.Track.Id) return left;
            if (right.receiver?.Track != null && id == right.receiver.Track.Id) return right;
        }
        return null;
    }

    /// <summary>
    /// Per-frame bookkeeping for one eye: arrival rate, age of the newest frame, and
    /// how many arrivals were superseded before Unity ever drew them. That last number
    /// is the direct evidence that the viewer converges on the latest frame instead of
    /// working through a backlog.
    /// </summary>
    private void SampleEye(EyeStream eye, float dt)
    {
        int arrived = Interlocked.CompareExchange(ref eye.framesArrived, 0, 0);
        int newFrames = arrived - eye.framesArrivedAtLastSample;
        eye.framesArrivedAtLastSample = arrived;

        if (newFrames > 0)
        {
            // More than one encoded frame between two renders means the earlier ones
            // were overwritten in the plugin's single-frame slot rather than queued.
            eye.coalescedFrames += newFrames - 1;
            eye.rendersSinceLastArrival = 0;
        }
        else
        {
            eye.rendersSinceLastArrival++;
        }

        // Exponentially smoothed arrival rate; dt-weighted so it is stable across
        // varying frame times.
        float instantaneous = dt > 0f ? newFrames / dt : 0f;
        eye.rxFps = Mathf.Lerp(eye.rxFps, instantaneous, 1f - Mathf.Exp(-dt / 0.5f));

        long ticks = Interlocked.Read(ref eye.lastFrameTicks);
        if (ticks > 0)
        {
            eye.frameAgeMs = (float)((clock.ElapsedTicks - ticks) * 1000.0 / Stopwatch.Frequency);
            bool stale = eye.frameAgeMs > staleFrameMs;
            if (stale && !eye.stale)
                eye.staleEvents++;
            eye.stale = stale;
        }
    }

    private void UpdateStatsText()
    {
        if (debugText == null)
            return;

        statsBuilder.Clear();
        if (signalingStatus != null)
            statsBuilder.Append(signalingStatus).Append('\n');
        statsBuilder.Append("pc ").Append(connectionState)
                    .Append("  tracks ").Append(videoTrackCount);
        if (!enableFrameStats && metadataLength <= 0)
            statsBuilder.Append("  (frame stats off)");
        statsBuilder.Append('\n');

        AppendEyeStats(left);
        AppendEyeStats(right);

        statsBuilder.Append("draw ").Append(Mathf.RoundToInt(1f / Mathf.Max(1e-4f, Time.smoothDeltaTime)))
                    .Append("fps  vfov ").Append(videoVFOV.ToString("0.#"))
                    .Append("  sep ").Append(stereoSeparationDeg.ToString("0.00"))
                    .Append("  zoom ").Append(videoScale.ToString("0.00"))
                    .Append("  d ").Append(videoPlaneDistance.ToString("0.0"))
                    .Append("  ppd ").Append(PixelsPerDegree(left).ToString("0.0"))
                    .Append('\n')
                    .Append(UnityEngine.XR.XRSettings.stereoRenderingMode)
                    .Append(eyeLayersIsolated ? "  per-eye layers ok" : "  PER-EYE LAYERS OFF");

        debugText.text = statsBuilder.ToString();
    }

    /// <summary>
    /// Source pixels per degree of the displayed image -- the number that says whether
    /// the video can look sharp at all. The Quest 3 panel resolves roughly 25 ppd, so
    /// anything well below that is undersampled and no display-side setting will fix
    /// it: either narrow videoVFOV (spend the pixels on less of the world) or send a
    /// larger frame.
    /// </summary>
    private float PixelsPerDegree(EyeStream eye)
    {
        if (eye.frameWidth == 0)
            return 0f;

        float aspect = EyeAspect(eye);
        float halfTanY = Mathf.Tan(Mathf.Clamp(videoVFOV, 1f, 170f) * 0.5f * Mathf.Deg2Rad)
                         * Mathf.Max(0.05f, videoScale);
        float hfovDeg = 2f * Mathf.Atan(halfTanY * aspect) * Mathf.Rad2Deg;
        return hfovDeg > 0.01f ? eye.frameWidth / hfovDeg : 0f;
    }

    private void AppendEyeStats(EyeStream eye)
    {
        statsBuilder.Append(eye.name).Append(' ')
                    .Append(eye.frameWidth).Append('x').Append(eye.frameHeight)
                    .Append("  rx ").Append(eye.rxFps.ToString("0.0"))
                    .Append("  age ").Append(Mathf.RoundToInt(eye.frameAgeMs)).Append("ms")
                    .Append("  jb ").Append(eye.jitterBufferMs.ToString("0"))
                    .Append('/').Append(eye.jitterBufferTargetMs.ToString("0")).Append("ms")
                    .Append("  drop ").Append(eye.framesDropped)
                    .Append("  frz ").Append(eye.freezeCount)
                    .Append("  coal ").Append(eye.coalescedFrames)
                    .Append("  ").Append(Mathf.RoundToInt(eye.kbps)).Append("kbps")
                    .Append("  ").Append(eye.decoder);
        if (eye.stale)
            statsBuilder.Append("  STALE");
        statsBuilder.Append('\n');
    }

    // ----------------------------------------------------------- stereo tuning

    /// <summary>
    /// Live in-headset stereo tuning, because these values can only be judged while
    /// looking through the headset at the real scene. Left Menu toggles it; while it
    /// is active the controller state is NOT forwarded to the robot, so the arms stay
    /// put while the thumbsticks are being used to tune.
    /// </summary>
    private void UpdateTuning(float dt)
    {
        if (OVRInput.GetDown(OVRInput.Button.Start))
        {
            tuningMode = !tuningMode;
            if (!tuningMode)
                SaveDisplayPrefs();
        }

        if (!tuningMode)
            return;

        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Controller.RTouch);
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        bool edgePage = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) > 0.5f;

        float before = TuningSignature();

        if (edgePage)
        {
            // Hold the right index trigger for the edge-treatment page.
            outerEdgeMask = Mathf.Clamp(outerEdgeMask + Deadzone(rightStick.x) * 0.15f * dt, 0f, 0.4f);
            edgeFeather = Mathf.Clamp(edgeFeather + Deadzone(rightStick.y) * 0.15f * dt, 0f, 0.4f);
            hudDistance = Mathf.Clamp(hudDistance + Deadzone(leftStick.y) * 2f * dt, 0.5f, 10f);
        }
        else
        {
            stereoSeparationDeg = Mathf.Clamp(stereoSeparationDeg + Deadzone(rightStick.x) * 2f * dt, -20f, 20f);
            videoVFOV = Mathf.Clamp(videoVFOV + Deadzone(rightStick.y) * 20f * dt, 5f, 170f);
            stereoVerticalTrimDeg = Mathf.Clamp(stereoVerticalTrimDeg + Deadzone(leftStick.y) * 2f * dt, -20f, 20f);
            videoPlaneDistance = Mathf.Clamp(videoPlaneDistance + Deadzone(leftStick.x) * 1f * dt, 0.3f, 10f);
        }

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            stereoSeparationDeg = 0f;
            stereoVerticalTrimDeg = 0f;
            videoScale = 1f;
            videoVFOV = 55f;
            videoPlaneDistance = 1f;
            edgeFeather = 0.03f;
            outerEdgeMask = 0f;
            hudDistance = 2.5f;
        }

        if (!Mathf.Approximately(before, TuningSignature()))
            layoutDirty = true;

        if (headWarningText != null)
            headWarningText.text = edgePage
                ? "STEREO TUNING - edges\n" +
                  "R stick X: outer mask   R stick Y: feather   L stick Y: HUD distance\n" +
                  "release trigger for geometry   Menu: save & exit"
                : "STEREO TUNING - robot input paused\n" +
                  "R stick: separation / FOV   L stick: v-trim / distance\n" +
                  "hold R trigger: edges   A: reset   Menu: save & exit";
    }

    private float TuningSignature()
    {
        return stereoSeparationDeg + videoVFOV + videoScale + stereoVerticalTrimDeg
             + videoPlaneDistance + edgeFeather + outerEdgeMask + hudDistance;
    }

    private static float Deadzone(float v)
    {
        return Mathf.Abs(v) < 0.15f ? 0f : v;
    }

    private void SaveDisplayPrefs()
    {
        PlayerPrefs.SetFloat("VideoVFOV", videoVFOV);
        PlayerPrefs.SetFloat("VideoScale", videoScale);
        PlayerPrefs.SetFloat("VideoPlaneDistance", videoPlaneDistance);
        PlayerPrefs.SetFloat("StereoSeparationDeg", stereoSeparationDeg);
        PlayerPrefs.SetFloat("StereoVerticalTrimDeg", stereoVerticalTrimDeg);
        PlayerPrefs.SetFloat("EdgeFeather", edgeFeather);
        PlayerPrefs.SetFloat("OuterEdgeMask", outerEdgeMask);
        PlayerPrefs.SetFloat("HudDistance", hudDistance);
        PlayerPrefs.Save();
    }

    // ------------------------------------------------------------------ gaze

    // function to calculate canvas width and height from VFOV, distance and aspect ratio
    private Vector2 CalculateCanvasSize(float vfov, float aspectRatio, float distance)
    {
        float halfVFOV = vfov / 2;
        float halfHeight = Mathf.Tan(halfVFOV * Mathf.Deg2Rad) * distance;
        float halfWidth = halfHeight * aspectRatio;
        return new Vector2(halfWidth * 2, halfHeight * 2);
    }

    Vector2 HitPoint2Pixel(Vector3 hitPoint, float height, float width, float distance, float vfov, EyeStream eye)
    {
        float halfVFOV = vfov / 2;
        float halfHeight = Mathf.Tan(halfVFOV * Mathf.Deg2Rad) * distance * Mathf.Max(0.05f, videoScale);
        float halfWidth = halfHeight * (float)width / height;

        // hitPoint is in eye-anchor space; the quad is offset within it by the stereo shift.
        float x = (hitPoint.x - eye.shiftX) / halfWidth * width / 2;
        float y = (hitPoint.y - eye.shiftY) / halfHeight * height / 2;

        return new Vector2(x + width / 2, -y + height / 2);
    }

    (Vector2, Vector3, Vector3, bool) GetEyeInfo(EyeStream eye)
    {
        if (eye.gaze == null || eye.image == null || eye.image.texture == null)
            return (Vector2.zero, Vector3.zero, Vector3.zero, false);

        Vector3 eyeDirection = eye.gaze.forward;
        (Vector3 hitPoint, bool hit) = CalculateHitPoint(eye.gaze);

        if (!hit)
            return (Vector2.zero, Vector3.zero, Vector3.zero, false);

        var texture = eye.image.texture;
        Vector2 pixel = HitPoint2Pixel(hitPoint, texture.height, texture.width, videoPlaneDistance, videoVFOV, eye);
        return (pixel, hitPoint, eyeDirection, true);
    }

    (Vector3, bool) CalculateHitPoint(Transform eye)
    {
        // Get the eye's forward direction in world space
        Vector3 eyeDirection = eye.forward;

        Plane screenPlane = new Plane(-eye.parent.forward, eye.position + eye.parent.forward * videoPlaneDistance);

        // Create a ray from the eye position in the eye's forward direction
        Ray eyeRay = new Ray(eye.position, eyeDirection);

        float distanceToPlane;
        bool hitPlane = screenPlane.Raycast(eyeRay, out distanceToPlane);

        if (hitPlane)
        {
            // Calculate the hit point in world space
            Vector3 globalHit = eyeRay.GetPoint(distanceToPlane);

            Vector3 localHit = eye.parent.InverseTransformPoint(globalHit);

            return (localHit, true);
        }

        return (Vector3.zero, false);
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;

        UpdateTuning(dt);

        if (layoutDirty)
            ApplyStereoLayout();

        SampleEye(left, dt);
        SampleEye(right, dt);

        (Vector2 leftPixel, Vector3 leftHit, Vector3 leftDirection, bool leftHitSuccess) = GetEyeInfo(left);
        (Vector2 rightPixel, Vector3 rightHit, Vector3 rightDirection, bool rightHitSuccess) = GetEyeInfo(right);

        if (leftHitSuccess && rightHitSuccess)
        {
            // Marker positions are relative to their canvas, which is itself offset by
            // the stereo shift, so take the shift back out.
            leftEyeMarker.transform.localPosition = new Vector3(leftHit.x - left.shiftX, leftHit.y - left.shiftY, 0);
            rightEyeMarker.transform.localPosition = new Vector3(rightHit.x - right.shiftX, rightHit.y - right.shiftY, 0);
        }

        uint leftTimestampCopy = left.frameTimestamp;
        uint rightTimestampCopy = right.frameTimestamp;

        // send data to the robot
        dataTimer += dt;
        if (dataChannel != null && !tuningMode && dataTimer >= 1f / dataFrequency)
        {
            dataTimer = 0f;
            headsetData.HPosition = headset.position;
            headsetData.HRotation = headset.rotation;
            headsetData.LPosition = leftController.position;
            headsetData.LRotation = leftController.rotation;
            headsetData.LThumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.LIndexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            headsetData.LHandTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            headsetData.LButtonOne = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
            headsetData.LButtonTwo = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
            headsetData.LButtonThumbstick = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.RPosition = rightController.position;
            headsetData.RRotation = rightController.rotation;
            headsetData.RThumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            headsetData.RIndexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            headsetData.RHandTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            headsetData.RButtonOne = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
            headsetData.RButtonTwo = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
            headsetData.RButtonThumbstick = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
            headsetData.LEyePixel = leftPixel;
            headsetData.REyePixel = rightPixel;
            headsetData.LeftTimestamp = leftTimestampCopy;
            headsetData.RightTimestamp = rightTimestampCopy;
            string message = JsonUtility.ToJson(headsetData);
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));
        }

        lock (dataChannelReceiveLock)
        {
            if (pendingInfoText != null)
            {
                infoText.text = pendingInfoText;
                pendingInfoText = null;
            }

            if (!tuningMode)
                headWarningText.text = headOutOfSync ? "Head out of sync!" : "";

            if (leftOutOfSync)
            {
                leftArmVisual.SetActive(true);
                leftArmVisual.transform.position = leftArmPosition;
                leftArmVisual.transform.rotation = leftArmRotation;
            }
            else
            {
                leftArmVisual.SetActive(false);
            }

            if (rightOutOfSync)
            {
                rightArmVisual.SetActive(true);
                rightArmVisual.transform.position = rightArmPosition;
                rightArmVisual.transform.rotation = rightArmRotation;
            }
            else
            {
                rightArmVisual.SetActive(false);
            }
        }

        if (showStats)
        {
            statsTextTimer += dt;
            if (statsTextTimer >= StatsTextInterval)
            {
                statsTextTimer = 0f;
                UpdateStatsText();
            }
        }
    }
}
