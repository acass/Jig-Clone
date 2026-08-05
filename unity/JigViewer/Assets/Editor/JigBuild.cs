using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Headless Android build, so the APK can be produced without opening the Editor.
public static class JigBuild
{
    [MenuItem("Jig/Build APK")]
    public static void Android()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "../../appBuild/app.apk",
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        });

        var summary = report.summary;
        Debug.Log($"JigBuild: {summary.result} {summary.totalSize} bytes, {summary.totalErrors} errors");

        if (summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
