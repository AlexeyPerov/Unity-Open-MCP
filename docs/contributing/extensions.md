# Contributing — extension domains

Contributor reference for shipped (embedded) domain tools and third-party
community packs. End-user setup and the domain catalog live in
[Extensions](../extensions.md).

## Embedded domain model

Shipped domain tools live in
`packages/bridge/Editor/TypedTools/Extensions/*`, not as separate UPM
packages. They compile in **only** when their Unity dependency is present,
using a **self-contained per-sub-asmdef gate**:

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
`cinemachine_package_required` (when the package is absent).

**Compile-gated** domains: Nav, Input, ProBuilder, Animation, Splines,
Timeline, Tilemap, ShaderGraph, VFX, MemoryProfiler, and UI (each gated on
its optional Unity package via the self-contained per-sub-asmdef gate above).

**Ungated** domains — Lighting, Audio, ParticleSystem, Constraints & LOD,
Terrain, SpriteAtlas, Texture — ship unconditionally because their types are
part of core engine modules present in every Unity install.

**Tilemap RuleTile** (`tilemap_create_rule_tile`) is the **two-defines-two-guards**
case: the outer pack gate (`UNITY_OPEN_MCP_EXT_TILEMAP`) lets the tool compile
in, but the RuleTile body is inner-guarded by `UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS`.
When extras is absent, the tool returns `tilemap_extras_required`.

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
5. Sync MCP surfaces in the same task (see below).

Use Navigation (`TypedTools/Extensions/Navigation/`) as the reference
template.

### Package-detection auto-activation

Most domain groups are **manual-activation**: the agent must call
`unity_open_mcp_manage_tools(action="activate", group="<domain>")` before the
domain's tools appear in `ListTools`. The default session contract lives in
[Tool groups](../api/tool-groups.md).

A domain group may additionally opt into **package-detection auto-activation**
by setting `autoActivate: true` (with a matching `unityPackage`) in the
canonical tool-group catalog
(`mcp-server/src/capabilities/tool-groups.ts`). When the project has the
group's `unityPackage` installed, the group **activates automatically** for the
session — its tools appear in `ListTools` with no manual `manage_tools` call.
This is additive to the manual-activation model:

- Auto-activation is **ephemeral** — same in-memory session store as manual
  activation, resets to defaults on MCP-server restart.
- Auto-activation is **reconciled lazily** from the live bridge's compiled-tool
  inventory (`GET /tools`): a group is package-present when any of its
  compiled-in tool names appears in that set. The reconciliation runs on
  `capabilities` and `manage_tools(list_groups)` calls.
- `manage_tools(list_groups)` and `capabilities` report each auto-activated
  group with `activationSource: "auto"` (and `autoActivated: true`) plus its
  `packageDependency`.
- When the package is absent, the group stays hidden and the capability output
  shows the install reason (`available: false (dependency missing: …)`).
- A manually-activated group wins: auto-activation never flips a group the
  operator deliberately deactivated.

Shader Graph (`shadergraph`), VFX Graph (`vfx`), and Memory Profiler
(`memoryprofiler`) are the shipped auto-activating domains.

### MCP + capability sync (embedded domain)

Every new or changed embedded domain must update these surfaces **in the same
task**:

1. **MCP tool definitions** — `mcp-server/src/tools/<domain>-<action>.ts` (one
   per tool) + import + add to the plan array in `mcp-server/src/tools/index.ts`
   + spread into `ALL_TOOLS`.
2. **Tool-group catalog** — `mcp-server/src/capabilities/tool-groups.ts` (group
   id, `domainDefine`, `unityPackage`, optional `autoActivate`).
3. **Capability category** — `mcp-server/src/capabilities/build-capabilities.ts`
   `TOOL_CATEGORY` entry per tool.
4. **Skill doc** — `skills/extensions/<domain>/SKILL.md` (agent playbook).
5. **Catalog mirrors** (kept in sync):
   - C#: `packages/bridge/Editor/UI/ExtensionCatalog.cs`
   - TS: `hub/src/lib/services/extensions.ts`

### Tool naming

Domain tools use:

```text
unity_open_mcp_<domain>_<action>
```

Example: `unity_open_mcp_navigation_surface_add`.

IDs are stable across the migration from standalone extension packs to the
embedded model — the MCP tool definitions and skills do not change.

## Community domain packs

`packages/extensions/` is the home for **third-party / community** domain packs
that are not shipped with the bridge. Each is a separate UPM package with its
own asmdef referencing the bridge. See
[`packages/extensions/AGENTS.md`](../../packages/extensions/AGENTS.md) for
the full authoring checklist.

### Removed shipped-domain copies

The five shipped domains (Nav, Input, ProBuilder, Particles, Animation)
previously had **legacy standalone copies** under
`packages/extensions/{navigation,inputsystem,probuilder,particlesystem,animation}/`.
These were **removed** — they were near-verbatim duplicates of the embedded
bridge tools and had drifted behind them:

- The embedded bridge tools (`packages/bridge/Editor/TypedTools/Extensions/*`,
  compile-gated by `UNITY_OPEN_MCP_EXT_<DOMAIN>`) are the single source of
  truth for shipped-domain tool surfaces.
- A manifest that pinned a removed pack via
  `file:../../packages/extensions/<domain>` (or a git pin) must drop that
  entry; the embedded tools provide the same surface with no separate install.
- New first-party domains go into
  `packages/bridge/Editor/TypedTools/Extensions/`, never here.

Use `packages/extensions/template/` as the reference scaffold for community
packs. A community pack must not reuse tool ids owned by a shipped embedded
domain.

## Related docs

- [Extensions](../extensions.md) — user-facing domain catalog and activation.
- [Tool groups](../api/tool-groups.md) — visibility and auto-activation.
- [MCP tools API](../api/mcp-tools.md) — tool contract overview.
- [Architecture](../architecture.md) — bridge / verify / MCP server boundaries.
- [`packages/extensions/AGENTS.md`](../../packages/extensions/AGENTS.md) — community pack rules.
