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
serve.sh            LAN dev server, prints the URL to paste into the app
unity/JigViewer/    the viewer
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

## Publishing a change (the point of all this)

```bash
./serve.sh                       # prints e.g. http://192.168.86.39:8000/index.json
$EDITOR content/watch/scene.json # change a step's move, caption, or label
```

Relaunch the app on the headset. The change is there. No rebuild.

That is the property the whole slice exists to demonstrate — it is what separates a
platform from a demo. `forceRefresh` on `JigLoader` is on by default so the disk cache
never hides it while authoring.

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

Node paths are relative to the model root. The bare leaf name also works when unambiguous.
An unknown path logs a warning and is skipped — remote content can never hard-fail the viewer.

## Checks

```bash
cd unity/JigViewer
/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -projectPath . \
  -executeMethod JigResolverCheck.Run -logFile -
```

Exits non-zero on failure. Covers the step-inheritance rule, snapshot isolation between
steps, graceful handling of malformed content, and parses the shipped `scene.json`.

Verified to actually fail: removing the inherit rule from `JigStepResolver` produces
`FAIL: step 2 dropped the inherited move` and exit 1.

## Known unknowns

Nothing below has run on hardware yet. Each is content-tunable without a rebuild.

- **Explode axis.** The watch stacks along glTF +Z; glTFast converts to Unity's left-handed
  space, so face-out is authored as Unity −Z. If the crystal slides sideways instead of
  lifting, flip the sign in `scene.json`.
- **`scale: 0.035`.** The model is authored in centimetre-ish units; this targets roughly a
  24cm object on a table. A presentation-size judgement, not a measurement.
- **Label offsets** are in model-local units and were placed by arithmetic, not by looking.

## Not built

Web viewer, iOS, authoring GUI, accounts, upload pipeline, analytics, narration audio,
QR/deep-link entry, multi-user, SOC 2. Each sits on top of the scene format rather than
changing it, which is why the format was settled first.
