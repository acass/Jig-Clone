using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Jig
{
    /// Prev / Next / step dots / caption, floating beside the model.
    ///
    /// Built from box colliders and XRSimpleInteractable rather than a world-space uGUI
    /// Canvas: it needs two buttons, and a Canvas would drag in a raycaster, an event camera
    /// and a scale convention for that. Works with both ray and poke because XRI treats any
    /// interactable the same way.
    public class JigStepPanel : MonoBehaviour
    {
        JigPlayer m_Player;
        TextMeshPro m_Caption;
        TextMeshPro m_Counter;
        Transform m_Camera;

        readonly List<Transform> m_Dots = new List<Transform>();

        static readonly Color Idle = new Color(0.45f, 0.45f, 0.50f, 1f);
        static readonly Color Active = Color.white;
        static readonly Color ButtonColor = new Color(0.18f, 0.18f, 0.22f, 1f);

        public static JigStepPanel Create(Transform parent, JigPlayer player, Vector3 localOffset)
        {
            var go = new GameObject("step-panel");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localOffset;

            var panel = go.AddComponent<JigStepPanel>();
            panel.m_Player = player;
            panel.Build();

            player.StepChanged += panel.OnStepChanged;
            panel.OnStepChanged(player.CurrentStep, player.CurrentCaption);

            return panel;
        }

        void Build()
        {
            m_Caption = MakeText("caption", new Vector3(0f, 0.10f, 0f), 0.025f);
            m_Caption.alignment = TextAlignmentOptions.Center;

            m_Counter = MakeText("counter", new Vector3(0f, -0.10f, 0f), 0.0175f);
            m_Counter.alignment = TextAlignmentOptions.Center;
            m_Counter.color = Idle;

            MakeButton("prev", new Vector3(-0.18f, 0f, 0f), "<", () => m_Player.Prev());
            MakeButton("next", new Vector3(0.18f, 0f, 0f), ">", () => m_Player.Next());

            BuildDots();
        }

        void BuildDots()
        {
            for (int i = 0; i < m_Player.StepCount; i++)
            {
                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dot.name = $"dot{i}";
                Destroy(dot.GetComponent<Collider>());
                dot.transform.SetParent(transform, false);
                JigUi.Tint(dot, Idle);

                // Centre the row on the panel regardless of step count.
                var x = (i - (m_Player.StepCount - 1) / 2f) * 0.045f;
                dot.transform.localPosition = new Vector3(x, -0.05f, 0f);
                dot.transform.localScale = Vector3.one * 0.018f;

                m_Dots.Add(dot.transform);
            }
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
            rect.sizeDelta = new Vector2(0.6f, 0.12f);

            return tmp;
        }

        void MakeButton(string name, Vector3 localPos, string glyph, System.Action onPress)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(0.07f, 0.07f, 0.02f);
            JigUi.Tint(go, ButtonColor);

            var interactable = go.AddComponent<XRSimpleInteractable>();
            interactable.selectEntered.AddListener(_ => onPress());

            var label = MakeText($"{name}-glyph", localPos + new Vector3(0f, 0f, -0.02f), 0.02f);
            label.text = glyph;
        }

        void OnStepChanged(int index, string caption)
        {
            if (m_Caption != null) m_Caption.text = caption ?? string.Empty;
            if (m_Counter != null) m_Counter.text = $"{index + 1} / {m_Player.StepCount}";

            for (int i = 0; i < m_Dots.Count; i++)
            {
                var r = m_Dots[i].GetComponent<Renderer>();
                if (r != null) r.material.color = i == index ? Active : Idle;
            }
        }

        void LateUpdate()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null) return;
                m_Camera = Camera.main.transform;
            }

            var flat = transform.position - m_Camera.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        }

        void OnDestroy()
        {
            if (m_Player != null) m_Player.StepChanged -= OnStepChanged;
        }
    }
}
