using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// Fixes three device-only symptoms in one pass:
///
/// 1. No head tracking (world unstable, doubled image): Main Camera had no
///    TrackedPoseDriver, so the HMD pose never drove the camera transform.
/// 2. No passthrough: HDR color buffers on Quest drop the alpha channel, so the
///    transparent-black clear that lets the room show through never reaches the
///    compositor. HDR must be off on both the camera and the URP asset.
/// 3. Pink runtime-loaded glTF models: no asset in the project references the
///    glTFast URP shaders, so the build strips them. Force-include them via
///    GraphicsSettings always-included shaders.
///
///   Unity -batchmode -nographics -projectPath . -executeMethod JigSceneFix.Run -logFile -
public static class JigSceneFix
{
    const string ScenePath = "Assets/Scenes/Jig.unity";
    const string UrpAssetPath = "Assets/Settings/JigUniversalRenderPipeline.asset";

    static readonly string[] k_GltfShaders =
    {
        "Shader Graphs/glTF-pbrMetallicRoughness",
        "Shader Graphs/glTF-pbrSpecularGlossiness",
        "Shader Graphs/glTF-unlit",
    };

    [MenuItem("Jig/Fix Scene (tracking + passthrough + shaders)")]
    public static void Run()
    {
        FixScene();
        DisableUrpHdr();
        IncludeGltfShaders();

        AssetDatabase.SaveAssets();
        Debug.Log("JigSceneFix: done.");
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    static void FixScene()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            Debug.LogError("JigSceneFix: no camera in scene.");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        // HDR off: passthrough compositing needs the alpha-0 clear to survive.
        cam.allowHDR = false;

        var tpd = cam.GetComponent<TrackedPoseDriver>();
        if (tpd == null)
        {
            tpd = cam.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            tpd.positionInput = new InputActionProperty(
                new InputAction("Position", binding: "<XRHMD>/centerEyePosition"));
            tpd.rotationInput = new InputActionProperty(
                new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation"));
            Debug.Log("JigSceneFix: TrackedPoseDriver added to Main Camera.");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("JigSceneFix: scene saved (HDR off, head tracking wired).");
    }

    static void DisableUrpHdr()
    {
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp == null)
        {
            Debug.LogError($"JigSceneFix: URP asset not found at {UrpAssetPath}.");
            return;
        }

        urp.supportsHDR = false;
        EditorUtility.SetDirty(urp);
        Debug.Log("JigSceneFix: URP HDR disabled.");
    }

    static void IncludeGltfShaders()
    {
        var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")
            .FirstOrDefault();
        var so = new SerializedObject(graphics);
        var list = so.FindProperty("m_AlwaysIncludedShaders");

        foreach (var name in k_GltfShaders)
        {
            var shader = Shader.Find(name);
            if (shader == null)
            {
                Debug.LogError($"JigSceneFix: shader '{name}' not found.");
                continue;
            }

            bool present = Enumerable.Range(0, list.arraySize)
                .Any(i => list.GetArrayElementAtIndex(i).objectReferenceValue == shader);
            if (present) continue;

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            Debug.Log($"JigSceneFix: always-include '{name}'.");
        }

        so.ApplyModifiedProperties();
    }
}
