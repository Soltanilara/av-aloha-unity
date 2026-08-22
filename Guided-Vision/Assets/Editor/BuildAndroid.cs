using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// One-command Android build, so rebuilding while tuning the vision pipeline does not
/// mean clicking through the Build Settings dialog every time.
///
///     Unity -batchmode -quit -projectPath Guided-Vision \
///           -executeMethod BuildAndroid.Build -logFile -
///
/// Output path defaults to &lt;project&gt;/Builds/GuidedVision.apk; override with
/// -buildOutput &lt;path&gt;. Exits non-zero on failure so it can be scripted.
/// </summary>
public static class BuildAndroid
{
    private const string DefaultOutput = "Builds/GuidedVision.apk";

    public static void Build()
    {
        string output = ArgValue("-buildOutput") ?? DefaultOutput;
        if (!Path.IsPathRooted(output))
            output = Path.Combine(Directory.GetCurrentDirectory(), output);

        Directory.CreateDirectory(Path.GetDirectoryName(output));

        var scenes = Array.ConvertAll(
            Array.FindAll(EditorBuildSettings.scenes, s => s.enabled),
            s => s.path);

        if (scenes.Length == 0)
            Fail("No enabled scenes in Build Settings.");

        Debug.Log($"BuildAndroid: {scenes.Length} scene(s) -> {output}");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
            Fail($"Build {summary.result}: {summary.totalErrors} error(s).");

        Debug.Log($"BuildAndroid: succeeded, {summary.totalSize / (1024 * 1024)} MB, " +
                  $"{summary.totalTime.TotalSeconds:0} s -> {output}");
        EditorApplication.Exit(0);
    }

    private static void Fail(string message)
    {
        Debug.LogError("BuildAndroid: " + message);
        EditorApplication.Exit(1);
    }

    private static string ArgValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }
}
