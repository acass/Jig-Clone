using System;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Jig
{
    /// One tappable row per manifest entry, floating beside the user.
    ///
    /// Same construction as JigStepPanel - primitives plus XRSimpleInteractable, no world-space
    /// uGUI Canvas. Parented to the Jig App object rather than the content root, because the
    /// content root is what placement anchors and grab moves; the picker must stay put and must
    /// survive a jig being unloaded.
    public class JigPickerPanel : MonoBehaviour
    {
        [Tooltip("Distance ahead of the user, and how far to the left, when it first appears.")]
        public float distance = 1.2f;
        public float leftOffset = 0.55f;

        [Tooltip("Grace period before placing, so the headset has a real pose to place against.")]
        public float settleSeconds = 1f;

        [Tooltip("Look this far off the panel for this long and it comes back to you.")]
        public float recentreAngle = 75f;
        public float recentreAfterSeconds = 2f;

        Transform m_Camera;
        bool m_Placed;
        float m_Spawned;
        float m_OutOfViewFor;

        static readonly Color RowColor = new Color(0.15f, 0.15f, 0.18f, 1f);

        public static JigPickerPanel Create(Transform parent, JigManifest manifest, Action<JigEntry> onPick)
        {
            var go = new GameObject("jig-picker");
            go.transform.SetParent(parent, false);

            var panel = go.AddComponent<JigPickerPanel>();
            panel.m_Spawned = Time.time;
            panel.Build(manifest, onPick);

            return panel;
        }

        void Build(JigManifest manifest, Action<JigEntry> onPick)
        {
            var header = MakeText("header", new Vector3(0f, 0.07f, 0f), 0.0175f);
            header.text = "Jigs";

            for (int i = 0; i < manifest.jigs.Count; i++)
            {
                // Capture per iteration: a lambda closing over the loop variable would hand every
                // row the last entry.
                var entry = manifest.jigs[i];
                if (entry == null) continue;

                MakeRow(entry, -i * 0.07f, onPick);
            }
        }

        void MakeRow(JigEntry entry, float y, Action<JigEntry> onPick)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"jig-{entry.id}";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            go.transform.localScale = new Vector3(0.30f, 0.05f, 0.02f);

            JigUi.Tint(go, RowColor);

            var interactable = go.AddComponent<XRSimpleInteractable>();
            interactable.selectEntered.AddListener(_ => onPick(entry));


            var label = MakeText($"jig-{entry.id}-label", new Vector3(0f, y, -0.02f), 0.015f);
            label.text = string.IsNullOrEmpty(entry.title) ? entry.id : entry.title;
        }

        TextMeshPro MakeText(string name, Vector3 localPos, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = size * 20f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            var rect = tmp.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0.35f, 0.06f);

            return tmp;
        }

        void LateUpdate()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null) return;
                m_Camera = Camera.main.transform;
            }

            // The picker hangs off the app object, which sits at the world origin - unlike the
            // step panel, which rides the content root that placement puts in front of the user.
            // So it has to position itself, once, against a settled head pose.
            if (!m_Placed && Time.time - m_Spawned >= settleSeconds)
            {
                var ahead = m_Camera.forward;
                ahead.y = 0f;
                if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;
                ahead.Normalize();

                var left = new Vector3(-ahead.z, 0f, ahead.x);   // yaw-left of the view direction
                transform.position = m_Camera.position + ahead * distance + left * leftOffset;
                m_Placed = true;
            }

            // Recentre when the user has looked away from it - a one-shot placement against an
            // unsettled head pose can leave the picker behind the user with no way back to it.
            var toPanel = transform.position - m_Camera.position;
            toPanel.y = 0f;
            var facing = m_Camera.forward;
            facing.y = 0f;
            if (m_Placed && toPanel.sqrMagnitude > 0.01f && facing.sqrMagnitude > 0.0001f &&
                Vector3.Angle(facing, toPanel) > recentreAngle)
            {
                m_OutOfViewFor += Time.deltaTime;
                if (m_OutOfViewFor > recentreAfterSeconds)
                {
                    m_Placed = false;          // re-place against the current head pose
                    m_OutOfViewFor = 0f;
                    m_Spawned = Time.time - settleSeconds;
                }
            }
            else
            {
                m_OutOfViewFor = 0f;
            }

            var flat = transform.position - m_Camera.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        }

        // ponytail: placed once, not follow-the-user. The user can walk away from the picker,
        // same as they can walk away from the model. Add a recentre button if that annoys.

        // ponytail: duplicates JigStepPanel's text and billboard helpers rather than extracting a
        // shared base class. Two call sites is not a pattern.
    }
}
