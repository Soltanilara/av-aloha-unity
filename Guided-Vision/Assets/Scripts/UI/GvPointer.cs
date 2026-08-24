using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A laser pointer driven by whichever input the user actually has in their hands:
/// either Touch controller, either tracked hand, or the mouse in the Editor.
///
/// It answers one question -- "where is the user pointing, and did they just click?" --
/// and draws the ray. It owns no UI logic; a consumer does its own hit test against the
/// ray and reports the result back through <see cref="SetHit"/> so the beam can stop at
/// the surface.
///
/// The source is chosen by **who is actually aiming at the target**, not by a fixed
/// handedness preference. That falls out right for left- and right-handed users without
/// a setting, and it means putting a controller down and raising a hand simply works.
/// Hysteresis keeps the beam from flickering between two sources aimed at nearly the
/// same place.
///
/// Meta's Interaction SDK is not in this project (core SDK only), and pulling it in for
/// one laser would be a large dependency for a small feature. This is roughly 250 lines
/// with no scene wiring.
/// </summary>
public class GvPointer : MonoBehaviour
{
    [Tooltip("What the pointer is aimed at. Used to pick the best-aimed source and to " +
             "place the Editor mouse fallback.")]
    public RectTransform target;

    public Transform head;

    [Header("Feel")]
    public float maxDistance = 6f;
    public float restLength = 2.5f;
    public float width = 0.005f;
    [Tooltip("A source must beat the current one by this many degrees before the beam " +
             "moves to it. Without it two hands aimed at the same row trade the beam " +
             "every frame.")]
    public float switchMarginDeg = 8f;

    [Header("Look")]
    public Color beamColor = new Color(0.45f, 0.70f, 1f, 0.55f);
    public Color beamHitColor = new Color(0.55f, 0.85f, 1f, 0.95f);
    public float reticleSize = 0.012f;

    /// <summary>True when some source is producing a usable ray this frame.</summary>
    public bool Active { get; private set; }
    public Ray Aim { get; private set; }
    public bool ClickDown { get; private set; }
    public bool ClickUp { get; private set; }
    public bool ClickHeld { get; private set; }

    /// <summary>"controller", "hand", "mouse" or "" -- for hint text.</summary>
    public string SourceKind { get; private set; } = "";

    private struct Source
    {
        public Ray ray;
        public bool clicking;
        public string kind;
        public OVRInput.Controller haptics;   // None for hands and mouse
        public int id;                        // stable across frames, for hysteresis
    }

    private OVRCameraRig rig;
    private readonly List<OVRHand> hands = new List<OVRHand>();
    private float nextHandScan;
    private bool anyHandLastFrame;

    private LineRenderer beam;
    private Transform reticle;
    private Material lineMaterial;

    private int currentId = -1;
    private bool wasClicking;
    private float hapticUntil;
    private bool hitValid;
    private Vector3 hitPoint;
    private OVRInput.Controller hapticOn = OVRInput.Controller.None;

    private readonly List<Source> sources = new List<Source>(6);

    private void Awake()
    {
        rig = FindAnyObjectByType<OVRCameraRig>();
        if (head == null)
            head = GvXr.Head();
        BuildVisuals();
    }

    private void BuildVisuals()
    {
        // Sprites/Default is in the project's Always Included Shaders, so it survives a
        // build; the fallbacks are only for a project where that is not true.
        var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default")
              ?? Shader.Find("Unlit/Color");
        lineMaterial = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        // Past the transparent queue, so the beam and reticle draw over the video plane
        // rather than being sorted behind it. A laser you cannot see while pointing at a
        // menu floating in front of a video wall is worse than no laser.
        lineMaterial.renderQueue = 4000;

        var go = new GameObject("Beam");
        go.transform.SetParent(transform, false);
        beam = go.AddComponent<LineRenderer>();
        beam.material = lineMaterial;
        beam.useWorldSpace = true;
        beam.positionCount = 2;
        beam.numCapVertices = 2;
        beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        beam.receiveShadows = false;
        beam.enabled = false;

        var dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
        dot.name = "Reticle";
        var col = dot.GetComponent<Collider>();
        if (col != null) Destroy(col);
        dot.transform.SetParent(transform, false);
        var mr = dot.GetComponent<MeshRenderer>();
        mr.sharedMaterial = lineMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        reticle = dot.transform;
        reticle.gameObject.SetActive(false);
    }

    private void Update()
    {
        Gather();
        var src = Choose();

        if (src.kind == null)
        {
            Active = false;
            SourceKind = "";
            ClickDown = ClickUp = ClickHeld = false;
            wasClicking = false;
            currentId = -1;
            Show(false, Vector3.zero);
            return;
        }

        // Switching source mid-press must not read as a new click, or letting go of one
        // controller and raising a hand would activate whatever the hand landed on.
        if (src.id != currentId)
        {
            currentId = src.id;
            wasClicking = src.clicking;
        }

        Active = true;
        Aim = src.ray;
        SourceKind = src.kind;
        ClickHeld = src.clicking;
        ClickDown = src.clicking && !wasClicking;
        ClickUp = !src.clicking && wasClicking;
        wasClicking = src.clicking;

        if (ClickDown && src.haptics != OVRInput.Controller.None)
            Pulse(src.haptics, 0.35f, 0.035f);

        // Consumers report their hit during Update; nothing is drawn until LateUpdate.
        hitValid = false;

        if (hapticOn != OVRInput.Controller.None && Time.unscaledTime >= hapticUntil)
        {
            OVRInput.SetControllerVibration(0f, 0f, hapticOn);
            hapticOn = OVRInput.Controller.None;
        }
    }

    /// <summary>
    /// Drawing happens here, not in Update, because the consumer's hit test also runs
    /// in Update and the order of two components' Update is undefined. Drawing early
    /// would show a stale beam on the frames the consumer happened to run first --
    /// the same trap that left the input uplink idle.
    /// </summary>
    private void LateUpdate()
    {
        if (!Active)
        {
            Show(false, Vector3.zero);
            return;
        }
        Show(true, hitValid ? hitPoint : Aim.origin + Aim.direction * restLength, hitValid);
    }

    // ------------------------------------------------------------------- sources

    private void Gather()
    {
        sources.Clear();
        AddController(OVRInput.Controller.RTouch, rig != null ? rig.rightControllerAnchor : null, 0);
        AddController(OVRInput.Controller.LTouch, rig != null ? rig.leftControllerAnchor : null, 1);

        // Rescan on a timer whenever nothing usable was found last frame, rather than
        // only when the list is empty: a hand destroyed and respawned leaves a list of
        // nulls, which is not empty and would never be refreshed.
        if (!anyHandLastFrame && Time.unscaledTime >= nextHandScan)
        {
            nextHandScan = Time.unscaledTime + 2f;
            hands.Clear();
            hands.AddRange(FindObjectsByType<OVRHand>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        }
        anyHandLastFrame = false;
        for (int i = 0; i < hands.Count; i++)
        {
            var h = hands[i];
            if (h == null || !h.isActiveAndEnabled || !h.IsTracked || !h.IsPointerPoseValid)
                continue;
            if (h.HandConfidence != OVRHand.TrackingConfidence.High)
                continue;
            var pose = h.PointerPose;
            if (pose == null)
                continue;
            anyHandLastFrame = true;
            sources.Add(new Source
            {
                ray = new Ray(pose.position, pose.forward),
                clicking = h.GetFingerIsPinching(OVRHand.HandFinger.Index),
                kind = "hand",
                haptics = OVRInput.Controller.None,
                id = 10 + i,
            });
        }

        AddEditorMouse();
    }

    private void AddController(OVRInput.Controller which, Transform anchor, int id)
    {
        if (anchor == null)
            return;
        if ((OVRInput.GetConnectedControllers() & which) != which)
            return;
        // Trigger only. A/X stays with stick navigation, which activates the *selected*
        // row -- if the trigger and the face button both clicked, one press would fire
        // the hovered row and the selected row, and "Connect" would load the scene twice.
        bool click = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, which) > 0.55f;
        sources.Add(new Source
        {
            ray = new Ray(anchor.position, anchor.forward),
            clicking = click,
            kind = "controller",
            haptics = which,
            id = id,
        });
    }

    /// <summary>
    /// Editor stand-in so the menu is clickable without a headset.
    ///
    /// It maps the cursor across the Game view onto the target rect and aims at that
    /// point from the head, rather than building a ray from the render camera --
    /// under XR the mouse is in Game-view pixels while the eye camera's
    /// ScreenPointToRay expects its own render target, and mixing them puts the ray
    /// tens of degrees off. Same lesson as the gaze simulator.
    /// </summary>
    private void AddEditorMouse()
    {
        if (!Application.isEditor || target == null || head == null)
            return;
        if (Screen.width <= 0 || Screen.height <= 0)
            return;
        Vector3 m = Input.mousePosition;
        float fx = m.x / Screen.width, fy = m.y / Screen.height;
        if (fx < 0f || fx > 1f || fy < 0f || fy > 1f)
            return;

        var r = target.rect;
        Vector3 local = new Vector3(Mathf.Lerp(r.xMin, r.xMax, fx),
                                    Mathf.Lerp(r.yMin, r.yMax, fy), 0f);
        Vector3 world = target.TransformPoint(local);
        Vector3 dir = (world - head.position).normalized;
        sources.Add(new Source
        {
            ray = new Ray(head.position, dir),
            clicking = Input.GetMouseButton(0),
            kind = "mouse",
            haptics = OVRInput.Controller.None,
            id = 99,
        });
    }

    /// <summary>Pick whichever source is aimed closest to the target, with hysteresis.</summary>
    private Source Choose()
    {
        var none = new Source { kind = null };
        if (sources.Count == 0)
            return none;
        Vector3 centre = target != null ? target.position
                       : (head != null ? head.position + head.forward * restLength : Vector3.zero);

        int best = -1, bestClicking = -1;
        float bestAngle = float.MaxValue, bestClickAngle = float.MaxValue;
        float currentAngle = float.MaxValue;
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            float a = Vector3.Angle(s.ray.direction, centre - s.ray.origin);
            // The source already being pressed wins outright: the user has committed.
            if (s.clicking && s.id == currentId)
                return s;
            if (s.id == currentId)
                currentAngle = a;
            if (a < bestAngle) { bestAngle = a; best = i; }
            if (s.clicking && a < bestClickAngle) { bestClickAngle = a; bestClicking = i; }
        }
        // Someone else pressed while the current source was idle -- they mean that one.
        if (bestClicking >= 0)
            return sources[bestClicking];
        if (currentAngle < float.MaxValue && bestAngle > currentAngle - switchMarginDeg)
            for (int i = 0; i < sources.Count; i++)
                if (sources[i].id == currentId)
                    return sources[i];
        return best >= 0 ? sources[best] : none;
    }

    // -------------------------------------------------------------------- output

    /// <summary>Consumer reports where the ray landed so the beam can stop there.</summary>
    public void SetHit(bool valid, Vector3 point)
    {
        hitValid = valid;
        hitPoint = point;
    }

    private void Show(bool on, Vector3 end, bool hit = false)
    {
        if (beam == null)
            return;
        beam.enabled = on;
        reticle.gameObject.SetActive(on && hit);
        if (!on)
            return;

        Vector3 start = Aim.origin;
        if ((end - start).sqrMagnitude > maxDistance * maxDistance)
            end = start + (end - start).normalized * maxDistance;

        beam.startWidth = beam.endWidth = width;
        beam.startColor = new Color(beamColor.r, beamColor.g, beamColor.b, 0f);
        beam.endColor = hit ? beamHitColor : beamColor;
        beam.SetPosition(0, start);
        beam.SetPosition(1, end);

        if (hit)
        {
            reticle.position = end;
            if (head != null)
                reticle.rotation = Quaternion.LookRotation(end - head.position, Vector3.up);
            float s = reticleSize * Mathf.Max(0.4f, Vector3.Distance(end, start));
            reticle.localScale = new Vector3(s, s, s);
        }
    }

    /// <summary>A short buzz. Ignored for hands and the mouse, which have none.</summary>
    public void Pulse(OVRInput.Controller which, float amplitude, float seconds)
    {
        if (which == OVRInput.Controller.None)
            return;
        OVRInput.SetControllerVibration(0.5f, Mathf.Clamp01(amplitude), which);
        hapticOn = which;
        hapticUntil = Time.unscaledTime + seconds;
    }

    /// <summary>Buzz whichever controller currently owns the beam, if any.</summary>
    public void PulseCurrent(float amplitude = 0.5f, float seconds = 0.04f)
    {
        for (int i = 0; i < sources.Count; i++)
            if (sources[i].id == currentId)
                Pulse(sources[i].haptics, amplitude, seconds);
    }

    /// <summary>
    /// Hide the beam when switched off. Update and LateUpdate stop running with the
    /// component, so without this the last frame's laser would hang in the air pointing
    /// at a menu that is no longer there.
    /// </summary>
    private void OnDisable()
    {
        Active = false;
        ClickDown = ClickUp = ClickHeld = false;
        wasClicking = false;
        currentId = -1;
        if (beam != null)
            beam.enabled = false;
        if (reticle != null)
            reticle.gameObject.SetActive(false);
        if (hapticOn != OVRInput.Controller.None)
        {
            OVRInput.SetControllerVibration(0f, 0f, hapticOn);
            hapticOn = OVRInput.Controller.None;
        }
    }

    private void OnDestroy()
    {
        if (hapticOn != OVRInput.Controller.None)
            OVRInput.SetControllerVibration(0f, 0f, hapticOn);
        if (lineMaterial != null)
            DestroyImmediate(lineMaterial);
    }
}
