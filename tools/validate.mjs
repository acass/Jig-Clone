#!/usr/bin/env node
// Validates every scene in content/ against the .glb it points at, using the same
// path and step rules the viewer uses (tools/editor/resolver.js).
//
// This is the check that would have caught "Hands/Hand Seconds": a path that is
// neither a full path nor an unambiguous bare leaf resolves to nothing, and the
// viewer's only complaint is a warning on a headset where nobody is reading logs.
//
// Run: node tools/validate.mjs        (exits non-zero on any error)

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildNodeIndex, resolveSteps } from "./editor/resolver.js";
import { parseGlbJson, unityNodeTree } from "./editor/glb.js";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const CONTENT = join(ROOT, "content");

let errors = 0;
let warnings = 0;

const fail = (where, msg) => { console.error(`  ERROR ${where}: ${msg}`); errors++; };
const warn = (where, msg) => { console.warn(`  warn  ${where}: ${msg}`); warnings++; };

const manifest = JSON.parse(readFileSync(join(CONTENT, "index.json"), "utf8"));

for (const entry of manifest.jigs ?? []) {
  console.log(`\n${entry.id}  (${entry.scene})`);

  let scene;
  try {
    scene = JSON.parse(readFileSync(join(CONTENT, entry.scene), "utf8"));
  } catch (e) {
    fail(entry.scene, e.message);
    continue;
  }

  if (!scene.model) { fail(entry.scene, "no 'model' field"); continue; }
  if (!scene.steps?.length) { fail(entry.scene, "no steps"); continue; }
  if (!(scene.scale > 0)) warn(entry.scene, `scale is ${scene.scale}; viewer falls back to 1`);

  let tree;
  const modelPath = join(CONTENT, dirname(entry.scene), scene.model);
  try {
    tree = unityNodeTree(parseGlbJson(readFileSync(modelPath)));
  } catch (e) {
    fail(scene.model, e.message);
    continue;
  }

  const index = buildNodeIndex(tree);
  const steps = resolveSteps(scene, (m) => warn(entry.scene, m));

  // A leaf that glTFast made ambiguous (multi-primitive meshes gain a sibling of
  // the same name) has no bare alias, so offer the full path instead.
  const suggest = (bad) => {
    const leaf = bad.split("/").pop();
    if (index.byPath.has(leaf)) return ` - did you mean '${leaf}'?`;
    // The shallowest match is the real node; a deeper one of the same name is the
    // synthetic primitive GameObject sitting underneath it.
    const full = [...index.byPath.keys()]
      .filter((k) => k.endsWith(`/${leaf}`))
      .sort((a, b) => a.split("/").length - b.split("/").length);
    return full.length ? ` - did you mean '${full[0]}'?` : "";
  };

  steps.forEach((state, i) => {
    for (const path of state.keys()) {
      if (!index.byPath.has(path)) {
        fail(`step ${i}`, `node path '${path}' matches no node${suggest(path)}`);
      }
    }
  });

  scene.steps.forEach((step, i) => {
    for (const label of step.labels ?? []) {
      if (!label?.anchor) { fail(`step ${i}`, "label with no anchor"); continue; }
      if (!index.byPath.has(label.anchor)) {
        fail(`step ${i}`, `label anchor '${label.anchor}' matches no node${suggest(label.anchor)}`);
      }
      if (label.offset && label.offset.length !== 3) {
        fail(`step ${i}`, `label '${label.text}' offset needs 3 numbers`);
      }
    }

    for (const c of step.callouts ?? []) {
      if (!c) continue;
      // An anchor is optional - a callout without one floats - but a WRONG anchor is a
      // typo the author meant to resolve, so it is an error rather than a silent float.
      if (c.anchor && !index.byPath.has(c.anchor)) {
        fail(`step ${i}`, `callout anchor '${c.anchor}' matches no node${suggest(c.anchor)}`);
      }
      if (!c.title && !c.body) fail(`step ${i}`, "callout has neither title nor body");
      if (c.offset && c.offset.length !== 3) {
        fail(`step ${i}`, `callout '${c.title ?? c.body}' offset needs 3 numbers`);
      }
      if (c.width != null && !(c.width > 0)) {
        fail(`step ${i}`, `callout '${c.title ?? c.body}' width must be positive`);
      }
    }
  });

  if (errors === 0) console.log(`  ${scene.steps.length} steps, ${index.byPath.size} resolvable paths`);
}

console.log(`\n${errors} error(s), ${warnings} warning(s)`);
process.exit(errors ? 1 : 0);
