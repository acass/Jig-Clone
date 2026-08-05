using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Jig
{
    /// Wires the slice together: fetch manifest -> load first Jig -> bind player -> build UI.
    ///
    /// Deliberately loads the FIRST manifest entry rather than showing a picker. The picker is
    /// stage-8 work; what matters now is that the content came off the network.
    [RequireComponent(typeof(JigLoader))]
    public class JigApp : MonoBehaviour
    {
        [Tooltip("Root the Jig is parented to. Placement and grab own this transform.")]
        public Transform contentRoot;

        public JigPlacement placement;

        JigLoader m_Loader;
        JigPlayer m_Player;
        LoadedJig m_Jig;

        readonly List<JigLabel> m_Labels = new List<JigLabel>();

        async void Start()
        {
            m_Loader = GetComponent<JigLoader>();

            // Magenta in a build almost always means the pipeline the materials were authored
            // for is not the one running, so say which one is live.
            Debug.Log($"[jig] render pipeline: " +
                      $"{UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"}");

            if (contentRoot == null)
            {
                contentRoot = new GameObject("jig-content").transform;
                contentRoot.SetParent(transform, false);
            }

            try
            {
                var manifest = await m_Loader.LoadManifest();
                Debug.Log($"[jig] manifest lists {manifest.jigs.Count} jig(s); loading '{manifest.jigs[0].id}'");

                m_Jig = await m_Loader.LoadJig(manifest.jigs[0], contentRoot);

                m_Player = gameObject.AddComponent<JigPlayer>();
                m_Player.Bind(m_Jig);
                m_Player.StepChanged += OnStepChanged;

                EnableGrab(contentRoot);

                JigStepPanel.Create(contentRoot, m_Player, new Vector3(0.35f, 0.15f, 0f));

                OnStepChanged(m_Player.CurrentStep, m_Player.CurrentCaption);
            }
            catch (JigContentException e)
            {
                // Content problems are expected and must be legible, not a stack trace on a
                // headset where nobody can read the console.
                Debug.LogError($"[jig] {e.Message}");
            }
        }

        void EnableGrab(Transform root)
        {
            // Grab acts on the ROOT only. Step tweens write child transforms, so the two never
            // contend for the same transform.
            var body = root.gameObject.GetComponent<Rigidbody>() ?? root.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var grab = root.gameObject.AddComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true;

            // Collider sized to the loaded model rather than a guessed constant.
            var bounds = ComputeLocalBounds(root);
            var box = root.gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
        }

        static Bounds ComputeLocalBounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.one * 0.2f);

            var world = renderers[0].bounds;
            foreach (var r in renderers) world.Encapsulate(r.bounds);

            return new Bounds(
                root.InverseTransformPoint(world.center),
                root.InverseTransformVector(world.size));
        }

        void OnStepChanged(int index, string caption)
        {
            foreach (var l in m_Labels)
                if (l != null) Destroy(l.gameObject);
            m_Labels.Clear();

            if (m_Jig?.Scene?.steps == null || index < 0 || index >= m_Jig.Scene.steps.Count) return;

            var step = m_Jig.Scene.steps[index];
            if (step.labels == null) return;

            foreach (var spec in step.labels)
            {
                if (spec == null || string.IsNullOrEmpty(spec.anchor)) continue;

                if (!m_Player.TryGetNode(spec.anchor, out var anchor))
                {
                    Debug.LogWarning($"[jig] label anchor '{spec.anchor}' matches no node - label skipped.");
                    continue;
                }

                var offset = spec.offset != null && spec.offset.Length == 3
                    ? new Vector3(spec.offset[0], spec.offset[1], spec.offset[2])
                    : Vector3.zero;

                var label = JigLabel.Create(anchor.parent, anchor, spec.text, offset);
                label.FadeIn();
                m_Labels.Add(label);
            }
        }

        // ponytail: no controller shortcut. The panel buttons already cover navigation, and a
        // legacy-Input button mapping would be a guess at Quest's binding on an input stack
        // this project no longer uses. Add an InputActionReference here if it's wanted.
    }
}
