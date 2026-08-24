using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// The in-session menu: leave, reconnect, recentre, and the few display knobs worth
/// reaching for while a robot is in front of you.
///
/// The hard requirement is that it stays out of the way. Teleoperation is the job; a
/// menu is an interruption, so it is hidden by default, summoned by a gesture nobody
/// makes by accident, and it never pauses the video behind it -- you can see what the
/// robot is doing while you decide.
///
/// There are three ways in, because there is no single input the operator is guaranteed
/// to have: **hold B or Y**, **tap the left Menu button**, or -- with no controllers at
/// all -- **hold a middle-finger pinch**. The holds are deliberate rather than taps
/// because a hand brushes a button while working and the cost of an accidental menu
/// mid-manipulation is far higher than the cost of a gesture taking half a second. A
/// progress pill appears the moment a hold starts, so a gesture that is being seen never
/// looks like one that is being ignored.
///
/// It replaces the old TransitionStartScene, which loaded a scene outright on a single
/// unguarded press.
/// </summary>
public class GvSessionMenu : MonoBehaviour
{
    [Header("Summon")]
    [Tooltip("Seconds to hold B / Y before the menu opens. A tap is left alone: hands " +
             "brush buttons during teleoperation, and an accidental menu mid-task is " +
             "worse than a deliberate one being half a second slower.")]
    public float holdToOpen = 0.5f;

    [Tooltip("Seconds to hold a middle-finger pinch, for hand tracking. Longer than the " +
             "button hold because a pinch has no click to confirm it started.")]
    public float handHoldToOpen = 0.6f;

    [Header("Placement")]
    [Tooltip("Metres in front of the viewer. Clamped to sit in front of the video plane: " +
             "a menu behind it is both hidden by it and, if drawn anyway, at odds with " +
             "what the eyes are converging on.")]
    public float menuDistance = 0.85f;
    public float menuHeightOffset = -0.15f;

    [Header("Scenes")]
    public string startSceneName = "GvStartScene";

    private readonly List<GvMenuItem> items = new List<GvMenuItem>();
    private readonly GvNav nav = new GvNav();

    private Canvas canvas;
    private RectTransform listRoot;
    private TextMeshProUGUI titleText;
    private TextMeshProUGUI statusText;
    private TextMeshProUGUI hintText;
    private Transform head;
    private GvPointer pointer;
    private GvStereoDisplay display;
    private GvRobotSession session;
    private GvHandTracking handTracking;

    private Canvas summonCanvas;
    private RectTransform summonFill;
    private TextMeshProUGUI summonText;

    private const float RowHeight = 46f;
    private const float RowGap = 4f;

    /// <summary>Panel chrome the list has to fit between: title and status above, hint
    /// below. The panel is grown to fit its rows rather than the rows being trusted to
    /// fit a fixed panel -- a row rendered past the bottom edge is not merely ugly, it
    /// is unclickable, because the pointer only accepts hits inside the list viewport.
    /// That is how Disconnect ended up reachable by stick and by nothing else.</summary>
    private const float TopChrome = 116f;
    private const float BottomChrome = 52f;
    private const float PanelWidth = 760f;

    /// <summary>
    /// Drawn above the eye canvases, which sit at 0. Without this the menu is sorted
    /// behind the video quad and never appears, however correctly it was summoned.
    /// </summary>
    private const int MenuSortingOrder = 200;

    /// <summary>The canvas is authored for this distance; nearer placements scale down
    /// to match, so the panel keeps the same angular size wherever it lands.</summary>
    private const float ReferenceDistance = 1.1f;

    /// <summary>Middle-pinch strength that counts as held. High: a partial curl during
    /// manipulation must not start a countdown.</summary>
    private const float PinchOn = 0.85f;

    private bool open;
    private int selected;
    private int hoverRow = -1;
    private int hoverStep;
    private bool pointerEngaged;
    private Vector2 lastPointerLocal;
    private float held;
    private bool wasPinching;
    private float nextHandScan;

    public bool IsOpen => open;

    private void Start()
    {
        display = FindAnyObjectByType<GvStereoDisplay>(FindObjectsInactive.Include);
        session = GvRobotSession.Instance;
        head = GvXr.Head();
        Build();
        SetOpen(false);
    }

    // ------------------------------------------------------------------ chrome

    private void Build()
    {
        canvas = GvMenuUi.CreateCanvas(transform, "GvSessionMenu",
                                       new Vector2(PanelWidth, TopChrome + BottomChrome),
                                       MenuSortingOrder);
        GvMenuUi.Panel(canvas.transform, GvMenuUi.Background);

        titleText = GvMenuUi.Label(canvas.transform, "Title", "Session", 36f, GvMenuUi.Text);
        var trt = titleText.rectTransform;
        trt.anchorMin = new Vector2(0f, 1f);
        trt.anchorMax = new Vector2(1f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(28f, -24f);
        trt.sizeDelta = new Vector2(-56f, 44f);

        statusText = GvMenuUi.Label(canvas.transform, "Status", "", 22f, GvMenuUi.Dim);
        var srt = statusText.rectTransform;
        srt.anchorMin = new Vector2(0f, 1f);
        srt.anchorMax = new Vector2(1f, 1f);
        srt.pivot = new Vector2(0.5f, 1f);
        srt.anchoredPosition = new Vector2(28f, -72f);
        srt.sizeDelta = new Vector2(-56f, 30f);

        hintText = GvMenuUi.Label(canvas.transform, "Hint", "", 20f, GvMenuUi.Dim);
        var hrt = hintText.rectTransform;
        hrt.anchorMin = new Vector2(0f, 0f);
        hrt.anchorMax = new Vector2(1f, 0f);
        hrt.pivot = new Vector2(0.5f, 0f);
        hrt.anchoredPosition = new Vector2(28f, 16f);
        hrt.sizeDelta = new Vector2(-56f, 28f);

        var lgo = new GameObject("List", typeof(RectTransform));
        lgo.transform.SetParent(canvas.transform, false);
        listRoot = (RectTransform)lgo.transform;
        GvMenuUi.Stretch(listRoot, 22f, 22f, TopChrome, BottomChrome);

        pointer = gameObject.AddComponent<GvPointer>();
        pointer.head = head;
        pointer.target = (RectTransform)canvas.transform;

        BuildSummonPill();
        BuildItems();
    }

    /// <summary>
    /// The progress pill shown while a summon gesture is being held.
    ///
    /// Its real job is not decoration: a hold with no feedback is indistinguishable from
    /// an input that is not being read at all, which is precisely how a menu that was
    /// opening correctly but rendering behind the video read as a dead button.
    /// </summary>
    private void BuildSummonPill()
    {
        summonCanvas = GvMenuUi.CreateCanvas(transform, "GvSummon", new Vector2(300f, 64f),
                                             MenuSortingOrder);
        GvMenuUi.Panel(summonCanvas.transform, GvMenuUi.Background);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer),
                                  typeof(UnityEngine.UI.Image));
        fill.transform.SetParent(summonCanvas.transform, false);
        summonFill = (RectTransform)fill.transform;
        summonFill.anchorMin = new Vector2(0f, 0f);
        summonFill.anchorMax = new Vector2(0f, 1f);
        summonFill.pivot = new Vector2(0f, 0.5f);
        summonFill.anchoredPosition = new Vector2(4f, 0f);
        summonFill.sizeDelta = new Vector2(0f, -8f);
        var img = fill.GetComponent<UnityEngine.UI.Image>();
        img.color = new Color(GvMenuUi.Selected.r, GvMenuUi.Selected.g, GvMenuUi.Selected.b, 0.75f);
        img.raycastTarget = false;

        summonText = GvMenuUi.Label(summonCanvas.transform, "T", "Menu", 26f, GvMenuUi.Text,
                                    TextAlignmentOptions.Center);
        GvMenuUi.Stretch(summonText.rectTransform);

        summonCanvas.gameObject.SetActive(false);
    }

    private void ShowSummon(float progress, string label)
    {
        if (summonCanvas == null)
            return;
        if (progress <= 0f)
        {
            if (summonCanvas.gameObject.activeSelf)
                summonCanvas.gameObject.SetActive(false);
            return;
        }
        if (!summonCanvas.gameObject.activeSelf)
            summonCanvas.gameObject.SetActive(true);
        Place(summonCanvas.transform, menuHeightOffset - 0.26f);
        summonText.text = label;
        // The pill is 300 px wide with a 4 px inset on each side.
        summonFill.sizeDelta = new Vector2(Mathf.Clamp01(progress) * 292f, -8f);
    }

    private void BuildItems()
    {
        foreach (var it in items)
            if (it.Row != null)
                Destroy(it.Row.gameObject);
        items.Clear();

        items.Add(new GvMenuItem
        {
            Label = "Resume",
            Activate = () => SetOpen(false),
        });
        items.Add(new GvMenuItem
        {
            Label = "Recentre view",
            Activate = () =>
            {
                Place();
                if (display != null)
                    display.ApplyHudDistance();
            },
        });
        items.Add(new GvMenuItem
        {
            Label = "Reconnect",
            Value = () => "video + control",
            Activate = () =>
            {
                if (display != null)
                    display.RestartSession();
                SetOpen(false);
            },
        });
        items.Add(new GvMenuItem { Label = "", IsHeader = true });

        var p = session != null ? session.Profile : null;
        if (p != null && display != null)
        {
            items.Add(Float("Magnification", () => display.videoScale,
                            v => { display.videoScale = v; p.videoScale = v; Relayout(); },
                            0.05f, 0.2f, 3f, "{0:0.00}x"));
            items.Add(Float("Plane distance", () => display.videoPlaneDistance,
                            v => { display.videoPlaneDistance = v; p.videoPlaneDistance = v; Relayout(); },
                            0.1f, 0.3f, 10f, "{0:0.0} m"));
            // Only where the headset offers a choice. Exposed in-session rather than as a
            // build setting because the answer is a judgement the eyes make: a rate the
            // app cannot sustain is reprojected, and reprojection is felt, not measured.
            // The HUD's missed-frame count next to this row is the whole experiment.
            if (display.AvailableFrequencies.Length > 1)
                items.Add(new GvMenuItem
                {
                    Label = "Refresh rate",
                    Value = () => string.Format("{0:0} Hz", display.DisplayHz),
                    Adjust = CycleFrequency,
                });
            var scene = FindAnyObjectByType<GvSceneCommands>(FindObjectsInactive.Include);
            if (scene != null)
                items.Add(new GvMenuItem
                {
                    Label = "Show controllers / hands",
                    Value = () => scene.showTrackedHardware ? "on" : "off",
                    Adjust = _ => scene.showTrackedHardware = !scene.showTrackedHardware,
                    Activate = () => scene.showTrackedHardware = !scene.showTrackedHardware,
                });
            items.Add(new GvMenuItem
            {
                Label = "Outline the sharp patch",
                Value = () => display.foveaOutline ? "on" : "off",
                Adjust = _ => display.foveaOutline = !display.foveaOutline,
                Activate = () => display.foveaOutline = !display.foveaOutline,
            });
        }

        items.Add(new GvMenuItem { Label = "", IsHeader = true });
        items.Add(new GvMenuItem
        {
            Label = "Disconnect",
            Value = () => "back to robot list",
            Activate = Leave,
        });

        float y = 0f;
        foreach (var it in items)
        {
            it.Row = GvMenuUi.Row(listRoot, string.IsNullOrEmpty(it.Label) ? "sep" : it.Label,
                                  y, RowHeight);
            y += RowHeight + RowGap;
            it.Highlight = GvMenuUi.Panel(it.Row, new Color(0, 0, 0, 0));
            it.LabelText = GvMenuUi.Label(it.Row, "L", it.Label, 26f, GvMenuUi.Text);
            GvMenuUi.Stretch(it.LabelText.rectTransform, 16f, 300f);
            it.ValueText = GvMenuUi.Label(it.Row, "V", "", 24f, GvMenuUi.Accent,
                                          TextAlignmentOptions.Right);
            if (it.Adjust != null)
            {
                GvMenuUi.Stepper(it);
                GvMenuUi.Stretch(it.ValueText.rectTransform, 0f, 136f);
            }
            else
            {
                GvMenuUi.Stretch(it.ValueText.rectTransform, 0f, 16f);
            }
        }
        ResizeToContent(Mathf.Max(0f, y - RowGap));
        if (!Selectable(selected))
            MoveSelection(1);
    }

    /// <summary>
    /// Grow the panel to hold exactly the rows that were built.
    ///
    /// The row set is not fixed -- the display rows only exist once a profile and a
    /// display do -- so a hard-coded panel height is a guess that is wrong for at least
    /// one of the cases. Sizing to content makes every row that exists both visible and
    /// hittable by construction, which is the property that actually matters.
    /// </summary>
    private void ResizeToContent(float contentHeight)
    {
        if (canvas == null)
            return;
        var rt = (RectTransform)canvas.transform;
        rt.sizeDelta = new Vector2(PanelWidth, TopChrome + contentHeight + BottomChrome);
    }

    private static GvMenuItem Float(string label, System.Func<float> get,
                                    System.Action<float> set, float step,
                                    float min, float max, string fmt)
    {
        return new GvMenuItem
        {
            Label = label,
            Value = () => string.Format(fmt, get()),
            Adjust = dir => set(Mathf.Clamp(
                Mathf.Round((get() + dir * step) / step) * step, min, max)),
        };
    }

    /// <summary>Step to the next refresh rate the headset offers.</summary>
    private void CycleFrequency(int dir)
    {
        var freqs = display.AvailableFrequencies;      // ascending
        if (freqs.Length == 0)
            return;
        float now = display.DisplayHz;
        int i = 0;
        for (int k = 0; k < freqs.Length; k++)
            if (Mathf.Abs(freqs[k] - now) < 0.5f) { i = k; break; }
        i = Mathf.Clamp(i + (dir >= 0 ? 1 : -1), 0, freqs.Length - 1);
        // The ceiling is what ConfigureDisplay reads, so moving it is what makes the
        // choice survive a reconnect rather than being undone by the next restart.
        display.maxDisplayFrequency = freqs[i];
        display.ApplyDisplayFrequency();
    }

    private void Relayout()
    {
        if (display != null)
            display.ApplyStereoLayout();
    }

    private void Leave()
    {
        if (display != null)
            display.enabled = false;
        if (session != null)
            session.Disconnect();
        SceneManager.LoadScene(startSceneName);
    }

    // ------------------------------------------------------------------ lifecycle

    private void SetOpen(bool value)
    {
        open = value;
        if (canvas != null)
            canvas.gameObject.SetActive(value);
        if (pointer != null)
            pointer.enabled = value;
        if (value)
        {
            Place();
            BuildItems();
        }
        held = 0f;
        ShowSummon(0f, null);
    }

    private void Place()
    {
        if (canvas == null)
            return;
        Place(canvas.transform, menuHeightOffset);
        if (pointer != null)
            pointer.head = head;
    }

    private void Place(Transform panel, float heightOffset)
    {
        if (head == null)
            head = GvXr.Head();
        if (head == null)
            return;
        float d = PlaceDistance();
        GvMenuUi.PlaceInFront(panel, head, d, heightOffset);
        // Authored for ReferenceDistance; shrink to match so clamping the distance moves
        // the panel without also making it loom.
        panel.localScale = Vector3.one * (d / ReferenceDistance / GvMenuUi.PixelsPerMetre);
    }

    /// <summary>
    /// In front of the video plane, always.
    ///
    /// Both are world-space canvases in the transparent queue, so equal sorting orders
    /// resolve back-to-front by distance: the default 1.1 m menu sat *behind* a 1.0 m
    /// video quad that covers the whole field of view and was painted straight over. The
    /// sorting order now settles the draw order, but the geometry has to agree with it --
    /// occlusion saying "in front" while vergence says "behind" is a headache in a
    /// headset, not merely a rendering detail.
    /// </summary>
    private float PlaceDistance()
    {
        float d = menuDistance;
        if (display != null && display.videoPlaneDistance > 0.05f)
            d = Mathf.Min(d, display.videoPlaneDistance * 0.7f);
        return Mathf.Max(d, 0.4f);
    }

    private void Update()
    {
        PollSummon();
        if (!open)
            return;

        nav.Poll();
        if (nav.Any)
            pointerEngaged = false;
        if (nav.Down) MoveSelection(1);
        if (nav.Up) MoveSelection(-1);
        if (nav.Recenter) Place();

        UpdatePointer();

        var item = Current();
        if (item != null)
        {
            if (nav.Right) item.Adjust?.Invoke(1);
            if (nav.Left) item.Adjust?.Invoke(-1);
            if (nav.Select && item.Enabled())
                item.Activate?.Invoke();
        }
        HandlePointerClick();
        if (nav.Back)
            SetOpen(false);

        Refresh();
    }

    /// <summary>
    /// Hold B or Y, tap the left Menu button, or hold a middle-finger pinch. While open,
    /// any of the same gestures closes it.
    ///
    /// Asymmetric on purpose: getting in must be deliberate, because the cost of opening
    /// this by accident during teleoperation is high, while getting out should be instant
    /// because by then the operator is looking at a menu and clearly means to.
    ///
    /// The **middle** finger, not the index, is what a pinch has to use here. Index pinch
    /// is the universal click -- this menu's own laser fires on it -- and it is the
    /// obvious thing to map a gripper to. Middle-finger pinch is a motion nobody makes
    /// while working, which is the whole requirement for a gesture that must never fire
    /// by accident mid-manipulation.
    /// </summary>
    private void PollSummon()
    {
        bool buttonHeld = false, buttonDown = false, menuDown = false;
        try
        {
            buttonHeld = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch)
                      || OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch)
                      || OVRInput.Get(OVRInput.Button.Two);
            buttonDown = OVRInput.GetDown(OVRInput.Button.Two);
            // The left Menu button. Not reachable by accident, and it is the one button
            // on the controller whose entire job is "show me the menu".
            menuDown = OVRInput.GetDown(OVRInput.Button.Start);
        }
        catch (System.Exception)
        {
            // No OVR runtime; the keyboard path below still works in the Editor.
        }

        bool pinching = MiddlePinching();
        bool pinchDown = pinching && !wasPinching;
        wasPinching = pinching;

        if (open)
        {
            if (buttonDown || menuDown || pinchDown
                || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.M))
                SetOpen(false);
            return;
        }

        if (menuDown || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.M))
        {
            SetOpen(true);
            return;
        }

        // A button beats a pinch when somehow both are happening, so the shorter hold wins
        // rather than the two fighting over one timer.
        float need = buttonHeld ? holdToOpen : (pinching ? handHoldToOpen : 0f);
        if (need <= 0f)
        {
            held = 0f;
            ShowSummon(0f, null);
            return;
        }

        held += Time.unscaledDeltaTime;
        if (held >= need)
        {
            SetOpen(true);
            return;
        }
        // Nothing shown for the first fraction of a second: a brush against B should not
        // flash a pill into the operator's view.
        ShowSummon(held < 0.12f ? 0f : held / need, "Menu");
    }

    /// <summary>
    /// True while either hand holds a middle-finger pinch.
    ///
    /// Reuses the uplink's hand reader rather than scanning for OVRHand again -- one
    /// place that knows how to talk to the skeleton, and no second copy to drift. It is
    /// resolved lazily because the uplink creates it in its own Start, and the order of
    /// two components' Start is undefined.
    /// </summary>
    private bool MiddlePinching()
    {
        if (handTracking == null)
        {
            // Throttled: the uplink creates it in its own Start, so one retry is the
            // normal case, but a scene without an uplink must not pay a scene-wide
            // search every frame for the rest of the session.
            if (Time.unscaledTime < nextHandScan)
                return false;
            nextHandScan = Time.unscaledTime + 2f;
            handTracking = FindAnyObjectByType<GvHandTracking>(FindObjectsInactive.Include);
            if (handTracking == null)
                return false;
        }
        return Held(handTracking.Left) || Held(handTracking.Right);
    }

    private static bool Held(GvHandState h) =>
        h.Tracked && h.Pinch != null && h.Pinch.Length > 2 && h.Pinch[2] >= PinchOn;

    // ------------------------------------------------------------------ pointing

    private void UpdatePointer()
    {
        hoverRow = -1;
        hoverStep = 0;
        if (pointer == null || canvas == null || !pointer.Active)
            return;

        Vector3 world;
        Vector2 local;
        bool inPanel = GvMenuUi.RayHit((RectTransform)canvas.transform, pointer.Aim,
                                       out world, out local);
        pointer.SetHit(inPanel, world);
        if (!inPanel || !GvMenuUi.Contains(listRoot, world))
            return;

        if ((local - lastPointerLocal).sqrMagnitude > 16f)
            pointerEngaged = true;
        lastPointerLocal = local;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Row == null || !Selectable(i) || !GvMenuUi.Contains(it.Row, world))
                continue;
            hoverRow = i;
            if (it.Adjust != null)
            {
                if (GvMenuUi.Contains(it.Minus, world)) hoverStep = -1;
                else if (GvMenuUi.Contains(it.Plus, world)) hoverStep = 1;
            }
            if (pointerEngaged)
                selected = i;
            break;
        }
    }

    private void HandlePointerClick()
    {
        if (pointer == null || !pointer.ClickDown || hoverRow < 0)
            return;
        var it = items[hoverRow];
        if (!it.Enabled())
            return;
        selected = hoverRow;
        pointerEngaged = true;
        if (hoverStep != 0 && it.Adjust != null)
        {
            it.Adjust(hoverStep);
            pointer.PulseCurrent(0.35f, 0.03f);
        }
        else if (it.Activate != null)
        {
            pointer.PulseCurrent(0.7f, 0.06f);
            it.Activate();
        }
    }

    // ------------------------------------------------------------------ display

    private GvMenuItem Current() =>
        (selected >= 0 && selected < items.Count) ? items[selected] : null;

    private bool Selectable(int i) =>
        i >= 0 && i < items.Count && !items[i].IsHeader &&
        (items[i].Activate != null || items[i].Adjust != null);

    private void MoveSelection(int dir)
    {
        if (items.Count == 0)
            return;
        for (int n = 0; n < items.Count; n++)
        {
            selected = (selected + dir + items.Count) % items.Count;
            if (Selectable(selected))
                return;
        }
    }

    private void Refresh()
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Highlight != null)
                it.Highlight.color = i == selected ? GvMenuUi.Selected : new Color(0, 0, 0, 0);
            if (it.ValueText != null)
                it.ValueText.text = it.Value != null ? it.Value() : "";
            if (it.MinusBg != null)
                it.MinusBg.color = (i == hoverRow && hoverStep < 0) ? GvMenuUi.StepHot : GvMenuUi.Step;
            if (it.PlusBg != null)
                it.PlusBg.color = (i == hoverRow && hoverStep > 0) ? GvMenuUi.StepHot : GvMenuUi.Step;
        }

        var p = session != null ? session.Profile : null;
        bool up = session != null && session.Connected;
        statusText.text = p == null ? "no robot"
                        : string.Format("{0} at {1}   {2}", p.name, p.host,
                                        up ? "connected" : "disconnected");
        statusText.color = up ? GvMenuUi.Dim : GvMenuUi.Warn;
        hintText.text = pointer != null && pointer.Active && pointer.SourceKind == "hand"
            ? "Point and pinch your index finger   *   middle-finger pinch to resume"
            : "Point and pull the trigger   *   B / Y or Menu to resume";
    }
}
