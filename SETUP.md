# JigClone — setup

A Quest 3 passthrough viewer for stepped 3D presentations, fetched over HTTP at runtime.
Recreates the core of [JigSpace](https://www.jig.com) as a vertical slice.

Full rationale and the decisions behind it: `~/.claude/plans/what-would-it-take-shimmering-finch.md`.

## What is here

```
content/            the Jig itself - served over HTTP, never bundled
  index.json        manifest listing available Jigs
  watch/scene.json  6-step chronograph teardown
  watch/*.glb       Khronos ChronographWatch sample asset
serve.sh            dev server, prints the URLs for the app, the viewer and the editor
tools/
  serve.py          GET on the LAN, PUT from this machine only
  editor/           the authoring editor - open it in a browser
                    resolver.js, glb.js and views.js are shared with the web viewer
  viewer/           the web viewer - the same Jig by link, no app
  validate.mjs      checks content against the .glb before it reaches a headset
unity/JigViewer/    the headset viewer
```

## Versions

Resolved by Package Manager on this machine, not pinned by hand:

| | |
|---|---|
| Unity | 6000.3.12f1 |
| com.unity.cloud.gltfast | 6.19.0 |
| com.unity.xr.arfoundation | 6.6.1 |
| com.unity.xr.meta-openxr | 2.5.1 |
| com.unity.xr.interaction.toolkit | 3.5.1 |
| com.unity.render-pipelines.universal | 17.3.0 |
| com.unity.nuget.newtonsoft-json | 3.2.2 |

## Remaining setup — must be done in the Editor

Everything else is scripted (`JigPackageBootstrap`, `JigSceneBootstrap`). These are not,
deliberately: XR Plug-in Management and the OpenXR feature toggles live in version-specific
serialized assets, and generating them blind produces a build that looks configured and
renders black.

1. Open `unity/JigViewer`, then `Assets/Scenes/Jig.unity`.
2. **Project Settings > XR Plug-in Management > Android**: tick **OpenXR**.
3. **XR Plug-in Management > OpenXR > Android**:
   - Interaction profile: **Oculus Touch Controller Profile**
   - Enable **Meta Quest Support**, **Meta Quest: Passthrough**,
     **Meta Quest: Plane Detection**, **Meta Quest: Anchors**
   - Plane detection will silently return nothing if Anchors is off. Known Meta OpenXR issue.
4. Select **Jig App** in the scene, set `Manifest Url` on the `JigLoader` component to
   whatever `./serve.sh` prints.
5. **File > Build Settings > Android**, connect the Quest 3, **Build and Run**.

Already scripted, nothing to do: packages (`JigPackageBootstrap`), scene and Android player
settings (`JigSceneBootstrap`), URP asset created and assigned (`JigRenderPipelineBootstrap`).

Note that installing the URP *package* does not put a project on URP — a pipeline asset has
to exist and be assigned, or the project silently stays on Built-in and glTFast imports every
material pink. That is done; `Assets/Settings/JigUniversalRenderPipeline.asset`.

## On-device prerequisite

Plane detection on Quest does **not** scan the room. Meta OpenXR reads the headset's saved
Room Setup, and only returns up-facing surfaces (Table, Floor, Bed).

Run **Settings > Physical Space > Space Setup** on the headset first, and include a table.
Without it the app finds zero planes forever and falls back to placing the Jig in front of
you after 3 seconds.

Plane detection and passthrough only work in an Android build. Neither works in Play mode.

## Authoring

```bash
./serve.sh                       # prints the app URL and the editor URL
open http://127.0.0.1:8000/editor/
```

The editor loads the model, so nothing has to be guessed: click a part to get the
exact node path the viewer will resolve, drag it to author a step's `move`, place
labels and see the leader line, and step through the sequence. Save writes
`scene.json` back in place.

**Do not hand-write node paths.** Two of the labels shipped in `scene.json` used a
path that resolved to nothing, so they never appeared on the headset and the only
symptom was a warning in logcat. `node tools/validate.mjs` now catches that class of
mistake, and the editor cannot produce it in the first place.

Hand-editing still works if you prefer:

```bash
$EDITOR content/watch/scene.json  # change a step's move, caption, or label
node tools/validate.mjs           # before you go near the headset
```

Relaunch the app on the headset. The change is there. No rebuild.

That is the property the whole slice exists to demonstrate — it is what separates a
platform from a demo. `forceRefresh` on `JigLoader` is on by default so the disk cache
never hides it while authoring.

## The web viewer

```bash
./serve.sh                       # prints the viewer URL too
open "http://127.0.0.1:8000/viewer/?jig=chronograph-teardown&step=3"
```

The same content the headset fetches, in a browser, with nothing to install — which
is how a Jig gets shared with someone who does not own a Quest. It uses the editor's
`resolver.js`, `glb.js` and `views.js`, so the step rules, the node paths and the
callout layout are one implementation, not a second one that drifts.

- `?jig=<id>&step=<n>` is the whole of its state, rewritten as you step, so a copied
  link reopens exactly what was on screen. Steps are 1-based in the URL.
- **Share** gives that link and an `<iframe>` snippet. The embed adds `ui=0`, which
  drops the branding and the picker but keeps the step controls.
- Published by the Pages workflow at `<pages-url>/viewer/`. `index.json` stays at the
  root, so `JigLoader.manifestUrl` is unchanged and the APK does not care.

Two deliberate differences from the headset:

- **`rotate` is not applied.** It is authored in Unity's ZXY Euler order and the
  mapping into three's is unverified, so the viewer shows a note instead of showing
  the wrong thing — the same stance the editor takes. No shipped content authors it.
- **No passthrough, no anchoring, no room.** It is a viewer on a grid, not AR.

Only `resolver.js`, `glb.js`, `views.js` and `vendor/` are published from the editor
directory — never `editor.js` or its page. The editor saves through a loopback-only
PUT that does not exist on Pages, so publishing it would offer an authoring UI that
cannot author.

## The scene format

Steps carry **offsets from each node's rest pose**, not absolute transforms — several nodes
in the sample model have non-zero rest translations, so absolute values would teleport them.
An omitted field inherits the previous step's resolved value.

```json
{
  "path": "Hands/Hand Seconds",
  "move":   [0, 0, -1.2],
  "rotate": [0, 90, 0],
  "scale":  [1, 1, 1],
  "visible": true
}
```

### Labels and callouts

Two text types, and they are not the same thing:

```json
"labels": [
  { "anchor": "Glass Face", "text": "Sapphire crystal", "offset": [2.6, 0.8, 0] }
],
"callouts": [
  { "anchor": "Glass Face", "title": "Sapphire crystal",
    "body": "Second only to diamond in hardness.", "offset": [-6.5, 1.8, 0], "width": 4.5 }
]
```

A **label** is a few words on a leader line; it must have an anchor. A **callout** is a
panel with a heading and a wrapped paragraph, and its `anchor` is **optional** — omit it
and the callout floats beside the model with no leader line, which is what a step-level
aside needs. `width` is the wrap width in the same model-local units as `offset`, so at
`scale: 0.035` a width of `4.5` is about 16cm on the table.

A scene written before callouts existed has no `callouts` key; it deserializes to an
empty list and behaves exactly as before.

### Node paths

Paths are relative to the `jig:<id>` container, **not** to the glTF's root nodes. Two
rules decide what a valid path looks like, and both come from how glTFast builds the
hierarchy rather than from what is in the glTF:

- **A scene node is usually in the way.** glTFast's default is
  `SceneObjectCreation.WhenMultipleRootNodes`, so a glTF with more than one root node
  gets a `Scene` GameObject inserted. Full paths then start `Scene/…`. A glTF with
  exactly one root node gets none.
- **The bare leaf name works only when unambiguous.** A mesh with multiple primitives
  is split: primitive 0 stays on the node, and every additional primitive becomes a
  child GameObject *named after the mesh*. So node `Hand Seconds` (2 primitives) gains
  a sibling also called `Hand Seconds`, that leaf is no longer unique, and only the
  full path `Scene/Hands/Hand Seconds` resolves.

`Glass Face` works because that mesh has one primitive. `Hand Seconds` does not.
Nothing about the glTF tells you which — run `node tools/validate.mjs`, or use the
editor, which shows the path that will actually resolve.

An unknown path logs a warning and is skipped — remote content can never hard-fail
the viewer.

## Checks

```bash
cd unity/JigViewer
/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -projectPath . \
  -executeMethod JigResolverCheck.Run -logFile -
```

Exits non-zero on failure. Covers the step-inheritance rule, snapshot isolation between
steps, graceful handling of malformed content, and parses the shipped `scene.json`.

Three more, none of which need Unity:

```bash
node tools/validate.mjs      # every authored path resolves against the actual .glb
node tools/test_format.mjs   # scene.json survives a save unchanged; resolver rules hold
python3 tools/test_serve.py  # the dev server refuses writes from anything but this machine
```

`test_format.mjs` also covers `setVisibleDeep`, the rule that makes `visible: false`
work on a multi-primitive mesh without taking a separately-authored part down with
it. Verified to actually fail: removing the descent produces
`FAIL ...and so does its split-off second primitive` and exit 1.

`test_format.mjs` exists because an earlier serialiser silently replaced every array in
`scene.json` with a placeholder string. Saving is an overwrite, so a lossy serialiser
destroys authored content — that check is the one that caught it.

Verified to actually fail: removing the inherit rule from `JigStepResolver` produces
`FAIL: step 2 dropped the inherited move` and exit 1.

## Coordinate space

glTFast converts glTF to Unity by **negating X**. Y and Z pass through unchanged
(`NodeExtension.cs:65`). Every number in `scene.json` is therefore in Unity space, and
the editor converts at exactly one point (`flipX` in `tools/editor/resolver.js`).

The editor renders the model in plain three.js space and does *not* mirror it: Unity's
X negation plus its left-handed frame produce the same image as three's unmodified
right-handed one. Mirroring to "match Unity" would show a mirrored model.

## Known unknowns

- **`scale: 0.035`.** The model is authored in centimetre-ish units; this targets roughly a
  24cm object on a table. A presentation-size judgement, not a measurement.
- **Label offsets.** Now placed by looking, in the editor. The viewer positions a label at
  the anchor's position *at the moment the step change fires*, which for an animated step
  is still the previous step's position — so a label on a part that moves in the same step
  sits where the part came from. The editor previews the intended position instead.
**Fixed 2026-08-08, both need a headset to confirm:**

- **Label placement.** `StepChanged` fires as the tween *starts*, so a label positioned
  from `anchor.localPosition` landed where the part was coming *from*. `JigApp` now asks
  `JigPlayer.ResolvedLocalPosition` for where the anchor will *end up*.
- **Hiding a multi-primitive part.** `SetVisible` only touched the node's own renderers,
  so `visible: false` on a 2-primitive part hid half of it. It now descends into
  sub-objects, stopping at any transform the scene names separately — so hiding a group
  still does not take its authored parts with it.

**Resolved** (was: explode axis). The case back's rest translation is `+Z`
(`[0, 0, 0.01]`), so **−Z is the face side** and the signs authored in `scene.json` are
correct. The earlier explanation here — that the handedness conversion flipped it — was
wrong; Z is not converted at all.

## Not built

Phone AR (WebXR on Android, USDZ Quick Look on iOS), accounts, analytics, narration
audio, QR codes, multi-user, SOC 2. Each sits on top of the scene format rather than
changing it, which is why the format was settled first.

The authoring GUI now exists (`tools/editor/`), but it is single-user, local, and has no
undo — git is the undo, so commit before a long authoring session. Rotation is not
authored by gizmo: Unity uses ZXY intrinsic Euler order and three.js defaults to XYZ,
and that mapping has not been verified, so `rotate` stays hand-edited and the editor
says so rather than previewing it wrongly.
