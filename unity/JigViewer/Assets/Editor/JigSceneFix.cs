using System.Linq;
using Jig;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// Fixes four device-only symptoms in one pass:
///
/// 1. No head tracking (world unstable, doubled image): Main Camera had no
///    TrackedPoseDriver, so the HMD pose never drove the camera transform.
/// 2. No passthrough: HDR color buffers on Quest drop the alpha channel, so the
///    transparent-black clear that lets the room show through never reaches the
///    compositor. HDR must be off on both the camera and the URP asset.
/// 3. Pink runtime-loaded glTF models: no asset in the project references the
///    glTFast URP shaders, so the build strips them. Force-include them via
///    GraphicsSettings always-included shaders.
/// 4. Nothing is pressable: the scene had no interactors at all, so every
///    XRSimpleInteractable and XRGrabInteractable registered with the interaction
///    manager and then waited forever for a selector that did not exist.
///
///   Unity -batchmode -nographics -projectPath . -executeMethod JigSceneFix.Run -logFile -
public static class JigSceneFix
{
    const string ScenePath = "Assets/Scenes/Jig.unity";
    const string UrpAssetPath = "Assets/Settings/JigUniversalRenderPipeline.asset";

    // The hand whose secondary button re-places the Jig, and whose ray JigPlacement casts.
    const string PrimaryHand = "RightHand";

    // Shaders no asset in the project references, so the build strips them and anything using
    // them renders magenta on device. The glTF three are the runtime-loaded models; the URP two
    // are what GameObject.CreatePrimitive falls back to, which is every button, dot and picker
    // row in the floating panels.
    static readonly string[] k_AlwaysIncludeShaders =
    {
        "Shader Graphs/glTF-pbrMetallicRoughness",
        "Shader Graphs/glTF-pbrSpecularGlossiness",
        "Shader Graphs/glTF-unlit",
        JigUi.UnlitShader,
        "Universal Render Pipeline/Lit",
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

        AddInteractors();
        WirePlacement();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("JigSceneFix: scene saved (HDR off, head tracking wired, interactors added).");
    }

    /// Builds the controller rig. Without this nothing in the scene can be selected: the step
    /// panel buttons, the grab interactable and the jig picker are all interactables with no
    /// interactor to drive them.
    ///
    /// Actions are constructed inline rather than referencing an .inputactions asset, matching
    /// the TrackedPoseDriver above. That keeps the whole input setup in source.
    static void AddInteractors()
    {
        var existing = Object.FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None);
        if (existing.Length > 0)
        {
            // Already rigged, but a rig built by an older run may predate the ray visual or still
            // carry the grip-pose bindings, so top it up rather than returning and silently
            // leaving the ray invisible or mis-aimed.
            foreach (var r in existing)
            {
                if (r.GetComponent<JigRayVisual>() == null)
                    r.gameObject.AddComponent<JigRayVisual>();

                var hand = r.name.StartsWith("LeftHand") ? "LeftHand" : PrimaryHand;
                ApplyPointerPose(r.GetComponent<TrackedPoseDriver>(), hand);
                EditorUtility.SetDirty(r.gameObject);
            }

            Debug.Log($"JigSceneFix: {existing.Length} interactor(s) already present; " +
                      "ray visuals ensured, pointer poses re-bound.");
            return;
        }

        if (Object.FindFirstObjectByType<XRInteractionManager>() == null)
            new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();

        var offset = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>()?.CameraFloorOffsetObject;
        if (offset == null)
        {
            Debug.LogError("JigSceneFix: no XR Origin camera offset - cannot parent interactors.");
            return;
        }

        foreach (var hand in new[] { "LeftHand", PrimaryHand })
            MakeRayInteractor(offset.transform, hand);

        Debug.Log("JigSceneFix: ray interactors added for both hands.");
    }

    /// Binds a pose driver to the controller's AIM pose.
    ///
    /// Not the device/grip pose: the grip pose is oriented along the handle, which on a Touch
    /// controller tilts up and forward, so a ray built from it visibly misses where the user is
    /// pointing. OpenXR exposes a separate pointer pose for aiming, and both are present on the
    /// device (verified in the runtime control dump).
    static void ApplyPointerPose(TrackedPoseDriver tpd, string hand)
    {
        if (tpd == null) return;

        tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        tpd.positionInput = new InputActionProperty(
            new InputAction($"{hand} Position", binding: $"<XRController>{{{hand}}}/pointerPosition"));
        tpd.rotationInput = new InputActionProperty(
            new InputAction($"{hand} Rotation", binding: $"<XRController>{{{hand}}}/pointerRotation"));
    }

    static XRRayInteractor MakeRayInteractor(Transform parent, string hand)
    {
        var go = new GameObject($"{hand} Ray Interactor");
        go.transform.SetParent(parent, false);

        ApplyPointerPose(go.AddComponent<TrackedPoseDriver>(), hand);

        var ray = go.AddComponent<XRRayInteractor>();
        go.AddComponent<JigRayVisual>();

        // InputAction mode means the action is defined here rather than in a project asset.
        // XRBaseInputInteractor.OnEnable enables these for us, unlike a bare InputActionProperty.
        ray.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
        ray.selectInput.inputActionPerformed = new InputAction(
            $"{hand} Select", InputActionType.Button, $"<XRController>{{{hand}}}/triggerPressed");

        return ray;
    }

    /// Hands JigPlacement the anchor manager and the ray it needs. Both are new dependencies:
    /// without the anchor manager placement writes a raw transform and the model drifts, and
    /// without the ray there is no way to re-place it.
    static void WirePlacement()
    {
        var placement = Object.FindFirstObjectByType<JigPlacement>();
        if (placement == null)
        {
            Debug.LogError("JigSceneFix: no JigPlacement in scene.");
            return;
        }

        if (placement.anchorManager == null)
            placement.anchorManager = Object.FindFirstObjectByType<ARAnchorManager>();

        if (placement.rayInteractor == null)
        {
            placement.rayInteractor = Object.FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None)
                .FirstOrDefault(r => r.name.StartsWith(PrimaryHand));
        }

        // Re-place is on the secondary button, NOT the trigger: the trigger is select, so binding
        // both would re-place the model every time the user grabs it or presses a step button.
        if (placement.placeAction.action == null)
        {
            placement.placeAction = new InputActionProperty(
                new InputAction("Place", InputActionType.Button,
                                $"<XRController>{{{PrimaryHand}}}/secondaryButton"));
        }

        EditorUtility.SetDirty(placement);
        Debug.Log($"JigSceneFix: placement wired (anchorManager={placement.anchorManager != null}, " +
                  $"rayInteractor={placement.rayInteractor != null}).");
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

        foreach (var name in k_AlwaysIncludeShaders)
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
