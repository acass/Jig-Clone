using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Jig
{
    public class LoadedJig
    {
        public JigScene Scene;
        public GameObject Model;   // instantiated glTF, parented under the supplied root
    }

    /// Fetches Jigs over HTTP and instantiates them. Content is cached on disk by URL so a
    /// second launch is fast, but the cache is bypassable - the whole point of the slice is
    /// that editing content on the host changes the app, so a stale cache would hide the
    /// only property we are trying to prove.
    public class JigLoader : MonoBehaviour
    {
        [Tooltip("URL of index.json. Point this at ./serve.sh output, or the GitHub Pages URL.")]
        public string manifestUrl = "http://192.168.86.39:8000/index.json";

        [Tooltip("Ignore the on-disk cache and re-download every launch. Leave on while authoring.")]
        public bool forceRefresh = true;

        string CacheDir => Path.Combine(Application.persistentDataPath, "jigcache");

        public async Task<JigManifest> LoadManifest()
        {
            var bytes = await Fetch(manifestUrl, cacheable: false);
            var manifest = Deserialize<JigManifest>(bytes, manifestUrl);
            if (manifest?.jigs == null || manifest.jigs.Count == 0)
                throw new JigContentException($"Manifest at {manifestUrl} lists no jigs.");
            return manifest;
        }

        public async Task<LoadedJig> LoadJig(JigEntry entry, Transform parent)
        {
            if (entry == null || string.IsNullOrEmpty(entry.scene))
                throw new JigContentException("Manifest entry has no scene path.");

            var sceneUrl = Combine(manifestUrl, entry.scene);
            var scene = Deserialize<JigScene>(await Fetch(sceneUrl, cacheable: false), sceneUrl);

            if (scene == null)
                throw new JigContentException($"{sceneUrl} did not parse into a scene.");
            if (string.IsNullOrEmpty(scene.model))
                throw new JigContentException($"{sceneUrl} has no 'model' field.");
            if (scene.steps == null || scene.steps.Count == 0)
                throw new JigContentException($"{sceneUrl} has no steps.");
            if (scene.scale <= 0f || float.IsNaN(scene.scale))
            {
                Debug.LogWarning($"[jig] {sceneUrl} has scale {scene.scale}; falling back to 1.");
                scene.scale = 1f;
            }

            var modelUrl = Combine(sceneUrl, scene.model);
            var glb = await Fetch(modelUrl, cacheable: true);

            var import = new GltfImport();
            if (!await import.Load(glb, new Uri(modelUrl)))
                throw new JigContentException($"glTF at {modelUrl} failed to load.");

            var container = new GameObject($"jig:{scene.id}");
            container.transform.SetParent(parent, false);

            if (!await import.InstantiateMainSceneAsync(container.transform))
            {
                Destroy(container);
                throw new JigContentException($"glTF at {modelUrl} loaded but instantiated nothing.");
            }

            return new LoadedJig { Scene = scene, Model = container };
        }

        async Task<byte[]> Fetch(string url, bool cacheable)
        {
            var cachePath = Path.Combine(CacheDir, Hash(url));

            if (cacheable && !forceRefresh && File.Exists(cachePath))
                return File.ReadAllBytes(cachePath);

            using var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                // A cached copy is better than nothing when the dev server is off.
                if (cacheable && File.Exists(cachePath))
                {
                    Debug.LogWarning($"[jig] {url} unreachable ({req.error}); using cached copy.");
                    return File.ReadAllBytes(cachePath);
                }
                throw new JigContentException($"Could not fetch {url}: {req.error}");
            }

            var data = req.downloadHandler.data;

            if (cacheable)
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllBytes(cachePath, data);
                }
                catch (IOException e)
                {
                    // Caching is an optimisation; failing to cache must not fail the load.
                    Debug.LogWarning($"[jig] could not cache {url}: {e.Message}");
                }
            }

            return data;
        }

        static T Deserialize<T>(byte[] bytes, string url) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(bytes));
            }
            catch (JsonException e)
            {
                throw new JigContentException($"{url} is not valid JSON: {e.Message}");
            }
        }

        /// Resolves a relative content path against the URL it was referenced from.
        static string Combine(string baseUrl, string relative)
        {
            if (relative.StartsWith("http://") || relative.StartsWith("https://"))
                return relative;
            return new Uri(new Uri(baseUrl), relative).ToString();
        }

        static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)))
                .Replace("-", string.Empty).Substring(0, 32);
        }
    }

    /// Thrown for content the viewer cannot use. Always carries the URL, because the first
    /// question when a Jig fails to appear is always "which file, and where did it come from".
    public class JigContentException : Exception
    {
        public JigContentException(string message) : base(message) { }
    }
}
