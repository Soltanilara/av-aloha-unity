using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// What the robot is allowed to draw in the operator's world, and what it gets told back.
///
/// This is the component that stops the Unity app needing edits. Teleoperation policy --
/// where an arm should go, when to record, what counts as close enough -- lives on the
/// robot in Python. This end owns presentation: what a guide looks like, whether the
/// operator is holding controllers or using bare hands, how big a label should be at
/// arm's length. See docs/HEADSET_API.md.
///
/// **Inert until spoken to.** With no robot publishing `ui/*`, nothing here allocates,
/// renders or costs a frame. That is deliberate: a feature the robot has not asked for
/// must not be able to break a session that was working.
///
/// Two layers, on purpose:
///
///  * `ui/guide` is *semantic*. The robot sends a pose and a tolerance; this end decides
///    it should look like a hand target or a controller target, based on what is actually
///    being tracked at that instant, and reports back when the operator gets there. The
///    robot never learns which input device was picked up, and the guide is right on the
///    frame they swap.
///  * `ui/marker` is *general*, because no fixed vocabulary survives a real project.
///
/// Tolerance checking lives here rather than on the robot because it needs pose data at
/// tracking rate. Asking the robot would mean a 90 Hz round trip to answer a question
/// this end can answer locally, and getting a stale answer for the trouble.
/// </summary>
public class GvSceneCommands : MonoBehaviour
{
    [Tooltip("Markers with no ttl are kept until replaced. Anything advisory should carry " +
             "one: a robot that dies leaves guidance on screen telling the operator to do " +
             "something that is no longer true, and it looks exactly like it is working.")]
    public float defaultGuideTtl = 0f;

    [Tooltip("Draw Meta's own controller and hand meshes. Off during teleoperation: they " +
             "sit behind the video wall, so they are invisible until you back away and " +
             "then confusing when you do, and guides are the deliberate replacement.")]
    public bool showTrackedHardware = false;

    private sealed class Marker
    {
        public string Id;
        public GameObject Root;
        public string Frame;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public float Until;               // 0 = no expiry
        public LineRenderer Line;
    }

    private sealed class Guide
    {
        public GameObject Root;
        public Transform Ring;
        public LineRenderer Lead;
        public TextMeshProUGUI Label;
        public Vector3 Pos;
        public Quaternion Rot;
        public float Tol = 0.03f;
        public float AngTol = -1f;
        public float Hold = 0.4f;
        public string Text = "";
        public float Until;
        public float InsideSince = -1f;
        public bool Reported;
    }

    private readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
    private readonly Guide[] guides = new Guide[2];      // 0 left, 1 right

    private GvRobotSession session;
    private GvHandTracking handTracking;
    private OVRCameraRig rig;
    private Transform head;
    private Material lineMat;
    private Material solidMat;
    private readonly List<string> expired = new List<string>();
    private float nextHardwarePass;

    /// <summary>Messages accepted, for the HUD. Without this a blank view is equally
    /// consistent with the robot not publishing and the headset not rendering, and
    /// those have nothing in common as problems.</summary>
    public int MarkersReceived { get; private set; }
    public int GuidesReceived { get; private set; }
    public int MarkerCount => markers.Count;

    private void Start()
    {
        session = GvRobotSession.Instance;
        rig = FindAnyObjectByType<OVRCameraRig>();
        head = GvXr.Head();

        var link = session != null ? session.Link : null;
        if (link == null)
            return;
        link.Subscribe("ui/marker", OnMarker);
        link.Subscribe("ui/guide", OnGuide);
        link.Subscribe("ui/toast", OnToast);
        link.Subscribe("hx", OnHaptics);
    }

    // -------------------------------------------------------- controller/hand meshes

    /// <summary>
    /// Show or hide Meta's controller and hand meshes.
    ///
    /// Reapplied on a timer rather than once, because the hand meshes are spawned by the
    /// SDK after the scene loads and again whenever tracking is regained -- a one-shot
    /// pass at Start hides whatever happens to exist at that instant and nothing after.
    /// </summary>
    private void ApplyHardwareVisibility()
    {
        if (rig == null)
            rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig == null)
            return;
        foreach (var anchor in new[] { rig.leftHandAnchor, rig.rightHandAnchor,
                                       rig.leftControllerAnchor, rig.rightControllerAnchor })
        {
            if (anchor == null)
                continue;
            foreach (var r in anchor.GetComponentsInChildren<Renderer>(true))
                r.enabled = showTrackedHardware;
        }
    }

    // ------------------------------------------------------------------ inbound

    private void OnToast(object data)
    {
        if (!(data is Dictionary<string, object> m))
            return;
        GvToast.Post(GvMsgPack.GetString(m, "txt", ""),
                     GvMsgPack.GetString(m, "sev", "info"),
                     GvMsgPack.GetFloat(m, "secs", 3f));
    }

    private void OnHaptics(object data)
    {
        if (!(data is Dictionary<string, object> m))
            return;
        string side = GvMsgPack.GetString(m, "side", "r");
        var which = side == "l" ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        float amp = Mathf.Clamp01(GvMsgPack.GetFloat(m, "amp", 0.5f));
        float secs = Mathf.Clamp(GvMsgPack.GetFloat(m, "secs", 0.05f), 0.01f, 1f);
        StartCoroutine(Buzz(which, amp, secs));
    }

    private System.Collections.IEnumerator Buzz(OVRInput.Controller which, float amp, float secs)
    {
        try { OVRInput.SetControllerVibration(0.5f, amp, which); }
        catch (System.Exception) { yield break; }
        yield return new WaitForSecondsRealtime(secs);
        try { OVRInput.SetControllerVibration(0f, 0f, which); }
        catch (System.Exception) { /* runtime went away mid-buzz */ }
    }

    private void OnGuide(object data)
    {
        if (!(data is Dictionary<string, object> m))
            return;
        int side = GvMsgPack.GetString(m, "side", "r") == "l" ? 0 : 1;

        if (GvMsgPack.GetBool(m, "clear", false))
        {
            DestroyGuide(side);
            return;
        }

        GuidesReceived++;
        var g = guides[side] ?? (guides[side] = BuildGuide(side));
        var p = GvMsgPack.GetFloats(m, "p");
        var q = GvMsgPack.GetFloats(m, "q");
        if (p != null && p.Length >= 3) g.Pos = new Vector3(p[0], p[1], p[2]);
        if (q != null && q.Length >= 4) g.Rot = new Quaternion(q[0], q[1], q[2], q[3]);
        g.Tol = Mathf.Max(0.005f, GvMsgPack.GetFloat(m, "tol", 0.03f));
        g.AngTol = GvMsgPack.GetFloat(m, "ang", -1f);
        g.Hold = Mathf.Max(0f, GvMsgPack.GetFloat(m, "hold", 0.4f));
        g.Text = GvMsgPack.GetString(m, "label", "");
        float ttl = GvMsgPack.GetFloat(m, "ttl", defaultGuideTtl);
        g.Until = ttl > 0f ? Time.unscaledTime + ttl : 0f;
        // A moved target is a new target: the operator has not "arrived" at it yet, and
        // reporting reached again without them doing anything would be a lie.
        g.InsideSince = -1f;
        g.Reported = false;
    }

    private void OnMarker(object data)
    {
        if (!(data is Dictionary<string, object> m))
            return;

        string clear = GvMsgPack.GetString(m, "clear", null);
        if (clear != null)
        {
            ClearMarkers(clear);
            return;
        }

        var list = GvMsgPack.GetList(m, "m");
        if (list == null)
            return;
        foreach (var item in list)
        {
            if (!(item is Dictionary<string, object> spec))
                continue;
            MarkersReceived++;
            UpsertMarker(spec);
        }
    }

    private void ClearMarkers(string prefix)
    {
        expired.Clear();
        foreach (var kv in markers)
            if (prefix == "*" || kv.Key.StartsWith(prefix))
                expired.Add(kv.Key);
        foreach (var id in expired)
            DestroyMarker(id);
    }

    // ------------------------------------------------------------------ markers

    private void UpsertMarker(Dictionary<string, object> spec)
    {
        string id = GvMsgPack.GetString(spec, "id", null);
        if (string.IsNullOrEmpty(id))
            return;
        string type = GvMsgPack.GetString(spec, "t", "sphere");
        if (type == "del")
        {
            DestroyMarker(id);
            return;
        }

        DestroyMarker(id);                      // rebuild rather than mutate: a marker
                                                // that changes type is a different object
        var mk = new Marker
        {
            Id = id,
            Frame = GvMsgPack.GetString(spec, "f", "origin"),
            Root = new GameObject("mk:" + id),
        };
        mk.Root.transform.SetParent(transform, false);

        var p = GvMsgPack.GetFloats(spec, "p");
        var q = GvMsgPack.GetFloats(spec, "q");
        mk.LocalPos = (p != null && p.Length >= 3) ? new Vector3(p[0], p[1], p[2]) : Vector3.zero;
        mk.LocalRot = (q != null && q.Length >= 4)
                    ? new Quaternion(q[0], q[1], q[2], q[3]) : Quaternion.identity;
        float ttl = GvMsgPack.GetFloat(spec, "ttl", 0f);
        mk.Until = ttl > 0f ? Time.unscaledTime + ttl : 0f;

        Color colour = ColourOf(spec, new Color(0.35f, 0.75f, 1f, 0.8f));
        var scale = GvMsgPack.GetFloats(spec, "s");
        float uniform = GvMsgPack.GetFloat(spec, "s", 0.05f);

        switch (type)
        {
            case "pose":
                BuildAxes(mk.Root.transform, uniform > 0f ? uniform : 0.05f);
                break;
            case "box":
                BuildSolid(mk.Root.transform, PrimitiveType.Cube, colour,
                           scale != null && scale.Length >= 3
                               ? new Vector3(scale[0], scale[1], scale[2])
                               : Vector3.one * uniform);
                break;
            case "sphere":
                BuildSolid(mk.Root.transform, PrimitiveType.Sphere, colour,
                           Vector3.one * (uniform > 0f ? uniform : 0.05f));
                break;
            case "arrow":
            case "line":
                mk.Line = BuildLine(mk.Root.transform, spec, colour);
                break;
            case "text":
                BuildText(mk.Root.transform, GvMsgPack.GetString(spec, "txt", ""), colour,
                          GvMsgPack.GetFloat(spec, "size", 0.03f));
                break;
            default:
                Debug.LogWarning($"GvSceneCommands: unknown marker type '{type}'");
                break;
        }
        markers[id] = mk;
    }

    private static Color ColourOf(IDictionary<string, object> spec, Color fallback)
    {
        var c = GvMsgPack.GetFloats(spec, "c");
        if (c == null || c.Length < 3)
            return fallback;
        return new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
    }

    private void BuildSolid(Transform parent, PrimitiveType type, Color colour, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(type);
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(parent, false);
        go.transform.localScale = size;
        var mr = go.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.material = Tint(colour);
    }

    private void BuildAxes(Transform parent, float size)
    {
        var dirs = new[] { Vector3.right, Vector3.up, Vector3.forward };
        var cols = new[] { new Color(1f, 0.35f, 0.35f), new Color(0.4f, 1f, 0.4f),
                           new Color(0.4f, 0.6f, 1f) };
        for (int i = 0; i < 3; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localScale = dirs[i] * size + Vector3.one * (size * 0.08f);
            go.transform.localPosition = dirs[i] * (size * 0.5f);
            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.material = Tint(cols[i]);
        }
    }

    private LineRenderer BuildLine(Transform parent, IDictionary<string, object> spec, Color colour)
    {
        var go = new GameObject("line");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = LineMaterial;
        lr.useWorldSpace = false;
        lr.numCapVertices = 2;
        lr.startColor = lr.endColor = colour;
        lr.startWidth = lr.endWidth = GvMsgPack.GetFloat(spec, "w", 0.004f);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var pts = GvMsgPack.GetList(spec, "pts");
        if (pts == null || pts.Count < 2)
        {
            lr.positionCount = 0;
            return lr;
        }
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++)
        {
            var v = pts[i] as List<object>;
            lr.SetPosition(i, v != null && v.Count >= 3
                ? new Vector3(ToFloat(v[0]), ToFloat(v[1]), ToFloat(v[2]))
                : Vector3.zero);
        }
        return lr;
    }

    private static float ToFloat(object o) =>
        o is double d ? (float)d : o is long l ? l : 0f;

    private void BuildText(Transform parent, string text, Color colour, float metres)
    {
        var canvas = GvMenuUi.CreateCanvas(parent, "text", new Vector2(600f, 80f), 205);
        var label = GvMenuUi.Label(canvas.transform, "T", text, 48f, colour,
                                   TextAlignmentOptions.Center);
        GvMenuUi.Stretch(label.rectTransform);
        canvas.transform.localScale = Vector3.one * (metres / 80f);
    }

    private void DestroyMarker(string id)
    {
        if (!markers.TryGetValue(id, out var mk))
            return;
        if (mk.Root != null)
            Destroy(mk.Root);
        markers.Remove(id);
    }

    // ------------------------------------------------------------------ guides

    private Guide BuildGuide(int side)
    {
        var g = new Guide { Root = new GameObject(side == 0 ? "guide:l" : "guide:r") };
        g.Root.transform.SetParent(transform, false);

        var ring = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var col = ring.GetComponent<Collider>();
        if (col != null) Destroy(col);
        ring.transform.SetParent(g.Root.transform, false);
        var mr = ring.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.material = Tint(new Color(1f, 0.75f, 0.25f, 0.35f));
        g.Ring = ring.transform;

        BuildAxes(g.Root.transform, 0.06f);

        var lead = new GameObject("lead");
        lead.transform.SetParent(g.Root.transform, false);
        g.Lead = lead.AddComponent<LineRenderer>();
        g.Lead.material = LineMaterial;
        g.Lead.useWorldSpace = true;
        g.Lead.positionCount = 2;
        g.Lead.startWidth = g.Lead.endWidth = 0.003f;
        g.Lead.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var canvas = GvMenuUi.CreateCanvas(g.Root.transform, "label",
                                           new Vector2(600f, 70f), 205);
        g.Label = GvMenuUi.Label(canvas.transform, "T", "", 40f, GvMenuUi.Text,
                                 TextAlignmentOptions.Center);
        GvMenuUi.Stretch(g.Label.rectTransform);
        canvas.transform.localScale = Vector3.one * (0.03f / 70f);
        canvas.transform.localPosition = new Vector3(0f, 0.10f, 0f);
        return g;
    }

    private void DestroyGuide(int side)
    {
        if (guides[side] == null)
            return;
        Destroy(guides[side].Root);
        guides[side] = null;
    }

    /// <summary>
    /// Where the operator's hand or controller actually is, and what to call it.
    ///
    /// This is the whole point of the semantic layer: the robot asked for "left", and
    /// this end works out whether that currently means a tracked hand or a Touch
    /// controller. Hands win when they are tracked, matching the uplink's own rule that
    /// hands and controllers are alternatives rather than additions.
    /// </summary>
    private bool ActualPose(int side, out Vector3 pos, out Quaternion rot, out string what)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        what = "hand";

        if (handTracking == null)
            handTracking = FindAnyObjectByType<GvHandTracking>(FindObjectsInactive.Include);
        if (handTracking != null)
        {
            var h = side == 0 ? handTracking.Left : handTracking.Right;
            if (h.Tracked && h.Wrist.Valid)
            {
                var origin = rig != null ? rig.trackingSpace : null;
                pos = origin != null ? origin.TransformPoint(h.Wrist.Position) : h.Wrist.Position;
                rot = origin != null ? origin.rotation * h.Wrist.Rotation : h.Wrist.Rotation;
                return true;
            }
        }

        var anchor = rig == null ? null
                   : (side == 0 ? rig.leftControllerAnchor : rig.rightControllerAnchor);
        var which = side == 0 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        try
        {
            if (anchor != null && (OVRInput.GetConnectedControllers() & which) == which)
            {
                pos = anchor.position;
                rot = anchor.rotation;
                what = "controller";
                return true;
            }
        }
        catch (System.Exception)
        {
            // No runtime.
        }
        return false;
    }

    private void UpdateGuide(int side)
    {
        var g = guides[side];
        if (g == null)
            return;
        if (g.Until > 0f && Time.unscaledTime >= g.Until)
        {
            DestroyGuide(side);
            return;
        }

        Vector3 target = ToWorld(g.Pos);
        g.Root.transform.SetPositionAndRotation(target, ToWorldRot(g.Rot));

        Vector3 actual;
        Quaternion actualRot;
        string what;
        bool live = ActualPose(side, out actual, out actualRot, out what);

        g.Lead.enabled = live;
        if (!live)
        {
            g.Label.text = g.Text;
            g.InsideSince = -1f;
            return;
        }

        g.Lead.SetPosition(0, actual);
        g.Lead.SetPosition(1, target);

        float d = Vector3.Distance(actual, target);
        bool near = d <= g.Tol;
        if (near && g.AngTol > 0f)
            near = Quaternion.Angle(actualRot, g.Root.transform.rotation) <= g.AngTol;

        // Scaled to the tolerance, so the sphere *is* the target: touching it is arriving.
        g.Ring.localScale = Vector3.one * (g.Tol * 2f);
        var mr = g.Ring.GetComponent<MeshRenderer>();
        mr.material.color = near ? new Color(0.35f, 0.95f, 0.45f, 0.45f)
                                 : new Color(1f, 0.75f, 0.25f, 0.3f);

        string side_ = side == 0 ? "left" : "right";
        g.Label.text = string.IsNullOrEmpty(g.Text)
            ? $"{side_} {what}   {d * 100f:0} cm"
            : $"{g.Text}   {d * 100f:0} cm";

        if (!near)
        {
            g.InsideSince = -1f;
            g.Reported = false;
            return;
        }
        if (g.InsideSince < 0f)
            g.InsideSince = Time.unscaledTime;
        if (g.Reported || Time.unscaledTime - g.InsideSince < g.Hold)
            return;

        g.Reported = true;
        var link = session != null ? session.Link : null;
        link?.Publish("ui/guide/reached", GvRobotSession.Map(
            "side", side == 0 ? "l" : "r", "label", g.Text, "src", what));
        try { OVRInput.SetControllerVibration(0.5f, 0.7f, side == 0
                  ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch); }
        catch (System.Exception) { /* hands have no motor */ }
    }

    // ------------------------------------------------------------------ frames

    private Vector3 ToWorld(Vector3 local)
    {
        var origin = rig != null ? rig.trackingSpace : null;
        return origin != null ? origin.TransformPoint(local) : local;
    }

    private Quaternion ToWorldRot(Quaternion local)
    {
        var origin = rig != null ? rig.trackingSpace : null;
        return origin != null ? origin.rotation * local : local;
    }

    private Transform FrameOf(string frame)
    {
        switch (frame)
        {
            case "head":
            case "view": return head;
            case "hand_l": return rig != null ? rig.leftHandAnchor : null;
            case "hand_r": return rig != null ? rig.rightHandAnchor : null;
            default: return rig != null ? rig.trackingSpace : null;
        }
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextHardwarePass)
        {
            nextHardwarePass = Time.unscaledTime + 1.5f;
            ApplyHardwareVisibility();
        }
        if (markers.Count > 0)
            UpdateMarkers();
        UpdateGuide(0);
        UpdateGuide(1);
    }

    private void UpdateMarkers()
    {
        if (head == null)
            head = GvXr.Head();

        expired.Clear();
        float now = Time.unscaledTime;
        foreach (var kv in markers)
        {
            var mk = kv.Value;
            if (mk.Until > 0f && now >= mk.Until)
            {
                expired.Add(kv.Key);
                continue;
            }
            var parent = FrameOf(mk.Frame);
            if (parent == null)
                continue;
            mk.Root.transform.SetPositionAndRotation(
                parent.TransformPoint(mk.LocalPos), parent.rotation * mk.LocalRot);
            // A view-frame marker faces the viewer rather than inheriting the head's roll,
            // which would tilt text with every glance sideways.
            if (mk.Frame == "view" && head != null)
                mk.Root.transform.rotation =
                    Quaternion.LookRotation(mk.Root.transform.position - head.position, Vector3.up);
        }
        foreach (var id in expired)
            DestroyMarker(id);
    }

    // ------------------------------------------------------------------ materials

    /// <summary>A tinted instance of the shared marker material, or null if no shader
    /// resolved. Assigning a null material draws nothing, which beats throwing from
    /// inside a subscriber callback and taking the rest of the batch with it.</summary>
    private Material Tint(Color c)
    {
        var baseMat = SolidMaterial;
        return baseMat == null ? null : new Material(baseMat) { color = c };
    }

    private Material SolidMaterial
    {
        get
        {
            if (solidMat == null)
            {
                // Sprites/Default, not Unlit/Color, for two independent reasons. It is in
                // the project's Always Included Shaders, so Shader.Find still resolves it
                // in a build -- Unlit/Color is not, so it returned null on device and
                // every marker got a null material and drew nothing, while working
                // perfectly in the Editor where all shaders are loaded. And it blends:
                // Unlit/Color ignores alpha outright, so a translucent guide sphere would
                // have been a solid one.
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                if (sh == null)
                {
                    Debug.LogError("GvSceneCommands: no usable shader; markers cannot draw.");
                    return null;
                }
                solidMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                // Past the transparent queue for the same reason the pointer is: the video
                // plane covers the view and would otherwise sort in front of everything.
                solidMat.renderQueue = 4000;
            }
            return solidMat;
        }
    }

    private Material LineMaterial
    {
        get
        {
            if (lineMat == null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                if (sh == null)
                    return null;
                lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                lineMat.renderQueue = 4000;
            }
            return lineMat;
        }
    }

    private void OnDestroy()
    {
        if (solidMat != null) DestroyImmediate(solidMat);
        if (lineMat != null) DestroyImmediate(lineMat);
    }
}
