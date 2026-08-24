using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The connection and configuration menu, built entirely in code at runtime.
///
/// Robots on the LAN announce themselves (gvlink/beacon.py) and appear here within a
/// couple of seconds; anything else is picked from the saved list or typed in. Over
/// Tailscale a remote robot is just an address like any other, so there is no separate
/// "remote" concept to build.
///
/// Three ways in, all driving the same single selection so they can never disagree:
/// a laser from either controller or either tracked hand, the thumbstick as a d-pad,
/// and arrow keys plus Enter. Pointing is what people reach for first in a headset, so
/// it is there; the stick stays because it is the one that still works when a hand is
/// out of tracking range or you are holding something, which is exactly the situation
/// this menu exists for. The keyboard path keeps the whole menu drivable in the Editor.
///
/// Hover moves the selection rather than living beside it, so there is one highlighted
/// row and never a hover and a selection arguing about which is "current". Whichever
/// device the user touched last wins, the way a mouse and keyboard share a desktop app.
/// </summary>
public class GvStartMenu : MonoBehaviour
{
    [Header("Scenes")]
    public string passthroughSceneName = "GvPassthroughScene";

    [Header("Placement")]
    public float menuDistance = 1.6f;

    [Tooltip("Metres below eye level. The menu is placed relative to where you are " +
             "actually looking, so this is an offset from the eye, not from the floor.")]
    public float menuHeightOffset = -0.1f;

    [Tooltip("Bring the menu back in front of you when it has been this far outside " +
             "your view for a moment. Without it, a menu placed before tracking settles " +
             "-- or left behind when you turn around -- is simply lost.")]
    public float recenterAngle = 42f;
    public float recenterDelay = 0.9f;

    [Header("Discovery")]
    public int beaconPort = 15550;

    [Tooltip("Show the room behind the menu. This is the moment the headset has just " +
             "gone on and nothing has been drawn yet; a black void there is where people " +
             "walk into furniture. Falls back to a solid colour where passthrough is " +
             "unavailable.")]
    public bool showPassthrough = true;

    private enum Page { Robots, Settings, Address }

    private const float RowHeight = 46f;
    private const float ListTop = 150f;

    private static readonly string[] Keys =
        { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "<-" };

    private GvConfig config;
    private GvDiscovery discovery;
    private Transform head;

    private Canvas canvas;
    private RectTransform listRoot;
    private RectTransform content;
    private RectTransform backButton;
    private Image backBg;
    private RectTransform scrollTrack;
    private RectTransform scrollThumb;
    private float scroll;
    private TextMeshProUGUI titleText;
    private TextMeshProUGUI statusText;
    private TextMeshProUGUI hintText;

    private readonly List<GvMenuItem> items = new List<GvMenuItem>();
    private readonly GvNav nav = new GvNav();

    private GvPointer pointer;
    private int hoverRow = -1;
    private int hoverStep;              // -1 minus, +1 plus, 0 neither
    private bool pointerEngaged;
    private bool hoverBack;
    private string pageHint = "";
    private Vector2 lastPointerLocal;

    private bool placed;
    private float outOfViewFor;
    private float waitingForPose;
    private const float PoseTimeout = 0.5f;

    private Page page = Page.Robots;
    private int selected;
    private float rebuildTimer;
    private string typedHost = "";
    private int keyIndex;

    // ------------------------------------------------------------------ lifecycle

    private void Start()
    {
        config = GvConfig.Load();
        discovery = new GvDiscovery();
        discovery.Start(beaconPort);

        head = GvXr.Head();
        if (showPassthrough)
            GvPassthroughBackdrop.Ensure(gameObject);
        BuildChrome();

        // Built here rather than wired in the scene, for the same reason the menu is:
        // nothing to go missing, and the two always match.
        pointer = gameObject.AddComponent<GvPointer>();
        pointer.head = head;
        pointer.target = (RectTransform)canvas.transform;

        GoTo(Page.Robots);
    }

    private void OnDestroy()
    {
        discovery?.Dispose();
        discovery = null;
    }

    private void BuildChrome()
    {
        canvas = GvMenuUi.CreateCanvas(transform, "GvStartMenu", new Vector2(900f, 720f));
        // Deliberately NOT placed here. At Start the XR runtime has not yet written a
        // head pose, so the camera still sits wherever the rig was authored -- and with
        // a floor-level tracking origin that is the floor, not eye height. Placing now
        // puts the menu about 1.7 m below where the wearer's eyes end up, which reads as
        // the UI simply not existing. Place on the first frame with a real pose instead.

        GvMenuUi.Panel(canvas.transform, GvMenuUi.Background);

        titleText = GvMenuUi.Label(canvas.transform, "Title", "Quest VR Teleoperation", 42f, GvMenuUi.Text);
        var trt = titleText.rectTransform;
        trt.anchorMin = new Vector2(0f, 1f);
        trt.anchorMax = new Vector2(1f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(30f, -28f);
        trt.sizeDelta = new Vector2(-60f, 50f);

        statusText = GvMenuUi.Label(canvas.transform, "Status", "", 24f, GvMenuUi.Dim);
        var srt = statusText.rectTransform;
        srt.anchorMin = new Vector2(0f, 1f);
        srt.anchorMax = new Vector2(1f, 1f);
        srt.pivot = new Vector2(0.5f, 1f);
        srt.anchoredPosition = new Vector2(30f, -86f);
        srt.sizeDelta = new Vector2(-60f, 32f);

        hintText = GvMenuUi.Label(canvas.transform, "Hint", "", 22f, GvMenuUi.Dim);
        var hrt = hintText.rectTransform;
        hrt.anchorMin = new Vector2(0f, 0f);
        hrt.anchorMax = new Vector2(1f, 0f);
        hrt.pivot = new Vector2(0.5f, 0f);
        hrt.anchoredPosition = new Vector2(30f, 20f);
        hrt.sizeDelta = new Vector2(-60f, 30f);

        // Back lives in the chrome, not at the end of the list. It was the last row of a
        // list twelve rows long in a viewport that fits nine, so it rendered below the
        // panel entirely -- off the background and, because the pointer only tests inside
        // the panel, unclickable. A control that leaves the page must not be able to
        // scroll off it.
        backButton = GvMenuUi.Chip(canvas.transform, "Back", "\u2039  Back", out backBg);

        var lgo = new GameObject("List", typeof(RectTransform));
        lgo.transform.SetParent(canvas.transform, false);
        listRoot = (RectTransform)lgo.transform;
        GvMenuUi.Stretch(listRoot, 24f, 34f, ListTop, 60f);
        lgo.AddComponent<RectMask2D>();     // rows past the bottom are clipped, not spilled

        var cgo = new GameObject("Content", typeof(RectTransform));
        cgo.transform.SetParent(listRoot, false);
        content = (RectTransform)cgo.transform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = new Vector2(0f, 0f);
        content.offsetMax = new Vector2(0f, 0f);

        GvMenuUi.Scrollbar(canvas.transform, ListTop, 60f, out scrollTrack, out scrollThumb);
    }

    // ---------------------------------------------------------------------- pages

    private void GoTo(Page next)
    {
        if (page == Page.Settings && next != Page.Settings)
            config.Save();      // one write per visit, not one per nudge
        page = next;
        selected = 0;
        scroll = 0f;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var it in items)
            if (it.Row != null)
                Destroy(it.Row.gameObject);
        items.Clear();

        switch (page)
        {
            case Page.Robots: BuildRobots(); break;
            case Page.Settings: BuildSettings(); break;
            case Page.Address: BuildAddress(); break;
        }

        float y = 0f;
        foreach (var it in items)
        {
            it.Row = GvMenuUi.Row(content, it.Label, y, RowHeight);
            y += RowHeight + 4f;

            it.Highlight = GvMenuUi.Panel(it.Row, new Color(0, 0, 0, 0));
            it.LabelText = GvMenuUi.Label(it.Row, "L", it.Label, it.IsHeader ? 24f : 28f,
                                          it.IsHeader ? GvMenuUi.Dim : GvMenuUi.Text);
            GvMenuUi.Stretch(it.LabelText.rectTransform, 16f, 300f);
            it.ValueText = GvMenuUi.Label(it.Row, "V", "", 26f, GvMenuUi.Accent,
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

        content.sizeDelta = new Vector2(0f, Mathf.Max(0f, y - 4f));

        selected = Mathf.Clamp(selected, 0, Mathf.Max(0, items.Count - 1));
        if (!Selectable(selected))
            MoveSelection(1);
        Refresh();
    }

    private void BuildRobots()
    {
        titleText.text = "Connect";
        pageHint = "";

        var found = discovery.Snapshot();
        items.Add(new GvMenuItem { Label = "ON THIS NETWORK", IsHeader = true });
        if (found.Count == 0)
        {
            items.Add(new GvMenuItem
            {
                Label = "  searching...",
                IsHeader = true,
                Value = () => discovery.Running ? "" : "listener failed",
            });
        }
        foreach (var b in found)
        {
            var beacon = b;
            var cam = beacon.PrimaryCamera;
            items.Add(new GvMenuItem
            {
                Label = "  " + beacon.name,
                Value = () => cam != null
                    ? $"{beacon.SourceAddress}   {cam.w}x{cam.h}@{cam.fps} {cam.codec}"
                    : beacon.SourceAddress,
                Activate = () => Connect(beacon),
            });
        }

        var saved = config.robots;
        if (saved.Count > 0)
        {
            items.Add(new GvMenuItem { Label = "SAVED", IsHeader = true });
            foreach (var p in saved)
            {
                var profile = p;
                items.Add(new GvMenuItem
                {
                    Label = "  " + profile.name,
                    Value = () => $"{profile.host}:{profile.videoPort}",
                    Activate = () => Connect(profile),
                });
            }
        }

        items.Add(new GvMenuItem { Label = "", IsHeader = true });
        items.Add(new GvMenuItem
        {
            Label = "Enter an address",
            Activate = () => { typedHost = ""; keyIndex = 0; GoTo(Page.Address); },
        });
        items.Add(new GvMenuItem
        {
            Label = "Display settings",
            Activate = () => GoTo(Page.Settings),
        });
    }

    private void BuildSettings()
    {
        titleText.text = "Display settings";
        pageHint = "B / Esc to go back";

        var p = config.Last ?? config.GetOrCreate("default");

        AddFloat(p, "Video vertical FOV", () => p.videoVFOV, v => p.videoVFOV = v, 1f, 10f, 170f, "{0:0}°");
        AddFloat(p, "Stereo separation", () => p.stereoSeparationDeg, v => p.stereoSeparationDeg = v, 0.1f, -20f, 20f, "{0:0.0}°");
        AddFloat(p, "Vertical trim", () => p.stereoVerticalTrimDeg, v => p.stereoVerticalTrimDeg = v, 0.1f, -20f, 20f, "{0:0.0}°");
        AddFloat(p, "Magnification", () => p.videoScale, v => p.videoScale = v, 0.05f, 0.2f, 3f, "{0:0.00}x");
        AddFloat(p, "Plane distance", () => p.videoPlaneDistance, v => p.videoPlaneDistance = v, 0.1f, 0.3f, 10f, "{0:0.0} m");
        AddFloat(p, "Edge feather", () => p.edgeFeather, v => p.edgeFeather = v, 0.01f, 0f, 0.5f, "{0:0.00}");
        AddFloat(p, "Outer edge mask", () => p.outerEdgeMask, v => p.outerEdgeMask = v, 0.01f, 0f, 0.5f, "{0:0.00}");
        AddFloat(p, "Foveal blend", () => p.foveaFeather, v => p.foveaFeather = v, 0.01f, 0.01f, 0.5f, "{0:0.00}");
        items.Add(new GvMenuItem { Label = "Stream", IsHeader = true });
        items.Add(new GvMenuItem
        {
            Label = "Video resolution",
            Value = () => string.Format("{0}x{1}", p.canvasWidth, p.canvasHeight),
            // Stepped through fixed sizes rather than nudged: this one configures the
            // hardware decoder at session start, so arbitrary values buy nothing and
            // odd sizes are exactly what decoders are fussy about.
            Adjust = dir => CycleCanvas(p, dir),
            Activate = () => CycleCanvas(p, 1),
        });
        AddFloat(p, "Peripheral detail", () => p.coarseScale, v => p.coarseScale = v,
                 0.05f, 0.15f, 1f, "{0:0.00}");
        AddFloat(p, "Sharp patch size", () => p.foveaScale, v => p.foveaScale = v,
                 0.05f, 0.25f, 1f, "{0:0.00}");

        AddFloat(p, "HUD distance", () => p.hudDistance, v => p.hudDistance = v, 0.1f, 0.5f, 10f, "{0:0.0} m");

        // Shown either way, but honest about it: hiding a setting the hardware cannot
        // do leaves someone hunting for a feature they read about.
        if (GvXr.EyeTrackingAvailable)
        {
            AddToggle(p, "Foveated streaming", () => p.foveation, v => p.foveation = v);
        }
        else
        {
            items.Add(new GvMenuItem
            {
                Label = "Foveated streaming",
                Value = () => "needs eye tracking",
                Enabled = () => false,
                Activate = () => { },
            });
        }
        AddToggle(p, "Outline the sharp patch", () => p.foveaOutline, v => p.foveaOutline = v);
        AddToggle(p, "Software decode (bring-up)", () => p.softwareVideo, v => p.softwareVideo = v);
    }

    private static readonly int[] CanvasSizes = { 512, 640, 768, 896, 1024, 1280 };

    /// <summary>
    /// Step the transmitted canvas through sizes a hardware decoder is happy with.
    ///
    /// Bigger is more detail and more bandwidth, and costs encode and decode time on
    /// both ends -- most of a canvas is the two layers, so this is the one control that
    /// moves everything at once.
    /// </summary>
    private static void CycleCanvas(GvRobotProfile p, int dir)
    {
        int at = System.Array.IndexOf(CanvasSizes, p.canvasWidth);
        if (at < 0)
            at = System.Array.IndexOf(CanvasSizes, 1024);
        at = Mathf.Clamp(at + (dir >= 0 ? 1 : -1), 0, CanvasSizes.Length - 1);
        p.canvasWidth = p.canvasHeight = CanvasSizes[at];
    }

    private void AddToggle(GvRobotProfile profile, string label,
                           Func<bool> get, Action<bool> set)
    {
        items.Add(new GvMenuItem
        {
            Label = label,
            Value = () => get() ? "on" : "off",
            // Both, so it responds to a stick nudge and to a click on either stepper.
            Adjust = _ => set(!get()),
            Activate = () => set(!get()),
        });
    }

    private void AddFloat(GvRobotProfile profile, string label, Func<float> get,
                          Action<float> set, float step, float min, float max, string fmt)
    {
        items.Add(new GvMenuItem
        {
            Label = label,
            Value = () => string.Format(fmt, get()),
            Adjust = dir => set(Mathf.Clamp(
                Mathf.Round((get() + dir * step) / step) * step, min, max)),
        });
    }

    private void BuildAddress()
    {
        titleText.text = "Enter an address";
        pageHint = "A to type   *   B to go back";

        items.Add(new GvMenuItem
        {
            Label = "Address",
            Value = () => string.IsNullOrEmpty(typedHost) ? "_" : typedHost,
            IsHeader = true,
        });
        items.Add(new GvMenuItem
        {
            Label = "Key",
            Value = () => BuildKeyStrip(),
            Adjust = dir => keyIndex = (keyIndex + dir + Keys.Length) % Keys.Length,
            Activate = PressKey,
        });
        items.Add(new GvMenuItem { Label = "", IsHeader = true });
        items.Add(new GvMenuItem
        {
            Label = "Connect",
            Value = () => IsPlausibleHost(typedHost) ? "" : "incomplete",
            Enabled = () => IsPlausibleHost(typedHost),
            Activate = () =>
            {
                var p = config.GetOrCreate(typedHost);
                p.host = typedHost;
                Connect(p);
            },
        });
    }

    private string BuildKeyStrip()
    {
        var sb = new System.Text.StringBuilder(64);
        for (int i = 0; i < Keys.Length; i++)
        {
            if (i == keyIndex)
                sb.Append('[').Append(Keys[i]).Append(']');
            else
                sb.Append(' ').Append(Keys[i]).Append(' ');
        }
        return sb.ToString();
    }

    private void PressKey()
    {
        string k = Keys[keyIndex];
        if (k == "<-")
        {
            if (typedHost.Length > 0)
                typedHost = typedHost.Substring(0, typedHost.Length - 1);
        }
        else if (typedHost.Length < 45)
        {
            typedHost += k;
        }
    }

    /// <summary>Enough of an address to be worth trying. Not a validator.</summary>
    private static bool IsPlausibleHost(string s) =>
        !string.IsNullOrEmpty(s) && s.Length >= 7 && s.IndexOf('.') > 0;

    // ------------------------------------------------------------------ connecting

    private void Connect(GvBeacon beacon)
    {
        var p = config.GetOrCreate(beacon.name);
        beacon.ApplyTo(p);
        Connect(p);
    }

    private void Connect(GvRobotProfile profile)
    {
        config.lastRobot = profile.name;
        if (!config.Save())
        {
            statusText.text = "could not save settings; connecting anyway";
            statusText.color = GvMenuUi.Warn;
        }
        Debug.Log($"GvStartMenu: connecting to {profile.name} at {profile.host}:{profile.videoPort}");
        SceneManager.LoadScene(passthroughSceneName);
    }

    // ----------------------------------------------------------------------- input

    private void Update()
    {
        KeepInView();
        nav.Poll();
        UpdatePointer();

        // Whichever device was touched last owns the selection, so a laser resting on
        // one row does not fight the stick.
        if (nav.Any)
            pointerEngaged = false;

        if (nav.Recenter)
            Recenter();
        if (nav.Back)
        {
            if (page != Page.Robots)
                GoTo(Page.Robots);
        }
        if (nav.Down) MoveSelection(1);
        if (nav.Up) MoveSelection(-1);

        var item = Current();
        if (item != null)
        {
            if (nav.Right) item.Adjust?.Invoke(1);
            if (nav.Left) item.Adjust?.Invoke(-1);
            if (nav.Select && item.Enabled())
                item.Activate?.Invoke();
        }

        HandlePointerClick();
        UpdateScroll();

        // The discovery list changes on its own, so the robot page is rebuilt on a
        // timer. Other pages are static and would only flicker the selection.
        if (page == Page.Robots)
        {
            rebuildTimer += Time.unscaledDeltaTime;
            if (rebuildTimer >= 1.0f)
            {
                rebuildTimer = 0f;
                int keep = selected;
                Rebuild();
                selected = Mathf.Clamp(keep, 0, Mathf.Max(0, items.Count - 1));
                if (!Selectable(selected))
                    MoveSelection(1);
            }
        }

        Refresh();
    }

    /// <summary>
    /// Put the menu where the wearer is looking, and keep it findable.
    ///
    /// Two problems, one answer. The first frame's head pose is not real -- the runtime
    /// has not written one yet -- so the initial placement waits for a pose that has
    /// actually moved. The second is that a world-locked panel is easy to lose: turn
    /// around, or start the app facing a wall, and it is behind you with no way to know
    /// which way. So if it sits well outside the view for a moment, it comes back.
    ///
    /// The delay matters. Re-centring the instant it leaves the view would drag the
    /// panel along with every glance, which is far more unpleasant than losing it.
    /// </summary>
    private void KeepInView()
    {
        if (head == null)
        {
            head = GvXr.Head();
            if (head == null)
                return;
        }
        if (pointer != null && pointer.head == null)
            pointer.head = head;

        if (!placed)
        {
            // A pose of exactly zero means the runtime has not written one yet. Waiting
            // for any non-trivial pose costs a frame or two and is the difference
            // between the menu appearing in front of you and appearing under the floor.
            //
            // The timeout is not belt-and-braces: in the Editor with no headset the
            // camera legitimately sits at the origin and never moves, so waiting for a
            // pose that will never arrive would mean no menu at all.
            waitingForPose += Time.unscaledDeltaTime;
            bool posed = head.position != Vector3.zero || head.rotation != Quaternion.identity;
            if (!posed && waitingForPose < PoseTimeout)
                return;
            Recenter();
            return;
        }

        Vector3 toMenu = canvas.transform.position - head.position;
        if (toMenu.sqrMagnitude < 1e-4f)
            return;
        float angle = Vector3.Angle(head.forward, toMenu);
        outOfViewFor = angle > recenterAngle ? outOfViewFor + Time.unscaledDeltaTime : 0f;
        if (outOfViewFor >= recenterDelay)
            Recenter();
    }

    /// <summary>Bring the panel back in front of the wearer, level with the horizon.</summary>
    public void Recenter()
    {
        if (head == null || canvas == null)
            return;
        GvMenuUi.PlaceInFront(canvas.transform, head, menuDistance, menuHeightOffset);
        placed = true;
        outOfViewFor = 0f;
    }

    /// <summary>
    /// Intersect the pointer with the menu panel and work out what it is over.
    ///
    /// Done against the known row rects rather than through Unity's GraphicRaycaster:
    /// there is no EventSystem to configure, no raycast targets to keep enabled, and
    /// controllers, hands and the Editor mouse all arrive as the same world-space ray.
    /// </summary>
    private void UpdatePointer()
    {
        hoverRow = -1;
        hoverStep = 0;
        hoverBack = false;
        if (pointer == null || canvas == null || !pointer.Active)
            return;

        Vector3 world;
        Vector2 local;
        bool inPanel = GvMenuUi.RayHit((RectTransform)canvas.transform, pointer.Aim,
                                       out world, out local);
        pointer.SetHit(inPanel, world);
        if (!inPanel)
            return;

        // A pointer that has not moved must not steal the selection from the stick.
        if ((local - lastPointerLocal).sqrMagnitude > 16f)
            pointerEngaged = true;
        lastPointerLocal = local;

        if (page != Page.Robots && GvMenuUi.Contains(backButton, world))
        {
            hoverBack = true;
            return;
        }

        // Rows are clipped to the list viewport, so a row scrolled up under the header
        // is still geometrically inside the panel. Require the hit to be in the viewport
        // too, or you can click something you cannot see.
        if (!GvMenuUi.Contains(listRoot, world))
            return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Row == null || !Selectable(i))
                continue;
            if (!GvMenuUi.Contains(it.Row, world))
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
        if (pointer == null || !pointer.ClickDown)
            return;
        if (hoverBack)
        {
            pointer.PulseCurrent(0.7f, 0.06f);
            GoTo(Page.Robots);
            return;
        }
        if (hoverRow < 0)
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

    /// <summary>
    /// Tell the user what the thing currently in their hands does, rather than listing
    /// every binding. Nobody reads a legend of six controls; they read the one line that
    /// matches what they are holding.
    /// </summary>
    private string InputHint()
    {
        if (pointer != null && pointer.Active)
        {
            switch (pointer.SourceKind)
            {
                case "hand": return "Point and pinch";
                case "controller": return "Point and pull the trigger";
                case "mouse": return "Point and click";
            }
        }
        return "Stick / arrows to move   *   A / Enter to select   *   click a stick to recentre";
    }

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

    /// <summary>
    /// Keep the selected row inside the viewport. Scrolling is driven by the selection
    /// rather than by a drag, because every input here -- stick, keys, laser -- moves a
    /// selection, and a list that follows it never needs a scroll gesture at all.
    /// </summary>
    private void UpdateScroll()
    {
        if (content == null || listRoot == null)
            return;
        float view = listRoot.rect.height;
        float total = content.sizeDelta.y;
        float max = Mathf.Max(0f, total - view);

        if (selected >= 0 && selected < items.Count)
        {
            float step = RowHeight + 4f;
            float top = selected * step;
            scroll = Mathf.Clamp(scroll, top + RowHeight - view, top);
        }
        scroll = Mathf.Clamp(scroll, 0f, max);
        content.anchoredPosition = new Vector2(0f, scroll);

        bool overflow = max > 0.5f;
        if (scrollTrack != null)
            scrollTrack.gameObject.SetActive(overflow);
        if (overflow && scrollThumb != null)
        {
            float h = Mathf.Max(28f, view * view / Mathf.Max(total, 1f));
            float y = (view - h) * (scroll / max);
            scrollThumb.sizeDelta = new Vector2(0f, h);
            scrollThumb.anchoredPosition = new Vector2(0f, -y);
        }
    }

    private void Refresh()
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            bool on = i == selected;
            if (it.Highlight != null)
                it.Highlight.color = on ? GvMenuUi.Selected : new Color(0, 0, 0, 0);
            if (it.LabelText != null)
                it.LabelText.color = it.IsHeader ? GvMenuUi.Dim
                    : (it.Enabled() ? GvMenuUi.Text : GvMenuUi.Dim);
            if (it.ValueText != null)
                it.ValueText.text = it.Value != null ? it.Value() : "";
            if (it.MinusBg != null)
                it.MinusBg.color = (i == hoverRow && hoverStep < 0) ? GvMenuUi.StepHot : GvMenuUi.Step;
            if (it.PlusBg != null)
                it.PlusBg.color = (i == hoverRow && hoverStep > 0) ? GvMenuUi.StepHot : GvMenuUi.Step;
        }

        if (backButton != null)
        {
            backButton.gameObject.SetActive(page != Page.Robots);
            if (backBg != null)
                backBg.color = hoverBack ? GvMenuUi.Hover : GvMenuUi.Step;
        }

        hintText.text = string.IsNullOrEmpty(pageHint)
            ? InputHint()
            : InputHint() + "   *   " + pageHint;

        if (page == Page.Robots)
        {
            int n = discovery.Snapshot().Count;
            statusText.text = n == 0
                ? $"listening for robots on :{beaconPort}"
                : $"{n} robot{(n == 1 ? "" : "s")} on this network";
        }
        else
        {
            statusText.text = config.lastRobot ?? "";
        }
    }
}
