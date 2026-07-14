# Bridge package rules

## Scope

Rules for `packages/bridge/` — the Unity Editor HTTP bridge (`com.alexeyperov.unity-open-mcp-bridge`). Root `AGENTS.md` also applies.

## Package shape

- Editor-only Unity package. All code lives under `Editor/` and uses the `UnityOpenMcpBridge` namespace.
- Hard dependency on `com.alexeyperov.unity-open-mcp-verify` — the gate flow calls into verify for checkpoint/validate/delta. Do not break this dependency direction.
- `Tests/Editor/` holds EditMode tests. No playmode tests.

## Tool registration

- Registry tools are discovered via `[BridgeToolType]` on a class + `[BridgeTool]` on methods (`Editor/Bridge/Attributes/`). The HTTP server (`BridgeHttpServer.cs`) also classifies tools via hardcoded sets in `Editor/Bridge/BridgeToolClassification.cs` (`KnownTools` / `DirectResponseTools` / `MutatingTools`) for legacy tools — prefer the registry path for new tools. (The UI-side `Editor/UI/BridgeToolCatalog.cs` is a separate class that builds the Tools-tab catalog — different concern, same short name historically.)
- **Tools-tab catalog source.** `BridgeToolCatalog.Build()` (`Editor/UI/BridgeToolCatalog.cs`) is the union of three sources: the 10 hardcoded meta-tools (with mirrored input schemas), `BridgeToolRegistry.All()` (attribute-discovered), and `BridgeToolClassification.KnownTools` (the ~90 typed tools dispatched by the hardcoded `switch` in `BridgeHttpServer.DispatchTool` that carry no `[BridgeTool]` attribute). The `KnownTools` pass is what keeps the Tools tab in parity with `GET /tools` (`HandleToolsList`) — without it the UI silently hides the gameobject/scene/component/material/prefab/package/build/settings/profiler family. When adding a typed tool, add its id to `KnownTools` (+ `DirectResponseTools` for read-only, `MutatingTools` for mutators) and a `case` in `DispatchTool`; the catalog picks it up automatically. Canonical human titles + JSON schemas live server-side (`mcp-server/src/tools/`), so the catalog synthesizes a display title from the id.
- **Test-assembly exclusion in `Scan()`.** `BridgeToolRegistry.Scan()` / `BridgeResourceRegistry.Scan()` skip any assembly that references `nunit.framework` — the framework every EditMode test asmdef pulls in. This stops the `test_*` scanner fixtures (`Tests/Editor/Registry/AttributeScannerTests.cs`) from leaking into the Tools tab, `GET /tools`, or the group→tools capability map when a project loads the bridge under `testables`. The scanner tests opt back in via the internal `Scan(includeTestAssemblies: true)` overload. Do not remove this guard; do not change the `internal` overload to `public`.
- Every new tool must declare:
  - A unique `Name` (the MCP tool name, `unity_open_mcp_*` / `unity_senses_*`).
  - `IsMutating` — true if the tool changes Unity state.
  - `Gate` — the default gate mode for mutating tools (`Enforce` / `Warn` / `Off`).
  - `Group` — the tool-group id from the canonical catalog in `mcp-server/src/capabilities/tool-groups.ts` (M18 Plan 2). Tools that should be always-visible meta-tools omit `Group` (defaults to null). Domain tools set `Group = "<domain-id>"` (e.g. `"navigation"`, `"input-system"`); typed editor tools set `"typed-editor"`. The id must match one of the catalog entries exactly so the bridge-side `GroupToTools()` mapping reconciles with the MCP server.
- Mutating tools must accept and honor the request-level `gate` value. Read-only tools set `Gate = Off` and `ReadOnlyHint = true`.
- When adding/removing/renaming a tool, update the hardcoded `KnownTools` / `DirectResponseTools` / `MutatingTools` sets in `Editor/Bridge/BridgeToolClassification.cs` **only if** the tool is not registry-discovered. Registry tools are picked up automatically. (`BridgeHttpServer` aliases these sets locally so the dispatch path reads them as plain `KnownTools.Contains(...)`.)

## Tool-group visibility (M18 Plan 2)

- Sessions start with only the `core` group visible in `ListTools`; every other group is hidden until the connected MCP session activates it via `unity_open_mcp_manage_tools`.
- The bridge does NOT track session state — the MCP server owns it (`ToolSessionState`). The bridge's role is compiled-state reporting only.
- `BridgeToolRegistry.GroupToTools()` exposes the group→tools map for the bridge capability surface.
- `GET /tools` (`BridgeHttpServer.HandleToolsList`) returns the unioned tool inventory (KnownTools ∪ registry) plus the registry-side group→tools map. The MCP server consults this from `capabilities` and `manage_tools(list_groups)` to report per-group compiled-state availability (`available: true/false/null`).
- Group ids in `[BridgeTool(Group = "...")]` MUST match the canonical catalog in `mcp-server/src/capabilities/tool-groups.ts` exactly — the bridge and MCP server reconcile on this string.

## Embedded domain tools (M18)

- Shipped domain tools (for example Nav, Input, ProBuilder, Particles, and Animation) live under `Editor/TypedTools/Extensions/<Domain>/`, **not** in `packages/extensions/`. See [Embedded domain model](../../docs/contributing/extensions.md#embedded-domain-model).
- **Compile-gating is mandatory for optional compile-time dependencies.** The domain sub-asmdef owns both the `versionDefines` entry that maps its Unity package/module to `UNITY_OPEN_MCP_EXT_<DOMAIN>` and the matching `defineConstraints` entry, plus the domain package reference. The source and gated test asmdef use the same define.
- **No runtime reflection probing** for shipped domains. When the dependency is absent, the tools are simply not compiled in; the capability surface reports `available: false (dependency missing: <package>)`. Reflection is reserved for version-split APIs only (Cinemachine 2.x/3.x in M18 Plan 7) and must document the split + minimum version.
- **No manual Player Settings scripting-define writes.** The wizard must not write `UNITY_OPEN_MCP_EXT_*` symbols — they come exclusively from the asmdef `versionDefines`.
- New first-party domains follow the Navigation reference template (`Editor/TypedTools/Extensions/Navigation/`): sub-asmdef with `defineConstraints` + package ref, source files wrapped in `#if UNITY_OPEN_MCP_EXT_<DOMAIN>`, `[BridgeToolType]` + `[BridgeTool]` discovery (registry scans all loaded assemblies), stable `unity_open_mcp_<domain>_<action>` IDs, gate contracts on mutating tools, and a gated EditMode test asmdef under `Tests/Editor/TypedTools/<Domain>/`.
- Tool IDs and JSON response schemas are stable across the embed migration — do not change them when moving a domain out of `packages/extensions/`.
- Follow the canonical [end-to-end domain checklist](../../docs/contributing/extensions.md#end-to-end-domain-checklist) in the same task. It covers MCP definitions and domain/tool array plus `ALL_TOOLS` registration, tool groups, `TOOL_CATEGORY`, domain skills, API docs, `EmbeddedDomainCatalog` and Hub mirrors where applicable, token-estimate regeneration, EditMode/S0/S4 coverage, and demo package/`testables` updates when an optional dependency is required.
- Name all three dimensions explicitly; they can differ. For example, tool-group ID `input-system`, tool prefix `unity_open_mcp_inputsystem_*`, source folder `InputSystem`, and skill folder `skills/extensions/inputsystem/`.

## Gate policy

- The gate flow (checkpoint → mutate → validate → delta) is the bridge's core safety contract. Do not add a mutating dispatch path that bypasses `GatePolicy.Execute`.
- `paths_hint` is mandatory for mutating tool calls — there is no whole-project fallback. Do not add one.
- `Editor/Bridge/BridgeRequestBody.cs` and the dispatch in `BridgeHttpServer.cs` are canonical for effective-mode selection: a valid request `gate` wins; an omitted, malformed, or unknown value falls back to the project default (`BridgeGateDefaultPolicy`). A `[BridgeTool].Gate` value is catalog/recommendation metadata and does not override the project default. `Editor/Gate/GatePolicy.cs` (`GatePolicy.Execute`) is canonical for executing the selected mode. Keep [Bridge HTTP API — Gate policy](../../docs/api/bridge-http.md#gate-policy) aligned when externally relevant precedence or fallback behavior changes.

## Transport

- `BridgeHttpServer` binds `127.0.0.1` by default. Remote bind (`0.0.0.0`) is opt-in via `bindAddress` and requires `authMode: "required"`; follow [Remote bind and authentication](#remote-bind-and-authentication). Do not add a non-localhost binding path that skips the `BridgeBindAddress.Decide` check.
- All Unity API calls happen on the main thread via `MainThreadDispatcher`. Never call `UnityEditor`/`UnityEngine` Editor APIs from the listener worker thread.
- JSON is hand-rolled via `StringBuilder` (no Newtonsoft dependency in the bridge). Follow the existing `EscapeString`/`Build*Envelope` patterns; do not introduce a JSON serializer dependency.
- **Hand-rolled JSON must use the shared `BridgeJson` appenders.** Build JSON value tokens with `AppendJsonString`, `AppendJsonBool`, `AppendJsonNumber`, or their field variants; do not split string literals across appends, rely on C# bool casing, or duplicate the escape switch. Use `EscapeStringContent` only for content without surrounding quotes. `Tests/Editor/Bridge/BridgeJsonTests.cs` pins the contract. Extension sub-assemblies keep their own escape helper because `BridgeJson` is internal; do not make it public for that purpose. `OutputSerializer.EscapeJsonString` is the only other sanctioned helper and intentionally follows the reflection serializer's established escaping. Do not introduce a third helper.

## Auth (M14)

- A 256-bit per-session bearer token (`BridgeAuthToken.Generate`) is minted into the instance lock on every `Acquire` and mirrored as `authToken` in the lock JSON. The TS-side `InstanceLock` interface (`mcp-server/src/instance-discovery.ts`) must carry the same field; `resolveAuthToken` reads it.
- Enforcement is opt-in via `authMode` in `.unity-open-mcp/settings.json` (`BridgeAuthPolicy`: `"none"` default | `"required"`). The token is always minted regardless, so flipping to `required` needs no restart.
- The HTTP gate is `BridgeAuthCheck.IsAuthorized` (pure, unit-tested) called from `BridgeHttpServer.CheckAuth` before routing. No endpoint is exempt. Unknown `authMode` values fail closed (`required`).
- Token comparison is constant-time (`BridgeAuthToken.EqualsConstantTime`). Do not replace it with `==`/`string.Equals`.

## Deny heuristic (M14 T5.2 / T5.3)

- `execute_csharp` and `execute_menu` run a configurable regex deny heuristic (`BridgeDenyList`) BEFORE the mutation. Default patterns block editor exit, bulk asset delete, unbounded builds, and `File/Quit`. Override via `csharpDenyPatterns` / `menuDenyPatterns` in `.unity-open-mcp/settings.json` — a non-empty array replaces the defaults; null/empty (which JsonUtility serializes identically) ⇒ built-in defaults. Invalid regex is dropped at settings-load.
- The bypass contract is `gate: "off"` + `confirm_bypass: true` (both required). The pure decision is `BridgeDenyBypass`; the dispatcher reads it from the request body so the heuristic fires before the effective gate mode is resolved.
- `execute_menu` keeps a hardcoded `File/Quit` block (`IsHardBlocked`) that the configurable list cannot disable — last-resort guard.
- Denials return `denied_by_policy` (csharp) / `menu_blocked` (menu) and flow through the normal gate envelope; the matched pattern is embedded in the message and extracted into the audit record.

## Remote bind and authentication

- The listener bind address is configurable (`bindAddress`: `"127.0.0.1"` default | `"0.0.0.0"`). The bridge refuses to start on a non-loopback interface unless `authMode` is `"required"` — `BridgeBindAddress.Decide` is the pure decision, called from `BridgeHttpServer.Start` before opening the socket.
- The bridge does not terminate TLS. Remote access is documented for a trusted LAN/VPN behind a reverse proxy or SSH tunnel in [Bridge HTTP API — Authentication and bind mode](../../docs/api/bridge-http.md#authentication-and-bind-mode). Do not add TLS termination to the bridge; terminate upstream.

## On-disk audit log (M14 T5.5)

- Opt-in via `auditLogEnabled` in `.unity-open-mcp/settings.json`. When on, every gate mutation (pass / fail / warn) and deny-list refusal is appended to a rolling JSON-lines file at `~/.unity-open-mcp/audit/audit-<projectHash>.jsonl` (5 MiB active, 5 retained rotations).
- Writes are serialized through a lock and best-effort: an I/O failure is logged once and the record dropped — audit logging never breaks the dispatch path. `BridgeAuditLog.AuditDirOverride` is a test-only hook (mirrors `InstancePortResolver.InstancesDirOverride`); never set it in production.
- The audit record is built in `BridgeHttpServer.RecordAudit`, called from `RecordGateRun` alongside the in-memory history. The outcome vocabulary is `passed` | `warned` | `failed` | `skipped` | `denied`; `bypassedDenyList` flags the gate=off+confirm escape hatch.

## Multi-instance port and discovery

- This file owns the three-way deterministic-port contract. Normalize the project path by replacing backslashes with forward slashes and trimming trailing slashes without lowercasing. Hash its UTF-8 bytes with SHA-256, interpret the first 8 bytes as a big-endian unsigned integer, and compute `20000 + (value % 10000)`.
- Keep the shared computation byte-identical in bridge `Editor/Bridge/InstancePortResolver.cs` (`ComputePort`), MCP `mcp-server/src/instance-discovery.ts` (`computePort`), and Hub `hub/src-tauri/src/config/bridge_port.rs` (`compute_port`). Their override/discovery precedence is side-specific, but hashing, normalization, range, or fallback changes require reviewing and updating all three in the same task.
- Keep the shared fixtures consistent in `Tests/Editor/Bridge/InstancePortResolverTests.cs`, `mcp-server/src/instance-discovery.test.ts`, and the `#[cfg(test)]` module in `hub/src-tauri/src/config/bridge_port.rs`.
- `UNITY_OPEN_MCP_BRIDGE_PORT` (env) and `-UNITY_OPEN_MCP_BRIDGE_PORT=<n>` (Unity arg) override the deterministic default — override always wins, so existing pinned-port configs keep working.
- Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` via `BridgeInstanceLock`. The file doubles as the heartbeat (`BridgeHeartbeat` rewrites it every 0.5s + on forced state transitions: compile, play-mode change, domain reload).
- Stale locks (crashed Unity) are swept on `Acquire` by PID-liveness (`Process.GetProcessById` — throws on dead pid). The MCP server is read-only on the lock and falls back to the hash when a lock's pid is dead; do not add lock mutation on the MCP side.
- **Retain the lock across domain reload.** Call `ForceStopListener(releaseLock: false)` before assembly reload and during port-in-use recovery; delete the lock only on graceful quit. Always tear down residual listener/thread state even when `_running` is false. The stale-heartbeat + live-PID signature is required by MCP `classifyInstance` to return `bridge_compile_failed`. Port-in-use recovery retries in-process only; never terminate foreign PIDs. See [Contributor troubleshooting — stale heartbeat vs live PID](../../docs/troubleshooting-contributors.md#stale-heartbeat-vs-live-pid).
- **Skip the listener in worker and batch processes.** Gate listener startup and hook registration with `IsWorkerOrBatchProcess` / `Application.isBatchMode`; `BridgeBatchEntry` does not use the listener. On terminal startup failure, restart the heartbeat only when this process already holds the lock, and never mint a lock for an unbound port. See [Contributor troubleshooting — worker/batch collision](../../docs/troubleshooting-contributors.md#worker-batch-listener-collision-historical-regression-guard).
- New editor states go through `BridgeInstanceLock.State*` constants so both sides agree on the vocabulary (`idle`/`compiling`/`reloading`/`entering_playmode`/`playing`/`exiting_playmode`).
- `InstancePortResolver.InstancesDirOverride` is a test-only hook for sandboxing lock I/O — never set it in production code.

## UI

- `Editor/UI/UnityOpenMcpBridgeWindow.cs` is the single EditorWindow (6 peer tabs: Status, Tools, Gate, Activity, Settings, Extensions). Batch is a section under Activity; Info is a toolbar About foldout (`?` button). Add new surfaces as tab sections, not separate windows, unless there is a strong reason. When changing the `BridgeWindowTab` enum, update the prefs migration (`MigrateSelectedTab`) and `BridgeWindowTabIaContractTests`.
- **Tooltips over help boxes.** Add a hover tooltip via `GUIContent(text, tooltip)` whenever a label or value exposes one of these internal-term categories: mutability/read-only state, gate mode or gate-run fields (Delta, Durations, Categories), registry/legacy source, activity filters/status, compiled/activation state, or extension-pack status. Do not duplicate a tooltip beside an explanatory help box. Reuse the shared `Tooltip*` constants in `Editor/UI/Tabs/UnityOpenMcpBridgeWindow.Constants.cs`. When adding a new internal-term category, extend the tooltip contract assertions in `Tests/Editor/UI/BridgeWindowTabIaContractTests.cs`.
- **Tools-tab pagination.** The Tools list paginates at `ToolsPageSize = 20` rows per page. `BuildFilteredToolList` narrows by filter + search once per frame; `DrawToolPagination` renders numbered page buttons plus an "All" affordance when the filtered set is ≤ `ToolsShowAllThreshold` (150). When changing page-size semantics, update both constants together.
- **Tools-tab token estimates.** Each catalog row shows a per-tool `~{N} tokens` chip, the filters header shows the active-set total, and a collapsible per-group breakdown shows each group's active vs total cost. The numbers come from the generated `Editor/UI/BridgeToolTokenEstimates.cs` (per-tool `EstimateFor(name)`, group `GroupFor(name)`, K-formatting via `Format(int)`), **not** a hand-maintained table. It is regenerated from the MCP-server tool schemas by `scripts/generate-token-estimates.mjs` (import `mcp-server/src/tools/index.ts` + `tool-groups.ts` via `--experimental-strip-types`; chars/4 heuristic over the tool's MCP wire JSON). CI runs an advisory drift check (`.github/workflows/version-sync.yml`, `continue-on-error`) on PRs touching the tool schemas or the codegen script; `sync-version.mjs bump` prints a reminder to regenerate. When adding/removing a tool, regenerate the table — never edit `BridgeToolTokenEstimates.cs` by hand. The active total reflects the **bridge toggle policy** (per-tool enable/disable), not per-session `manage_tools` activation (which lives in the MCP server).
- **Tools-tab bulk actions.** The Tools tab offers bulk **Enable all / Disable all** on the current filtered view (search + Enabled/Disabled filter — not the whole catalog) and per-group **Enable / Disable** buttons in the token breakdown. The policy seam is `BridgeToolTogglePolicy.SetEnabled(filtered, enabled)` / `SetGroupEnabled` — one `settings.json` write + one `Changed` event per bulk action, tools outside the filtered set keep their state. Group names come from `BridgeToolCatalog.ToolNamesForGroup`. Disabling more than `BulkDisableConfirmThreshold` (10) tools prompts a native `EditorUtility.DisplayDialog` first; re-enabling is one-click. The IMGUI confirm is not unit-tested — the policy reconciliation is (`BridgeToolTogglePolicyBulkTests`).
- Settings persist in `.unity-open-mcp/settings.json` via `BridgeProjectSettings`. Follow the existing v1 schema; extend in place.

## Verification

- C# changes: add or update the narrowest EditMode test in `Tests/Editor/`.
- Tool contract changes: update the MCP schema/handler and appropriate domain/tool array plus `ALL_TOOLS` registration in `mcp-server/src/tools/`; update the owning page indexed by `docs/api/mcp-tools.md`, `skills/unity-open-mcp/SKILL.md` when agent workflow changes, and `skills/extensions/<domain>/SKILL.md` for domain behavior.
- Schema, tool-catalog, or group changes: regenerate `Editor/UI/BridgeToolTokenEstimates.cs` with `node scripts/generate-token-estimates.mjs`.
- Routed tool changes: run the applicable S0 registration/reachability slice and the narrowest S1–S5 behavioral suite described in `docs/troubleshooting-contributors.md#mcp-test-suite-catalog`.
- Instance-ID fields: follow the canonical [Instance IDs convention](../../docs/code-conventions.md#instance-ids) and use `InstanceId` helpers; do not assume IDs fit in `int` or a JSON number.
- Gate flow changes: verify the delta math (new/resolved errors+warnings) in the integration tests.
