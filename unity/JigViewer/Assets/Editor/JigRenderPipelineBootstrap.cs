using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// Creates and assigns a URP asset.
///
/// Installing the URP package does NOT put the project on URP - it only makes the type
/// available. Without an assigned pipeline asset the project stays on Built-in, and glTFast
/// picks its shaders from the ACTIVE pipeline at import time, so every material on the loaded
/// model comes through pink. Cheaper to fix here than to discover on the headset.
///
///   Unity -batchmode -nographics -projectPath . -executeMethod JigRenderPipelineBootstrap.Run -logFile -
public static class JigRenderPipelineBootstrap
{
    const string Dir = "Assets/Settings";
    const string RendererPath = Dir + "/JigUniversalRenderer.asset";
    const string PipelinePath = Dir + "/JigUniversalRenderPipeline.asset";

    public static void Run()
    {
        System.IO.Directory.CreateDirectory(Dir);

        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(rendererData, RendererPath);

        var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
        AssetDatabase.CreateAsset(pipeline, PipelinePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        AssetDatabase.SaveAssets();

        var active = GraphicsSettings.defaultRenderPipeline;
        if (active == null)
        {
            Debug.LogError("[urp] pipeline asset did not stick - project is still on Built-in.");
            EditorApplication.Exit(1);
        }

        Debug.Log($"[urp] active pipeline: {active.name}");
        EditorApplication.Exit(0);
    }
}
