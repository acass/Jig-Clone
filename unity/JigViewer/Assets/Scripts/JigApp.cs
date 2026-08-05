using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Jig
{
    /// Wires the slice together: fetch manifest -> show picker -> load a Jig -> bind player ->
    /// build UI. Loading is re-entrant: picking a second Jig tears the first one down in place.
    [RequireComponent(typeof(JigLoader))]
    public class JigApp : MonoBehaviour
    {
        [Tooltip("Root the Jig is parented to. Placement and grab own this transform.")]
        public Transform contentRoot;

        public JigPlacement placement;

        JigLoader m_Loader;
        JigPlayer m_Player;
        LoadedJig m_Jig;
        JigStepPanel m_Panel;
        BoxCollider m_GrabBox;

        // Bumped on every load request. A load whose generation is stale by the time its download
        // finishes throws its result away instead of racing the newer one into the scene.
        int m_LoadGeneration;

        public bool IsLoading { get; private set; }

        readonly List<JigLabel> m_Labels = new List<JigLabel>();

        async void Start()
        {
            m_Loader = GetComponent<JigLoader>();

            // Magenta in a build almost always means the pipeline the materials were authored
            // for is not the one running, so say which one is live.
            Debug.Log($"[jig] render pipeline: " +
                      $"{UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"}");

            // Controllers connect a moment after the session starts, so dump twice.
            LogInputDevices("startup");
            Invoke(nameof(LogInputDevicesLate), 5f);

            if (contentRoot == null)
            {
                contentRoot = new GameObject("jig-content").transform;
                contentRoot.SetParent(transform, false);
            }

            JigManifest manifest;
            try
            {
                manifest = await m_Loader.LoadManifest();
            }
            catch (JigContentException e)
            {
                // Content problems are expected and must be legible, not a stack trace on a
                // headset where nobody can read the console.
                Debug.LogError($"[jig] {e.Message}");
                return;
            }

            Debug.Log($"[jig] manifest lists {manifest.jigs.Count} jig(s)");
            if (manifest.jigs.Count == 0) return;

            // Parented to this object, not contentRoot: contentRoot gets anchored by placement,
            // grabbed by the user, and emptied on every jig switch.
            JigPickerPanel.Create(transform, manifest,
                                  entry => { if (!IsLoading) _ = LoadJig(entry); });

            // With a single entry there is nothing to pick, so behave as before and just load it.
            if (manifest.jigs.Count == 1)
                await LoadJig(manifest.jigs[0]);
        }

        void LogInputDevicesLate() => LogInputDevices("t+5s");

        /// Which input devices the app can actually see. If no XRController appears here, the
        /// controller bindings cannot resolve and nothing is pressable no matter what the scene
        /// contains - which is a completely different problem from an unaimable ray.
        static void LogInputDevices(string when)
        {
            // Only the hand-tagged devices: everything else is keyboards and the on-screen mouse.
            // Controllers connect a few seconds after the session starts, so an empty list at
            // startup is normal and not a fault.
            foreach (var d in UnityEngine.InputSystem.InputSystem.devices)
                if (d.usages.Count > 0)
                    Debug.Log($"[jig] controller ({when}): {d.layout} usages=[{string.Join(",", d.usages)}]");
        }

        async Task LoadJig(JigEntry entry)
        {
            var generation = ++m_LoadGeneration;
            IsLoading = true;

            UnloadCurrent();

            try
            {
                Debug.Log($"[jig] loading '{entry.id}'");
                var jig = await m_Loader.LoadJig(entry, contentRoot);

                if (generation != m_LoadGeneration)
                {
                    // A newer pick superseded this one mid-download.
                    Destroy(jig.Model);
                    return;
                }

                m_Jig = jig;

                // The player is reused across loads: Bind fully resets its own state, and
                // Destroy on a component is deferred to end of frame, so destroying and
                // re-adding would leave two live players writing the same transforms.
                if (m_Player == null)
                {
                    m_Player = gameObject.AddComponent<JigPlayer>();
                    m_Player.StepChanged += OnStepChanged;   // subscribe once, ever
                }
                m_Player.Bind(m_Jig);

                // Order matters: after Bind (which applies scene.scale, so the collider is sized
                // to the scaled model) and before the step panel exists (ComputeLocalBounds walks
                // every child renderer of contentRoot and would otherwise include the panel).
                // The await above is what guarantees the old panel's deferred Destroy has landed
                // by now - do not "optimise" it away.
                EnableGrab(contentRoot);

                m_Panel = JigStepPanel.Create(contentRoot, m_Player, new Vector3(0.35f, 0.15f, 0f));

                OnStepChanged(m_Player.CurrentStep, m_Player.CurrentCaption);
            }
            catch (JigContentException e)
            {
                Debug.LogError($"[jig] {e.Message}");
            }
            finally
            {
                if (generation == m_LoadGeneration) IsLoading = false;
            }
        }

        /// Destroys the content of the current Jig. Synchronous, and must stay that way: it runs
        /// before the first await in LoadJig so the scene is never left showing two Jigs.
        void UnloadCurrent()
        {
            if (m_Panel != null)
            {
                Destroy(m_Panel.gameObject);   // its OnDestroy unsubscribes it from StepChanged
                m_Panel = null;
            }

            // The label GameObjects are parented inside the model and die with it.
            m_Labels.Clear();

            if (m_Jig?.Model != null) Destroy(m_Jig.Model);
            m_Jig = null;

            // Player, rigidbody, grab interactable and collider are all reused - see LoadJig.
        }

        void EnableGrab(Transform root)
        {
            // Grab acts on the ROOT only. Step tweens write child transforms, so the two never
            // contend for the same transform.
            var body = root.gameObject.GetComponent<Rigidbody>() ?? root.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            // Idempotent: re-adding the interactable would unregister and re-register it with the
            // interaction manager, which strands a selection if the user is holding the model.
            if (root.gameObject.GetComponent<XRGrabInteractable>() == null)
            {
                var grab = root.gameObject.AddComponent<XRGrabInteractable>();
                grab.throwOnDetach = false;
                grab.useDynamicAttach = true;
            }

            // Only the extents are per-Jig, so keep the collider and resize it.
            m_GrabBox ??= root.gameObject.AddComponent<BoxCollider>();

            var bounds = ComputeLocalBounds(root);
            m_GrabBox.center = bounds.center;
            m_GrabBox.size = bounds.size;

            Debug.Log($"[jig] grab collider center={bounds.center} size={bounds.size}");
        }

        /// Bounds of the MODEL only, expressed in the root's local space.
        ///
        /// Built by transforming each renderer's world-space AABB corners into local space, one
        /// corner at a time. The obvious shortcut - InverseTransformVector on the world size -
        /// is wrong: size is an extent, not a direction, so rotating it mixes the axes and can
        /// hand BoxCollider a negative size. The root IS rotated (FacingUser on placement, and
        /// again once it is parented under an anchor), and the resulting oversized collider sits
        /// in front of the user swallowing every raycast before it reaches the model.
        static Bounds ComputeLocalBounds(Transform root)
        {
            var bounds = new Bounds();
            var started = false;

            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                // The panels and callouts are children of the root but are not part of the model,
                // and including them inflates the grab volume over the UI the user is trying to
                // click. Filtering by component keeps this independent of construction order.
                if (r.GetComponentInParent<JigStepPanel>() != null) continue;
                if (r.GetComponentInParent<JigLabel>() != null) continue;

                var wb = r.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? wb.min.x : wb.max.x,
                        (i & 2) == 0 ? wb.min.y : wb.max.y,
                        (i & 4) == 0 ? wb.min.z : wb.max.z);

                    var local = root.InverseTransformPoint(corner);
                    if (!started)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        started = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }

            return started ? bounds : new Bounds(Vector3.zero, Vector3.one * 0.2f);
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

        // ponytail: no controller shortcut for step navigation. The panel buttons cover it now
        // that the scene actually has an interactor to press them with (see JigSceneFix).
        //
        // ponytail: no CancellationToken on load. A superseded download still completes and is
        // thrown away; on a LAN that is a few hundred ms of wasted bandwidth, not a bug. A real
        // token would mean threading it through JigLoader.Fetch and GltfImport.Load.
    }
}
