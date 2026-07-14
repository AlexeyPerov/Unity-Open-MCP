# Extension packs rules

## Scope

Rules for `packages/extensions/` — the home for **third-party / community** domain extension UPM packages that add typed helpers on top of the core bridge. Root `AGENTS.md` also applies. Read `packages/bridge/AGENTS.md` when a change touches bridge-owned mirrors; sibling rules are not inherited automatically.

Shipped first-party domains are embedded in `packages/bridge/Editor/TypedTools/Extensions/`; do not add or mirror them here. This subtree owns only community packs and the `template/` scaffold.

Each pack lives in its own subfolder (`packages/extensions/<domain>/`) and ships as a standalone UPM package (`com.alexeyperov.unity-open-mcp-ext-<domain>`). Packs are opt-in — projects add only the ones they need to `Packages/manifest.json`.

## Package shape

- Editor-only. All code under `Editor/`, namespace `UnityOpenMcpExtensions.<Domain>`.
- asmdef references the bridge (`com.alexeyperov.unity-open-mcp-bridge.Editor`) + the domain Unity package only. Do **not** reference verify — extensions are thin typed wrappers.
- `autoReferenced: false` so the pack does not bleed into every assembly.
- `package.json` declares the bridge + the domain Unity package as dependencies.
- The template at `packages/extensions/template/` is the reference scaffold — copy it, do not install it directly.

## Tool registration

- Tools are discovered via `[BridgeToolType]` on a class + `[BridgeTool]` on methods — the bridge registry scans every loaded assembly. **No core bridge edits per pack.** This is the whole point of the extension boundary.
- Tool ids follow `unity_open_mcp_<domain>_<action>` (snake_case, domain prefix).
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

Follow the canonical [end-to-end domain checklist](../../docs/contributing/extensions.md#end-to-end-domain-checklist) in the same task. At minimum:

1. Export MCP definitions, register them in the appropriate domain/tool array, and ensure that array is included in `ALL_TOOLS`.
2. Update `tool-groups.ts`, `TOOL_CATEGORY`, the domain skill, MCP API docs, token estimates, and narrow tests.
3. Add community-pack metadata to `packages/bridge/Editor/UI/ExtensionCatalog.cs` and the Hub `EXTENSION_PACKS` mirror. Never use `ExtensionCatalog` for shipped embedded domains.
4. Update the demo manifest, lock, and `testables` when the pack or its tests must be installed for CI.

Name the dimensions separately when they differ: tool-group ID (usually hyphenated, such as `input-system`), tool ID prefix (such as `inputsystem`), and source/skill folder name.

## Verification

- `Tests/Editor/<Domain>ToolsTests.cs` — EditMode tests proving:
  - All tool ids are discovered by `BridgeToolRegistry` (registry-discovery contract).
  - Every mutating tool refuses empty `paths_hint` (the gate contract).
  - The core round-trip for the pack's primary workflow.
- Add the pack to the demo project's package manifest/lock and `testables` list so CI runs the tests.
- Tool contract changes: update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task so schemas stay in sync.
