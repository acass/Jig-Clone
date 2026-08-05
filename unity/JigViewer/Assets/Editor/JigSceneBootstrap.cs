using Jig;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

/// Builds the viewer scene and sets the Android player options, so the scene graph is
/// reproducible from source instead of being hand-assembled in the inspector.
///
///   Unity -batchmode -nographics -projectPath . -executeMethod JigSceneBootstrap.Run -logFile -
///
/// Does NOT touch XR Plug-in Management or the OpenXR feature toggles. Those live in
/// version-specific serialized assets, and synthesising them blind is how you get a build
/// that looks configured and renders black. Enable them in the Editor - see SETUP.md.
public static class JigSceneBootstrap
{
    const string ScenePath = "Assets/Scenes/Jig.unity";

    public static void Run()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- AR session ---
        var sessionGo = new GameObject("AR Session");
        sessionGo.AddComponent<ARSession>();

        // --- XR Origin + camera ---
        var originGo = new GameObject("XR Origin");
        var origin = originGo.AddComponent<XROrigin>();

        var offsetGo = new GameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(offsetGo.transform, false);

        var cam = camGo.AddComponent<Camera>();
        // Passthrough shows through wherever the app renders nothing, so the background must
        // be fully transparent black. A skybox or opaque clear colour hides the room.
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;

        camGo.AddComponent<ARCameraManager>();
        camGo.AddComponent<ARCameraBackground>();

        origin.Camera = cam;
        origin.CameraFloorOffsetObject = offsetGo;

        // Managers live on the XR Origin; AR Foundation resolves them from there.
        var planeManager = originGo.AddComponent<ARPlaneManager>();
        originGo.AddComponent<ARRaycastManager>();
        // Meta OpenXR requires anchors to be enabled for planes to be reported at all.
        originGo.AddComponent<ARAnchorManager>();

        // --- App ---
        var appGo = new GameObject("Jig App");
        var loader = appGo.AddComponent<JigLoader>();
        var app = appGo.AddComponent<JigApp>();

        var contentRoot = new GameObject("Jig Content");
        app.contentRoot = contentRoot.transform;

        // JigPlacement goes on the XR Origin, not the app object: it needs the ARRaycastManager
        // that already lives there, and AR Foundation's managers resolve their origin from
        // their own GameObject.
        var placement = originGo.AddComponent<JigPlacement>();
        placement.planeManager = planeManager;
        placement.content = contentRoot.transform;
        app.placement = placement;

        Debug.Log($"[scene] manifestUrl defaults to '{loader.manifestUrl}' - set it to what serve.sh prints.");

        // --- Save + register ---
        System.IO.Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        ConfigureAndroid();

        AssetDatabase.SaveAssets();
        Debug.Log($"[scene] wrote {ScenePath}");
        EditorApplication.Exit(0);
    }

    static void ConfigureAndroid()
    {
        var group = NamedBuildTarget.Android;

        PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetApplicationIdentifier(group, "com.jigclone.viewer");
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.companyName = "JigClone";
        PlayerSettings.productName = "JigViewer";

        // The LAN dev server is plain HTTP; Android blocks cleartext by default.
        // ponytail: allowed globally because this build only ever talks to the dev server and
        // GitHub Pages. Narrow to a domain allowlist in network_security_config.xml if this
        // ever ships anywhere real.
        PlayerSettings.Android.useCustomKeystore = false;
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

        Debug.Log("[scene] Android: IL2CPP, ARM64, minSdk 32, cleartext HTTP allowed");
    }
}
