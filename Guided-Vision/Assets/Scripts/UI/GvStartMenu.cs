using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The connection and configuration menu, built entirely in code at runtime.
///
/// Replaces the old Firestore StartScene. Robots on the LAN announce themselves
/// (gvlink/beacon.py) and appear here within a couple of seconds; anything else is
/// picked from the saved list or typed in. Over Tailscale a remote robot is just an
/// address like any other, so there is no separate "remote" concept to build.
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
    public float menuHeightOffset = -0.1f;

    [Header("Discovery")]
    public int beaconPort = 15550;

    [Tooltip("Switch off any canvas already in the scene. The menu builds its own, and " +
             "the scene this grew from still carries the old Firestore UI; disabling " +
             "it here beats surgery on the scene file, and costs nothing once that UI " +
             "is finally deleted.")]
    public bool hideExistingCanvases = true;

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
    private readonly Nav nav = new Nav();

    private GvPointer pointer;
    private int hoverRow = -1;
    private int hoverStep;              // -1 minus, +1 plus, 0 neither
    private bool pointerEngaged;
    private bool hoverBack;
    private string pageHint = "";
    private Vector2 lastPointerLocal;

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

        head = Camera.main != null ? Camera.main.transform : null;
        if (showPassthrough)
            GvPassthroughBackdrop.Ensure(gameObject);
        if (hideExistingCanvases)
            HideOtherCanvases();
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

    private void HideOtherCanvases()
    {
        int hidden = 0;
        foreach (var c in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude))
        {
            if (c.transform.IsChildOf(transform))
                continue;
            c.gameObject.SetActive(false);
            hidden++;
        }
        if (hidden > 0)
            Debug.Log($"GvStartMenu: hid {hidden} pre-existing canvas(es).");
    }

    private void BuildChrome()
    {
        canvas = GvMenuUi.CreateCanvas(transform, "GvStartMenu", new Vector2(900f, 720f));
        GvMenuUi.PlaceInFront(canvas.transform, head, menuDistance, menuHeightOffset);

        GvMenuUi.Panel(canvas.transform, GvMenuUi.Background);

        titleText = GvMenuUi.Label(canvas.transform, "Title", "Guided Vision", 42f, GvMenuUi.Text);
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
        AddFloat(p, "HUD distance", () => p.hudDistance, v => p.hudDistance = v, 0.1f, 0.5f, 10f, "{0:0.0} m");

        AddToggle(p, "Foveated streaming", () => p.foveation, v => p.foveation = v);
        AddToggle(p, "Outline the sharp patch", () => p.foveaOutline, v => p.foveaOutline = v);
        AddToggle(p, "Software decode (bring-up)", () => p.softwareVideo, v => p.softwareVideo = v);
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
        nav.Poll();
        UpdatePointer();

        // Whichever device was touched last owns the selection, so a laser resting on
        // one row does not fight the stick.
        if (nav.Any)
            pointerEngaged = false;

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

        var ct = (RectTransform)canvas.transform;
        var plane = new Plane(ct.forward, ct.position);
        float dist;
        if (!plane.Raycast(pointer.Aim, out dist))
        {
            pointer.SetHit(false, Vector3.zero);
            return;
        }

        Vector3 world = pointer.Aim.GetPoint(dist);
        Vector3 lp = ct.InverseTransformPoint(world);
        var local = new Vector2(lp.x, lp.y);
        bool inPanel = ct.rect.Contains(local);
        pointer.SetHit(inPanel, world);
        if (!inPanel)
            return;

        // A pointer that has not moved must not steal the selection from the stick.
        if ((local - lastPointerLocal).sqrMagnitude > 16f)
            pointerEngaged = true;
        lastPointerLocal = local;

        if (page != Page.Robots && Contains(backButton, world))
        {
            hoverBack = true;
            return;
        }

        // Rows are clipped to the list viewport, so a row scrolled up under the header
        // is still geometrically inside the panel. Require the hit to be in the viewport
        // too, or you can click something you cannot see.
        if (!Contains(listRoot, world))
            return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Row == null || !Selectable(i))
                continue;
            if (!Contains(it.Row, world))
                continue;
            hoverRow = i;
            if (it.Adjust != null)
            {
                if (Contains(it.Minus, world)) hoverStep = -1;
                else if (Contains(it.Plus, world)) hoverStep = 1;
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
        return "Stick / arrows to move   *   A / Enter to select";
    }

    private static bool Contains(RectTransform rt, Vector3 world)
    {
        if (rt == null)
            return false;
        Vector3 lp = rt.InverseTransformPoint(world);
        return rt.rect.Contains(new Vector2(lp.x, lp.y));
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

    /// <summary>
    /// Stick-or-keyboard navigation with auto-repeat.
    ///
    /// The stick is treated as a d-pad: an analogue axis feeding a list produces either
    /// a runaway scroll or a dead zone nobody can tune, so it is quantised and repeated
    /// on a timer instead.
    /// </summary>
    private sealed class Nav
    {
        private const float Deadzone = 0.55f;
        private const float FirstRepeat = 0.42f;
        private const float NextRepeat = 0.13f;

        public bool Up, Down, Left, Right, Select, Back;

        /// <summary>Any stick or key input this frame -- used to hand the selection
        /// back from the pointer to the stick.</summary>
        public bool Any => Up || Down || Left || Right || Select || Back;

        private Vector2 held;
        private float repeatAt;

        public void Poll()
        {
            Up = Down = Left = Right = Select = Back = false;

            Vector2 axis = Vector2.zero;
            try
            {
                axis = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick)
                     + OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            }
            catch (Exception)
            {
                // No OVR runtime (plain Editor play mode); keys still work.
            }

            var dir = new Vector2(
                Mathf.Abs(axis.x) > Deadzone ? Mathf.Sign(axis.x) : 0f,
                Mathf.Abs(axis.y) > Deadzone ? Mathf.Sign(axis.y) : 0f);
            // A diagonal push should not move two ways at once; the larger wins.
            if (dir.x != 0f && dir.y != 0f)
            {
                if (Mathf.Abs(axis.x) >= Mathf.Abs(axis.y)) dir.y = 0f;
                else dir.x = 0f;
            }

            bool fresh = dir != held;
            held = dir;
            if (fresh)
                repeatAt = Time.unscaledTime + FirstRepeat;

            bool fire = dir != Vector2.zero && (fresh || Time.unscaledTime >= repeatAt);
            if (fire && !fresh)
                repeatAt = Time.unscaledTime + NextRepeat;

            if (fire)
            {
                Up = dir.y > 0f;
                Down = dir.y < 0f;
                Right = dir.x > 0f;
                Left = dir.x < 0f;
            }

            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Up = true;
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Down = true;
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) Left = true;
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) Right = true;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)) Select = true;
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) Back = true;

            try
            {
                if (OVRInput.GetDown(OVRInput.Button.One)) Select = true;
                if (OVRInput.GetDown(OVRInput.Button.Two)) Back = true;
            }
            catch (Exception)
            {
            }
        }
    }
}
