# Extension packs rules

## Scope

Rules for `packages/extensions/` — optional domain extension UPM packages that add typed helpers on top of the core bridge. Inherits root `AGENTS.md` and `packages/bridge/AGENTS.md`; deeper rules win on overlap.

Each pack lives in its own subfolder (`packages/extensions/<domain>/`) and ships as a standalone UPM package (`com.alexeyperov.unity-open-mcp-ext-<domain>`). Packs are opt-in — projects add only the ones they need to `Packages/manifest.json`.

## Package shape

- Editor-only. All code under `Editor/`, namespace `UnityOpenMcpExtensions.<Domain>`.
- asmdef references the bridge (`com.alexeyperov.unity-open-mcp-bridge.Editor`) + the domain Unity package only. Do **not** reference verify — extensions are thin typed wrappers.
- `autoReferenced: false` so the pack does not bleed into every assembly.
- `package.json` declares the bridge + the domain Unity package as dependencies.
- The template at `packages/extensions/template/` is the reference scaffold — copy it, do not install it directly.

## Tool registration

- Tools are discovered via `[BridgeToolType]` on a class + `[BridgeTool]` on methods — the bridge registry scans every loaded assembly. **No core bridge edits per pack.** This is the whole point of the extension boundary.
- Tool ids follow `unity_open_mcp_<domain>_<action>` (snake_case, domain prefix). Mirrors the kebab `<domain>-*` ids in the upstream Unity-MCP reference packs.
- Mutating tools declare `IsMutating = true`, accept a `string[] paths_hint` parameter (snake_case `paths_hint` bound by name), and set `Lifecycle = EditorSettle` (or `RestartThenSettle` if the op can domain-reload).
- Read-only tools set `ReadOnlyHint = true`, `Gate = GateMode.Off`, `Lifecycle = LifecyclePolicy.None`.
- The `paths_hint` contract is mandatory for mutating tools — refuse with `paths_hint_required` when empty. No whole-project fallback (root bridge rule).

## Namespace aliasing (AI Navigation and similar)

Some Unity packages shadow types in `UnityEngine.AI`. `NavMeshSurface` / `NavMeshLink` / `NavMeshModifier` / `NavMeshModifierVolume` exist in BOTH `UnityEngine.AI` (deprecated) and `Unity.AI.Navigation`. A bare `using UnityEngine.AI;` next to `using Unity.AI.Navigation;` produces CS0104 ambiguity. Always alias the package types explicitly:

```csharp
using UnityEngine.AI; // NavMesh, NavMeshAgent, NavMeshBuildSettings
using NavMeshSurface = Unity.AI.Navigation.NavMeshSurface;
using NavMeshLink = Unity.AI.Navigation.NavMeshLink;
```

The same pattern applies to any future pack whose domain types shadow a `UnityEngine.*` namespace.

## MCP + capability sync (per pack)

Every pack must update four core surfaces **in the same task** so the pack is discoverable end-to-end:

1. **MCP tool definitions** — `mcp-server/src/tools/<domain>-<action>.ts` (one per tool) + import + add to the plan array in `mcp-server/src/tools/index.ts` + spread into `ALL_TOOLS`.
2. **Capability category** — `mcp-server/src/capabilities/build-capabilities.ts` `TOOL_CATEGORY` entry per tool.
3. **Skill doc** — `skills/extensions/<domain>/SKILL.md` (agent playbook).
4. **Catalog mirrors** (kept in sync):
   - C#: `packages/bridge/Editor/UI/ExtensionCatalog.cs`
   - TS: `hub/src/lib/services/extensions.ts`

See `docs/extensions.md` §Authoring a new pack for the full checklist.

## Verification

- `Tests/Editor/<Domain>ToolsTests.cs` — EditMode tests proving:
  - All tool ids are discovered by `BridgeToolRegistry` (registry-discovery contract).
  - Every mutating tool refuses empty `paths_hint` (the gate contract).
  - The core round-trip for the pack's primary workflow.
- Add the pack to the demo project's `Packages/manifest.json` `testables` list so CI runs the tests.
- Tool contract changes: update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task so schemas stay in sync.
