using UnityEngine;
using TMPro;

/// <summary>How much of the telemetry to show.</summary>
public enum GvHudMode
{
    /// <summary>Nothing. Teleoperation with no instrumentation in the way.</summary>
    Off = 0,
    /// <summary>One line, low and to the left: link, rate, throughput, refresh.</summary>
    Compact = 1,
    /// <summary>Everything -- per-eye decode, loss, gaze, frame timing.</summary>
    Full = 2,
}

/// <summary>
/// The telemetry readout.
///
/// Replaces the scene-authored DebugCanvas, which sat in the middle of the view at a
/// size chosen for reading a debug log on a monitor. In a headset the centre is where
/// the work is; instrumentation belongs at the edge of vision, small, and switchable
/// off entirely -- the whole point of the video is to look at it.
///
/// Placement follows the same lazy rule as <see cref="GvToast"/>: it stays put until the
/// head has turned far enough that it would otherwise leave the view, then eases across.
/// A readout welded to the head moves with every tremor, which is both hard to read and
/// a reliable way to make someone ill.
///
/// Built in code for the same reason the menus are: no scene wiring to go missing, and
/// changing the layout is a diff rather than a binary asset.
/// </summary>
public class GvHud : MonoBehaviour
{
    [Tooltip("Metres in front of the viewer. Far enough not to force a vergence change " +
             "away from the video plane every time you glance at it.")]
    public float distance = 1.8f;

    [Tooltip("Left and down from centre, in metres at that distance. Down-and-left is " +
             "where a readout belongs: out of the manipulation area, and the direction " +
             "the eyes drop to most comfortably.")]
    public float lateralOffset = -0.42f;
    public float heightOffset = -0.34f;

    public float followDeadzoneDeg = 18f;
    public float followTime = 0.5f;

    private const int SortingOrder = 190;          // above the video, below the menu
    private const float PanelWidth = 620f;
    private const float Pad = 16f;
    private const float ReferenceDistance = 1.8f;

    private Canvas canvas;
    private RectTransform panel;
    private UnityEngine.UI.Image background;
    private TextMeshProUGUI label;
    private Transform head;
    private Vector3 followForward;
    private bool placed;
    private string lastText = "";

    private GvHudMode mode = GvHudMode.Compact;

    public GvHudMode Mode
    {
        get => mode;
        set
        {
            mode = value;
            if (canvas != null)
                canvas.gameObject.SetActive(mode != GvHudMode.Off);
        }
    }

    public GvHudMode Cycle(int dir)
    {
        int n = System.Enum.GetValues(typeof(GvHudMode)).Length;
        Mode = (GvHudMode)(((int)mode + dir + n) % n);
        return mode;
    }

    private void Awake()
    {
        head = GvXr.Head();
        Build();
        Mode = mode;
    }

    private void Build()
    {
        canvas = GvMenuUi.CreateCanvas(transform, "GvHudCanvas",
                                       new Vector2(PanelWidth, 120f), SortingOrder);
        panel = (RectTransform)canvas.transform;
        background = GvMenuUi.Panel(panel, new Color(0.04f, 0.05f, 0.07f, 0.62f));

        label = GvMenuUi.Label(panel, "Text", "", 21f, new Color(0.78f, 0.84f, 0.90f, 1f));
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(Pad, Pad);
        label.rectTransform.offsetMax = new Vector2(-Pad, -Pad);
        label.alignment = TextAlignmentOptions.TopLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.lineSpacing = 6f;
    }

    /// <summary>
    /// Set the readout. Cheap to call every tick: the text is only pushed into TMP when
    /// it actually differs, because assigning identical text still forces a mesh rebuild.
    /// </summary>
    public void Set(string text)
    {
        if (mode == GvHudMode.Off || label == null || text == lastText)
            return;
        lastText = text;
        // Fixed-advance digits. Without this every changing digit reflows the line and
        // the whole readout twitches, which reads as instability in the video behind it.
        label.text = "<mspace=0.56em>" + text + "</mspace>";
        label.ForceMeshUpdate();
        float h = Mathf.Max(46f, label.textBounds.size.y + Pad * 2f);
        float w = Mathf.Clamp(label.textBounds.size.x + Pad * 2f, 320f, 1100f);
        panel.sizeDelta = new Vector2(w, h);
    }

    private void LateUpdate()
    {
        if (mode == GvHudMode.Off)
            return;
        if (head == null)
        {
            head = GvXr.Head();
            if (head == null)
                return;
        }

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
            followForward = Vector3.Slerp(followForward, want,
                1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.05f, followTime)));
        }

        Vector3 right = Vector3.Cross(Vector3.up, followForward).normalized;
        float d = Mathf.Max(0.3f, distance);
        panel.position = head.position + followForward * d
                       + right * lateralOffset + Vector3.up * heightOffset;
        panel.rotation = Quaternion.LookRotation(followForward, Vector3.up);
        // Pivot is centred, so the panel grows symmetrically; anchoring the *text* block
        // top-left is what keeps a growing readout from shifting the lines already read.
        panel.localScale = Vector3.one * (d / ReferenceDistance / GvMenuUi.PixelsPerMetre);
    }

    /// <summary>Re-place immediately, for "recentre view".</summary>
    public void Recentre() => placed = false;
}
