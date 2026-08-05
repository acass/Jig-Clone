using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

// One-shot bootstrap: adds the packages the JigViewer slice needs, letting the
// Package Manager resolve versions compatible with this editor rather than pinning
// strings by hand. Run once via -executeMethod JigPackageBootstrap.Run, then delete.
public static class JigPackageBootstrap
{
    static readonly string[] Packages =
    {
        "com.unity.render-pipelines.universal",
        "com.unity.ugui",
        "com.unity.nuget.newtonsoft-json",
        "com.unity.cloud.gltfast",
        "com.unity.xr.arfoundation",
        "com.unity.xr.meta-openxr",
        "com.unity.xr.interaction.toolkit",
    };

    public static void Run()
    {
        var failures = new List<string>();

        foreach (var id in Packages)
        {
            Debug.Log($"[bootstrap] adding {id}");
            AddRequest req = Client.Add(id);

            while (!req.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (req.Status == StatusCode.Success)
                Debug.Log($"[bootstrap] OK {req.Result.packageId}");
            else
            {
                var msg = req.Error != null ? req.Error.message : "unknown error";
                Debug.LogError($"[bootstrap] FAILED {id}: {msg}");
                failures.Add($"{id}: {msg}");
            }
        }

        if (failures.Count > 0)
        {
            Debug.LogError($"[bootstrap] {failures.Count} package(s) failed:\n  " + string.Join("\n  ", failures));
            EditorApplication.Exit(1);
        }

        Debug.Log("[bootstrap] all packages added");
        EditorApplication.Exit(0);
    }
}
