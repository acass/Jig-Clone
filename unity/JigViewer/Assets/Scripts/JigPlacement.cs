using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Jig
{
    /// Places the Jig on a real surface.
    ///
    /// Quest does NOT scan for planes at runtime: Meta OpenXR returns the planes already
    /// stored in the headset's Room Setup / Scene Model, and only up-facing classifications
    /// (Table, Floor, Bed). If the user has never run Space Setup there will be zero planes
    /// forever, no matter how long we wait - so we time out and place the model in front of
    /// them rather than leaving them staring at an empty room.
    [RequireComponent(typeof(ARRaycastManager))]
    public class JigPlacement : MonoBehaviour
    {
        [Tooltip("How long to wait for Room Setup planes before falling back to floating placement.")]
        public float planeWaitSeconds = 3f;

        [Tooltip("Distance ahead of the user for fallback placement.")]
        public float fallbackDistance = 1.5f;

        public ARPlaneManager planeManager;
        public ARAnchorManager anchorManager;
        public Transform content;

        [Tooltip("Controller ray used for re-placement. Assigned by JigSceneFix.")]
        public XRRayInteractor rayInteractor;

        [Tooltip("Button that re-places the Jig. Deliberately not the trigger - that is select.")]
        public InputActionProperty placeAction;

        ARRaycastManager m_Raycaster;
        readonly List<ARRaycastHit> m_Hits = new List<ARRaycastHit>();

        ARAnchor m_Anchor;

        public bool Placed { get; private set; }
        public string StatusMessage { get; private set; } = "Looking for a surface...";

        void Awake() => m_Raycaster = GetComponent<ARRaycastManager>();

        void OnEnable()
        {
            // An InputActionProperty holding an inline action is NOT auto-enabled. (XRI enables
            // its own XRInputButtonReader actions in XRBaseInputInteractor.OnEnable; nothing does
            // it for a plain InputActionProperty.) Forgetting this is silent - the button simply
            // never fires.
            placeAction.action?.Enable();
            StartCoroutine(WaitForPlanes());
        }

        void OnDisable() => placeAction.action?.Disable();

        void Update()
        {
            if (placeAction.action == null || !placeAction.action.WasPressedThisFrame()) return;
            if (rayInteractor == null) return;

            var origin = rayInteractor.rayOriginTransform != null
                ? rayInteractor.rayOriginTransform
                : rayInteractor.transform;

            if (!TryPlaceFromRay(new Ray(origin.position, origin.forward)))
                Debug.Log("[jig] re-place: ray hit no plane.");
        }

        IEnumerator WaitForPlanes()
        {
            var deadline = Time.time + planeWaitSeconds;

            while (Time.time < deadline)
            {
                if (planeManager != null && planeManager.trackables.count > 0)
                {
                    StatusMessage = "Point at a surface and press B.";
                    yield break;
                }
                yield return null;
            }

            // The user may have already ray-placed inside the wait window; do not stomp them.
            if (Placed) yield break;

            StatusMessage = "No room set up - run Space Setup in headset settings for surface placement.";
            PlaceInFront();
        }

        /// Ray from a controller or hand into the room; first hit on a plane wins.
        /// Returns whether a plane was hit - anchoring then completes asynchronously.
        public bool TryPlaceFromRay(Ray ray)
        {
            if (m_Raycaster.Raycast(ray, m_Hits, TrackableType.PlaneWithinPolygon) && m_Hits.Count > 0)
            {
                var hit = m_Hits[0].pose.position;
                _ = PlaceAt(new Pose(hit, FacingUser(hit)));
                return true;
            }
            return false;
        }

        void PlaceInFront()
        {
            var cam = Camera.main;
            if (cam == null) return;

            var ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;

            var at = cam.transform.position + ahead.normalized * fallbackDistance;
            _ = PlaceAt(new Pose(at, FacingUser(at)));
        }

        /// Both placement paths funnel through here so anchoring is never bypassed.
        ///
        /// Writing content.position directly is what makes the model drift: the pose is fixed in
        /// Unity world space, but the headset revises its idea of where that space is on every
        /// tracking update. Parenting under an ARAnchor lets AR Foundation apply those revisions.
        async Awaitable PlaceAt(Pose pose)
        {
            // Meta caps live anchors, so retire the previous one rather than accumulating.
            if (m_Anchor != null)
            {
                anchorManager.TryRemoveAnchor(m_Anchor);
                m_Anchor = null;
            }

            if (anchorManager != null)
            {
                // TryAddAnchorAsync is the only creation path on Quest: AddAnchor(Pose) does not
                // exist in AR Foundation 6.x, and AttachAnchor returns null because
                // MetaOpenXRAnchorSubsystem reports supportsTrackableAttachments = false.
                var result = await anchorManager.TryAddAnchorAsync(pose);

                if (result.status.IsSuccess())
                {
                    m_Anchor = result.value;
                    content.SetParent(m_Anchor.transform, worldPositionStays: false);
                    content.localPosition = Vector3.zero;
                    content.localRotation = Quaternion.identity;
                    Placed = true;
                    StatusMessage = string.Empty;
                    return;
                }

                Debug.LogWarning($"[jig] anchor creation failed ({result.status}); placing unanchored.");
            }

            // ponytail: unanchored fallback. Drifts, but a session that cannot anchor should still
            // show the model rather than nothing.
            content.SetParent(null, worldPositionStays: false);
            content.SetPositionAndRotation(pose.position, pose.rotation);
            Placed = true;
        }

        /// Yaw-only rotation so the Jig faces the viewer without tipping over.
        static Quaternion FacingUser(Vector3 at)
        {
            var cam = Camera.main;
            if (cam == null) return Quaternion.identity;

            var away = at - cam.transform.position;
            away.y = 0f;
            return away.sqrMagnitude < 0.0001f
                ? Quaternion.identity
                : Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
