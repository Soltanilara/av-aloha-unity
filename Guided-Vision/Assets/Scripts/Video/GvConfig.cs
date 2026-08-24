using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// One robot's connection details and display tuning.
///
/// This replaces the scattered PlayerPrefs string keys the old StartScene wrote --
/// fifteen loose floats with no owner, no defaults in one place, and no way to keep
/// more than one robot's settings. Being a JSON file in persistentDataPath also means
/// it can be adb-pushed and retuned without a rebuild.
/// </summary>
[Serializable]
public class GvRobotProfile
{
    public string name = "robot";
    public string host = "127.0.0.1";

    public int controlPort = 15551;
    public int videoPort = 15552;
    public int inputPort = 15553;

    [Tooltip("Transmitted canvas per eye. Must match the sender; the decoder is " +
             "configured once and never reconfigured mid-session.")]
    public int canvasWidth = 1024;
    public int canvasHeight = 1024;

    [Tooltip("The camera's own resolution before atlas packing. Drives the quad " +
             "aspect ratio -- the canvas is square, the imagery is not.")]
    public int sourceWidth = 1920;
    public int sourceHeight = 1200;

    public bool foveation = true;

    // Display tuning. Defaults match GvStereoDisplay's.
    public float videoPlaneDistance = 1.0f;
    public float videoVFOV = 55f;
    public float videoScale = 1.0f;
    public float stereoSeparationDeg = 0f;
    public float stereoVerticalTrimDeg = 0f;
    public float edgeFeather = 0.03f;
    public float outerEdgeMask = 0f;
    public float hudDistance = 2.5f;
    public float foveaFeather = 0.15f;

    /// <summary>Diagnostic border around the high-resolution patch.</summary>
    public bool foveaOutline = false;

    /// <summary>
    /// Decode in C# from MJPEG instead of MediaCodec. Slow and wasteful, and the point
    /// is bring-up: it separates "does the network, protocol, atlas and shader path
    /// work" from "does the MediaCodec-to-OES bridge work", which otherwise both
    /// present as a black screen. Chosen before connecting, because the codec is
    /// negotiated when the session opens.
    /// </summary>
    public bool softwareVideo = false;

    /// <summary>
    /// Fraction of the coarse band the periphery fills. Lower is a blurrier surround
    /// and a more pronounced foveal effect -- and less bandwidth.
    /// </summary>
    public float coarseScale = 0.35f;

    /// <summary>Fraction of the fovea band the sharp crop fills.</summary>
    public float foveaScale = 0.5f;

    public GvRobotProfile Clone() => (GvRobotProfile)MemberwiseClone();
}

/// <summary>Everything the app remembers between runs.</summary>
[Serializable]
public class GvConfig
{
    public const string FileName = "gvconfig.json";

    public string lastRobot = "";
    public List<GvRobotProfile> robots = new List<GvRobotProfile>();

    public static string Path => System.IO.Path.Combine(Application.persistentDataPath, FileName);

    public static GvConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonUtility.FromJson<GvConfig>(File.ReadAllText(Path));
                if (cfg != null)
                {
                    cfg.robots ??= new List<GvRobotProfile>();
                    return cfg;
                }
            }
        }
        catch (Exception e)
        {
            // A corrupt config must not brick the app -- start fresh and say so.
            Debug.LogWarning($"GvConfig: could not read {Path}: {e.Message}");
        }
        return new GvConfig();
    }

    public bool Save()
    {
        try
        {
            File.WriteAllText(Path, JsonUtility.ToJson(this, true));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"GvConfig: could not write {Path}: {e.Message}");
            return false;
        }
    }

    public GvRobotProfile Find(string robotName) =>
        robots.Find(r => r != null && r.name == robotName);

    /// <summary>Existing profile for this name, or a new one appended.</summary>
    public GvRobotProfile GetOrCreate(string robotName)
    {
        var p = Find(robotName);
        if (p != null)
            return p;
        p = new GvRobotProfile { name = robotName };
        robots.Add(p);
        return p;
    }

    public GvRobotProfile Last => string.IsNullOrEmpty(lastRobot) ? null : Find(lastRobot);
}
