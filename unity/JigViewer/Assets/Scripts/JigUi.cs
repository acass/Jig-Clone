using UnityEngine;

namespace Jig
{
    /// Shared surface treatment for the floating panels.
    ///
    /// Panel geometry is UNLIT for two independent reasons, both of which produce an unusable
    /// panel on device if ignored:
    ///
    /// 1. The scene has no lights at all - it is a passthrough scene, lit by the real room. A
    ///    URP/Lit primitive therefore renders black.
    /// 2. Nothing else in the project references a URP shader, so the build strips it and every
    ///    CreatePrimitive object comes out magenta. JigSceneFix force-includes the shader named
    ///    here for exactly that reason - the same failure, and the same fix, as the pink glTF
    ///    models.
    public static class JigUi
    {
        public const string UnlitShader = "Universal Render Pipeline/Unlit";

        /// Makes a primitive render as a flat colour regardless of scene lighting.
        public static void Tint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;

            var shader = Shader.Find(UnlitShader);
            if (shader != null)
                renderer.material.shader = shader;
            else
                Debug.LogWarning($"[jig] shader '{UnlitShader}' not found - panel will render magenta. " +
                                 "Run Jig/Fix Scene to force-include it.");

            renderer.material.color = color;
        }
    }
}
