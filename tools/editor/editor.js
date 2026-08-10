// Jig authoring editor.
//
// Renders a Jig the way the Quest viewer will, and writes scene.json back through
// serve.py's localhost-only PUT. All format semantics live in resolver.js, shared
// with tools/validate.mjs, so there is one implementation of the rules.
//
// The model is rendered in plain three.js space with NO mirroring - see the note
// at the top of resolver.js. Numbers cross into Unity space through flipX() and
// nowhere else.

import * as THREE from "three";
import { GLTFLoader } from "./vendor/GLTFLoader.js";
import { OrbitControls } from "./vendor/OrbitControls.js";
import { TransformControls } from "./vendor/TransformControls.js";
import { buildNodeIndex, flipX, formatScene, resolveSteps } from "./resolver.js";
import { parseGlbJson, unityNodeTree } from "./glb.js";
import { buildCalloutViews, buildLabelViews, disposeViews, updateLeader } from "./views.js";

const $ = (id) => document.getElementById(id);

/// Small DOM helper. Everything user-authored goes in as text, never as markup.
function el(tag, className, text) {
  const n = document.createElement(tag);
  if (className) n.className = className;
  if (text != null) n.textContent = text;
  return n;
}

const state = {
  manifest: null,
  entry: null,      // the manifest entry being edited
  scene: null,      // the parsed scene.json - the document of record
  container: null,  // Object3D standing in for Unity's `jig:<id>` object
  index: null,      // buildNodeIndex over the Unity-shaped proxy tree
  byObject: null,   // three Object3D -> proxy, for click selection
  rest: new Map(),  // proxy -> {pos, quat, scale} captured before any step is applied
  step: 0,
  selected: null,   // { kind: "node", node } | { kind: "label", i }
  labelViews: [],   // one Group per label in the current step
  calloutViews: [], // one Group per callout in the current step
  dirty: false,
};

// -- three.js scaffolding --------------------------------------------------

const viewport = $("viewport");
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
viewport.appendChild(renderer.domElement);

const scene3 = new THREE.Scene();
scene3.background = new THREE.Color(0x16171a);

const camera = new THREE.PerspectiveCamera(45, 1, 0.001, 500);
camera.position.set(0.5, 0.4, 0.7);

scene3.add(new THREE.HemisphereLight(0xffffff, 0x30323a, 2.2));
const key = new THREE.DirectionalLight(0xffffff, 1.6);
key.position.set(1.5, 2.5, 1.8);
scene3.add(key);

// Ground plane for size reference. The viewer puts the model on a table, so a
// floor is the closest honest analogue to how it will actually be seen.
const grid = new THREE.GridHelper(2, 40, 0x3a3f4a, 0x24272d);
scene3.add(grid);

const orbit = new OrbitControls(camera, renderer.domElement);
orbit.enableDamping = true;
orbit.dampingFactor = 0.08;

const gizmo = new TransformControls(camera, renderer.domElement);
gizmo.setMode("translate");
gizmo.setSpace("local");   // move is a LOCAL offset from rest, so drag in local space
// r169+ splits the controls object from its visual helper; adding the controls
// object itself to the scene renders nothing at all.
scene3.add(gizmo.getHelper());
gizmo.addEventListener("dragging-changed", (e) => { orbit.enabled = !e.value; });
gizmo.addEventListener("objectChange", onGizmoDrag);

function resize() {
  const w = viewport.clientWidth, h = viewport.clientHeight;
  renderer.setSize(w, h);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewport);
resize();

renderer.setAnimationLoop(() => {
  orbit.update();
  for (const v of state.labelViews) updateLeader(v, tintFor("label", v.i));
  for (const v of state.calloutViews) updateLeader(v, tintFor("callout", v.i));
  renderer.render(scene3, camera);
});

/// The selected view's leader line is tinted, which is how you tell which label in
/// a crowded step you are dragging.
function tintFor(kind, i) {
  return state.selected?.kind === kind && state.selected.i === i ? 0x6ea8fe : 0xffffff;
}

// -- status ----------------------------------------------------------------

function status(msg, kind = "") {
  const s = $("status");
  s.textContent = msg;
  s.className = kind;
}

function markDirty() {
  state.dirty = true;
  $("save").disabled = false;
}

// -- loading ---------------------------------------------------------------

const contentBase = () => new URL("../", location.href);

async function loadManifest() {
  state.manifest = await (await fetch(new URL("index.json", contentBase()), { cache: "no-store" })).json();
  renderJigList();
  if (state.manifest.jigs?.length) await loadJig(state.manifest.jigs[0]);
}

async function loadJig(entry) {
  status(`loading ${entry.id}…`);
  state.entry = entry;

  const sceneUrl = new URL(entry.scene, contentBase());
  state.scene = await (await fetch(sceneUrl, { cache: "no-store" })).json();

  const modelUrl = new URL(state.scene.model, sceneUrl);
  const buffer = await (await fetch(modelUrl, { cache: "no-store" })).arrayBuffer();

  // Paths come from the glTF's own node names, NOT from the three.js scene graph -
  // three sanitises names and splits multi-primitive meshes differently. See glb.js.
  const tree = unityNodeTree(parseGlbJson(buffer));
  const gltf = await new Promise((ok, err) =>
    new GLTFLoader().parse(buffer, new URL(".", modelUrl).href, ok, err));

  if (state.container) {
    scene3.remove(state.container);
    state.container.traverse((o) => {
      o.geometry?.dispose();
      if (o.material) [].concat(o.material).forEach((m) => m.dispose());
    });
  }

  // Stands in for Unity's `jig:<id>` container. Authored paths are relative to it.
  state.container = new THREE.Group();
  state.container.name = `jig:${state.scene.id}`;
  state.container.scale.setScalar(state.scene.scale > 0 ? state.scene.scale : 1);
  state.container.add(gltf.scene);
  scene3.add(state.container);

  // Hang the real three objects off the proxy tree. Proxies without one are the
  // synthetic GameObjects glTFast makes for extra mesh primitives: they exist only
  // so path ambiguity is counted the way the viewer counts it, and are never moved.
  state.byObject = new Map();
  const attach = async (proxy) => {
    if (proxy.isScene) proxy.obj = gltf.scene;
    else if (proxy.nodeIndex != null) proxy.obj = await gltf.parser.getDependency("node", proxy.nodeIndex);
    if (proxy.obj) state.byObject.set(proxy.obj, proxy);
    for (const c of proxy.children) await attach(c);
  };
  await attach(tree);

  state.index = buildNodeIndex(tree);

  // Rest pose, captured before any step is applied - the same thing
  // JigPlayer.IndexHierarchy records, and what every offset is relative to.
  state.rest.clear();
  for (const proxy of state.index.aliases.keys()) {
    if (!proxy.obj) continue;
    state.rest.set(proxy, {
      pos: proxy.obj.position.clone(),
      quat: proxy.obj.quaternion.clone(),
      scale: proxy.obj.scale.clone(),
    });
  }

  state.selected = null;
  state.dirty = false;
  $("save").disabled = true;
  $("jigTitle").textContent = state.scene.title || state.scene.id;
  $("jigTitle").className = "";
  $("scale").value = state.scene.scale ?? 1;

  renderJigList();
  applyStep(0);

  // Frame AFTER the first step is applied: labels and callouts are parented inside the
  // container, so framing before they exist puts them outside the view.
  frameModel();
  status(`${state.scene.steps.length} steps, ${state.index.byPath.size} resolvable paths`, "good");
}

function frameModel() {
  const box = new THREE.Box3().setFromObject(state.container);
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3()).length() || 1;
  const centre = box.getCenter(new THREE.Vector3());

  orbit.target.copy(centre);
  camera.position.copy(centre).add(new THREE.Vector3(0.6, 0.45, 0.8).multiplyScalar(size));
  camera.near = size / 200;
  camera.far = size * 200;
  camera.updateProjectionMatrix();
  grid.scale.setScalar(Math.max(size * 2, 0.05));
}

// -- applying a step -------------------------------------------------------

/// Shows or hides a node the way an author means it: the whole part.
///
/// The viewer does NOT currently do this for multi-primitive meshes.
/// JigPlayer.SetVisible touches only GetComponents<Renderer>() on the node's own
/// GameObject, so the extra GameObjects glTFast creates per additional primitive
/// keep rendering. hasSplitPrimitives flags that so the note is shown rather than
/// the editor quietly previewing something the headset will not do.
function setNodeVisible(proxy, visible) {
  if (proxy.obj) proxy.obj.visible = visible;
}

function hasSplitPrimitives(proxy) {
  return proxy.children.some((c) => c.nodeIndex == null && !c.isScene);
}

function applyStep(i) {
  state.step = Math.max(0, Math.min(i, state.scene.steps.length - 1));

  const warnings = [];
  const resolved = resolveSteps(state.scene, (m) => warnings.push(m))[state.step] ?? new Map();

  const hidden = [];
  for (const [proxy, rest] of state.rest) {
    const s = state.index.stateFor(proxy, resolved);
    const obj = proxy.obj;
    obj.position.copy(rest.pos).add(new THREE.Vector3(...flipX(s.move)));
    obj.scale.set(rest.scale.x * s.scale[0], rest.scale.y * s.scale[1], rest.scale.z * s.scale[2]);
    obj.quaternion.copy(rest.quat);
    setNodeVisible(proxy, s.visible);
    if (!s.visible && hasSplitPrimitives(proxy)) hidden.push(proxy.name);
  }

  const notes = $("notes");
  notes.replaceChildren();
  for (const w of warnings) addNote(w, "warn");
  for (const p of resolved.keys()) {
    if (!state.index.byPath.has(p)) addNote(`'${p}' matches no node - it does nothing on the headset`, "bad");
  }
  // Rotation is authored in Unity's ZXY Euler order; the mapping into three's is
  // unverified, so it is not previewed rather than previewed wrongly.
  if ([...resolved.values()].some((s) => s.rotate.some((n) => n !== 0))) {
    addNote("this step authors 'rotate', which is not previewed here", "warn");
  }
  for (const name of hidden) {
    addNote(`'${name}' is a multi-primitive mesh - the viewer will only hide part of it`, "bad");
  }

  rebuildLabelViews();
  rebuildCalloutViews();
  renderStepList();
  renderSelection();
  syncGizmo();

  $("caption").value = state.scene.steps[state.step].caption ?? "";
  $("duration").value = state.scene.steps[state.step].duration ?? 0.8;
}

function addNote(text, kind) {
  const d = el("div", "note", text);
  if (kind === "bad") d.style.color = "var(--bad)";
  $("notes").appendChild(d);
}

// -- labels ----------------------------------------------------------------

/// The sprites themselves live in views.js, shared with the web viewer, so an
/// author places a callout that a shared link renders identically.
function rebuildLabelViews() {
  disposeViews(state.labelViews);
  state.labelViews = buildLabelViews({
    specs: state.scene.steps[state.step].labels,
    index: state.index,
    container: state.container,
    lineParent: scene3,
  });
}

function rebuildCalloutViews() {
  disposeViews(state.calloutViews);
  state.calloutViews = buildCalloutViews({
    specs: state.scene.steps[state.step].callouts,
    index: state.index,
    container: state.container,
    lineParent: scene3,
  });
}

// -- selection and the gizmo ----------------------------------------------

const raycaster = new THREE.Raycaster();

renderer.domElement.addEventListener("pointerdown", (e) => {
  if (gizmo.dragging || !state.container) return;
  const r = renderer.domElement.getBoundingClientRect();
  raycaster.setFromCamera(
    new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1),
    camera
  );

  const sprites = state.labelViews.map((v) => v.sprite);
  const labelHit = raycaster.intersectObjects(sprites, false)[0];
  if (labelHit) {
    select({ kind: "label", i: state.labelViews.find((v) => v.sprite === labelHit.object).i });
    return;
  }

  const coSprites = state.calloutViews.map((v) => v.group.children[0]);
  const coHit = raycaster.intersectObjects(coSprites, false)[0];
  if (coHit) {
    select({ kind: "callout", i: state.calloutViews.find((v) => v.group.children[0] === coHit.object).i });
    return;
  }

  const hit = raycaster.intersectObject(state.container, true)[0];
  if (!hit) return select(null);

  // Walk up to the nearest node the viewer knows about: three splits a
  // multi-primitive mesh into children that have no counterpart in an authored path.
  let o = hit.object;
  while (o && !state.byObject.has(o)) o = o.parent;
  const proxy = state.byObject.get(o);
  select(proxy && proxy !== state.index.byPath.get("") ? { kind: "node", node: proxy } : null);
});

function select(sel) {
  state.selected = sel;
  renderSelection();
  syncGizmo();
}

function syncGizmo() {
  if (!state.selected) return gizmo.detach();
  if (state.selected.kind === "node") return gizmo.attach(state.selected.node.obj);
  const views = state.selected.kind === "callout" ? state.calloutViews : state.labelViews;
  const v = views.find((v) => v.i === state.selected.i);
  v ? gizmo.attach(v.group) : gizmo.detach();
}

/// The gizmo writes straight into scene.json: a drag is only meaningful once it is
/// expressed as an offset from rest, which is what the format stores.
function onGizmoDrag() {
  const sel = state.selected;
  if (!sel) return;
  const step = state.scene.steps[state.step];

  if (sel.kind === "node") {
    const delta = sel.node.obj.position.clone().sub(state.rest.get(sel.node).pos);
    setNodeMove(step, state.index.preferredPath(sel.node), flipX(delta.toArray()));
  } else if (sel.kind === "label") {
    const v = state.labelViews.find((v) => v.i === sel.i);
    step.labels[sel.i].offset = flipX(v.group.position.clone().sub(v.anchor.position).toArray());
  } else {
    const v = state.calloutViews.find((v) => v.i === sel.i);
    // A floating callout is positioned relative to the model root, so there is no
    // anchor position to subtract.
    const base = v.anchor ? v.anchor.position : new THREE.Vector3();
    step.callouts[sel.i].offset = flipX(v.group.position.clone().sub(base).toArray());
  }

  roundStep(step);
  markDirty();
  renderSelection();
}

function nodeEntry(step, path) {
  step.nodes ??= [];
  let entry = step.nodes.find((n) => n.path === path);
  if (!entry) step.nodes.push((entry = { path }));
  return entry;
}

function setNodeMove(step, path, move) {
  nodeEntry(step, path).move = move;
}

/// Gizmo drags produce 15 significant figures. Nobody authoring a teardown needs
/// sub-micron precision, and the noise makes every git diff of the content useless.
function roundStep(step) {
  const r = (a) => a?.map((n) => Math.round(n * 1e4) / 1e4);
  for (const n of step.nodes ?? []) {
    if (n.move) n.move = r(n.move);
    if (n.rotate) n.rotate = r(n.rotate);
    if (n.scale) n.scale = r(n.scale);
  }
  for (const l of step.labels ?? []) if (l.offset) l.offset = r(l.offset);
  for (const c of step.callouts ?? []) if (c.offset) c.offset = r(c.offset);
}

// -- right panel -----------------------------------------------------------

function renderSelection() {
  const sel = state.selected;
  const step = state.scene?.steps[state.step];

  const isNode = sel?.kind === "node";
  $("selNone").hidden = isNode;
  $("selBody").hidden = !isNode;

  if (isNode) {
    const path = state.index.preferredPath(sel.node);
    $("selPath").textContent = path;
    const resolved = state.index.stateFor(sel.node, resolveSteps(state.scene)[state.step]);
    const move = (step.nodes ?? []).find((n) => n.path === path)?.move ?? resolved.move;
    $("mx").value = move[0]; $("my").value = move[1]; $("mz").value = move[2];
    $("visible").checked = resolved.visible;
  }

  const isLabel = sel?.kind === "label";
  $("labelBody").hidden = !isLabel;
  if (isLabel) {
    const spec = step.labels[sel.i];
    $("labelText").value = spec.text ?? "";
    const o = spec.offset ?? [0, 0, 0];
    $("lx").value = o[0]; $("ly").value = o[1]; $("lz").value = o[2];
  }

  const isCallout = sel?.kind === "callout";
  $("calloutBody").hidden = !isCallout;
  if (isCallout) {
    const spec = step.callouts[sel.i];
    $("coTitle").value = spec.title ?? "";
    $("coBody").value = spec.body ?? "";
    $("coWidth").value = spec.width ?? 6;
    $("coAnchored").checked = !!spec.anchor;
    const o = spec.offset ?? [0, 0, 0];
    $("cx").value = o[0]; $("cy").value = o[1]; $("cz").value = o[2];
  }

  renderLabelList();
  renderCalloutList();
}

function renderCalloutList() {
  const ul = $("calloutList");
  ul.replaceChildren();
  const callouts = state.scene?.steps[state.step].callouts ?? [];
  if (!callouts.length) return ul.appendChild(el("li", "empty", "none"));

  callouts.forEach((spec, i) => {
    const on = state.selected?.kind === "callout" && state.selected.i === i;
    const li = el("li", "item" + (on ? " sel" : ""));
    const where = spec.anchor ? `→ ${spec.anchor}` : "(floating)";
    li.appendChild(el("span", "txt", `${spec.title || spec.body || "(empty)"} ${where}`));
    const del = el("span", "del", "✕");
    del.onclick = (e) => {
      e.stopPropagation();
      callouts.splice(i, 1);
      state.selected = null;
      markDirty();
      applyStep(state.step);
    };
    li.appendChild(del);
    li.onclick = () => select({ kind: "callout", i });
    ul.appendChild(li);
  });
}

function renderLabelList() {
  const ul = $("labelList");
  ul.replaceChildren();
  const labels = state.scene?.steps[state.step].labels ?? [];
  if (!labels.length) return ul.appendChild(el("li", "empty", "none"));

  labels.forEach((spec, i) => {
    const on = state.selected?.kind === "label" && state.selected.i === i;
    const li = el("li", "item" + (on ? " sel" : ""));
    li.appendChild(el("span", "txt", `${spec.text || "(no text)"} → ${spec.anchor}`));
    const del = el("span", "del", "✕");
    del.onclick = (e) => {
      e.stopPropagation();
      labels.splice(i, 1);
      state.selected = null;
      markDirty();
      applyStep(state.step);
    };
    li.appendChild(del);
    li.onclick = () => select({ kind: "label", i });
    ul.appendChild(li);
  });
}

// -- left panel ------------------------------------------------------------

function renderJigList() {
  const ul = $("jigList");
  ul.replaceChildren();
  for (const entry of state.manifest.jigs ?? []) {
    const li = el("li", "item" + (entry === state.entry ? " sel" : ""));
    li.appendChild(el("span", "txt", entry.title || entry.id));
    li.onclick = () => { if (confirmDiscard()) loadJig(entry); };
    ul.appendChild(li);
  }
}

function renderStepList() {
  const ul = $("stepList");
  ul.replaceChildren();
  state.scene.steps.forEach((s, i) => {
    const li = el("li", "item" + (i === state.step ? " sel" : ""));
    li.appendChild(el("span", "idx", String(i + 1).padStart(2, "0")));
    li.appendChild(el("span", "txt", s.caption || "(no caption)"));
    const del = el("span", "del", "✕");
    del.onclick = (e) => {
      e.stopPropagation();
      if (state.scene.steps.length === 1) return status("a jig needs at least one step", "bad");
      state.scene.steps.splice(i, 1);
      markDirty();
      applyStep(Math.min(state.step, state.scene.steps.length - 1));
    };
    li.appendChild(del);
    li.onclick = () => applyStep(i);
    ul.appendChild(li);
  });
}

// -- saving ----------------------------------------------------------------

async function put(path, body, type) {
  const res = await fetch(new URL(path, contentBase()), {
    method: "PUT",
    headers: { "Content-Type": type },
    body,
  });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
}

async function save() {
  if (!state.entry) return;
  try {
    status("saving…");
    await put(state.entry.scene, formatScene(state.scene), "application/json");
    state.dirty = false;
    $("save").disabled = true;
    status("saved", "good");
  } catch (e) {
    status(`save failed: ${e.message}`, "bad");
  }
}

function confirmDiscard() {
  return !state.dirty || confirm("Discard unsaved changes?");
}

// -- adding a model --------------------------------------------------------

async function addModel(file) {
  const slug = file.name.replace(/\.glb$/i, "").toLowerCase()
    .replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
  if (!slug) return status("could not make a name from that filename", "bad");

  try {
    status(`uploading ${file.name}…`);
    await put(`${slug}/${file.name}`, await file.arrayBuffer(), "model/gltf-binary");

    const scene = {
      id: slug,
      title: file.name.replace(/\.glb$/i, ""),
      model: file.name,
      // Same order of magnitude as the shipped watch: a presentation size, to be
      // tuned once it has been seen on a table.
      scale: 0.035,
      steps: [{ caption: "", duration: 0.8, nodes: [], labels: [], callouts: [] }],
    };
    await put(`${slug}/scene.json`, formatScene(scene), "application/json");

    const entry = { id: slug, title: scene.title, scene: `${slug}/scene.json` };
    state.manifest.jigs = (state.manifest.jigs ?? []).filter((j) => j.id !== slug).concat(entry);
    await put("index.json", formatScene(state.manifest), "application/json");

    state.dirty = false;
    renderJigList();
    await loadJig(entry);
    status(`added ${slug} - set the scale, then author steps`, "good");
  } catch (e) {
    status(`add failed: ${e.message}`, "bad");
  }
}

// -- wiring ----------------------------------------------------------------

$("save").onclick = save;

$("validate").onclick = () => {
  const bad = [];
  state.scene.steps.forEach((step, i) => {
    for (const n of step.nodes ?? []) if (!state.index.byPath.has(n.path)) bad.push(`step ${i + 1} node '${n.path}'`);
    for (const l of step.labels ?? []) if (!state.index.byPath.has(l.anchor)) bad.push(`step ${i + 1} anchor '${l.anchor}'`);
    for (const c of step.callouts ?? []) if (c.anchor && !state.index.byPath.has(c.anchor)) bad.push(`step ${i + 1} callout '${c.anchor}'`);
  });
  status(bad.length ? `${bad.length} unresolvable, first: ${bad[0]}` : "every path resolves", bad.length ? "bad" : "good");
};

$("caption").oninput = (e) => {
  state.scene.steps[state.step].caption = e.target.value;
  markDirty();
  renderStepList();
};

$("duration").oninput = (e) => {
  state.scene.steps[state.step].duration = parseFloat(e.target.value) || 0;
  markDirty();
};

$("scale").oninput = (e) => {
  const v = parseFloat(e.target.value);
  if (!(v > 0)) return;
  state.scene.scale = v;
  state.container.scale.setScalar(v);
  markDirty();
  applyStep(state.step);
};

for (const id of ["mx", "my", "mz"]) {
  $(id).oninput = () => {
    if (state.selected?.kind !== "node") return;
    setNodeMove(
      state.scene.steps[state.step],
      state.index.preferredPath(state.selected.node),
      [+$("mx").value || 0, +$("my").value || 0, +$("mz").value || 0]
    );
    markDirty();
    applyStep(state.step);
  };
}

for (const id of ["lx", "ly", "lz"]) {
  $(id).oninput = () => {
    if (state.selected?.kind !== "label") return;
    state.scene.steps[state.step].labels[state.selected.i].offset =
      [+$("lx").value || 0, +$("ly").value || 0, +$("lz").value || 0];
    markDirty();
    applyStep(state.step);
  };
}

$("labelText").oninput = (e) => {
  if (state.selected?.kind !== "label") return;
  state.scene.steps[state.step].labels[state.selected.i].text = e.target.value;
  markDirty();
  applyStep(state.step);
};

$("visible").onchange = (e) => {
  if (state.selected?.kind !== "node") return;
  nodeEntry(state.scene.steps[state.step], state.index.preferredPath(state.selected.node)).visible = e.target.checked;
  markDirty();
  applyStep(state.step);
};

$("clearNode").onclick = () => {
  if (state.selected?.kind !== "node") return;
  const step = state.scene.steps[state.step];
  const path = state.index.preferredPath(state.selected.node);
  step.nodes = (step.nodes ?? []).filter((n) => n.path !== path);
  markDirty();
  applyStep(state.step);
};

$("addLabel").onclick = () => {
  if (state.selected?.kind !== "node") return;
  const step = state.scene.steps[state.step];
  step.labels ??= [];
  step.labels.push({
    anchor: state.index.preferredPath(state.selected.node),
    text: "New label",
    offset: [2.6, 0.8, 0],
  });
  markDirty();
  applyStep(state.step);
  select({ kind: "label", i: step.labels.length - 1 });
};

const calloutField = (id, key, parse = (v) => v) => {
  $(id).oninput = (e) => {
    if (state.selected?.kind !== "callout") return;
    state.scene.steps[state.step].callouts[state.selected.i][key] = parse(e.target.value);
    markDirty();
    applyStep(state.step);
  };
};
calloutField("coTitle", "title");
calloutField("coBody", "body");
calloutField("coWidth", "width", (v) => parseFloat(v) || 6);

for (const id of ["cx", "cy", "cz"]) {
  $(id).oninput = () => {
    if (state.selected?.kind !== "callout") return;
    state.scene.steps[state.step].callouts[state.selected.i].offset =
      [+$("cx").value || 0, +$("cy").value || 0, +$("cz").value || 0];
    markDirty();
    applyStep(state.step);
  };
}

$("coAnchored").onchange = (e) => {
  if (state.selected?.kind !== "callout") return;
  const spec = state.scene.steps[state.step].callouts[state.selected.i];
  if (!e.target.checked) {
    delete spec.anchor;
  } else if (state.selected.node) {
    spec.anchor = state.index.preferredPath(state.selected.node);
  } else {
    // Anchoring needs a part to anchor TO, and the selection is the callout itself.
    e.target.checked = false;
    return status("select a part first, then Add callout, to anchor one", "bad");
  }
  markDirty();
  applyStep(state.step);
};

$("addCallout").onclick = () => {
  const step = state.scene.steps[state.step];
  step.callouts ??= [];
  // Anchored to the current selection when there is one, floating otherwise - which is
  // the difference between "this part is X" and a step-level aside.
  const anchored = state.selected?.kind === "node";
  step.callouts.push({
    ...(anchored ? { anchor: state.index.preferredPath(state.selected.node) } : {}),
    title: "New callout",
    body: "Say the thing a label cannot say in three words.",
    offset: anchored ? [5, 1, 0] : [7, 3, 0],
    width: 6,
  });
  markDirty();
  applyStep(state.step);
  select({ kind: "callout", i: step.callouts.length - 1 });
};

$("addStep").onclick = () => {
  state.scene.steps.splice(state.step + 1, 0, { caption: "", duration: 0.8, nodes: [], labels: [], callouts: [] });
  markDirty();
  applyStep(state.step + 1);
};

$("dupStep").onclick = () => {
  state.scene.steps.splice(state.step + 1, 0, structuredClone(state.scene.steps[state.step]));
  markDirty();
  applyStep(state.step + 1);
};

$("addModel").onclick = () => $("glbInput").click();
$("glbInput").onchange = (e) => {
  if (e.target.files[0]) addModel(e.target.files[0]);
  e.target.value = "";
};

addEventListener("keydown", (e) => {
  // e.target is not always an Element (a synthetic event can target window),
  // and an exception here would kill every shortcut at once.
  if (e.target instanceof Element && e.target.matches("input, textarea")) return;
  if (e.key.toLowerCase() === "s" && (e.metaKey || e.ctrlKey)) { e.preventDefault(); return save(); }
  if (e.key === "ArrowLeft") applyStep(state.step - 1);
  if (e.key === "ArrowRight") applyStep(state.step + 1);
  if (e.key === "Escape") select(null);
  if (e.key.toLowerCase() === "g") gizmo.setMode("translate");
});

addEventListener("beforeunload", (e) => { if (state.dirty) e.preventDefault(); });

// Local authoring tool: expose the live state so the model, the resolved steps and
// the node index can be inspected from the console when a jig misbehaves.
window.jig = state;

loadManifest().catch((e) => status(`could not load content: ${e.message}`, "bad"));
