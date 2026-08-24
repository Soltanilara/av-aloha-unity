using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One control the robot asked the session menu to show.
///
/// This is the piece that makes "never edit the Unity app again" true rather than
/// aspirational. Episode recording, control-mode switches, gripper presets, homing and
/// calibration routines are all a row and a handler in Python; none of them need a
/// rebuild, and none of them need this file to change either -- four row types cover
/// every control a teleoperation session has actually wanted, and the general marker
/// layer covers anything that is really a picture rather than a control.
///
/// The headset owns nothing but presentation. It does not know what "Record episode"
/// means, whether it is legal right now, or what happens next. It reports that the
/// operator touched it.
///
/// **Values are echoed locally and then sent.** A row that waited for the robot to
/// confirm before moving would feel broken over any real link, so the row changes
/// immediately and the robot corrects it by re-publishing the list -- the same
/// optimistic-update-with-authoritative-correction that any responsive UI over a network
/// ends up using. Rows are replaced wholesale on each publish, so there is no
/// partial-update or stale-row problem to reason about.
/// </summary>
public sealed class GvRobotRow
{
    public const string TypeButton = "button";
    public const string TypeToggle = "toggle";
    public const string TypeChoice = "choice";
    public const string TypeRange = "range";

    public string Id = "";
    public string Label = "";
    public string Type = TypeButton;
    public string Hint = "";
    public bool Enabled = true;

    /// <summary>Choices, for `choice` rows.</summary>
    public string[] Options = System.Array.Empty<string>();

    public bool BoolValue;
    public double Number;
    public string Text = "";

    public double Min = 0.0;
    public double Max = 1.0;
    public double Step = 0.1;

    /// <summary>Formatting hint for `range`, e.g. "{0:0.00} m". Empty picks one.</summary>
    public string Format = "";

    public static GvRobotRow FromMap(IDictionary<string, object> m)
    {
        if (m == null)
            return null;
        var r = new GvRobotRow
        {
            Id = GvMsgPack.GetString(m, "id", ""),
            Label = GvMsgPack.GetString(m, "label", ""),
            Type = GvMsgPack.GetString(m, "type", TypeButton),
            Hint = GvMsgPack.GetString(m, "hint", ""),
            Enabled = GvMsgPack.GetBool(m, "enabled", true),
            Format = GvMsgPack.GetString(m, "fmt", ""),
        };
        if (string.IsNullOrEmpty(r.Id))
            return null;                    // no id means no way to report it back
        if (string.IsNullOrEmpty(r.Label))
            r.Label = r.Id;

        var opts = GvMsgPack.GetList(m, "options");
        if (opts != null)
        {
            var list = new List<string>(opts.Count);
            foreach (var o in opts)
                list.Add(o == null ? "" : o.ToString());
            r.Options = list.ToArray();
        }

        r.Min = GvMsgPack.GetFloat(m, "min", 0f);
        r.Max = GvMsgPack.GetFloat(m, "max", 1f);
        r.Step = GvMsgPack.GetFloat(m, "step", 0.1f);
        if (r.Step <= 0.0)
            r.Step = 0.1;
        if (r.Max <= r.Min)
            r.Max = r.Min + 1.0;

        object v;
        if (m.TryGetValue("value", out v) && v != null)
            r.SetValue(v);
        else if (r.Type == TypeChoice && r.Options.Length > 0)
            r.Text = r.Options[0];
        else if (r.Type == TypeRange)
            r.Number = r.Min;
        return r;
    }

    private void SetValue(object v)
    {
        if (v is bool)
        {
            BoolValue = (bool)v;
            Number = BoolValue ? 1.0 : 0.0;
            Text = BoolValue ? "on" : "off";
            return;
        }
        if (v is string)
        {
            Text = (string)v;
            BoolValue = Text == "on" || Text == "true";
            return;
        }
        Number = GvMsgPack.Num(v, 0.0);
        BoolValue = Number != 0.0;
        Text = Number.ToString();
    }

    /// <summary>What the right-hand column shows. Empty for a plain button.</summary>
    public string Display()
    {
        switch (Type)
        {
            case TypeToggle: return BoolValue ? "on" : "off";
            case TypeChoice: return Text;
            case TypeRange:
                return string.Format(
                    string.IsNullOrEmpty(Format) ? DefaultFormat() : Format, Number);
        }
        return Hint;
    }

    /// <summary>
    /// Pick a sane number of decimals from the step, so 0.1 does not render as
    /// "0.30000001" and 0.001 does not render as "0.0".
    /// </summary>
    private string DefaultFormat()
    {
        if (Step >= 1.0) return "{0:0}";
        if (Step >= 0.1) return "{0:0.0}";
        if (Step >= 0.01) return "{0:0.00}";
        return "{0:0.000}";
    }

    /// <summary>Apply a nudge locally. Returns false when the row does not take one.</summary>
    public bool Adjust(int dir)
    {
        switch (Type)
        {
            case TypeToggle:
                BoolValue = !BoolValue;
                return true;
            case TypeChoice:
                if (Options.Length == 0)
                    return false;
                int i = System.Array.IndexOf(Options, Text);
                if (i < 0)
                    i = 0;
                i = ((i + dir) % Options.Length + Options.Length) % Options.Length;
                Text = Options[i];
                return true;
            case TypeRange:
                Number = System.Math.Round(
                    Mathf.Clamp((float)(Number + dir * Step), (float)Min, (float)Max),
                    6);
                return true;
        }
        return false;
    }

    /// <summary>The payload for `ui/menu/event`.</summary>
    public object EventValue()
    {
        switch (Type)
        {
            case TypeToggle: return BoolValue;
            case TypeChoice: return Text;
            case TypeRange: return Number;
        }
        return true;              // a button press has no value beyond having happened
    }
}
