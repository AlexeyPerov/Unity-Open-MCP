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
| UI (uGUI) | built-in (`Canvas` / `CanvasScaler` / `GraphicRaycaster` / `Image` / `Text` / `Button` / `Slider` / `Toggle` / `InputField` / layout groups / `EventSystem`) | *(none — ungated)* | `.../Extensions/UI/` |
| Constraints & LOD | built-in (`PositionConstraint` / `RotationConstraint` / `AimConstraint` / `ParentConstraint` / `ScaleConstraint` / `LODGroup`) | *(none — ungated)* | `.../Extensions/Constraints/` |
| Terrain | built-in (`Terrain` / `TerrainData` / `TreePrototype` / `TerrainLayer`) | *(none — ungated)* | `.../Extensions/Terrain/` |
| Cinemachine | `com.unity.cinemachine` ≥ 3.x | *(none — **reflection-gated**)* | `.../Extensions/Cinemachine/` |
| Timeline | `com.unity.timeline` | `UNITY_OPEN_MCP_EXT_TIMELINE` | `.../Extensions/Timeline/` |
| Tilemap | `com.unity.2d.tilemap` (+ `com.unity.2d.tilemap.extras` for RuleTile) | `UNITY_OPEN_MCP_EXT_TILEMAP` (+ `UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS` inner guard for `create_rule_tile`) | `.../Extensions/Tilemap/` |
| Shader Graph | `com.unity.shadergraph` | `UNITY_OPEN_MCP_EXT_SHADERGRAPH` | `.../Extensions/ShaderGraph/` (**auto-activating**) |

Navigation is the reference template; InputSystem, ProBuilder, ParticleSystem,
Animation, and Timeline share the same layout. Splines is the most recently
added compile-gated domain — it proves the embedded + grouped model extends to
additional Unity APIs. Lighting, Audio, UI, Constraints & LOD, and Terrain are
**ungated** domains: their types are unconditionally present in every Unity
install, so they ship without a `UNITY_OPEN_MCP_EXT_*` define and compile into
every bridge build (their `lighting` / `audio` / `ui` / `constraints` /
`terrain` tool groups are still hidden from `ListTools` until the session
activates them via `manage_tools`). Terrain's heightmap / splat writes cap at
513×513 per call to push agents toward tiled region writes. UI's TextMesh Pro
(`TMP_Text`) is optional and detected at call time via reflection — when absent,
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

## Embedded domain model

Shipped domain tools live in `packages/bridge/Editor/TypedTools/Extensions/*`,
not as separate UPM packages. They compile in **only** when their Unity
dependency is present, using a two-layer gate:

1. **`versionDefines` on the bridge root asmdef**
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   set `UNITY_OPEN_MCP_EXT_<DOMAIN>` when the Unity package/module resolves.
   The symbol naming convention is `UNITY_OPEN_MCP_EXT_<DOMAIN>` (uppercase,
   `<DOMAIN>` matches the tool-group id).
2. **A per-domain sub-asmdef** under `TypedTools/Extensions/<Domain>/` carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_<DOMAIN>"]` and references the
   domain package. Unity only compiles the sub-asmdef when the define is set,
   so the optional package reference never breaks a project that lacks it.

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
`cinemachine_package_required` (when the package is absent). All other shipped
domains (Nav, Input, ProBuilder, Particles, Animation, Splines, Timeline,
Tilemap, Lighting, Audio, UI, Constraints & LOD, Terrain) use compile-gating
only.

### Wiring in a new embedded domain

1. Add a `versionDefines` entry to the bridge root asmdef mapping the Unity
   dependency to `UNITY_OPEN_MCP_EXT_<DOMAIN>`.
2. Create `packages/bridge/Editor/TypedTools/Extensions/<Domain>/` with:
   - a sub-asmdef (`defineConstraints` + the domain package reference),
   - the tool source files, each wrapped in `#if UNITY_OPEN_MCP_EXT_<DOMAIN>`,
   - registry discovery via `[BridgeToolType]` + `[BridgeTool]` (the
     `BridgeToolRegistry` scans all loaded assemblies, so sub-asmdefs register
     automatically).
3. Keep tool IDs stable: `unity_open_mcp_<domain>_<action>`.
4. Preserve gate contracts on mutating tools (`paths_hint`, `IsMutating`,
   `Gate`, `Lifecycle`, idempotent/destructive hints) and JSON response schema
   parity with the MCP tool definition.
5. Mirror the layout for EditMode tests under
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

Shader Graph (`shadergraph` on `com.unity.shadergraph`) is the first shipped
auto-activating domain. Existing domain groups keep their manual-activation
behavior unless they explicitly opt in.

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
