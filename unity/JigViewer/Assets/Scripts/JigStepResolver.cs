using System;
using System.Collections.Generic;
using UnityEngine;

namespace Jig
{
    /// Resolved offset-from-rest state for one node at one step.
    public struct NodeState
    {
        public Vector3 Move;
        public Vector3 Rotate;
        public Vector3 Scale;
        public bool Visible;

        public static NodeState Rest => new NodeState
        {
            Move = Vector3.zero,
            Rotate = Vector3.zero,
            Scale = Vector3.one,
            Visible = true,
        };
    }

    /// Flattens a Jig's steps into one fully-resolved state table per step, applying the
    /// inherit-if-omitted rule. Pure function over data - no scene graph, no Unity objects -
    /// so it can be tested off-device, which is where the inheritance bugs actually live.
    public static class JigStepResolver
    {
        public static List<Dictionary<string, NodeState>> Resolve(JigScene scene, Action<string> warn = null)
        {
            var steps = new List<Dictionary<string, NodeState>>();
            if (scene?.steps == null)
                return steps;

            // Running state, carried forward across steps.
            var current = new Dictionary<string, NodeState>();

            for (int i = 0; i < scene.steps.Count; i++)
            {
                var step = scene.steps[i];
                if (step?.nodes != null)
                {
                    foreach (var n in step.nodes)
                    {
                        if (n == null || string.IsNullOrEmpty(n.path))
                        {
                            warn?.Invoke($"step {i}: node entry with no path, skipped");
                            continue;
                        }

                        if (!current.TryGetValue(n.path, out var s))
                            s = NodeState.Rest;

                        if (n.move != null) s.Move = ToVec3(n.move, s.Move, i, n.path, "move", warn);
                        if (n.rotate != null) s.Rotate = ToVec3(n.rotate, s.Rotate, i, n.path, "rotate", warn);
                        if (n.scale != null) s.Scale = ToVec3(n.scale, s.Scale, i, n.path, "scale", warn);
                        if (n.visible.HasValue) s.Visible = n.visible.Value;

                        current[n.path] = s;
                    }
                }

                // Snapshot: later steps must not mutate earlier ones.
                steps.Add(new Dictionary<string, NodeState>(current));
            }

            return steps;
        }

        static Vector3 ToVec3(float[] a, Vector3 fallback, int step, string path, string field, Action<string> warn)
        {
            if (a.Length != 3)
            {
                warn?.Invoke($"step {step}: '{path}'.{field} needs 3 numbers, got {a.Length} - ignored");
                return fallback;
            }

            for (int i = 0; i < 3; i++)
            {
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i]))
                {
                    warn?.Invoke($"step {step}: '{path}'.{field} has a non-finite value - ignored");
                    return fallback;
                }
            }

            return new Vector3(a[0], a[1], a[2]);
        }
    }
}
