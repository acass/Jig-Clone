// Reads a .glb and reconstructs the GameObject hierarchy glTFast will build, so
// authored node paths can be checked against what the viewer will actually see.
//
// This cannot be done from the three.js scene graph. three.js and glTFast differ
// in two ways that both change node paths:
//
//   1. three.js sanitises names - GLTFLoader.createUniqueName runs
//      PropertyBinding.sanitizeNodeName, which turns "Glass Face" into
//      "Glass_Face". Unity keeps the space.
//   2. Multi-primitive meshes are split differently. three.js makes the node a
//      Group with children "<name>_1", "<name>_2". glTFast puts primitive 0 on the
//      node GameObject itself and names each EXTRA primitive after the MESH
//      (GameObjectInstantiator.AddPrimitive), so a 2-primitive node called
//      "Hand Seconds" gains a second GameObject also called "Hand Seconds".
//
// (2) is not cosmetic: it makes that leaf name ambiguous, so JigPlayer's
// unambiguous-bare-leaf alias is NOT created and only the full path resolves.

import { sceneNodeName } from "./resolver.js";

const JSON_CHUNK = 0x4e4f534a;

/// Extracts the JSON chunk of a .glb. Accepts an ArrayBuffer or a Node Buffer.
export function parseGlbJson(data) {
  const buf = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer ?? data);
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);

  const magic = String.fromCharCode(...buf.subarray(0, 4));
  if (magic !== "glTF") throw new Error("not a .glb (bad magic)");

  let off = 12;
  while (off + 8 <= buf.byteLength) {
    const len = view.getUint32(off, true);
    const type = view.getUint32(off + 4, true);
    if (type === JSON_CHUNK) {
      const bytes = buf.subarray(off + 8, off + 8 + len);
      return JSON.parse(new TextDecoder().decode(bytes));
    }
    off += 8 + len + ((4 - (len % 4)) % 4);
  }
  throw new Error("no JSON chunk in .glb");
}

/// Builds the proxy tree standing in for Unity's hierarchy under the `jig:<id>`
/// container. Each proxy is {name, children, nodeIndex}; nodeIndex is null for the
/// synthetic GameObjects glTFast creates for extra mesh primitives, which an author
/// never targets but which JigPlayer.IndexHierarchy still walks and counts.
export function unityNodeTree(gltf) {
  const nodes = gltf.nodes ?? [];
  const meshes = gltf.meshes ?? [];
  const scene = (gltf.scenes ?? [])[gltf.scene ?? 0];
  if (!scene) throw new Error("glTF has no default scene");

  const build = (i) => {
    const def = nodes[i];
    const proxy = {
      name: def.name ?? `node_${i}`,
      nodeIndex: i,
      children: (def.children ?? []).map(build),
    };

    // Primitive 0 lives on the node GameObject itself; each additional primitive
    // becomes a child named after the mesh.
    if (def.mesh != null) {
      const mesh = meshes[def.mesh];
      const extra = (mesh?.primitives?.length ?? 1) - 1;
      for (let k = 0; k < extra; k++) {
        proxy.children.push({ name: mesh.name ?? `mesh_${def.mesh}`, nodeIndex: null, children: [] });
      }
    }

    return proxy;
  };

  const roots = (scene.nodes ?? []).map(build);
  const name = sceneNodeName(scene.name, roots.length);

  // The container is Unity's `jig:<id>` object; authored paths are relative to it.
  // isScene marks the proxy that corresponds to three.js's gltf.scene, which has
  // no glTF node index of its own but does need a three object attached to it.
  return {
    name: "",
    nodeIndex: null,
    children: name ? [{ name, nodeIndex: null, isScene: true, children: roots }] : roots,
  };
}
