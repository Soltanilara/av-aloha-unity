using System;
using UnityEngine;

/// <summary>
/// Stick-or-keyboard navigation with auto-repeat.
///
/// The stick is treated as a d-pad: an analogue axis feeding a list produces either
/// a runaway scroll or a dead zone nobody can tune, so it is quantised and repeated
/// on a timer instead.
/// </summary>
public sealed class GvNav
{
    private const float Deadzone = 0.55f;
    private const float FirstRepeat = 0.42f;
    private const float NextRepeat = 0.13f;

    public bool Up, Down, Left, Right, Select, Back, Recenter;

    /// <summary>Any stick or key input this frame -- used to hand the selection
    /// back from the pointer to the stick.</summary>
    public bool Any => Up || Down || Left || Right || Select || Back || Recenter;

    private Vector2 held;
    private float repeatAt;

    public void Poll()
    {
        Up = Down = Left = Right = Select = Back = Recenter = false;

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
        if (Input.GetKeyDown(KeyCode.R)) Recenter = true;

        try
        {
            if (OVRInput.GetDown(OVRInput.Button.One)) Select = true;
            if (OVRInput.GetDown(OVRInput.Button.Two)) Back = true;
            // Clicking a stick is unused elsewhere in this menu and is easy to find
            // by feel, which matters when the thing you are looking for is the UI.
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick)
                || OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick)) Recenter = true;
        }
        catch (Exception)
        {
        }
    }
}
