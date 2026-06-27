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

Navigation is the reference template; InputSystem, ProBuilder, ParticleSystem,
and Animation share the same layout. Splines is the most recently added domain
— it proves the embedded + grouped model extends to additional Unity APIs.

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

**None of the shipped extension domains** (Nav, Input, ProBuilder, Particles,
Animation, Splines) qualifies — all use compile-gating only. Splines, the most
recently added domain, deliberately uses the compile-gate path (a single stable
API) rather than the reflection-recommended Cinemachine, so the model lands on
the proven path first. Cinemachine remains the canonical reflection case
(2.x/3.x split) and is tracked as a follow-up domain.

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
