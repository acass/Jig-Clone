using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace Jig
{
    /// Draws the controller ray so the user can actually aim.
    ///
    /// Built at runtime rather than serialized into the scene because the LineRenderer needs a
    /// material, and a material asset referencing a URP shader is one more thing to create,
    /// track and have stripped from the build. The shader named by JigUi is already force-included
    /// by JigSceneFix, so constructing the material here is the shorter path.
    ///
    /// The colour comes from the material, NOT from LineRenderer's colour gradient: gradients are
    /// vertex colours, and URP/Unlit does not read vertex colour, so a gradient-only approach
    /// draws a white line no matter what you set.
    [DisallowMultipleComponent]
    public class JigRayVisual : MonoBehaviour
    {
        public Color rayColor = new Color(0.3f, 0.8f, 1f, 1f);
        public float width = 0.004f;
        public float length = 5f;

        void Awake()
        {
            var line = GetComponent<LineRenderer>() ?? gameObject.AddComponent<LineRenderer>();

            var shader = Shader.Find(JigUi.UnlitShader);
            if (shader == null)
            {
                Debug.LogWarning($"[jig] '{JigUi.UnlitShader}' missing - ray will be magenta or invisible.");
            }
            else
            {
                var mat = new Material(shader) { color = rayColor };
                line.material = mat;
            }

            line.widthMultiplier = width;
            line.numCapVertices = 2;
            line.useWorldSpace = true;

            var visual = gameObject.AddComponent<XRInteractorLineVisual>();
            visual.lineWidth = width;
            visual.overrideInteractorLineLength = true;
            visual.lineLength = length;
            visual.stopLineAtFirstRaycastHit = true;
        }
    }
}
