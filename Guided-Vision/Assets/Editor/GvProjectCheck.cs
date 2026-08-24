using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Static checks that a build would pass, runnable without doing a build.
///
/// This exists because of a specific failure that got all the way to a shipped scene:
/// deleting a script left three GameObjects holding a reference to a MonoBehaviour that
/// no longer existed. Unity does not treat that as an error. The Inspector shows a
/// yellow "missing script" line on an object nobody was looking at, the console stays
/// clean, the project compiles, and the build succeeds -- so the only thing standing
/// between that and the headset is somebody happening to click the right object.
///
/// The general rule this encodes: **anything whose breakage is silent needs a test, and
/// anything whose breakage is loud does not.** A compile error needs no check here. A
/// dangling script reference, an empty build scene list, or a component the code does
/// FindAnyObjectByType for and silently no-ops without, all do.
///
///     Tools &gt; Quest Teleop &gt; Check project
///
/// and in CI, where a non-zero exit is the whole point:
///
///     Unity -batchmode -quit -projectPath Guided-Vision \
///           -executeMethod GvProjectCheck.RunFromCommandLine
/// </summary>
public static class GvProjectCheck
{
    /// <summary>Components that must exist in a scene for it to do its job.</summary>
    private static readonly Dictionary<string, string[]> Required = new Dictionary<string, string[]>
    {
        { "GvStartScene",       new[] { "GvStartMenu" } },
        { "GvPassthroughScene", new[] { "GvStereoDisplay", "GvSessionMenu", "GvInputUplink",
                                        "GvSceneCommands", "GvToast", "GvHeadsetState" } },
    };

    private sealed class Report
    {
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int Checks;

        public void Pass(string what) { Checks++; Notes.Add("  ok    " + what); }
        public void Fail(string what) { Checks++; Failures.Add(what); Notes.Add("  FAIL  " + what); }
        public void Require(bool ok, string what) { if (ok) Pass(what); else Fail(what); }
    }

    [MenuItem("Tools/Quest Teleop/Check project")]
    public static void RunFromMenu()
    {
        var report = Run();
        if (report.Failures.Count == 0)
            Debug.Log($"GvProjectCheck: {report.Checks} checks passed.\n"
                      + string.Join("\n", report.Notes));
        else
            Debug.LogError($"GvProjectCheck: {report.Failures.Count} of {report.Checks} checks FAILED.\n"
                           + string.Join("\n", report.Notes));
    }

    /// <summary>Batch-mode entry point. Exits non-zero when anything failed.</summary>
    public static void RunFromCommandLine()
    {
        var report = Run();
        Console.WriteLine(string.Join("\n", report.Notes));
        if (report.Failures.Count > 0)
        {
            Console.Error.WriteLine($"GvProjectCheck: {report.Failures.Count} FAILED:");
            foreach (var f in report.Failures)
                Console.Error.WriteLine("  - " + f);
            EditorApplication.Exit(1);
            return;
        }
        Console.WriteLine($"GvProjectCheck: {report.Checks} checks passed.");
        EditorApplication.Exit(0);
    }

    private static Report Run()
    {
        var r = new Report();
        string reopen = EditorSceneManager.GetActiveScene().path;

        CheckBuildScenes(r);
        CheckPlayerSettings(r);
        CheckResources(r);

        if (!string.IsNullOrEmpty(reopen))
            EditorSceneManager.OpenScene(reopen, OpenSceneMode.Single);
        return r;
    }

    // ------------------------------------------------------------------ scenes

    private static void CheckBuildScenes(Report r)
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).ToArray();
        r.Require(scenes.Length > 0, "build settings list at least one enabled scene");

        // The first scene is what launches. Getting this backwards drops the operator
        // straight into a session with no robot chosen.
        if (scenes.Length > 0)
            r.Require(scenes[0].path.EndsWith("/GvStartScene.unity"),
                      "GvStartScene is the first build scene, so the app opens on the robot list");

        foreach (var s in scenes)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(s.path);
            if (!System.IO.File.Exists(s.path))
            {
                r.Fail($"build scene '{s.path}' does not exist on disk");
                continue;
            }

            var scene = EditorSceneManager.OpenScene(s.path, OpenSceneMode.Single);
            var objects = UnityEngine.Object
                .FindObjectsByType<GameObject>(FindObjectsInactive.Include);

            // The silent one. A null component slot is a MonoBehaviour whose script was
            // deleted or renamed; Unity keeps the slot and says nothing.
            var broken = new List<string>();
            foreach (var go in objects)
                if (go.GetComponents<Component>().Any(c => c == null))
                    broken.Add(Path(go.transform));
            r.Require(broken.Count == 0,
                      broken.Count == 0
                          ? $"{name}: no missing script references ({objects.Length} objects)"
                          : $"{name}: {broken.Count} object(s) with a missing script: "
                            + string.Join(", ", broken.Take(6)));

            string[] required;
            if (Required.TryGetValue(name, out required))
                foreach (var typeName in required)
                    r.Require(FindByTypeName(typeName) != null,
                              $"{name}: has a {typeName}");

            // Every menu, toast and readout builds its own canvas at runtime, so the
            // only canvases that belong in a saved scene are the two video quads that
            // GvStereoDisplay is bound to. Anything else is v1 UI that survived, and it
            // draws over the video -- which is how a legacy DebugCanvas ended up parked
            // in the middle of the operator's view.
            var display = UnityEngine.Object
                .FindAnyObjectByType<GvStereoDisplay>(FindObjectsInactive.Include);
            var stray = UnityEngine.Object
                .FindObjectsByType<Canvas>(FindObjectsInactive.Include)
                .Where(c => display == null || (c != display.leftCanvas && c != display.rightCanvas))
                .Select(c => Path(c.transform))
                .ToList();
            r.Require(stray.Count == 0,
                      stray.Count == 0
                          ? $"{name}: no stray canvases (only the bound eye quads, if any)"
                          : $"{name}: {stray.Count} canvas(es) not bound to the display: "
                            + string.Join(", ", stray));

            if (Required.ContainsKey(name) && display != null)
                r.Require(display.leftCanvas != null && display.rightCanvas != null
                          && display.leftImage != null && display.rightImage != null,
                          $"{name}: GvStereoDisplay eye canvas/image bindings are all set");
        }
    }

    // ------------------------------------------------------------------ settings

    private static void CheckPlayerSettings(Report r)
    {
        r.Require((int)PlayerSettings.Android.minSdkVersion >= 32,
                  $"minSdkVersion is {(int)PlayerSettings.Android.minSdkVersion} (Meta requires >= 32)");

        r.Require(PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.GameActivity,
                  $"Android application entry is {PlayerSettings.Android.applicationEntry} "
                  + "(Meta requires a single GameActivity)");

        r.Require(PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64,
                  $"target architecture is {PlayerSettings.Android.targetArchitectures} (want ARM64)");

        r.Require(PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP,
                  "scripting backend is IL2CPP");

        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
        r.Require(apis.Length > 0 && apis[0] == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3,
                  $"first Android graphics API is {(apis.Length > 0 ? apis[0].ToString() : "none")} "
                  + "(gvnative hands MediaCodec output over as a GL_OES texture, so ES3)");

        // A build with no internet permission cannot reach the robot at all, and the
        // failure looks exactly like the robot being down.
        r.Require(PlayerSettings.Android.forceInternetPermission,
                  "Internet access is forced on (otherwise the link fails like a dead robot)");
    }

    private static void CheckResources(Report r)
    {
        // Loaded by name at runtime from GvStereoDisplay, so a rename or a move fails
        // in the headset rather than at compile time.
        r.Require(Resources.Load<Shader>("StereoEyeView") != null,
                  "Resources/StereoEyeView shader loads by name");

        var so = Resources.Load<Shader>("StereoEyeView");
        if (so != null)
            r.Require(so.isSupported, "StereoEyeView compiles for the active target");

        // GvSceneCommands builds every marker material from this, and Shader.Find only
        // sees shaders that survived the build. A null here means invisible markers.
        r.Require(Shader.Find("Sprites/Default") != null,
                  "Sprites/Default is available for marker materials");
    }

    // ------------------------------------------------------------------ helpers

    private static UnityEngine.Object FindByTypeName(string typeName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, false))
            .FirstOrDefault(t => t != null);
        if (type == null)
            return null;
        return UnityEngine.Object.FindAnyObjectByType(type, FindObjectsInactive.Include);
    }

    private static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
    }
}
