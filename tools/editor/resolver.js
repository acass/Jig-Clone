// Shared Jig format semantics. Used by the editor in the browser and by
// tools/validate.mjs on the command line, so there is one implementation of the
// rules rather than two that drift.
//
// Ported from unity/JigViewer/Assets/Scripts/JigStepResolver.cs and
// JigPlayer.IndexHierarchy. Read those before changing anything here.

// -- coordinate space ------------------------------------------------------
//
// glTFast converts glTF to Unity by negating X and leaving Y and Z alone
// (gltfast Runtime/Scripts/NodeExtension.cs:65):
//
//     position = new Vector3(-translation[0], translation[1], translation[2])
//
// three.js applies no conversion, so a model rendered here in plain three.js
// already LOOKS the way Unity renders it - Unity's X negation plus its
// left-handed frame produce the same image as three's unmodified right-handed
// one. That is the whole point of the conversion, so do NOT mirror the scene to
// "match Unity"; that would show a mirrored model.
//
// What does differ is the NUMBERS. Every vector written to scene.json is in
// Unity space, so it crosses this one function and nowhere else. It is its own
// inverse.
export function flipX(v) {
  return [-v[0], v[1], v[2]];
}

// Scale is passed through unconverted by glTFast, so it needs no flip.

// -- step resolution -------------------------------------------------------

export const REST = Object.freeze({
  move: [0, 0, 0],
  rotate: [0, 0, 0],
  scale: [1, 1, 1],
  visible: true,
});

function toVec3(a, fallback, step, path, field, warn) {
  if (!Array.isArray(a) || a.length !== 3) {
    warn?.(`step ${step}: '${path}'.${field} needs 3 numbers, got ${a?.length} - ignored`);
    return fallback;
  }
  for (const n of a) {
    if (typeof n !== "number" || !Number.isFinite(n)) {
      warn?.(`step ${step}: '${path}'.${field} has a non-finite value - ignored`);
      return fallback;
    }
  }
  return [a[0], a[1], a[2]];
}

/// Flattens a scene's steps into one fully-resolved state table per step, applying
/// the inherit-if-omitted rule. Returns an array of Map(path -> state).
/// Mirrors JigStepResolver.Resolve exactly, including the per-step snapshot.
export function resolveSteps(scene, warn) {
  const steps = [];
  if (!scene?.steps) return steps;

  // Running state, carried forward across steps.
  const current = new Map();

  scene.steps.forEach((step, i) => {
    for (const n of step?.nodes ?? []) {
      if (!n || !n.path) {
        warn?.(`step ${i}: node entry with no path, skipped`);
        continue;
      }

      const s = { ...(current.get(n.path) ?? REST) };

      if (n.move != null) s.move = toVec3(n.move, s.move, i, n.path, "move", warn);
      if (n.rotate != null) s.rotate = toVec3(n.rotate, s.rotate, i, n.path, "rotate", warn);
      if (n.scale != null) s.scale = toVec3(n.scale, s.scale, i, n.path, "scale", warn);
      if (n.visible != null) s.visible = !!n.visible;

      current.set(n.path, s);
    }

    // Snapshot: later steps must not mutate earlier ones.
    steps.push(new Map(current));
  });

  return steps;
}

// -- node paths ------------------------------------------------------------

/// Whether glTFast inserts a "Scene" GameObject between the container and the
/// glTF's root nodes. Its default is SceneObjectCreation.WhenMultipleRootNodes
/// (gltfast Runtime/Scripts/InstantiationSettings.cs:79), so a glTF with exactly
/// one root node has NO scene node and its paths start one level higher.
///
/// Getting this wrong shifts every authored path by one component and every node
/// silently stops resolving, so it is replicated rather than assumed.
export function sceneNodeName(sceneName, rootChildCount) {
  return rootChildCount === 1 ? null : sceneName || "Scene";
}

/// Builds the path index the viewer will build, from a tree of {name, children}.
/// three.js Object3D satisfies that shape, and so does a parsed glTF node tree.
///
/// `root` is the equivalent of Unity's `jig:<id>` container - its children are
/// what paths are relative to. Mirrors JigPlayer.IndexHierarchy.
export function buildNodeIndex(root) {
  const byPath = new Map();     // path -> node
  const aliases = new Map();    // node -> [paths], in resolution order
  const byLeaf = new Map();     // leaf name -> [nodes]

  const walk = (node, prefix) => {
    for (const child of node.children ?? []) {
      const path = prefix ? `${prefix}/${child.name}` : child.name;
      byPath.set(path, child);
      aliases.set(child, [path]);

      if (!byLeaf.has(child.name)) byLeaf.set(child.name, []);
      byLeaf.get(child.name).push(child);

      walk(child, path);
    }
  };
  walk(root, "");

  // The bare leaf name also resolves, but only where it is unambiguous.
  for (const [leaf, nodes] of byLeaf) {
    if (nodes.length !== 1 || byPath.has(leaf)) continue;
    byPath.set(leaf, nodes[0]);
    aliases.get(nodes[0]).push(leaf);
  }

  return {
    byPath,
    aliases,
    /// The path to author for a node: the bare leaf where that is unambiguous,
    /// because it is shorter and it is what the existing content uses.
    preferredPath(node) {
      const paths = aliases.get(node);
      if (!paths) return null;
      return paths.length > 1 ? paths[paths.length - 1] : paths[0];
    },
    /// Resolved state for a node, matched against any key it is known by.
    /// Mirrors JigPlayer.StateFor.
    stateFor(node, stepState) {
      for (const key of aliases.get(node) ?? []) {
        if (stepState.has(key)) return stepState.get(key);
      }
      return REST;
    },
  };
}

// -- serialisation ---------------------------------------------------------

/// Serialises a scene the way the hand-written content is laid out: number arrays
/// and leaf objects on one line. Plain JSON.stringify explodes every [x,y,z] into
/// four lines, so each save would rewrite the whole file and the git history of the
/// content would stop being readable.
export function formatScene(o) {
  const pretty = JSON.stringify(o, null, 2);

  const compact = pretty
    // Arrays of plain numbers onto one line.
    .replace(/\[\n\s*(-?[\d.eE+-]+(?:,\n\s*-?[\d.eE+-]+)*)\n\s*\]/g,
      (_, body) => `[${body.split(",").map((n) => n.trim()).join(", ")}]`)
    // Leaf objects onto one line. [^{}] cannot span a nested object, so this only
    // ever matches the innermost ones.
    .replace(/\{[^{}]*\}/g, (m) => {
      const one = `{ ${m.slice(1, -1).trim().replace(/\s*\n\s*/g, " ")} }`;
      return one.length <= 100 ? one : m;
    });

  // The collapsing above is cosmetic and regex-based, so it is only used when it
  // provably round-trips. A caption containing a brace would otherwise be able to
  // corrupt the file, and content must never be risked for prettier diffs.
  try {
    if (JSON.stringify(JSON.parse(compact)) === JSON.stringify(o)) return compact + "\n";
  } catch { /* fall through */ }
  return pretty + "\n";
}
