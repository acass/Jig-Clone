using TMPro;
using UnityEngine;

namespace Jig
{
    /// A panel with a heading and a paragraph, for the things a label cannot say in three
    /// words. Built in code rather than as a prefab, the same as JigLabel and the panels -
    /// nothing to wire in the inspector, nothing to fall out of sync with the format.
    ///
    /// Not an extension of JigLabel. A label is a word on a leader line and sizes itself to
    /// its text; a callout is a fixed-width block with a background quad that has to be
    /// measured after layout. The shared parts - the fade and the billboard rule - are
    /// reused rather than reimplemented.
    public class JigCallout : MonoBehaviour
    {
        // Proportions of the wrap width. Kept relative so a callout looks the same whatever
        // model-local units the scene is authored in.
        const float PadFraction = 0.12f;
        const float TitleFraction = 0.13f;
        const float BodyFraction = 0.10f;

        Transform m_Anchor;          // null for a floating callout
        Transform m_Camera;
        LineRenderer m_Line;
        CanvasGroupFade m_Fade;

        static readonly Color PanelColor = new Color(0.09f, 0.10f, 0.13f, 1f);

        public static JigCallout Create(Transform parent, Transform anchor, JigCalloutSpec spec,
                                        Vector3 localPosition)
        {
            var go = new GameObject($"callout:{spec.title ?? spec.body}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var callout = go.AddComponent<JigCallout>();
            callout.m_Anchor = anchor;
            callout.m_Camera = Camera.main != null ? Camera.main.transform : null;

            var width = spec.width > 0f ? spec.width : 6f;
            var pad = width * PadFraction;

            var title = MakeText(go.transform, "title", spec.title, width, width * TitleFraction);
            title.fontStyle = FontStyles.Bold;

            var body = MakeText(go.transform, "body", spec.body, width, width * BodyFraction);

            // Lay the two blocks out from the top down, using the height TextMeshPro actually
            // produced. Guessing line counts from string length is what makes a callout with a
            // long body overflow its own background.
            var titleHeight = string.IsNullOrEmpty(spec.title) ? 0f : Height(title);
            var bodyHeight = string.IsNullOrEmpty(spec.body) ? 0f : Height(body);
            var gap = string.IsNullOrEmpty(spec.title) || string.IsNullOrEmpty(spec.body) ? 0f : pad * 0.4f;
            var content = titleHeight + gap + bodyHeight;

            title.rectTransform.localPosition = new Vector3(0f, content / 2f - titleHeight / 2f, 0f);
            body.rectTransform.localPosition = new Vector3(0f, content / 2f - titleHeight - gap - bodyHeight / 2f, 0f);

            callout.BuildBackground(width + pad * 2f, content + pad * 2f);
            callout.BuildLine(anchor);

            callout.m_Fade = go.AddComponent<CanvasGroupFade>();
            callout.m_Fade.Bind(callout.m_Line, title, body);

            return callout;
        }

        public void FadeIn() => m_Fade.FadeTo(1f);

        static float Height(TextMeshPro tmp)
        {
            // ForceMeshUpdate is what makes the bounds valid in the same frame the text was
            // set; without it every callout measures as zero-height on its first step.
            tmp.ForceMeshUpdate();
            return tmp.textBounds.size.y;
        }

        static TextMeshPro MakeText(Transform parent, string name, string text, float width, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text ?? string.Empty;
            tmp.fontSize = size * 20f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.textWrappingMode = TextWrappingModes.Normal;   // enableWordWrapping is [Obsolete]

            var rect = tmp.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, 0f);

            return tmp;
        }

        void BuildBackground(float width, float height)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "panel";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(transform, false);
            quad.transform.localScale = new Vector3(width, height, 1f);

            // Behind the text, not coplanar with it: a shared plane z-fights and the panel
            // flickers over its own words as the user moves.
            quad.transform.localPosition = new Vector3(0f, 0f, 0.01f);

            JigUi.Tint(quad, PanelColor);
        }

        void BuildLine(Transform anchor)
        {
            if (anchor == null) return;   // a floating callout has nothing to point at

            m_Line = gameObject.AddComponent<LineRenderer>();
            m_Line.useWorldSpace = true;
            m_Line.positionCount = 2;
            m_Line.widthMultiplier = 0.02f;
            m_Line.material = new Material(Shader.Find("Sprites/Default"));
            m_Line.startColor = m_Line.endColor = new Color(1f, 1f, 1f, 0.6f);
        }

        void LateUpdate()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null) return;
                m_Camera = Camera.main.transform;
            }

            // Same billboard rule as JigLabel: face the viewer but stay upright. LookAt(camera)
            // tips the panel when the user looks down at a model on a table.
            var flat = transform.position - m_Camera.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flat, Vector3.up);

            if (m_Line != null && m_Anchor != null)
            {
                m_Line.SetPosition(0, transform.position);
                m_Line.SetPosition(1, m_Anchor.position);
            }

        }
    }
}
