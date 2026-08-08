#!/usr/bin/env node
// Self-check for the shared format module.
//
// The round-trip case is the important one: the editor overwrites scene.json in
// place, so a lossy serialiser silently destroys authored content. An earlier
// version of formatScene did exactly that - it wrote raw placeholder strings where
// the arrays should have been - and this is the check that caught it.
//
// Run: node tools/test_format.mjs        (exits non-zero on failure)

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildNodeIndex, formatScene, resolveSteps } from "./editor/resolver.js";
import { parseGlbJson, unityNodeTree } from "./editor/glb.js";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const CONTENT = join(ROOT, "content");

let failures = 0;
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log(`  ${ok ? "ok   " : "FAIL "} ${name}`);
  if (!ok) {
    console.log(`         got:  ${JSON.stringify(got)}`);
    console.log(`         want: ${JSON.stringify(want)}`);
    failures++;
  }
};

console.log("formatScene");

// Every shipped file must survive a parse/serialise round trip unchanged.
for (const rel of ["watch/scene.json", "watch/quick-look.json", "index.json"]) {
  const original = readFileSync(join(CONTENT, rel), "utf8");
  const parsed = JSON.parse(original);
  check(`${rel} round-trips semantically`, JSON.parse(formatScene(parsed)), parsed);
}

// The layout is the reason this function exists rather than JSON.stringify.
const laid = formatScene({ steps: [{ nodes: [{ path: "A", move: [0, 1, -2.5] }] }] });
check("number arrays stay on one line", /"move": \[0, 1, -2\.5\]/.test(laid), true);
check("leaf objects stay on one line", /\{ "path": "A", "move": \[0, 1, -2\.5\] \}/.test(laid), true);
check("empty arrays survive", formatScene({ nodes: [] }).includes('"nodes": []'), true);

// Both of these broke a previous implementation that used in-band placeholders.
const braces = { caption: "a { brace } and [ bracket ]", nodes: [], move: [1, 2, 3] };
check("a brace inside a string cannot corrupt the file", JSON.parse(formatScene(braces)), braces);
const digits = { title: "2024", id: "7", v: [1, 2, 3] };
check("an all-digits string value is not swallowed", JSON.parse(formatScene(digits)), digits);

console.log("\nresolveSteps");

// The inherit-if-omitted rule, which is the bug class JigResolverCheck exists for.
const scene = {
  steps: [
    { nodes: [{ path: "A", move: [0, 0, -1] }] },
    { nodes: [{ path: "B", move: [0, 0, -2] }] },
    { nodes: [{ path: "A", visible: false }] },
  ],
};
const steps = resolveSteps(scene);
check("step 1 keeps A's move", steps[1].get("A").move, [0, 0, -1]);
check("step 2 inherits A's move when only visible is set", steps[2].get("A").move, [0, 0, -1]);
check("step 2 keeps B from step 1", steps[2].get("B").move, [0, 0, -2]);
check("step 0 is not mutated by later steps", steps[0].has("B"), false);
check("visible=false is distinguished from absent", steps[2].get("A").visible, false);

const bad = resolveSteps({ steps: [{ nodes: [{ path: "A", move: [1, 2] }] }] });
check("a malformed move falls back rather than throwing", bad[0].get("A").move, [0, 0, 0]);

console.log("\nnode paths (glTFast hierarchy)");

// A multi-primitive mesh gains a sibling GameObject of the same name, which makes
// that leaf ambiguous and removes its bare-name alias. Getting this wrong is what
// made two labels in the shipped content silently never render.
const tree = unityNodeTree(parseGlbJson(readFileSync(join(CONTENT, "watch/ChronographWatch.glb"))));
const index = buildNodeIndex(tree);
check("a single-primitive node keeps its bare-leaf alias", index.byPath.has("Glass Face"), true);
check("a multi-primitive node has NO bare-leaf alias", index.byPath.has("Hand Seconds"), false);
check("...and is reachable by full path", index.byPath.has("Scene/Hands/Hand Seconds"), true);
check("names keep their spaces, unlike three.js", index.byPath.has("Glass_Face"), false);

console.log(failures ? `\n${failures} failure(s)` : "\nall checks pass");
process.exit(failures ? 1 : 0);
