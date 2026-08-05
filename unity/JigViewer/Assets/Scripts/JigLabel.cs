using TMPro;
using UnityEngine;

namespace Jig
{
    /// A world-space callout: text that faces the viewer, with a leader line back to the part
    /// it is talking about. Built in code rather than as a prefab so there is nothing to wire
    /// up in the inspector and nothing to get out of sync with the format.
    public class JigLabel : MonoBehaviour
    {
        Transform m_Anchor;
        LineRenderer m_Line;
        TextMeshPro m_Text;
        Transform m_Camera;
        CanvasGroupFade m_Fade;

        public static JigLabel Create(Transform parent, Transform anchor, string text, Vector3 localOffset)
        {
            var go = new GameObject($"label:{text}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = anchor.localPosition + localOffset;

            var label = go.AddComponent<JigLabel>();
            label.m_Anchor = anchor;
            label.m_Camera = Camera.main != null ? Camera.main.transform : null;

            var textGo = new GameObject("text");
            textGo.transform.SetParent(go.transform, false);
            label.m_Text = textGo.AddComponent<TextMeshPro>();
            label.m_Text.text = text;
            label.m_Text.fontSize = 2.5f;
            label.m_Text.alignment = TextAlignmentOptions.Center;
            label.m_Text.color = Color.white;

            label.m_Line = go.AddComponent<LineRenderer>();
            label.m_Line.useWorldSpace = true;
            label.m_Line.positionCount = 2;
            label.m_Line.widthMultiplier = 0.02f;
            label.m_Line.material = new Material(Shader.Find("Sprites/Default"));
            label.m_Line.startColor = label.m_Line.endColor = new Color(1f, 1f, 1f, 0.6f);

            label.m_Fade = go.AddComponent<CanvasGroupFade>();
            label.m_Fade.Bind(label.m_Text, label.m_Line);

            return label;
        }

        public void FadeIn() => m_Fade.FadeTo(1f);

        void LateUpdate()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null) return;
                m_Camera = Camera.main.transform;
            }

            // Billboard: face the viewer, upright. Not LookAt(camera) - that tips the text
            // when the user looks down at a model on a table.
            var flat = transform.position - m_Camera.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flat, Vector3.up);

            if (m_Anchor != null)
            {
                m_Line.SetPosition(0, transform.position);
                m_Line.SetPosition(1, m_Anchor.position);
            }
        }
    }

    /// Minimal alpha fade for the label's text and leader line. TextMeshPro and LineRenderer
    /// do not share a fade mechanism, so this drives both.
    public class CanvasGroupFade : MonoBehaviour
    {
        TextMeshPro m_Text;
        LineRenderer m_Line;
        float m_Alpha;
        float m_Target;

        public void Bind(TextMeshPro text, LineRenderer line)
        {
            m_Text = text;
            m_Line = line;
            Apply(0f);
        }

        public void FadeTo(float target) => m_Target = target;

        void Update()
        {
            if (Mathf.Approximately(m_Alpha, m_Target)) return;
            m_Alpha = Mathf.MoveTowards(m_Alpha, m_Target, Time.deltaTime * 3f);
            Apply(m_Alpha);
        }

        void Apply(float a)
        {
            m_Alpha = a;
            if (m_Text != null) m_Text.alpha = a;
            if (m_Line != null)
            {
                var c = m_Line.startColor;
                c.a = a * 0.6f;
                m_Line.startColor = m_Line.endColor = c;
            }
        }
    }
}
