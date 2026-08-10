// Web viewer - the same Jig the Quest app shows, in a browser, with no app to
// install and a link that can be shared.
//
// It reads exactly the content the headset reads (../index.json and the scene.json
// it points at) and applies the same rules, through the same modules the authoring
// editor and tools/validate.mjs use: resolver.js for step semantics and node paths,
// glb.js for the glTFast hierarchy, views.js for labels and callouts. Nothing about
// the format is reimplemented here.
//
// What IS new here is the step tween, ported from JigPlayer.TweenTo.
//
// Read-only by design: no PUT, no editing, so this is the half of the editor that
// is safe to publish.

import * as THREE from "three";
import { GLTFLoader } from "../editor/vendor/GLTFLoader.js";
import { OrbitControls } from "../editor/vendor/OrbitControls.js";
import { authoredNodes, buildNodeIndex, flipX, resolveSteps, setVisibleDeep } from "../editor/resolver.js";
import { parseGlbJson, unityNodeTree } from "../editor/glb.js";
import { buildCalloutViews, buildLabelViews, disposeViews, updateLeader } from "../editor/views.js";

const $ = (id) => document.getElementById(id);

const state = {
  manifest: null,
  entry: null,
  scene: null,
  container: null,
  index: null,
  resolved: [],     // one Map(path -> state) per step, from resolveSteps
  authored: null,   // nodes the scene names, which setVisibleDeep must not descend into
  rest: new Map(),  // proxy -> {pos, quat, scale} before any step is applied
  step: 0,
  labelViews: [],
  calloutViews: [],
  tween: null,      // the in-flight step transition, or null
};

// -- three.js scaffolding --------------------------------------------------
//
// Deliberately the same lighting and framing as the editor: what an author sees
// while placing a callout is what a viewer gets from the link.

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

const grid = new THREE.GridHelper(2, 40, 0x3a3f4a, 0x24272d);
scene3.add(grid);

const orbit = new OrbitControls(camera, renderer.domElement);
orbit.enableDamping = true;
orbit.dampingFactor = 0.08;

function resize() {
  const w = viewport.clientWidth, h = viewport.clientHeight;
  renderer.setSize(w, h);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewport);
resize();

const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = clock.getDelta();
  if (state.tween) advanceTween(dt);
  orbit.update();
  for (const v of state.labelViews) updateLeader(v);
  for (const v of state.calloutViews) updateLeader(v);
  renderer.render(scene3, camera);
});

function status(msg, kind = "") {
  $("status").textContent = msg;
  $("status").className = kind;
}

// -- loading ---------------------------------------------------------------
//
// The viewer sits one directory below the content root, exactly like the editor,
// and the publish step preserves that shape - so this one expression is correct
// both on a laptop against serve.py and on GitHub Pages.

const contentBase = () => new URL("../", location.href);

async function loadManifest() {
  state.manifest = await (await fetch(new URL("index.json", contentBase()), { cache: "no-store" })).json();

  const picker = $("picker");
  picker.replaceChildren();
  for (const entry of state.manifest.jigs ?? []) {
    const opt = document.createElement("option");
    opt.value = entry.id;
    opt.textContent = entry.title || entry.id;
    picker.appendChild(opt);
  }
  picker.hidden = (state.manifest.jigs?.length ?? 0) < 2;

  const params = new URLSearchParams(location.search);
  const entry = state.manifest.jigs?.find((j) => j.id === params.get("jig")) ?? state.manifest.jigs?.[0];
  if (!entry) throw new Error("the manifest lists no jigs");
  // Steps are 1-based in the URL, because a shared link is read by people.
  await loadJig(entry, (Number(params.get("step")) || 1) - 1);
}

async function loadJig(entry, step = 0) {
  status(`loading ${entry.id}…`);
  state.entry = entry;
  state.tween = null;

  const sceneUrl = new URL(entry.scene, contentBase());
  state.scene = await (await fetch(sceneUrl, { cache: "no-store" })).json();

  const modelUrl = new URL(state.scene.model, sceneUrl);
  const buffer = await (await fetch(modelUrl, { cache: "no-store" })).arrayBuffer();

  // Paths come from the glTF's own node names, NOT from the three.js scene graph -
  // three sanitises names and splits multi-primitive meshes differently. See glb.js.
  const tree = unityNodeTree(parseGlbJson(buffer));
  const gltf = await new Promise((ok, err) =>
    new GLTFLoader().parse(buffer, new URL(".", modelUrl).href, ok, err));

  disposeViews(state.labelViews);
  disposeViews(state.calloutViews);
  state.labelViews = [];
  state.calloutViews = [];

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

  const attach = async (proxy) => {
    if (proxy.isScene) proxy.obj = gltf.scene;
    else if (proxy.nodeIndex != null) proxy.obj = await gltf.parser.getDependency("node", proxy.nodeIndex);
    for (const c of proxy.children) await attach(c);
  };
  await attach(tree);

  state.index = buildNodeIndex(tree);
  state.resolved = resolveSteps(state.scene, (m) => console.warn(`[jig] ${m}`));
  state.authored = authoredNodes(state.resolved, state.index);

  // Rest pose, captured before any step is applied - what every offset is relative to.
  state.rest.clear();
  for (const proxy of state.index.aliases.keys()) {
    if (!proxy.obj) continue;
    state.rest.set(proxy, {
      pos: proxy.obj.position.clone(),
      quat: proxy.obj.quaternion.clone(),
      scale: proxy.obj.scale.clone(),
    });
  }

  document.title = $("title").textContent = state.scene.title || state.scene.id;
  $("picker").value = entry.id;

  goTo(step, { instant: true });

  // Frame AFTER the first step is applied: labels and callouts are parented inside
  // the container, so framing before they exist puts them outside the view.
  frameModel();
  status("");
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

// -- steps -----------------------------------------------------------------

/// Ported from JigPlayer.GoTo/TweenTo. Two things in there are not obvious and are
/// kept deliberately:
///
///   - The tween starts from where things ACTUALLY are, not from the previous
///     step's target, so clicking through steps faster than the animation retargets
///     smoothly instead of snapping back.
///   - A part is shown immediately but hidden only once the motion has finished, so
///     it is never yanked out of existence while the user is watching it move.
function goTo(i, { instant = false } = {}) {
  if (!state.scene?.steps?.length) return;
  const index = Math.max(0, Math.min(i, state.scene.steps.length - 1));
  state.step = index;

  const target = state.resolved[index] ?? new Map();
  const duration = instant ? 0 : Math.max(0, state.scene.steps[index].duration ?? 0.8);

  const parts = [];
  for (const [proxy, rest] of state.rest) {
    const s = state.index.stateFor(proxy, target);
    const obj = proxy.obj;
    parts.push({
      proxy,
      from: { pos: obj.position.clone(), scale: obj.scale.clone() },
      to: {
        pos: rest.pos.clone().add(new THREE.Vector3(...flipX(s.move))),
        scale: new THREE.Vector3(rest.scale.x * s.scale[0], rest.scale.y * s.scale[1], rest.scale.z * s.scale[2]),
      },
      visible: s.visible,
    });
    if (s.visible) setVisibleDeep(proxy, true, state.authored);
  }

  state.tween = { parts, duration, elapsed: 0 };
  if (duration === 0) advanceTween(0);

  rebuildViews();
  renderChrome();
  writeUrl();
}

function advanceTween(dt) {
  const t = state.tween;
  t.elapsed += dt;
  const done = t.elapsed >= t.duration;
  // Unity uses Mathf.SmoothStep, which is the same 3t^2-2t^3 easing.
  const raw = t.duration > 0 ? Math.min(1, t.elapsed / t.duration) : 1;
  const k = raw * raw * (3 - 2 * raw);

  for (const p of t.parts) {
    const obj = p.proxy.obj;
    obj.position.lerpVectors(p.from.pos, p.to.pos, k);
    obj.scale.lerpVectors(p.from.scale, p.to.scale, k);
  }

  if (!done) return;
  for (const p of t.parts) {
    p.proxy.obj.position.copy(p.to.pos);
    p.proxy.obj.scale.copy(p.to.scale);
    if (!p.visible) setVisibleDeep(p.proxy, false, state.authored);
  }
  state.tween = null;
}

function rebuildViews() {
  const step = state.scene.steps[state.step];
  disposeViews(state.labelViews);
  disposeViews(state.calloutViews);
  const common = { index: state.index, container: state.container, lineParent: scene3 };
  state.labelViews = buildLabelViews({ specs: step.labels, ...common });
  state.calloutViews = buildCalloutViews({ specs: step.callouts, ...common });
}

// -- chrome ----------------------------------------------------------------

function renderChrome() {
  const steps = state.scene.steps;
  $("caption").textContent = steps[state.step].caption ?? "";
  $("counter").textContent = `${state.step + 1} / ${steps.length}`;
  $("prev").disabled = state.step === 0;
  $("next").disabled = state.step === steps.length - 1;

  const dots = $("dots");
  dots.replaceChildren();
  steps.forEach((s, i) => {
    const b = document.createElement("button");
    b.textContent = String(i + 1);
    b.className = i === state.step ? "on" : "";
    b.title = s.caption || `Step ${i + 1}`;
    b.onclick = () => goTo(i);
    dots.appendChild(b);
  });

  // Rotation is authored in Unity's ZXY Euler order and the mapping into three's
  // is unverified, so it is not applied rather than applied wrongly - the same
  // stance the editor takes. No shipped content authors it.
  const rotates = [...(state.resolved[state.step]?.values() ?? [])]
    .some((s) => s.rotate.some((n) => n !== 0));
  $("note").textContent = rotates ? "this step authors 'rotate', which the web viewer does not apply" : "";
}

/// The address bar is the share surface: whatever is on screen is what a copied
/// link reopens. replaceState so the back button still leaves the page.
function writeUrl() {
  const p = new URLSearchParams(location.search);
  p.set("jig", state.entry.id);
  p.set("step", String(state.step + 1));
  history.replaceState(null, "", `${location.pathname}?${p}`);
}

// -- wiring ----------------------------------------------------------------

$("prev").onclick = () => goTo(state.step - 1);
$("next").onclick = () => goTo(state.step + 1);

$("picker").onchange = (e) => {
  const entry = state.manifest.jigs.find((j) => j.id === e.target.value);
  if (entry) loadJig(entry).catch((err) => status(err.message, "bad"));
};

addEventListener("keydown", (e) => {
  if (e.key === "ArrowLeft") goTo(state.step - 1);
  if (e.key === "ArrowRight") goTo(state.step + 1);
});

$("share").onclick = () => {
  const url = location.href;
  $("shareUrl").value = url;
  // ui=0 drops the chrome so an embed is just the Jig.
  const embed = new URL(url);
  embed.searchParams.set("ui", "0");
  $("shareEmbed").value =
    `<iframe src="${embed}" width="720" height="480" style="border:0" allowfullscreen></iframe>`;
  $("shareDialog").showModal();
};

$("closeShare").onclick = () => $("shareDialog").close();

/// navigator.clipboard is unavailable on a plain-HTTP origin, which is exactly how
/// this is served on the LAN during authoring - so fall back to selecting the text
/// and telling the user to copy it, rather than failing silently.
async function copyFrom(el, button) {
  const original = button.textContent;
  try {
    await navigator.clipboard.writeText(el.value);
    button.textContent = "Copied";
  } catch {
    el.select();
    button.textContent = "Press ⌘C";
  }
  setTimeout(() => { button.textContent = original; }, 1600);
}

$("copyUrl").onclick = () => copyFrom($("shareUrl"), $("copyUrl"));
$("copyEmbed").onclick = () => copyFrom($("shareEmbed"), $("copyEmbed"));

if (new URLSearchParams(location.search).get("ui") === "0") document.body.classList.add("embed");

// Same escape hatch the editor has: when a jig misbehaves in a browser, the model,
// the resolved steps and the node index are all reachable from the console.
window.jig = state;

loadManifest().catch((e) => status(`could not load content: ${e.message}`, "bad"));
