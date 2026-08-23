using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// One selectable line in the menu: a label, an optional value on the right, and what
/// pressing or nudging it does.
///
/// Deliberately not a prefab. The whole menu is built in code so there is no scene
/// wiring to break, no inspector references to go missing, and changing the layout is
/// a diff rather than a binary asset.
/// </summary>
public sealed class GvMenuItem
{
    public string Label;
    public Func<string> Value;          // right-hand column; null for none
    public Action Activate;             // select button
    public Action<int> Adjust;          // left / right, -1 or +1
    public Func<bool> Enabled = () => true;
    public bool IsHeader;

    public RectTransform Row;
    public TextMeshProUGUI LabelText;
    public TextMeshProUGUI ValueText;
    public Image Highlight;

    // Pointer targets for Adjust rows. A stick nudges left/right, but a laser needs
    // something to actually hit, so adjustable rows grow a minus and a plus.
    public RectTransform Minus;
    public RectTransform Plus;
    public Image MinusBg;
    public Image PlusBg;
}

/// <summary>Small helpers for assembling a world-space menu at runtime.</summary>
public static class GvMenuUi
{
    public const float PixelsPerMetre = 1000f;

    public static readonly Color Background = new Color(0.05f, 0.06f, 0.08f, 0.92f);
    public static readonly Color Selected = new Color(0.20f, 0.45f, 0.75f, 0.95f);
    public static readonly Color Text = new Color(0.92f, 0.94f, 0.96f, 1f);
    public static readonly Color Dim = new Color(0.55f, 0.60f, 0.66f, 1f);
    public static readonly Color Accent = new Color(0.45f, 0.80f, 0.55f, 1f);
    public static readonly Color Warn = new Color(0.95f, 0.60f, 0.35f, 1f);
    public static readonly Color Step = new Color(1f, 1f, 1f, 0.10f);
    public static readonly Color StepHot = new Color(0.45f, 0.80f, 0.55f, 0.55f);
    public static readonly Color Hover = new Color(0.20f, 0.45f, 0.75f, 0.45f);

    public static Canvas CreateCanvas(Transform parent, string name, Vector2 sizePx)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas),
                                typeof(CanvasScaler), typeof(GraphicRaycaster));
        go.transform.SetParent(parent, false);
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rt = (RectTransform)go.transform;
        rt.sizeDelta = sizePx;
        // A world-space canvas is authored in pixels and shrunk to metres, so text
        // stays crisp without fighting tiny font sizes.
        rt.localScale = Vector3.one / PixelsPerMetre;

        // Unity's raycaster is left off: GvPointer intersects the canvas plane itself
        // and hit-tests the known row rects, which needs no EventSystem, no per-Graphic
        // raycast targets, and behaves identically for controllers, hands and the mouse.
        go.GetComponent<GraphicRaycaster>().enabled = false;
        return canvas;
    }

    public static Image Panel(Transform parent, Color color)
    {
        var go = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        Stretch(rt);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TextMeshProUGUI Label(Transform parent, string name, string text,
                                        float size, Color color,
                                        TextAlignmentOptions align = TextAlignmentOptions.Left)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                                typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Ellipsis;
        return t;
    }

    public static void Stretch(RectTransform rt, float left = 0, float right = 0,
                               float top = 0, float bottom = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>A full-width row anchored to the top of its parent at a given offset.</summary>
    public static RectTransform Row(Transform parent, string name, float y, float height)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(0f, 0f);
        rt.offsetMax = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(0f, -y);
        rt.sizeDelta = new Vector2(0f, height);
        return rt;
    }

    /// <summary>A small pill button pinned to the panel's top-right corner.</summary>
    public static RectTransform Chip(Transform parent, string name, string text, out Image bg)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-28f, -26f);
        rt.sizeDelta = new Vector2(150f, 52f);

        bg = go.GetComponent<Image>();
        bg.color = Step;
        bg.raycastTarget = false;

        var t = Label(rt, "T", text, 26f, Text, TextAlignmentOptions.Center);
        Stretch(t.rectTransform);
        return rt;
    }

    /// <summary>
    /// A slim scroll indicator down the right edge of the list. Read-only: it shows how
    /// much list there is and where you are in it, which is the part people actually
    /// need. Dragging it would be a poor target for a laser anyway.
    /// </summary>
    public static void Scrollbar(Transform parent, float top, float bottom,
                                 out RectTransform track, out RectTransform thumb)
    {
        var tg = new GameObject("ScrollTrack", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        tg.transform.SetParent(parent, false);
        track = (RectTransform)tg.transform;
        track.anchorMin = new Vector2(1f, 0f);
        track.anchorMax = new Vector2(1f, 1f);
        track.pivot = new Vector2(1f, 1f);
        track.offsetMin = new Vector2(-6f, bottom);
        track.offsetMax = new Vector2(-18f, -top);
        var ti = tg.GetComponent<Image>();
        ti.color = new Color(1f, 1f, 1f, 0.06f);
        ti.raycastTarget = false;

        var hg = new GameObject("ScrollThumb", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        hg.transform.SetParent(track, false);
        thumb = (RectTransform)hg.transform;
        thumb.anchorMin = new Vector2(0f, 1f);
        thumb.anchorMax = new Vector2(1f, 1f);
        thumb.pivot = new Vector2(0.5f, 1f);
        thumb.offsetMin = new Vector2(0f, 0f);
        thumb.offsetMax = new Vector2(0f, 0f);
        var hi = hg.GetComponent<Image>();
        hi.color = new Color(1f, 1f, 1f, 0.28f);
        hi.raycastTarget = false;
    }

    /// <summary>
    /// The minus / plus hit zones on an adjustable row, and the space left for its value.
    /// </summary>
    public static void Stepper(GvMenuItem item, float size = 52f, float gap = 8f)
    {
        item.Minus = Zone(item.Row, "-", "\u2212", size, gap + size + gap, out item.MinusBg);
        item.Plus = Zone(item.Row, "+", "+", size, gap, out item.PlusBg);
    }

    private static RectTransform Zone(Transform parent, string name, string glyph,
                                      float size, float rightInset, out Image bg)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-rightInset, 0f);
        rt.sizeDelta = new Vector2(size, size * 0.72f);

        bg = go.GetComponent<Image>();
        bg.color = Step;
        bg.raycastTarget = false;

        var t = Label(rt, "G", glyph, 28f, Text, TextAlignmentOptions.Center);
        Stretch(t.rectTransform);
        return rt;
    }

    /// <summary>
    /// Place a world-space panel in front of the viewer, level with the horizon.
    ///
    /// The forward direction is flattened: inheriting the head's pitch and roll puts a
    /// reading surface at an angle nobody chose, and a menu that is not level reads as
    /// the world being tilted.
    /// </summary>
    public static void PlaceInFront(Transform panel, Transform head, float distance,
                                    float heightOffset = 0f)
    {
        if (head == null)
            return;
        var forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        forward.Normalize();

        panel.position = head.position + forward * distance + Vector3.up * heightOffset;
        panel.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}
