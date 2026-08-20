using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class CommandLineBuild
{
    public static void BuildAndroid()
    {
        string outputPath = Environment.GetEnvironmentVariable("UNITY_BUILD_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = "Builds/TwoStreamGuidedVision.apk";
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[]
            {
                "Assets/Scenes/StartScene.unity",
                "Assets/Scenes/PassthroughScene.unity"
            },
            locationPathName = fullOutputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception($"Android build failed: {report.summary.result}");
        }

        Console.WriteLine($"Android build succeeded: {fullOutputPath}");
    }
}
