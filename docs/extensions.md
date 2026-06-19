# Extension packs

Unity Open MCP ships a lean core bridge (~14 meta-tools) plus a small set of typed editor tools. Optional **domain extension packs** add typed helpers for specific Unity features (NavMesh, Input System, ProBuilder, Splines, Terrain, Tilemap, Particle System, Animation) without bloating the core package.

Each pack is a separate UPM package — opt in to the ones you need. The bridge auto-discovers tools in any installed pack via the `[BridgeToolType]` assembly scan; **no per-pack wiring in the core bridge**.

## Installing a pack

Add the pack's UPM id to the project's `Packages/manifest.json`. Two paths:

1. **Hub AI Setup wizard** — Step 3 → "Optional extension packs" lists shipped packs as checkboxes. Selecting one surfaces the exact manifest line to add.
2. **Manual / dev install** — add the entry by hand. For projects inside this monorepo, use the `file:` form so changes to the pack source hot-reload:

   ```json
   "com.alexeyperov.unity-open-mcp-ext-navigation": "file:../../packages/extensions/navigation"
   ```

   For external projects, use a Git URL or published version.

The bridge window's **Extensions** tab (in-Editor) lists every catalogued pack with install status — green dot = installed, amber = available (not in manifest), grey = planned (not yet shipped).

## Catalog

Single source of truth, mirrored in two places — keep both in sync when a pack ships:

- C# (bridge window reads this): `packages/bridge/Editor/UI/ExtensionCatalog.cs`
- TypeScript (Hub wizard reads this): `hub/src/lib/services/extensions.ts`

| Pack | Domain | Status | Unity dependency |
|---|---|---|---|
| Navigation (NavMesh) | `navigation` | shipped | `com.unity.ai.navigation` |
| Input System | `inputsystem` | planned | `com.unity.inputsystem` |
| ProBuilder | `probuilder` | planned | `com.unity.probuilder` |
| Splines | `splines` | planned | `com.unity.splines` |
| Terrain | `terrain` | planned | (built-in) |
| Tilemap | `tilemap` | planned | `com.unity.2d.tilemap` |
| Particle System | `particle_system` | planned | (built-in) |
| Animation | `animation` | planned | (built-in) |

## Tool naming convention

Pack tools follow `unity_open_mcp_<domain>_<action>` (snake_case, domain prefix). This mirrors the kebab `navigation-*` / `inputsystem-*` ids in the upstream Unity-MCP reference packs but keeps the core `unity_open_mcp_` prefix so agents and the capability discovery treat them as first-class bridge tools.

Example: `unity_open_mcp_navigation_surface_add` (not `unity_open_mcp_navmesh_surface_add` — the domain is "navigation" per the package name).

## Authoring a new pack

### 1. Copy the template

```
packages/extensions/<domain>/
  package.json
  Editor/
    com.alexeyperov.unity-open-mcp-ext-<domain>.Editor.asmdef
    <Domain>Tools.cs
  Tests/Editor/
    com.alexeyperov.unity-open-mcp-ext-<domain>.Editor.Tests.asmdef
    <Domain>ToolsTests.cs
```

The template at `packages/extensions/template/` is a worked reference (echo + touch stubs). Copy it, rename the package + namespace, and add your typed tools.

### 2. Package shape

- **Editor-only.** All code under `Editor/`, namespace `UnityOpenMcpExtensions.<Domain>`.
- **asmdef references** the bridge only (`com.alexeyperov.unity-open-mcp-bridge.Editor`) plus the domain Unity package (e.g. `Unity.AI.Navigation`). Do **not** reference verify — extensions are thin typed wrappers.
- `autoReferenced: false` so the pack does not bleed into every assembly.
- `package.json` declares the bridge + the domain Unity package as dependencies.

### 3. Bridge tool contract

Each typed method:

- `[BridgeToolType]` on the containing class (the registry scans every loaded assembly — no core bridge edits per pack).
- `[BridgeTool("unity_open_mcp_<domain>_<action>", ...)]` on each method.
- Returns a hand-rolled JSON string (reuse the bridge's `JsonBody` helpers — no Newtonsoft dependency).
- **Mutating tools** declare `IsMutating = true`, accept a `string[] paths_hint` parameter (the snake_case `paths_hint` field is bound by name), and set `Lifecycle = EditorSettle` (or `RestartThenSettle` if the op can domain-reload).
- **Read-only tools** set `ReadOnlyHint = true`, `Gate = GateMode.Off`, `Lifecycle = LifecyclePolicy.None`.

The template's `TemplateEchoTool.cs` and the navigation pack's `NavigationTools.cs` are worked examples.

### 4. MCP sync checklist

For every pack, update four core surfaces **in the same task** so the pack is discoverable end-to-end:

| Surface | File | What to add |
|---|---|---|
| MCP tool definitions | `mcp-server/src/tools/<domain>-<action>.ts` (one per tool) | Tool name, description, inputSchema |
| Tool index | `mcp-server/src/tools/index.ts` | Import + add to `M16_PLAN10_TOOLS` (or a new plan array) + spread into `ALL_TOOLS` |
| Capability category | `mcp-server/src/capabilities/build-capabilities.ts` | `TOOL_CATEGORY` entry per tool (`unity_open_mcp_<domain>_<action>: "<domain>"`) |
| Skill doc | `skills/extensions/<domain>/SKILL.md` | Agent playbook: preconditions, canonical workflow, tool reference |

Plus the catalog mirrors:

| Surface | File | What to add |
|---|---|---|
| C# catalog | `packages/bridge/Editor/UI/ExtensionCatalog.cs` | `ExtensionPack` entry (id, domain, tool ids, `shipped: true`) |
| TS catalog | `hub/src/lib/services/extensions.ts` | Mirror entry |

### 5. Tests

- `Tests/Editor/<Domain>ToolsTests.cs` — EditMode tests proving:
  - All tool ids are discovered by `BridgeToolRegistry` (registry-discovery contract).
  - Every mutating tool refuses empty `paths_hint` (the gate contract).
  - The core round-trip for the pack's primary workflow (e.g. for navigation: surface_add → bake → `hasNavMeshData: true`).
- Add the pack to the demo project's `Packages/manifest.json` `testables` list so CI runs the tests.

### 6. Docs

- The pack's `SKILL.md` is agent-facing (workflows + tool reference). Keep it lean.
- Update `docs/api/mcp-tools.md` with the pack's tool entries when you ship the pack (route/batch tables).

## Acceptance criteria (per pack)

Reproduced from the milestone spec:

- All catalog tools registered; the primary workflow covered by EditMode tests.
- Gate / `paths_hint` enforced on mutating tools.
- MCP + capability discovery synced; skill doc exists.
- Catalog mirrors (C# + TS) updated and `shipped: true`.

## Why extension packs

The core meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) can already reach any Unity API. Typed helpers improve the **agent success rate** — schema-validated inputs, structured JSON outputs, and explicit gate contracts mean fewer trial-and-error calls and fewer silent breakages. The pack boundary keeps the core bridge lean (no domain code in `packages/bridge/`) while letting projects opt into the typed surface for the domains they actually use.
