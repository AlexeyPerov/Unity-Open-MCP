# Extension Packs

Domain-specific typed MCP tools for Unity. Shipped domains are **embedded**
inside the bridge package and activate automatically when their Unity
dependency is present — no separate install, no manifest entry, no onboarding
step.

## Shipped domains (embedded, compile-gated)

| Domain | Unity dependency | Define symbol | Location |
|---|---|---|---|
| Navigation (NavMesh) | `com.unity.ai.navigation` | `UNITY_OPEN_MCP_EXT_NAVIGATION` | `packages/bridge/Editor/TypedTools/Extensions/Navigation/` |
| Input System | `com.unity.inputsystem` | `UNITY_OPEN_MCP_EXT_INPUTSYSTEM` | `.../Extensions/InputSystem/` |
| ProBuilder | `com.unity.probuilder` | `UNITY_OPEN_MCP_EXT_PROBUILDER` | `.../Extensions/ProBuilder/` |
| Particle System | `UnityEngine.ParticleSystemModule` (built-in) | `UNITY_OPEN_MCP_EXT_PARTICLESYSTEM` | `.../Extensions/ParticleSystem/` |
| Animation | `com.unity.modules.animation` (built-in) | `UNITY_OPEN_MCP_EXT_ANIMATION` | `.../Extensions/Animation/` |
| Splines | `com.unity.splines` | `UNITY_OPEN_MCP_EXT_SPLINES` | `.../Extensions/Splines/` |
| Lighting | built-in (`Light` / `ReflectionProbe` / `RenderSettings` / `Lightmapping`) | *(none — ungated)* | `.../Extensions/Lighting/` |
| Audio | built-in (`AudioSource` / `AudioListener` / `AudioMixer` / `AudioMixerGroup`) | *(none — ungated)* | `.../Extensions/Audio/` |
| UI (uGUI) | `com.unity.ugui` (`Canvas` / `CanvasScaler` / `GraphicRaycaster` / `Image` / `Text` / `Button` / `Slider` / `Toggle` / `InputField` / layout groups / `EventSystem`) | `UNITY_OPEN_MCP_EXT_UI` | `.../Extensions/UI/` |
| Constraints & LOD | built-in (`PositionConstraint` / `RotationConstraint` / `AimConstraint` / `ParentConstraint` / `ScaleConstraint` / `LODGroup`) | *(none — ungated)* | `.../Extensions/Constraints/` |
| Terrain | built-in (`Terrain` / `TerrainData` / `TreePrototype` / `TerrainLayer`) | *(none — ungated)* | `.../Extensions/Terrain/` |
| SpriteAtlas | built-in (`SpriteAtlas` / `SpriteAtlasAsset` / `SpriteAtlasPackingSettings` / `SpriteAtlasTextureSettings` in `UnityEngine.U2D` / `UnityEditor.U2D`) | *(none — ungated)* | `.../Extensions/SpriteAtlas/` |
| Texture | built-in (`TextureImporter` in `UnityEditor`) | *(none — ungated)* | `.../Extensions/Texture/` |
| Cinemachine | `com.unity.cinemachine` ≥ 3.x | *(none — **reflection-gated**)* | `.../Extensions/Cinemachine/` |
| Timeline | `com.unity.timeline` | `UNITY_OPEN_MCP_EXT_TIMELINE` | `.../Extensions/Timeline/` |
| Tilemap | `com.unity.2d.tilemap` (+ `com.unity.2d.tilemap.extras` for RuleTile) | `UNITY_OPEN_MCP_EXT_TILEMAP` (+ `UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS` inner guard for `create_rule_tile`) | `.../Extensions/Tilemap/` |
| Shader Graph | `com.unity.shadergraph` | `UNITY_OPEN_MCP_EXT_SHADERGRAPH` | `.../Extensions/ShaderGraph/` (**auto-activating**) |
| VFX Graph | `com.unity.visualeffectgraph` | `UNITY_OPEN_MCP_EXT_VFX` | `.../Extensions/VFX/` (**auto-activating**) |
| Memory Profiler | `com.unity.memoryprofiler` | `UNITY_OPEN_MCP_EXT_MEMORYPROFILER` | `.../Extensions/MemoryProfiler/` (**auto-activating**, sense-prefixed tool) |

Navigation is the reference template; InputSystem, ProBuilder, ParticleSystem,
Animation, and Timeline share the same layout. Splines is the most recently
added compile-gated domain — it proves the embedded + grouped model extends to
additional Unity APIs. Lighting, Audio, Constraints & LOD, Terrain,
SpriteAtlas, and Texture are **ungated** domains: their types are
unconditionally present in every Unity install, so they ship without a
`UNITY_OPEN_MCP_EXT_*` define and compile into every bridge build (their
`lighting` / `audio` / `constraints` / `terrain` / `sprite2d` tool
groups are still hidden from `ListTools` until the session activates them via
`manage_tools`). SpriteAtlas + Texture share the `sprite2d` group (the 2D art
pipeline activates together). Terrain's heightmap / splat writes cap at
513×513 per call to push agents toward tiled region writes. UI (uGUI) is
compile-gated on `com.unity.ugui` (Unity 6 made the formerly built-in uGUI
types optional); its `ui` tool group is hidden from `ListTools` until the
session activates it via `manage_tools`. UI's TextMesh Pro (`TMP_Text`) is
optional and detected at call time via reflection — when absent,
`ui_element_add` returns `tmp_package_required` (no silent legacy-`Text`
fallback).

**Cinemachine is the only reflection-gated pack** (the canonical version-split
case): its 2.x→3.x split changed the camera class itself (`CinemachineVirtualCamera`
→ `Unity.Cinemachine.CinemachineCamera`), so the assembly always compiles and
the supported version is detected at call time. See §Reflection fallback
policy below. **Tilemap's RuleTile** (`tilemap_create_rule_tile`) is the
**two-defines-two-guards** case: the outer pack gate
(`UNITY_OPEN_MCP_EXT_TILEMAP`) lets the tool compile in, but the RuleTile body
is inner-guarded by `UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS`. When extras is absent,
the tool returns a clean `tilemap_extras_required` install error — no broken
reference.

**Shader Graph is the first auto-activating pack**: the `shadergraph` group
activates automatically for the session when `com.unity.shadergraph` is
installed — its tools appear in `ListTools` with no manual `manage_tools`
call. This is the package-detection auto-activation model (additive to the
manual-activation model the other groups use); see §Package-detection
auto-activation below. Shader Graph's editing API (`UnityEditor.ShaderGraph`:
`GraphData`, `AbstractMaterialNode`, slots) is partially internal, so the
mutating tools (`create` / `node_add` / `node_connect`) wrap it behind a
single reflection helper and degrade to a structured
`shadergraph_api_unavailable` error when the installed version exposes a
different surface; `shader_graph_open` parses the `.shadergraph` JSON directly
and is always stable. The graph tools are complementary to the inspect surface
(`shader_get_data` / `shader_list_all`), which reads **compiled shader
properties** rather than the authored graph.

**VFX Graph** (`com.unity.visualeffectgraph`) is the second auto-activating
pack: the `vfx` group activates automatically when the package is installed. Its
editor graph model (`UnityEditor.VFX`: `VFXGraph`, `VFXContext`, `VFXBlock`,
`VFXSlot`) is **more internal/unstable** than Shader Graph's — even the
competitor ships only list/open for the same reason. The read paths (`vfx_list` /
`vfx_open`) work over the public **runtime** `UnityEngine.VFX.VisualEffectAsset`
type and the serialized file format (version-stable); the mutating path
(`vfx_block_edit`) reflects over the editor graph model and **requires the VFX
Graph window to be open** (no stable public headless entry point), degrading to
a structured `vfx_block_edit_requires_editor_window` error otherwise.

**Memory Profiler** (`com.unity.memoryprofiler`) is the third auto-activating
pack: the `memoryprofiler` group activates automatically when the package is
installed. It ships a single **sense-prefixed** tool
(`unity_senses_memory_snapshot_capture`) because it pairs with the existing
senses profiler family (`profiler_get_script_stats` /
`profiler_capture_frame`) rather than the typed-editor authoring surface —
capture a memory snapshot, then read CPU/frame context from the profiler tools
for a fuller performance picture than a standalone memory tool. The capture is
**read-only re: game/project state but produces a `.snap` file** (`Gate = Off`,
`ReadOnlyHint = true`, `Lifecycle = EditorSettle` — capture can take seconds).
The capture surface moved namespaces across Unity versions
(`Unity.Profiling.Memory.MemoryProfiler` new vs `UnityEditor.MemoryProfiler` /
`Profiling.Memory.Experimental.MemoryProfiler` legacy) and is **callback-based
(async)** — the tool reflects over whichever surface is present, invokes
`TakeSnapshot` with a delegate, and blocks (bounded by a timeout, pumping editor
updates so the callback can fire) until the callback reports completion. When
the API cannot be reached, the tool returns a structured
`memoryprofiler_api_unavailable` error.

## Embedded domain model

Shipped domain tools live in `packages/bridge/Editor/TypedTools/Extensions/*`,
not as separate UPM packages. They compile in **only** when their Unity
dependency is present, using a **self-contained per-sub-asmdef gate**:

1. **A per-domain sub-asmdef** under `TypedTools/Extensions/<Domain>/` carries
   BOTH:
   - a `versionDefines` entry matching the Unity dependency (`name` = package
     or module id, `expression` = semver expression like `1.0.0`) that
     defines `UNITY_OPEN_MCP_EXT_<DOMAIN>` when the dependency resolves, AND
   - a `defineConstraints: ["UNITY_OPEN_MCP_EXT_<DOMAIN>"]` entry on the same
     symbol.
   The gate is fully self-contained on the sub-asmdef. Unity only compiles
   the sub-asmdef when the `versionDefines` rule fires, so the optional
   package reference never breaks a project that lacks it.

The symbol naming convention is `UNITY_OPEN_MCP_EXT_<DOMAIN>` (uppercase,
`<DOMAIN>` matches the tool-group id).

> **Why per-sub-asmdef (not the root bridge asmdef).** Unity 6's
> `versionDefines` semantics are per-assembly: a symbol defined by a
> `versionDefines` entry on asmdef A only satisfies `defineConstraints` on
> asmdef A itself — it does **not** propagate to sibling sub-asmdefs. An
> earlier design placed all `versionDefines` on the root bridge asmdef and
> relied on them flowing to the sub-asmdefs' `defineConstraints`; on Unity 6
> that silently failed for every gated domain (the symbol was set on the
> root but never satisfied the sub-asmdef constraints, so no gated
> sub-assembly compiled). Putting both halves of the gate on the SAME
> sub-asmdef is the only shape that reliably resolves on Unity 6.

Each domain source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_<DOMAIN>` as a belt-and-suspenders guard that
documents the gate at the source level.

### Why compile-gating (not reflection)

Compile-gating is the **primary and mandatory** mechanism for every shipped
domain. There is **no runtime reflection probing** for compile-gated domains:
when the dependency is absent, the tools are simply not compiled in, and the
capability surface reports the domain as
`available: false (dependency missing: <Unity package name>)`. No silent
no-op, no failed dispatch.

### Reflection fallback policy (narrow exception)

Reflection-based runtime type detection is allowed **only** when a domain's
public API is **version-split** across Unity/package versions (canonical case:
Cinemachine 2.x `CinemachineVirtualCamera` vs 3.x `CinemachineCamera`). A
reflection domain must document:

- the API split it bridges,
- the minimum version it targets, and
- the clear error returned for unsupported versions.

**Only one shipped extension domain** — Cinemachine — uses reflection, because
its 2.x `CinemachineVirtualCamera` vs 3.x `Unity.Cinemachine.CinemachineCamera`
API split spans a class rename (no compile-time symbol can bridge both). The
`CinemachineVersion` layer in `CinemachineJson.cs` detects the installed major
at call time and returns `cinemachine_3x_required` (when 2.x is installed) or
`cinemachine_package_required` (when the package is absent). The
**compile-gated** domains are Nav, Input, ProBuilder, Particles, Animation,
Splines, Timeline, Tilemap, ShaderGraph, VFX, MemoryProfiler, and UI (each
gated on its optional Unity package via the self-contained per-sub-asmdef
gate described above). The **ungated** domains — Lighting, Audio,
Constraints & LOD, Terrain, SpriteAtlas, Texture — ship unconditionally
because their types are part of the core engine modules present in every
Unity install.

### Wiring in a new embedded domain

1. Create `packages/bridge/Editor/TypedTools/Extensions/<Domain>/` with:
   - a sub-asmdef carrying BOTH a `versionDefines` entry (mapping the Unity
     dependency → `UNITY_OPEN_MCP_EXT_<DOMAIN>`) AND a `defineConstraints`
     entry on the same symbol, plus the domain package reference. The two
     halves of the gate live on the SAME asmdef so Unity 6 resolves them
     reliably (see the note above).
   - the tool source files, each wrapped in `#if UNITY_OPEN_MCP_EXT_<DOMAIN>`,
   - registry discovery via `[BridgeToolType]` + `[BridgeTool]` (the
     `BridgeToolRegistry` scans all loaded assemblies, so sub-asmdefs register
     automatically).
2. Keep tool IDs stable: `unity_open_mcp_<domain>_<action>`.
3. Preserve gate contracts on mutating tools (`paths_hint`, `IsMutating`,
   `Gate`, `Lifecycle`, idempotent/destructive hints) and JSON response schema
   parity with the MCP tool definition.
4. Mirror the layout for EditMode tests under
   `packages/bridge/Tests/Editor/TypedTools/<Domain>/` with their own gated
   test sub-asmdef.

Use Navigation (`TypedTools/Extensions/Navigation/`) as the reference
template.

### Package-detection auto-activation

Most domain groups are **manual-activation**: a fresh session starts with only
`core` (and the always-on `gate-and-verify` / `asset-intelligence` /
`typed-editor` / `diagnostics` groups) visible, and the agent must call
`unity_open_mcp_manage_tools(action="activate", group="<domain>")` before the
domain's tools appear in `ListTools`.

A domain group may additionally opt into **package-detection auto-activation**
by setting `autoActivate: true` (with a matching `unityPackage`) in the
canonical tool-group catalog
(`mcp-server/src/capabilities/tool-groups.ts`). When the project has the
group's `unityPackage` installed, the group **activates automatically** for the
session — its tools appear in `ListTools` with no manual `manage_tools` call.
This mirrors the "package installed → tools appear" UX of the broader tooling
ecosystem, additive to the manual-activation model:

- Auto-activation is **ephemeral** — same in-memory session store as manual
  activation, resets to defaults on MCP-server restart.
- Auto-activation is **reconciled lazily** from the live bridge's compiled-tool
  inventory (`GET /tools`): a group is package-present when any of its
  compiled-in tool names appears in that set. The reconciliation runs on
  `capabilities` and `manage_tools(list_groups)` calls, so an agent that calls
  either sees a fresh snapshot.
- `manage_tools(list_groups)` and `capabilities` report each auto-activated
  group with `activationSource: "auto"` (and `autoActivated: true`) plus its
  `packageDependency`, so an agent can tell **why** a group is visible.
- When the package is absent, the group stays hidden and the capability output
  shows the "install X" reason (`available: false (dependency missing:
  com.unity.shadergraph)`).
- A manually-activated group wins: auto-activation never flips a group the
  operator deliberately deactivated, and a package that goes away only drops a
  group that was auto-activated (not one the agent re-activated by hand).

Shader Graph (`shadergraph` on `com.unity.shadergraph`), VFX Graph (`vfx` on
`com.unity.visualeffectgraph`), and Memory Profiler (`memoryprofiler` on
`com.unity.memoryprofiler`) are the shipped auto-activating domains. Existing
domain groups keep their manual-activation behavior unless they explicitly opt
in.

## Tool naming

Domain tools use:

```text
unity_open_mcp_<domain>_<action>
```

Example: `unity_open_mcp_navigation_surface_add`.

IDs are stable across the migration from standalone extension packs to the
embedded model — the MCP tool definitions and skills do not change.

## Legacy / community domain packs (advanced path)

`packages/extensions/` is the home for **third-party / community** domain packs
that are not shipped with the bridge. Each is a separate UPM package with its
own asmdef referencing the bridge.

### Deprecated shipped-domain copies

The five shipped domains (Nav, Input, ProBuilder, Particles, Animation) also
have **legacy copies** under `packages/extensions/{navigation,inputsystem,probuilder,particlesystem,animation}/`.
These are **deprecated** and are **retained only so pinned manifests keep
resolving**:

- Do **not** install `com.alexeyperov.unity-open-mcp-ext-<domain>` for any
  shipped domain in a new project — the bridge's embedded tools are the source
  of truth and activate automatically when the Unity dependency is present.
- Installing a legacy pack **and** the embedded copy registers the same tool
  ids twice. `BridgeToolRegistry` keeps the first-registered entry and records
  the collision (`DuplicateCount` / `DuplicateToolNames` after each scan),
  emitting a non-fatal warning.
- The Hub wizard **never** installs legacy ext packs — only the bridge, verify,
  and opt-in Unity domain packages.
- New first-party domains go into `packages/bridge/Editor/TypedTools/Extensions/`,
  never here.

### Authoring a community pack

Use `packages/extensions/template/` as the reference scaffold — copy it, rename
the package, and add typed `[BridgeTool]` helpers for your domain. A community
pack must not reuse tool ids owned by a shipped embedded domain (that would
duplicate-register).

### Manual install (community pack only)

Add the package id under `dependencies` in `Packages/manifest.json`. Example
for a local monorepo development copy of a community pack:

```json
{
  "dependencies": {
    "com.example.my-mcp-ext": "file:../../packages/extensions/my-ext"
  }
}
```

## Related docs

- [Architecture](architecture.md) — bridge / verify / MCP server boundaries.
- [MCP tools API](api/mcp-tools.md)
