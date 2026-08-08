using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Jig
{
    /// Drives a loaded Jig through its steps.
    ///
    /// Only ever writes to CHILD node transforms. The root is owned by placement and grab,
    /// so a user can pick the model up mid-step without the two fighting over one transform.
    public class JigPlayer : MonoBehaviour
    {
        public event Action<int, string> StepChanged;   // index, caption

        JigScene m_Scene;
        Transform m_Root;
        List<Dictionary<string, NodeState>> m_Resolved;

        readonly Dictionary<string, Transform> m_Nodes = new Dictionary<string, Transform>();
        readonly Dictionary<Transform, (Vector3 pos, Quaternion rot, Vector3 scale)> m_Rest =
            new Dictionary<Transform, (Vector3, Quaternion, Vector3)>();

        // Every key in m_Nodes that points at a given transform (full path, plus the leaf-name
        // alias when unique). Needed because the resolved table may be keyed by either.
        readonly Dictionary<Transform, List<string>> m_Aliases = new Dictionary<Transform, List<string>>();

        // Transforms the scene names explicitly, by any of their paths. SetVisible refuses to
        // descend into these so a group can never take an authored part down with it.
        readonly HashSet<Transform> m_Authored = new HashSet<Transform>();

        Coroutine m_Tween;

        public int StepCount => m_Resolved?.Count ?? 0;
        public int CurrentStep { get; private set; } = -1;
        public string CurrentCaption =>
            m_Scene != null && CurrentStep >= 0 && CurrentStep < m_Scene.steps.Count
                ? m_Scene.steps[CurrentStep].caption
                : string.Empty;

        public void Bind(LoadedJig jig)
        {
            // A rebind (picker switching jigs) leaves the previous tween running against
            // transforms that are about to be destroyed. TweenTo null-guards them so it will not
            // throw, but it keeps writing for up to `duration` seconds and would then stomp the
            // new jig's step 0.
            if (m_Tween != null)
            {
                StopCoroutine(m_Tween);
                m_Tween = null;
            }

            m_Scene = jig.Scene;
            m_Root = jig.Model.transform;
            m_Root.localScale = Vector3.one * m_Scene.scale;

            m_Nodes.Clear();
            m_Rest.Clear();
            m_Aliases.Clear();
            m_Authored.Clear();
            IndexHierarchy(m_Root);

            m_Resolved = JigStepResolver.Resolve(m_Scene, w => Debug.LogWarning($"[jig] {w}"));

            m_Authored.Clear();
            foreach (var step in m_Resolved)
                foreach (var path in step.Keys)
                    if (m_Nodes.TryGetValue(path, out var authored))
                        m_Authored.Add(authored);

            // Warn once, at load, for paths that will never resolve - far easier to debug than
            // silently motionless geometry on a headset.
            foreach (var step in m_Resolved)
                foreach (var path in step.Keys)
                    if (!m_Nodes.ContainsKey(path))
                        Debug.LogWarning($"[jig] no node matches path '{path}' - that entry will do nothing.");

            CurrentStep = -1;
            GoTo(0, instant: true);
        }

        public void Next() => GoTo(CurrentStep + 1);
        public void Prev() => GoTo(CurrentStep - 1);

        public void GoTo(int index, bool instant = false)
        {
            if (m_Resolved == null || m_Resolved.Count == 0) return;

            index = Mathf.Clamp(index, 0, m_Resolved.Count - 1);
            if (index == CurrentStep) return;

            CurrentStep = index;

            if (m_Tween != null) StopCoroutine(m_Tween);

            var duration = instant ? 0f : Mathf.Max(0f, m_Scene.steps[index].duration);
            m_Tween = StartCoroutine(TweenTo(m_Resolved[index], duration));

            StepChanged?.Invoke(CurrentStep, CurrentCaption);
        }

        IEnumerator TweenTo(Dictionary<string, NodeState> target, float duration)
        {
            // Snapshot where things ACTUALLY are, not where the previous step said they should
            // be. Interrupting a tween mid-flight then retargets from the current pose instead
            // of snapping back to the last completed step.
            var from = new List<(Transform t, Vector3 p, Quaternion r, Vector3 s)>();
            var to = new List<(Vector3 p, Quaternion r, Vector3 s)>();

            foreach (var kv in m_Rest)
            {
                var t = kv.Key;
                var state = StateFor(t, target);

                var rest = kv.Value;
                var targetPos = rest.pos + state.Move;
                var targetRot = rest.rot * Quaternion.Euler(state.Rotate);
                var targetScale = Vector3.Scale(rest.scale, state.Scale);

                from.Add((t, t.localPosition, t.localRotation, t.localScale));
                to.Add((targetPos, targetRot, targetScale));

                // Show immediately; hide only once the motion has finished, so a part is never
                // yanked out of existence while the user is watching it move.
                if (state.Visible) SetVisible(t, true);
            }

            for (float e = 0f; duration > 0f && e < duration; e += Time.deltaTime)
            {
                var k = Mathf.SmoothStep(0f, 1f, e / duration);
                for (int i = 0; i < from.Count; i++)
                {
                    var f = from[i];
                    if (f.t == null) continue;
                    f.t.localPosition = Vector3.Lerp(f.p, to[i].p, k);
                    f.t.localRotation = Quaternion.Slerp(f.r, to[i].r, k);
                    f.t.localScale = Vector3.Lerp(f.s, to[i].s, k);
                }
                yield return null;
            }

            for (int i = 0; i < from.Count; i++)
            {
                var f = from[i];
                if (f.t == null) continue;
                f.t.localPosition = to[i].p;
                f.t.localRotation = to[i].r;
                f.t.localScale = to[i].s;
            }

            foreach (var kv in m_Rest)
                if (!StateFor(kv.Key, target).Visible)
                    SetVisible(kv.Key, false);

            m_Tween = null;
        }

        /// Shows or hides a part, including the sub-objects that are not parts in their own
        /// right.
        ///
        /// This used to touch only GetComponents<Renderer>() on the node itself. That is wrong
        /// for a multi-primitive mesh: glTFast keeps primitive 0 on the node and gives every
        /// ADDITIONAL primitive its own child GameObject, so `visible: false` on a 2-primitive
        /// part hid half of it and left the rest floating.
        ///
        /// Descent stops at any transform the scene authors separately, which preserves the
        /// original rule that hiding a group must not silently take its named parts with it.
        void SetVisible(Transform t, bool visible)
        {
            foreach (var r in t.GetComponents<Renderer>())
                r.enabled = visible;

            foreach (Transform child in t)
                if (!m_Authored.Contains(child))
                    SetVisible(child, visible);
        }

        void IndexHierarchy(Transform root)
        {
            var byLeaf = new Dictionary<string, List<Transform>>();

            void Walk(Transform t, string prefix)
            {
                foreach (Transform child in t)
                {
                    var path = string.IsNullOrEmpty(prefix) ? child.name : $"{prefix}/{child.name}";
                    m_Nodes[path] = child;
                    m_Rest[child] = (child.localPosition, child.localRotation, child.localScale);
                    m_Aliases[child] = new List<string> { path };

                    if (!byLeaf.TryGetValue(child.name, out var list))
                        byLeaf[child.name] = list = new List<Transform>();
                    list.Add(child);

                    Walk(child, path);
                }
            }

            Walk(root, string.Empty);

            // glTFast parents the glTF scene under our container, so authored paths would
            // otherwise all need a scene-name prefix. Accept the bare leaf name too, but only
            // where it is unambiguous.
            foreach (var kv in byLeaf)
            {
                if (kv.Value.Count != 1 || m_Nodes.ContainsKey(kv.Key)) continue;
                m_Nodes[kv.Key] = kv.Value[0];
                m_Aliases[kv.Value[0]].Add(kv.Key);
            }
        }

        /// Resolved state for a transform, matched against any of the keys it is known by.
        NodeState StateFor(Transform t, Dictionary<string, NodeState> target)
        {
            if (m_Aliases.TryGetValue(t, out var keys))
                foreach (var key in keys)
                    if (target.TryGetValue(key, out var state))
                        return state;
            return NodeState.Rest;
        }

        public bool TryGetNode(string path, out Transform node) => m_Nodes.TryGetValue(path, out node);

        /// Where `node` will be once the current step has finished animating.
        ///
        /// StepChanged fires as the tween STARTS, so anything positioned from
        /// node.localPosition at that moment is placed where the part is coming FROM. Labels
        /// were being pinned to the previous step's position for exactly that reason.
        public Vector3 ResolvedLocalPosition(Transform node)
        {
            if (node == null || !m_Rest.TryGetValue(node, out var rest)) return Vector3.zero;
            if (m_Resolved == null || CurrentStep < 0 || CurrentStep >= m_Resolved.Count) return rest.pos;
            return rest.pos + StateFor(node, m_Resolved[CurrentStep]).Move;
        }
    }
}
