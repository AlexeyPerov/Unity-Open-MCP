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

Navigation was the reference template (M18 Plan 1); InputSystem, ProBuilder,
ParticleSystem, and Animation migrated into the same layout in M18 Plan 3.
All five shipped domains are now embedded.

## Embedded domain model

Shipped domain tools live in `packages/bridge/Editor/TypedTools/Extensions/*`,
not as separate UPM packages. They compile in **only** when their Unity
dependency is present, using a two-layer gate:

1. **`versionDefines` on the bridge root asmdef**
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   set `UNITY_OPEN_MCP_EXT_<DOMAIN>` when the Unity package/module resolves.
   The symbol naming convention is `UNITY_OPEN_MCP_EXT_<DOMAIN>` (uppercase,
   `<DOMAIN>` matches the tool-group id used by M18 Plan 2).
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

**None of the 5 shipped extension domains** (Nav, Input, ProBuilder,
Particles, Animation) qualifies — all use compile-gating only. The first
reflection domain is Cinemachine, planned for M18 Plan 7 (backlog domains).

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

`packages/extensions/` remains the home for **third-party / community** domain
packs that are not shipped with the bridge. Each is a separate UPM package
with its own asmdef referencing the bridge. The shipped domains listed above
**must not** also live here (that would double-register tool IDs).

This folder is **frozen** for shipped domains after M18 Plan 6 (legacy
cleanup). New first-party domains go into `TypedTools/Extensions/`.

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
- [Unity version compatibility](unity-version-compat.md) — version-gated APIs
  and the define-symbol model.
- [MCP tools API](api/mcp-tools.md)
