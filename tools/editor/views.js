// How a Jig's labels and callouts are drawn in a browser.
//
// Shared by the authoring editor and the web viewer (tools/viewer/), so a callout
// an author places is the same callout a shared link shows. The rules the sprites
// follow come from the Unity viewer - JigLabel.Create and JigCallout - and the
// numbers cross into Unity space through flipX() and nowhere else.
//
// Nothing here reads global state: every function takes what it needs, because the
// editor and the viewer keep their state in different shapes.

import * as THREE from "three";
import { flipX } from "./resolver.js";

/// A few words on a leader line. Sized in canvas pixels here; the caller scales the
/// sprite into world units, which depends on the container's scale.
export function labelSprite(text) {
  const pad = 16, font = 44;
  const c = document.createElement("canvas");
  let ctx = c.getContext("2d");
  ctx.font = `${font}px sans-serif`;
  c.width = Math.ceil(ctx.measureText(text).width) + pad * 2;
  c.height = font + pad * 2;

  ctx = c.getContext("2d");
  ctx.font = `${font}px sans-serif`;
  ctx.fillStyle = "rgba(20,22,27,.82)";
  ctx.fillRect(0, 0, c.width, c.height);
  ctx.fillStyle = "#fff";
  ctx.textBaseline = "middle";
  ctx.fillText(text, pad, c.height / 2);

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false }));
  sprite.scale.set(c.width / c.height, 1, 1);
  return sprite;
}

/// Draws a callout as the viewer will: a fixed-width panel with a bold heading over
/// a wrapped body, and a leader line only when it is anchored.
export function calloutSprite(spec, widthWorld) {
  const px = 512;
  const scale = px / widthWorld;              // world units -> canvas pixels
  const pad = px * 0.12;
  const titleSize = px * 0.13, bodySize = px * 0.10;

  const c = document.createElement("canvas");
  let ctx = c.getContext("2d");

  const wrap = (text, font) => {
    ctx.font = font;
    const out = [];
    for (const para of (text || "").split("\n")) {
      let line = "";
      for (const word of para.split(/\s+/).filter(Boolean)) {
        const next = line ? `${line} ${word}` : word;
        if (ctx.measureText(next).width > px && line) { out.push(line); line = word; }
        else line = next;
      }
      out.push(line);
    }
    return out.filter((l, i, a) => l || a.length === 1);
  };

  const titleFont = `bold ${titleSize}px sans-serif`;
  const bodyFont = `${bodySize}px sans-serif`;
  const titleLines = spec.title ? wrap(spec.title, titleFont) : [];
  const bodyLines = spec.body ? wrap(spec.body, bodyFont) : [];

  const gap = titleLines.length && bodyLines.length ? pad * 0.4 : 0;
  const content = titleLines.length * titleSize * 1.25 + gap + bodyLines.length * bodySize * 1.35;

  c.width = px + pad * 2;
  c.height = content + pad * 2;

  ctx = c.getContext("2d");
  ctx.fillStyle = "rgba(23,26,33,.94)";
  ctx.fillRect(0, 0, c.width, c.height);

  ctx.fillStyle = "#fff";
  ctx.textBaseline = "top";
  let y = pad;
  ctx.font = titleFont;
  for (const l of titleLines) { ctx.fillText(l, pad, y); y += titleSize * 1.25; }
  y += gap;
  ctx.font = bodyFont;
  ctx.fillStyle = "#d6d9e0";
  for (const l of bodyLines) { ctx.fillText(l, pad, y); y += bodySize * 1.35; }

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false }));
  sprite.scale.set(c.width / scale, c.height / scale, 1);
  return sprite;
}

function leaderLine() {
  return new THREE.Line(
    new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3()]),
    new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.6, depthTest: false })
  );
}

/// Removes views built by the two builders below, including their leader lines,
/// which are parented to the world rather than to the group.
export function disposeViews(views) {
  for (const v of views) {
    v.group.parent?.remove(v.group);
    v.line?.parent?.remove(v.line);
  }
}

/// One view per label in a step. Labels whose anchor does not resolve are skipped,
/// exactly as the Unity viewer skips them.
///
/// Matches JigLabel.Create: parented to the anchor's PARENT and positioned at the
/// anchor's own local position plus the offset, so the offset is a vector in the
/// parent's local space.
export function buildLabelViews({ specs, index, container, lineParent }) {
  const views = [];

  (specs ?? []).forEach((spec, i) => {
    const anchor = index.byPath.get(spec.anchor)?.obj;
    if (!anchor) return;

    const group = new THREE.Group();
    const sprite = labelSprite(spec.text || "(no text)");
    group.add(sprite);
    anchor.parent.add(group);
    group.position.copy(anchor.position).add(new THREE.Vector3(...flipX(spec.offset ?? [0, 0, 0])));

    // Sized in world units: a sprite inside a model scaled to 0.035 would
    // otherwise be illegible.
    sprite.scale.multiplyScalar(0.035 / (container.scale.x || 1));

    const line = leaderLine();
    lineParent.add(line);

    views.push({ group, sprite, line, anchor, i });
  });

  return views;
}

/// One view per callout in a step. An unanchored callout floats beside the model
/// with no leader line, which is what a step-level aside needs.
export function buildCalloutViews({ specs, index, container, lineParent }) {
  const views = [];

  (specs ?? []).forEach((spec, i) => {
    const anchor = spec.anchor ? index.byPath.get(spec.anchor)?.obj : null;
    const parent = anchor ? anchor.parent : container;

    const group = new THREE.Group();
    const sprite = calloutSprite(spec, spec.width > 0 ? spec.width : 6);
    group.add(sprite);
    parent.add(group);

    const base = anchor ? anchor.position : new THREE.Vector3();
    group.position.copy(base).add(new THREE.Vector3(...flipX(spec.offset ?? [0, 0, 0])));

    let line = null;
    if (anchor) {
      line = leaderLine();
      lineParent.add(line);
    }

    views.push({ group, sprite, line, anchor, i });
  });

  return views;
}

const tmpA = new THREE.Vector3(), tmpB = new THREE.Vector3();

/// Redraws a view's leader line in world space. Called every frame because the
/// anchor moves during a step tween. `color` lets the editor tint the selection.
export function updateLeader(v, color = 0xffffff) {
  if (!v.line || !v.anchor) return;
  v.group.getWorldPosition(tmpA);
  v.anchor.getWorldPosition(tmpB);
  const p = v.line.geometry.attributes.position;
  p.setXYZ(0, tmpA.x, tmpA.y, tmpA.z);
  p.setXYZ(1, tmpB.x, tmpB.y, tmpB.z);
  p.needsUpdate = true;
  v.line.material.color.set(color);
}
