using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

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
        public Transform content;

        ARRaycastManager m_Raycaster;
        readonly List<ARRaycastHit> m_Hits = new List<ARRaycastHit>();

        public bool Placed { get; private set; }
        public string StatusMessage { get; private set; } = "Looking for a surface...";

        void Awake() => m_Raycaster = GetComponent<ARRaycastManager>();

        void OnEnable() => StartCoroutine(WaitForPlanes());

        IEnumerator WaitForPlanes()
        {
            var deadline = Time.time + planeWaitSeconds;

            while (Time.time < deadline)
            {
                if (planeManager != null && planeManager.trackables.count > 0)
                {
                    StatusMessage = "Point at a surface and pull the trigger.";
                    yield break;
                }
                yield return null;
            }

            StatusMessage = "No room set up - run Space Setup in headset settings for surface placement.";
            PlaceInFront();
        }

        /// Ray from a controller or hand into the room; first hit on a plane wins.
        public bool TryPlaceFromRay(Ray ray)
        {
            if (m_Raycaster.Raycast(ray, m_Hits, TrackableType.PlaneWithinPolygon) && m_Hits.Count > 0)
            {
                var pose = m_Hits[0].pose;
                content.position = pose.position;
                content.rotation = FacingUser(pose.position);
                Placed = true;
                StatusMessage = string.Empty;
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

            content.position = cam.transform.position + ahead.normalized * fallbackDistance;
            content.rotation = FacingUser(content.position);
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
