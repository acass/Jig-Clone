using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Management;

/// Assigns the OpenXR loader to the Android build target and turns on the Meta features the
/// viewer needs. XR Plug-in Management stores these in serialized assets that a fresh clone
/// does not have, and a build made without them installs as a flat 2D panel app: no
/// libopenxr_loader.so ships, so nothing ever initialises XR.
///
/// Also imports TMP Essential Resources. Without them TMP_Settings is null and every
/// TextMeshPro component throws in Awake, so no caption or label renders.
public static class JigXrBootstrap
{
    const string k_OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";

    static readonly string[] k_AndroidFeatures =
    {
        "com.unity.openxr.feature.metaquest",                  // Meta Quest Support
        "com.unity.openxr.feature.arfoundation-meta-session",  // Meta Quest: Session
        "com.unity.openxr.feature.arfoundation-meta-camera",   // Meta Quest: Passthrough
        "com.unity.openxr.feature.arfoundation-meta-plane",    // Meta Quest: Plane Detection
        "com.unity.openxr.feature.arfoundation-meta-anchor",   // Meta Quest: Anchors
        "com.unity.openxr.feature.input.oculustouch",          // Oculus Touch Controller Profile
        "com.unity.openxr.feature.compositionlayers",          // required by the Composition Layers package
    };

    [MenuItem("Jig/Bootstrap XR + TMP")]
    public static void Run()
    {
        AssignOpenXRLoader();
        EnableAndroidFeatures();

        // OpenXR rejects a Gamma build for GLES, and URP on Quest expects Linear anyway.
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Debug.Log("JigXrBootstrap: colour space set to Linear.");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JigXrBootstrap: done.");
    }

    /// AssetDatabase.ImportPackage is asynchronous, so this must run WITHOUT -quit: the import
    /// finishes on a later editor tick and the completion callback exits the process.
    ///
    ///   Unity -batchmode -nographics -projectPath . -executeMethod JigXrBootstrap.ImportTmp
    public static void ImportTmp()
    {
        if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0)
        {
            Debug.Log("JigXrBootstrap: TMP Essential Resources already present.");
            EditorApplication.Exit(0);
            return;
        }

        var package = Directory
            .GetDirectories("Library/PackageCache")
            .Select(d => Path.Combine(d, "Package Resources", "TMP Essential Resources.unitypackage"))
            .FirstOrDefault(File.Exists);

        if (package == null)
        {
            Debug.LogError("JigXrBootstrap: TMP Essential Resources.unitypackage not found.");
            EditorApplication.Exit(1);
            return;
        }

        AssetDatabase.importPackageCompleted += _ =>
        {
            AssetDatabase.SaveAssets();
            Debug.Log("JigXrBootstrap: TMP Essential Resources imported.");
            EditorApplication.Exit(0);
        };
        AssetDatabase.importPackageFailed += (_, error) =>
        {
            Debug.LogError($"JigXrBootstrap: TMP import failed: {error}");
            EditorApplication.Exit(1);
        };
        AssetDatabase.importPackageCancelled += _ =>
        {
            Debug.LogError("JigXrBootstrap: TMP import cancelled.");
            EditorApplication.Exit(1);
        };

        AssetDatabase.ImportPackage(package, false);
    }

    static void AssignOpenXRLoader()
    {
        EditorBuildSettings.TryGetConfigObject(
            XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);

        if (perTarget == null)
        {
            Debug.LogError("JigXrBootstrap: XRGeneralSettingsPerBuildTarget config object missing.");
            return;
        }

        var assetPath = AssetDatabase.GetAssetPath(perTarget);
        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);

        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            settings.name = "Android Settings";
            AssetDatabase.AddObjectToAsset(settings, assetPath);
            perTarget.SetSettingsForBuildTarget(BuildTargetGroup.Android, settings);
        }

        if (settings.Manager == null)
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = "Android Providers";
            AssetDatabase.AddObjectToAsset(manager, assetPath);
            settings.Manager = manager;
        }

        settings.InitManagerOnStart = true;

        if (!XRPackageMetadataStore.AssignLoader(settings.Manager, k_OpenXRLoader, BuildTargetGroup.Android))
            Debug.LogError("JigXrBootstrap: AssignLoader failed for OpenXRLoader / Android.");
        else
            Debug.Log("JigXrBootstrap: OpenXR loader assigned to Android.");

        EditorUtility.SetDirty(perTarget);
        EditorUtility.SetDirty(settings);
    }

    static void EnableAndroidFeatures()
    {
        foreach (var id in k_AndroidFeatures)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id);
            if (feature == null)
            {
                Debug.LogError($"JigXrBootstrap: feature {id} not found.");
                continue;
            }

            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            Debug.Log($"JigXrBootstrap: enabled {id}");
        }
    }
}
