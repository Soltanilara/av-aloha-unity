using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Transient messages in the operator's view: connection changes, and whatever the robot
/// has to say.
///
/// It follows the head *lazily* rather than being rigidly locked to it. A panel welded to
/// the head moves with every micro-saccade and small head tremor, which the vestibular
/// system reads as the world being unstable -- head-locked UI is one of the reliable ways
/// to make someone ill in a headset. Easing toward the head's direction, and only once it
/// has drifted far enough to matter, keeps it findable without ever moving with you.
///
/// Deliberately not the menu. A message is something you read while continuing to work;
/// a menu is something you stop to use.
/// </summary>
public class GvToast : MonoBehaviour
{
    [Tooltip("Metres in front of the viewer.")]
    public float distance = 1.2f;

    [Tooltip("Below the horizon, in metres at that distance. Out of the way of the work, " +
             "still inside the comfortable downward gaze range.")]
    public float heightOffset = -0.32f;

    [Tooltip("Degrees the head may turn before the panel starts following. Below this it " +
             "stays put, so reading it never involves chasing it.")]
    public float followDeadzoneDeg = 14f;

    [Tooltip("Seconds to catch up once it does follow. Slow on purpose.")]
    public float followTime = 0.45f;

    public int maxVisible = 3;

    private const int SortingOrder = 210;
    private const float PanelWidth = 620f;
    private const float RowHeight = 46f;

    private sealed class Entry
    {
        public string Text;
        public Color Colour;
        public float Until;
        public TextMeshProUGUI Label;
    }

    private readonly List<Entry> entries = new List<Entry>();
    private Canvas canvas;
    private RectTransform panel;
    private UnityEngine.UI.Image background;
    private Transform head;
    private Vector3 followForward;
    private bool placed;

    private static GvToast instance;

    /// <summary>Post a message from anywhere, creating the display if needed.</summary>
    public static void Post(string text, string severity = "info", float seconds = 3f)
    {
        if (instance == null)
        {
            instance = FindAnyObjectByType<GvToast>(FindObjectsInactive.Include);
            if (instance == null)
            {
                var go = new GameObject("GvToast");
                instance = go.AddComponent<GvToast>();
            }
        }
        instance.Show(text, severity, seconds);
    }

    private void Awake()
    {
        if (instance == null)
            instance = this;
        head = GvXr.Head();
        Build();
    }

    private void Build()
    {
        canvas = GvMenuUi.CreateCanvas(transform, "GvToastCanvas",
                                       new Vector2(PanelWidth, RowHeight), SortingOrder);
        panel = (RectTransform)canvas.transform;
        background = GvMenuUi.Panel(panel, new Color(0.05f, 0.06f, 0.08f, 0.85f));
        canvas.gameObject.SetActive(false);
    }

    /// <summary>Queue a message. Severity is info | warn | error.</summary>
    public void Show(string text, string severity = "info", float seconds = 3f)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var colour = severity == "error" ? new Color(0.98f, 0.45f, 0.40f)
                   : severity == "warn" ? GvMenuUi.Warn
                   : GvMenuUi.Text;

        var label = GvMenuUi.Label(panel, "Row", text, 26f, colour,
                                   TextAlignmentOptions.Center);
        entries.Add(new Entry
        {
            Text = text,
            Colour = colour,
            Until = Time.unscaledTime + Mathf.Max(0.5f, seconds),
            Label = label,
        });
        // Oldest first out, so a burst of messages does not push the newest off screen.
        while (entries.Count > Mathf.Max(1, maxVisible))
        {
            if (entries[0].Label != null)
                Destroy(entries[0].Label.gameObject);
            entries.RemoveAt(0);
        }
        if (severity == "error")
            Pulse();
        Relayout();
    }

    /// <summary>Remove everything immediately -- used when leaving a session.</summary>
    public void Clear()
    {
        foreach (var e in entries)
            if (e.Label != null)
                Destroy(e.Label.gameObject);
        entries.Clear();
        Relayout();
    }

    private static void Pulse()
    {
        try
        {
            OVRInput.SetControllerVibration(0.5f, 0.5f, OVRInput.Controller.Active);
        }
        catch (System.Exception)
        {
            // No runtime; a message with no buzz is still a message.
        }
    }

    private void Relayout()
    {
        int n = entries.Count;
        if (canvas != null)
            canvas.gameObject.SetActive(n > 0);
        if (n == 0)
            return;

        panel.sizeDelta = new Vector2(PanelWidth, n * RowHeight + 16f);
        for (int i = 0; i < n; i++)
        {
            var rt = entries[i].Label.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(14f, 0f);
            rt.offsetMax = new Vector2(-14f, 0f);
            rt.anchoredPosition = new Vector2(0f, -(8f + i * RowHeight));
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, RowHeight);
        }
    }

    private void Update()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime < entries[i].Until)
                continue;
            if (entries[i].Label != null)
                Destroy(entries[i].Label.gameObject);
            entries.RemoveAt(i);
            Relayout();
        }
        if (entries.Count == 0)
            return;

        if (head == null)
        {
            head = GvXr.Head();
            if (head == null)
                return;
        }
        Follow();
    }

    private void Follow()
    {
        Vector3 want = head.forward;
        want.y = 0f;
        if (want.sqrMagnitude < 1e-4f)
            want = Vector3.forward;
        want.Normalize();

        if (!placed)
        {
            followForward = want;
            placed = true;
        }
        else if (Vector3.Angle(followForward, want) > followDeadzoneDeg)
        {
            // Ease, never snap. The deadzone decides *whether* to move; this decides how,
            // and a panel that jumps to a new heading is its own comfort problem.
            followForward = Vector3.Slerp(followForward, want,
                                          1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.05f, followTime)));
        }

        panel.position = head.position + followForward * distance + Vector3.up * heightOffset;
        panel.rotation = Quaternion.LookRotation(followForward, Vector3.up);
        panel.localScale = Vector3.one * (distance / 1.2f / GvMenuUi.PixelsPerMetre);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }
}
