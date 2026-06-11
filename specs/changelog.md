## 2026-06-12 00:30 MSK

- **M2 Plan 2 Task 4: Minimal `packages/verify` skeleton via `VerifyGateAdapter`.** Introduced verify package with `missing_references` baseline/check path and wired bridge gate lifecycle through `VerifyGateAdapter` per [questions-2.md](specs/questions/questions-2.md) Q2 answer B and Q11 answer A:
  - `packages/verify/package.json` — UPM package `com.alexeyperov.unity-agent-verify` v0.1.0.
  - `packages/verify/Editor/com.alexeyperov.unity-agent-verify.Editor.asmdef` — Editor-only assembly definition.
  - `packages/verify/Editor/Core/VerifyIssue.cs` — `VerifyIssue` model (ruleId, severity, assetPath, issueCode, description) and `VerifySeverity` enum (Error/Warning).
  - `packages/verify/Editor/Core/IssueKey.cs` — `IssueKey.Build()` producing stable `{ruleId}|{severity}|{assetPath}|{issueCode}` keys for gate delta.
  - `packages/verify/Editor/Core/VerifyScope.cs` — `VerifyScope` (paths array, includeDependents flag).
  - `packages/verify/Editor/Core/VerifyRunMode.cs` — `VerifyRunMode` enum (Checkpoint/Validate/Full).
  - `packages/verify/Editor/Core/VerifyResult.cs` — `VerifyResult` (issues list, categoriesRun, durationMs).
  - `packages/verify/Editor/Core/CheckpointFingerprint.cs` — `CheckpointFingerprint` (checkpointId, per-rule `RuleFingerprint` with error/warn counts + issue key sets).
  - `packages/verify/Editor/Core/IVerifyRule.cs` — `IVerifyRule` interface (Id, Scan).
  - `packages/verify/Editor/Core/VerifyRunner.cs` — `VerifyRunner.RunScoped()` orchestrates rule scan; `CreateCheckpoint()` builds fingerprint with stable issue keys.
  - `packages/verify/Editor/Rules/MissingReferences/MissingReferencesRule.cs` — detects missing scripts (null components) and missing serialized references (null objectReference with non-zero instanceID) on prefab assets.
  - `packages/bridge/Editor/Gate/GatePolicy.cs` — expanded with `DeltaData` (newErrors/Warnings, resolvedErrors/Warnings, issue key arrays) and `GateDispatchResult` (mutation + gate lifecycle result).
  - `packages/bridge/Editor/Gate/VerifyGateAdapter.cs` — implemented `CreateCheckpoint`, `ValidatePaths`, `ComputeDelta` as thin facade over `VerifyRunner`.
  - `packages/bridge/Editor/Bridge/BridgeHttpServer.cs` — replaced stub gate with real lifecycle: checkpoint → mutate → validate → delta → pass/fail/warn. `DispatchWithGate` runs full gate for mutating tools with `gate != off` and non-empty `paths_hint`. `BuildGateEnvelope` produces full gate response with validation results, delta counts, and agent next steps. Enforce mode sets `GateFailed = true` when `delta.newErrors > 0`. Error/timeout cases retain stub gate (skipped: true).
  - `packages/bridge/package.json` — added dependency on `com.alexeyperov.unity-agent-verify`.
  - `packages/bridge/Editor/com.alexeyperov.unity-agent-bridge.Editor.asmdef` — added reference to verify assembly.
  - Scope intentionally minimal: one rule (`missing_references`), no broad M3 rule expansion, no scene scanning, no batch/CI support.
- Marked Task 4 DONE in [execution-plan-2-meta-tools-gate.md](specs/execution/M2/execution-plan-2-meta-tools-gate.md).

## 2026-06-11 23:00 MSK

- **M2 Plan 2 Task 3: `execute_menu` allowlist and validate-skip rule.** Implemented read-only menu allowlist per [questions-2.md](specs/questions/questions-2.md) Q6 answer C (skip validate when menu is allowlisted and `paths_hint` empty):
  - `ExecuteMenuTool.cs` — added `ReadOnlyMenuAllowlist` HashSet with 17 read-only menu paths (`Assets/Refresh`, `Assets/Reimport All`, `Assets/Reveal in Finder`, `Assets/Show in Explorer`, `Edit/Selection`, `Edit/Project Settings`, `File/Open Scene`, `File/Open Project`, `GameObject/Align with View`, `GameObject/Move to View`, `Window/General/Hierarchy`/`Inspector`/`Project`/`Console`/`Scene`/`Game`, `Window/Layouts`). Added public `IsReadOnlyMenu(string)` method with prefix-based case-insensitive matching.
  - `BridgeHttpServer.cs` — modified `paths_hint` validation in `HandleToolDispatch`: when tool is `unity_agent_execute_menu` and `paths_hint` is empty, checks `ExecuteMenuTool.IsReadOnlyMenu(menu_path)` before rejecting. Allowlisted menus skip the `paths_hint_required` error and proceed to dispatch. Non-allowlisted menus still require strict `paths_hint`.
  - Non-allowlisted menus follow the normal gate flow with mandatory `paths_hint`.
- Updated documentation:
  - [specs/packages/bridge.md](specs/packages/bridge.md) — new §`execute_menu` allowlist and gate-skip rule section with allowlist table and matching behavior notes.
- Marked Task 3 DONE in [execution-plan-2-meta-tools-gate.md](specs/execution/M2/execution-plan-2-meta-tools-gate.md).

## 2026-06-11 22:00 MSK

- **M2 Plan 2 Task 2: Strict `paths_hint` validation for mutating tools.** Enforced non-empty `paths_hint` for all mutating meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) per [questions-2.md](specs/questions/questions-2.md) Q1 answer A (strict mode):
  - `BridgeHttpServer.cs` — added `MutatingTools` set identifying the three mutating tools. `HandleToolDispatch` validates `paths_hint` before dispatching to the main thread: calls `JsonBody.GetStringArray` to extract the array, rejects null or empty with a deterministic `paths_hint_required` error in the standard mutation envelope. Error message includes guidance on providing asset paths and states there is no whole-project fallback.
  - `BuildPathsHintErrorEnvelope` — new envelope builder returning `mutation.success: false`, error code `paths_hint_required`, gate stub (`skipped: true`), and `agentNextSteps` with a retry hint.
  - Read-only tools (`find_members`) are excluded from validation.
  - No fallback whole-project scan path exists — M2 is strictly require-paths.
- Updated documentation:
  - [specs/packages/bridge.md](specs/packages/bridge.md) — M2 gate behavior updated to state strict mode; new §`paths_hint` strict validation section with error behavior and no-fallback policy.
  - [specs/architecture/bridge-http-api.md](specs/architecture/bridge-http-api.md) — `paths_hint` constraint changed from "recommended non-empty" to "required non-empty for mutating tools"; M2 scope note updated; `paths_hint_required` error code added to the error codes table.
  - [specs/architecture/mcp-tools.md](specs/architecture/mcp-tools.md) — new §`paths_hint` strict validation paragraph after the `gate` section.
  - [specs/architecture/gate-policy.md](specs/architecture/gate-policy.md) — "If `paths_hint` is empty" paragraph replaced with single strict-mode statement.
- Marked Task 2 DONE in [execution-plan-2-meta-tools-gate.md](specs/execution/M2/execution-plan-2-meta-tools-gate.md).

## 2026-06-11 21:15 MSK (hub)

- **M1.5 Plan 1 Tasks 1–3: Project-list-UX quick wins (Asset Store shortcut, per-launch log, crash-log auto-tailing).** Implemented the first three Phase-1 polish items from [execution-plan-1-projects-list-ux.md](specs/execution/M1-5/execution-plan-1-projects-list-ux.md). Validation: `cargo test` 119/119 pass (35 new), `npm run check` 0 errors / 0 warnings on new code (5 pre-existing modal a11y warnings unchanged), `npm run build` clean, `cargo check` clean.
  - **Task 1 (M1.5-1) — Asset Download folder shortcut.**
    - Rust: [hub/src-tauri/src/config/logs.rs](hub/src-tauri/src/config/logs.rs) — new `AssetStorePaths` struct, `resolve_asset_store_paths()` resolver, `asset_store_paths` Tauri command. Per-OS parent resolution: `~/Library/Application Support/Unity` on macOS, `%LOCALAPPDATA%\Unity` on Windows. Picks the highest `Asset Store-<major>.<minor>` subfolder using a full-version tuple comparison (so `5.10` sorts above `5.2`). Falls back to the parent folder and surfaces a `missingMessage` when no versioned subfolder is present; surfaces a `missingMessage` on Linux (deferred platform). 5 new unit tests cover the resolver, command parity, version-sort, file-vs-dir handling, empty-parent handling, and the default.
    - Frontend: [hub/src/lib/services/config.ts](hub/src/lib/services/config.ts) — new `AssetStorePaths` type and `getAssetStorePaths()` invoke. [hub/src/lib/tabs/ToolsTab.svelte](hub/src/lib/tabs/ToolsTab.svelte) — new "Asset Store downloads" section after the Player logs panel: button is disabled with an inline `missingMessage` when the resolver cannot return a `versioned` path; otherwise opens the versioned folder (label "Open folder") or the parent (label "Open parent folder" + drawer log note) depending on `versioned`.
  - **Task 2 (M1.5-2) — Per-launch log file (always on).**
    - Rust: new module [hub/src-tauri/src/config/launch_log.rs](hub/src-tauri/src/config/launch_log.rs) — `LaunchRecord` (camelCase), tagged `LaunchOutcome` enum (`ok` / `error`), `append_record` (synchronous, rotation-aware), `append_record_async` (fire-and-forget on a dedicated `hub-launch-log` OS thread), `tail_lines`, `get_launch_log_tail` Tauri command (clamps the line count to 2 000), `build_record` helper. Rotation policy: 5 MB cap; on overflow the current file moves to `launches.log.1` and the previous `.1` is discarded — keeping at most 2 files (current + rotated) per the task spec. 12 unit tests cover record shape, append, rotation, oldest-copy drop, tail semantics, command clamping, and missing-file behaviour.
    - [hub/src-tauri/src/config/launch.rs](hub/src-tauri/src/config/launch.rs) — refactored `launch_project` to thread every success and failure path through a helper that always records the attempt. Records carry project id + name, path, resolved Unity version, resolved install path, PID (when known), the actual argument vector, the `-buildTarget` value (when set), and a typed error code on failure. The persistent `LaunchResult` shape is unchanged for the frontend.
    - Frontend: `LaunchLogTail` type + `getLaunchLogTail()` invoke. [hub/README.md](hub/README.md) documents the file path, record fields, rotation policy, and writer thread name.
  - **Task 3 (M1.5-3) — Crash-log auto-tailing in status drawer.**
    - Settings: new `DiagnosticsSettings { autoOpenDrawerOnLaunchFailure: bool }` block on the `Settings` schema in [hub/src-tauri/src/config/schemas.rs](hub/src-tauri/src/config/schemas.rs) (default `true`, with `#[serde(default = "...")]` so legacy M1 `settings.json` files continue to load). 3 new unit tests cover the default, round-trip, and the legacy-JSON compat path.
    - [hub/src/lib/state/settings.svelte.ts](hub/src/lib/state/settings.svelte.ts) — `setAutoOpenDrawerOnLaunchFailure` mutator (deep-clone + persist pattern, no rescan side effects).
    - [hub/src-tauri/src/config/logs.rs](hub/src-tauri/src/config/logs.rs) — new `crash_log_path` Tauri command that returns the platform crash folder (`~/Library/Logs/DiagnosticReports` on macOS, `%LOCALAPPDATA%\CrashDumps` on Windows) for the quick-action button.
    - [hub/src/lib/state.svelte.ts](hub/src/lib/state.svelte.ts) — new `LastLaunchFailure` shape with project id/name/path, ISO timestamp, `isLikelyCrash` flag, launch log path, and crash log path. `setLastLaunchFailure` / `clearLastLaunchFailure` setters. New `appendLaunchLog(line, autoOpen)` method that respects the toggle (no auto-expand when off).
    - [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) — on launch failure: fetches the last 200 lines of the per-launch log via `getLaunchLogTail(200)`, appends a header + the tail + a footer to the drawer using `appendLaunchLog` (which respects the toggle), and calls `setLastLaunchFailure` so the drawer can render the quick-action card. On `launchFailed` errors, also calls `getCrashLogPath` to populate the crash button. Successful launches explicitly clear `lastLaunchFailure` and use `appendDrawerLog` so they never auto-open the drawer.
    - [hub/src/lib/components/shell/StatusDrawer.svelte](hub/src/lib/components/shell/StatusDrawer.svelte) — new "launch failed" card rendered above the log lines when `lastLaunchFailure` is set. Card shows a `launch failed` chip, the project name, the ISO timestamp (formatted via `toLocaleString`), a `Reveal crash logs` button (only when `isLikelyCrash && crashLogPath` are both set), the on-disk launch log path, and a dismiss (×) button. Uses `revealItemInDir` from `@tauri-apps/plugin-opener` (already granted by the existing `opener:default` capability).
    - [hub/src/lib/tabs/SettingsTab.svelte](hub/src-lib/tabs/SettingsTab.svelte) — new toggle under Settings → Diagnostics: "Auto-open status drawer on launch failure" with a help line describing both the drawer auto-open behaviour and the on-disk `logs/launches.log` path. Settings is auto-saved; toggle changes take effect on the next launch attempt.
    - The existing diagnostics export (Plan 4 Task 2) was verified to still work independently — it pulls a tail from the in-memory `S.drawerLogs`, which now also receives the launch-log tail lines on failure. The on-disk file is the persistent record; the export captures whatever is in the in-memory tail at the moment the user clicks Export.

## 2026-06-11 20:00 MSK

- **M2 Plan 2 Task 1: Implement M2 meta-tool handlers.** Implemented all four meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`, `find_members`) with full dispatch wiring in the bridge package:
  - `Editor/Bridge/JsonBody.cs` — lightweight JSON body parser extracting strings, string arrays, booleans, integers, raw values, and mixed-type args arrays from request bodies. No external JSON dependency.
  - `Editor/MetaTools/OutputSerializer.cs` — serializes .NET return values to raw JSON for the mutation envelope. Handles primitives, strings, Unity Objects, dictionaries, enumerables, and fallback `ToString()`.
  - `Editor/MetaTools/RoslynHost.cs` — discovers and loads Roslyn compiler assemblies (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp`) from Unity 6 installation at `{Contents}/DotNetSdkRoslyn/` (fallback: `{Contents}/Tools/roslyn/`). Compiles C# source via reflection-based `CSharpCompilation` pipeline: parse syntax tree → create metadata references from loaded assemblies → emit to `MemoryStream` → return PE bytes or error diagnostics.
  - `Editor/MetaTools/ExecuteCSharpTool.cs` — wraps user code in a static method with default usings (`System`, `System.IO`, `System.Linq`, `System.Collections`, `System.Collections.Generic`, `UnityEngine`, `UnityEditor`) plus user-specified extras. Compiles via `RoslynHost`, loads the in-memory assembly, invokes the entry point, and returns serialized output. Error codes: `validation_error`, `roslyn_unavailable`, `compilation_error`, `execution_error`.
  - `Editor/MetaTools/InvokeMethodTool.cs` — finds types via reflection across loaded assemblies (supports fully qualified name, short name, and assembly filter). Converts JSON args to .NET types matching target method parameters. Supports static and instance methods. Error codes: `validation_error`, `type_not_found`, `method_not_found`, `instantiation_error`, `invocation_error`, `execution_error`.
  - `Editor/MetaTools/ExecuteMenuTool.cs` — executes Unity Editor menu items via `EditorApplication.ExecuteMenuItem`. Includes deny-list for `File/Quit`. Error codes: `validation_error`, `menu_blocked`, `menu_not_found`, `execution_error`.
  - `Editor/MetaTools/FindMembersTool.cs` — discovers types, methods, and properties across loaded assemblies. Supports query substring filter, kind filter (`type`, `method`, `property`, `all`), assembly filter, include flags for Unity Editor and project assemblies, and `max_results` (capped at 200). Returns structured JSON with `kind`, `name`, `declaring_type`, `signature`, `summary` per member.
  - `Editor/Bridge/ToolDispatchResult.cs` — unchanged API but `Output` field now carries raw JSON (not a plain string). Tools serialize their output via `OutputSerializer`.
  - `Editor/Bridge/BridgeHttpServer.cs` — `DispatchTool` routes to actual tool implementations via switch expression. `BuildMutationEnvelope` inserts `Output` as raw JSON (`result.Output ?? "null"`) instead of escaping it as a string. Added `using UnityAgentBridge.MetaTools`.
- Marked Task 1 DONE in [execution-plan-2-meta-tools-gate.md](specs/execution/M2/execution-plan-2-meta-tools-gate.md).

## 2026-06-11 18:00 MSK

- **M1.5 execution plan + backlog pull-in + UPM-Template-Creator idea.** Created [specs/execution/M1-5/](specs/execution/M1-5/) for the post-M1 polish & v1.1 foundation milestone:
  - [M1-5-hub-polish.md](specs/execution/M1-5/M1-5-hub-polish.md) — milestone spec: scope, non-scope, task-group → backlog mapping, done-when.
  - [execution-plan.md](specs/execution/M1-5/execution-plan.md) — index: 4 sub-plans, dependency graph, quick-wins-first ordering rationale, M1.5 exit criteria.
  - [execution-plan-1-projects-list-ux.md](specs/execution/M1-5/execution-plan-1-projects-list-ux.md) — 8 Phase 1 quick wins: Asset Download shortcut, per-launch log file, crash-log auto-tailing, frecency sort, platform-intent nudge, git branch display, drag-drop add project, relink missing-path row. Sequenced smallest/safest first.
  - [execution-plan-2-discovery-cli-lifecycle.md](specs/execution/M1-5/execution-plan-2-discovery-cli-lifecycle.md) — CLI mode, running-Unity detection, walk-up directory scan, basic + template-based new-project creation, Unity upgrade assistant, missing-project UX parity (Relink + Hide + Mark stale).
  - [execution-plan-3-tools-theme.md](specs/execution/M1-5/execution-plan-3-tools-theme.md) — additional log shortcuts, project-level env vars, three-way theme switch, Unity releases viewer.
  - [execution-plan-4-platform-niche.md](specs/execution/M1-5/execution-plan-4-platform-niche.md) — Linux first-class support, Explorer/Finder context-menu, GitHub token + git init/commit/push, WebGL local server, ADB logcat, build report viewer.
- **Backlog pruned.** [specs/hub/backlog.md](specs/hub/backlog.md) updated: removed the 24 items pulled into M1.5 (Phase 1, most of Phase 2, Phase 3 P1+P2, selected Phase 4). Kept only items that depend on M2/M4 (project list wizard health badges, wizard defaults settings page, external bridge adapters) and the explicit out-of-scope list. Added an "Active milestone" pointer at the top.
- **UPM-Template-Creator integration idea added to backlog** as a future-scope idea: project-type variant (`unity-project` | `unity-package`), package create/edit/validate, scan for packages, batch git operations, "update code from source folder" action, settings surface (`packages.templateFolder` / `packages.knownFolders` / `packages.scanRecentRoot`). Recommended for M1.6 / v1.2 ("Hub + UPM author surface"), explicitly **deferred from M1.5**. The Go runtime does not move into Hub; the workflow is reimplemented in TypeScript + Rust.
- [specs/execution/README.md](specs/execution/README.md) — added M1.5 rows (spec + execution plan).

## 2026-06-11 17:30 MSK

- **M2 Plan 1 Task 4: Formal bridge HTTP API doc.** Authored [specs/architecture/bridge-http-api.md](specs/architecture/bridge-http-api.md) — the dedicated API contract document for the Unity Agent Bridge HTTP interface:
  - `GET /ping` contract: response fields, 200/503 semantics, compile-wait polling guidance.
  - `POST /tools/{tool_name}` contract: URL parameters, common request body fields (`timeout_ms`, `gate`, `paths_hint`), known M2 tool names.
  - Response shapes: success mutation envelope, timeout envelope, execution error envelope — all with field-level documentation.
  - HTTP status code table: 200/404/405/500/503 with error code mapping (`tool_not_found`, `not_found`, `method_not_allowed`, `bridge_internal_error`, `timeout`, `execution_error`).
  - `isError` mapping rules for MCP server consumption per [gate-policy.md](specs/architecture/gate-policy.md).
  - M2 scope notes documenting gate stub, `paths_hint` enforcement status, scaffold tool implementations, and batch mode deferral.
  - Request examples for all five M2 endpoints (ping, execute_csharp, invoke_method, execute_menu, find_members).
  - Cross-links already present in [bridge.md](specs/packages/bridge.md), [mcp-server.md](specs/packages/mcp-server.md), and execution plan docs.
- Marked Task 4 DONE in [execution-plan-1-bridge-http.md](specs/execution/M2/execution-plan-1-bridge-http.md); Plan 1 exit criterion for API doc checked off.

## 2026-06-11 17:00 MSK

- **M2 Plan 1 Task 3: `/ping` endpoint contract + bridge session state.** Implemented session state caching and domain-reload-safe ping:
  - `Editor/Bridge/BridgeSession.cs` — rewritten to cache volatile state (`IsCompiling`, `IsPlaying`) on the main thread via `EditorApplication.update` callback, so background HTTP threads never access Unity APIs directly. Static state (`ProjectPath`, `UnityVersion`) cached once on initialize. Added `IsInitialized` flag and `volatile` fields for thread safety. `OnBeforeAssemblyReload` sets `compiling: true` and `connected: false`; `OnAfterAssemblyReload` re-caches static state and resets the initialized flag. Properties now read from cached fields instead of calling `EditorApplication.*` on every access.
  - `Editor/Bridge/BridgeHttpServer.cs` — `HandlePing` returns HTTP 503 with a safe fallback JSON (`connected: false, compiling: true`) if `BridgeSession.IsInitialized` is false (covers the window during domain reload when the HTTP listener is up but session hasn't re-initialized yet). `BuildPingJson` gates `connected` on both `BridgeSession.Connected` and `BridgeSession.IsInitialized`. Removed unused `using System.Diagnostics` import.
- Marked Task 3 DONE in [execution-plan-1-bridge-http.md](specs/execution/M2/execution-plan-1-bridge-http.md).

## 2026-06-11 16:00 MSK

- **M2 Plan 1 Task 2: Main-thread dispatch queue with timeout envelope.** Implemented dispatch infrastructure so all Unity API/mutation work executes on the main thread, with HTTP handlers awaiting completion with `timeout_ms` and returning stable mutation error envelopes on timeout/cancellation/faults:
  - `Editor/Bridge/MainThreadDispatcher.cs` — added `EnqueueAsync<T>(Func<T>, int timeoutMs)` returning `Task<T>`. Uses `TaskCompletionSource<T>` with `RunContinuationsAsynchronously` and a `System.Threading.Timer` that fires `TimeoutException` after `timeoutMs`. Existing fire-and-forget `Enqueue(Action)` preserved.
  - `Editor/Bridge/ToolDispatchResult.cs` — new data class carrying `Success`, `Output`, `ErrorCode`, `ErrorMessage`. Factory methods `Ok()` and `Fail(code, message)`.
  - `Editor/Bridge/BridgeHttpServer.cs` — added `POST /tools/{tool_name}` dispatch:
    - Routes POST to known tools (`unity_agent_execute_csharp`, `unity_agent_invoke_method`, `unity_agent_execute_menu`, `unity_agent_find_members`) through `HandleToolDispatch`.
    - Extracts `timeout_ms` (default 30000, clamped 1000–300000) and `gate` mode (default `enforce`) from request body via lightweight JSON scanning.
    - Enqueues tool execution to `MainThreadDispatcher.EnqueueAsync` and blocks on result.
    - Timeout returns `mutation.success: false` with `error.code: "timeout"` and actionable `agentNextSteps`.
    - Faults return `mutation.success: false` with `error.code: "execution_error"`.
    - All tool responses use the combined mutation+gate envelope per [mcp-tools.md](specs/architecture/mcp-tools.md) §Combined response shape; gate section is M2 stub (`skipped: true`).
    - Unknown tool names → 404 `tool_not_found`. Non-POST to `/tools/` → 405 `method_not_allowed`.
- Marked Task 2 DONE in [execution-plan-1-bridge-http.md](specs/execution/M2/execution-plan-1-bridge-http.md).

## 2026-06-11 14:30 MSK

- **M2 Plan 1 Task 1: Bridge package scaffold + listener lifecycle.** Scaffolded `packages/bridge` UPM package with full directory structure per [bridge.md](specs/packages/bridge.md) layout:
  - `package.json` — `com.alexeyperov.unity-agent-bridge` v0.1.0, Unity 6000.0 minimum.
  - `Editor/com.alexeyperov.unity-agent-bridge.Editor.asmdef` — Editor-only assembly definition.
  - `Editor/Bridge/BridgeHttpServer.cs` — `[InitializeOnLoad]` HTTP listener using `System.Net.HttpListener`. Binds to `127.0.0.1:19120` (localhost only). Port override via `UNITY_AGENT_BRIDGE_PORT` env var or `-UNITY_AGENT_BRIDGE_PORT=` command-line arg. Starts on domain load, stops on `beforeAssemblyReload` and `quitting`. Background listener thread dispatches requests to `ThreadPool` workers. Implements `GET /ping` returning `connected`, `projectPath`, `unityVersion`, `bridgeVersion`, `mode`, `compiling`, `isPlaying`. Unknown tools return 404 with `tool_not_found` error. Unknown endpoints return 404 with `not_found`. Internal errors return 500 with `bridge_internal_error`.
  - `Editor/Bridge/BridgeSession.cs` — Static session state holder exposing project path, Unity version, bridge version, compile/play mode, and connected status.
  - `Editor/Bridge/MainThreadDispatcher.cs` — `ConcurrentQueue<Action>` dispatcher draining on `EditorApplication.update`. Scaffold for Task 2 main-thread dispatch.
  - `Editor/Gate/GatePolicy.cs` — `GateMode` enum scaffold (Enforce/Warn/Off).
  - `Editor/Gate/VerifyGateAdapter.cs` — Empty adapter scaffold for M3 verify integration.
  - `Editor/MetaTools/` — Four empty tool scaffolds (`ExecuteCSharpTool`, `InvokeMethodTool`, `ExecuteMenuTool`, `FindMembersTool`).
- Marked Task 1 DONE in [execution-plan-1-bridge-http.md](specs/execution/M2/execution-plan-1-bridge-http.md).

## 2026-06-11 13:40 MSK (hub)

- **Platform intent: show the project’s default build target.** When no platform intent is set, the popup used to say “Unity will use the project’s default build settings” without telling the user *which* target that actually is. The active build target Unity will pick is persisted in `ProjectSettings/ProjectSettings.asset` as `m_BuildTarget` (with `m_BuildTargetGroup` as a fallback for older projects), so we now read it on demand and show it in the status line. New module [hub/src-tauri/src/config/build_target.rs](hub/src-tauri/src/config/build_target.rs) (`read_default_build_target` + `get_default_build_target` Tauri command, 8 unit tests) parses the YAML key with a tiny line scanner (no new crate), strips `'…'` / `"…"` quoting, ignores commented lines, and returns `NotRecorded` when the file or key is missing. Frontend: [hub/src/lib/services/config.ts](hub/src/lib/services/config.ts) exposes `getDefaultBuildTarget`; [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) calls it on `openSettingsPopup`, caches into `defaultBuildTargetMap`, and renders one of three messages — known target (with a friendly label like `iPhone` → `iOS`, `StandaloneOSX` → `macOS`, raw enum on `title` for power users), not recorded (typically a freshly-cloned project that has never been opened in Unity), or `reading default build target…` while the IPC is in flight. Also equalized the four log-shortcut button widths in the same popup. `cargo test config::build_target` → 8/8 pass; `npm run check` → 0 errors.

## 2026-06-11 13:40 MSK

- Added package-level deferred scope tracker: [specs/packages/backlog.md](specs/packages/backlog.md). Seeded with M2 question deferrals (Q3 Unity 2022+ Roslyn feasibility estimate, Q4 token/remote bridge security options, Q7 deny-list hardening) and generic M3+ package hardening placeholders.
- Updated agent/spec workflow rules for deferrals:
  - [AGENTS.md](AGENTS.md): new **Backlog** rule mapping hub deferrals to `specs/hub/backlog.md` and package/MCP deferrals to `specs/packages/backlog.md`, plus removal rule when work becomes active.
  - [specs/questions/README.md](specs/questions/README.md): added explicit deferred-answer routing to hub/packages backlog files.
- Applied M2 answers in decision docs:
  - [specs/questions/questions-2.md](specs/questions/questions-2.md): added backlog cross-links for deferred Q4/Q7 options; added Q5 decision note to create `architecture/bridge-http-api.md`; updated "Suggested doc additions/changes" table to match chosen answers (Q2 verify skeleton, Q5 separate API doc, Q7 deferral); updated M2 execution spec path to `execution/M2/M2-bridge-mcp.md`.
- Added formal bridge API contract doc per Q5:
  - [specs/architecture/bridge-http-api.md](specs/architecture/bridge-http-api.md): defines `/ping` and `/tools/{tool_name}` request/response patterns, timeout/error behavior, and M2 scope notes.
  - Linked new API doc from [specs/packages/bridge.md](specs/packages/bridge.md) and [specs/packages/mcp-server.md](specs/packages/mcp-server.md).
- Created M2 execution folder and split plan structure (mirrors M1 style):
  - [specs/execution/M2/M2-bridge-mcp.md](specs/execution/M2/M2-bridge-mcp.md) milestone spec.
  - [specs/execution/M2/execution-plan.md](specs/execution/M2/execution-plan.md) index.
  - [specs/execution/M2/execution-plan-1-bridge-http.md](specs/execution/M2/execution-plan-1-bridge-http.md).
  - [specs/execution/M2/execution-plan-2-meta-tools-gate.md](specs/execution/M2/execution-plan-2-meta-tools-gate.md).
  - [specs/execution/M2/execution-plan-3-mcp-server-live.md](specs/execution/M2/execution-plan-3-mcp-server-live.md).
  - [specs/execution/M2/execution-plan-4-demo-validation.md](specs/execution/M2/execution-plan-4-demo-validation.md).
- Updated spec indexes and links for M2 folderization:
  - [specs/execution/README.md](specs/execution/README.md): M2 plan path moved under `execution/M2/`, plus M2 execution-plan index row.
  - [specs/README.md](specs/README.md): added `packages/backlog.md`; added M2 milestone + plan rows under Execution.
  - [specs/execution/M1/execution-plan.md](specs/execution/M1/execution-plan.md): "Next milestone" link now points to `../M2/M2-bridge-mcp.md`.

## 2026-06-11 13:35 MSK

- **Platform intent: show the project's default build target.** When no platform intent is set, the popup used to say "Unity will use the project's default build settings" without telling the user *which* target that actually is. The active build target Unity will pick is persisted in `ProjectSettings/ProjectSettings.asset` as `m_BuildTarget` (with `m_BuildTargetGroup` as a fallback for older projects), so we now read it on demand and show it in the status line. New module [hub/src-tauri/src/config/build_target.rs](hub/src-tauri/src/config/build_target.rs) (`read_default_build_target` + `get_default_build_target` Tauri command, 8 unit tests) parses the YAML key with a tiny line scanner (no new crate), strips `'…'` / `"…"` quoting, ignores commented lines, and returns `NotRecorded` when the file or key is missing. Frontend: [hub/src/lib/services/config.ts](hub/src/lib/services/config.ts) exposes `getDefaultBuildTarget`; [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) calls it on `openSettingsPopup`, caches into `defaultBuildTargetMap`, and renders one of three messages — known target (with a friendly label like `iPhone` → `iOS`, `StandaloneOSX` → `macOS`, raw enum on `title` for power users), not recorded (typically a freshly-cloned project that has never been opened in Unity), or `reading default build target…` while the IPC is in flight. `cargo test config::build_target` → 8/8 pass; `npm run check` → 0 errors.

## 2026-06-11 13:25 MSK

- **Project settings popup: equal-width log shortcut buttons.** In the **Log shortcuts** panel, the four secondary buttons (3× **Open folder**, 1× **Open file**) had different widths because each sized to its text. Added `.log-row :global(.btn) { min-width: 6.5rem; justify-content: center; }` to [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) so all buttons share the same width and the labels are centered. The label column (`flex: 0 0 5.5rem`) is unchanged, so row alignment is preserved. `npm run check` → 0 errors.

## 2026-06-11 12:50 MSK

- **Project settings popup cleanup + launch args / platform intent / log shortcuts UX pass** ([hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte), [hub/src/lib/components/shell/StatusDrawer.svelte](hub/src/lib/components/shell/StatusDrawer.svelte), [hub/src/lib/tabs/UnityVersionsTab.svelte](hub/src/lib/tabs/UnityVersionsTab.svelte), [hub/src-tauri/capabilities/default.json](hub/src-tauri/capabilities/default.json)):
  - **Launch args** — placeholder changed from `-logFile -batchmode` (looked like a real value) to `Optional: additional Unity launch arguments…`. The textarea is now full width and the **Save / Reset / Info** buttons sit in a row below it with `flex: 1 1 0` so all three share the row equally. **Info** opens a dedicated modal (`.info-modal`, `min(34rem, 92vw)`) with three sections: an example (`-batchmode -nographics -logFile -`), four common arguments (`-batchmode -nographics -quit`, `-logFile -`, `-username … -password … -serial …`, `-silent-crashes`) with short descriptions, and a button that calls `openUrl(LAUNCH_ARGS_DOCS_URL)` (`https://docs.unity3d.com/Manual/CommandLineArguments.html`) and logs the open in the drawer. Escape and overlay-click both close the info modal. New `mini-panel-hint` style applied to both the Launch args and Platform intent panels.
  - **Platform intent** — added a description (`mini-panel-hint`) explaining that the value is a `BuildTarget` Hub appends via `-buildTarget <name>` on the next launch, that `None` is the default and uses the project's own build settings, and that the intent is only applied on the next launch (never to a running Editor). The `Current: —` line is gone; the status text now reads `Active: <name> (applied on next launch)` when set or `No platform intent set — Unity will use the project's default build settings.` when empty, so an empty intent no longer looks like a missing field.
  - **Popup header actions** — **Kill Unity** moved into the **More ▾** menu (renamed **Terminate Unity**, "Terminating…" while in flight, still destructive-styled, tooltip `Terminate pid <pid>` or `No recorded Unity PID`). **Reveal in file manager** removed from the More menu and from the right-click context menu (and the `handleReveal` function / `revealItemInDir` import are gone) — **Open Folder** is the only filesystem action. **Copy path** moved out of the More menu into the header action bar (now **Copy Path**) next to **Open Folder**, with a "Path missing" / "Copy project path to clipboard" tooltip.
  - **More menu + context menu** — both have Refresh and Remove from list items with `title` tooltips (`Refresh project version and size` / `Remove this project from the Hub list`). The right-click context menu was updated to match the popup (no Reveal, Copy path next to Open folder, Terminate Unity replaces Kill Unity, tooltips on Refresh/Remove).
  - **Log shortcuts** — the **Editor.log** `Open file` link is now a `<Button variant="secondary">` (was a custom `.link-btn` styled `<button>`) so its chrome matches the other three `Open folder` buttons; the `title` is the resolved file path. The unused `.link-btn` CSS was removed.
  - **macOS "Open Folder: not allowed"** — root cause was the Tauri v2 opener scope: `opener:allow-open-path` enables the command but the path still has to match an entry in the `Scope::allowed` list, which was empty. Added explicit glob scopes in [hub/src-tauri/capabilities/default.json](hub/src-tauri/capabilities/default.json): `{ "identifier": "opener:allow-open-path", "allow": [{ "path": "**" }] }` and the same for `opener:allow-reveal-item-in-dir` (the `Reveal` buttons in Settings → Diagnostics and Installs action bar were affected by the same scope check). `opener:default` + `opener:allow-open-url` retained for URL opens.
  - **Status / Log drawer** — chevron is now two distinct SVGs (`▾` for the collapsed state, `▴` for expanded) instead of a single polyline rotated 180°; the `.chevron-up` rule and the transform transition were removed.
  - **Projects tab toolbar** — refresh icon button nudged up 2px via `margin-top: -2px` on `.icon-btn` (it had been sitting ~2px below the `Add Project` baseline).
  - **Installs tab** — `.table-body` now has `padding-top: 0.4rem` so the first row has the same vertical offset the Projects tab has had since the per-row foldout rework; no rows themselves are styled differently, so subsequent rows keep their current spacing.
  - **Verification:** `npm run check` → 0 errors, 5 pre-existing a11y warnings (3 in the old code, 2 from the new info modal — all already silenced with `<!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->` following the existing pattern). `npm run build` → clean.

## 2026-06-11 12:05 MSK

- **Fix: project settings popup failed to open (`state_unsafe_mutation`).** Clicking the gear button set `settingsPopupFor` correctly, but rendering the popup called `getArgsDraft` / `getIntentDraft` from template expressions; those helpers lazily wrote into `$state` records during render, which Svelte 5 forbids and aborted the update. Both helpers are now pure reads (return the draft map entry or fall back to the stored project field without mutating). `handleSaveArgs` / `handleSaveIntent` use the same read helpers so saves still work when the user edits without an intermediate map write. [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte).

## 2026-06-11 11:55 MSK

- **Backlog: restructured into 4 phases after parity analysis vs `references/UnityLauncherPro`.** [specs/hub/backlog.md](specs/hub/backlog.md) now opens with a **Phase 1 — Quick wins** section that captures the small (≤ 2 d) ULP parity gaps: drag-and-drop add, CLI mode, per-launch log file, crash-log auto-tailing, frecency sorting, platform-intent UI nudge, relink missing path, Asset Download folder shortcut, and git branch display. The previous post-v1 / v1.1+ / v2+ structure was preserved as Phases 2–4. New Phase 2 P1 items: New project creation (scaffold-only), Theme support (`dark | light | system` with live switching, explicitly *not* ULP's full RGBA editor), and project-level custom env variables. Added an **Out of scope** section listing the ULP patterns hub should deliberately not copy (full theme editor, custom `JsonParser` / `ObservableDictionary`, monolithic XAML grids, Windows-only `App.config`).

## 2026-06-11 09:40 MSK

- **Projects tab: two-zone row click model + smaller settings icon.**
  - The project row is now divided into two clickable zones by a `1px` left border on the settings cell:
    1. **Launch zone** — everything from the left border to the settings cell (covers name, version, modified, size, status columns). Clicking anywhere here calls `handleLaunch(project.id)`. The row's `onclick` was changed from `selectRow` to `handleLaunch`.
    2. **Settings zone** — the rightmost cell, a full-height `<button class="settings-btn">` that calls `openSettingsPopup(project.id)` and stops propagation so the launch zone doesn't fire.
  - Removed the dedicated `name-path-clickable` clickable `<div>` (its `onclick`, `role="button"`, `tabindex`, `onkeydown`, and per-div hover background) — the launch zone on the row now handles it. The class was renamed to `.name-path` and stripped of `cursor: pointer` and the hover rule (the row provides both).
  - Removed the now-unused local `selectRow(id)` wrapper. The underlying `projectsStore.select` is still called from `openContextMenu` to select on right-click.
  - **Icon size** — the gear SVG is now `24x24` (25% smaller than the previous `32x32`); `stroke-width` adjusted from `1.6` to `1.8` to keep the glyph readable at the smaller size. The `<svg>` still carries `pointer-events: none`.
  - The row keeps its existing hover background (`#1e1f26`) + `cursor: pointer` as the launch-zone hover affordance; the settings button keeps its own distinct hover (`#2a2b33`, lighter text).
  - [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) — only file touched.
  - `npm run check` → 0 errors, 3 pre-existing a11y warnings; `npm run build` → clean.

## 2026-06-11 07:42 MSK

- **Projects tab: replace per-row foldout with a settings popup.**
  - Removed the `▸/◂` expand button (left column) and the entire inline `.expanded-panel` block (`{#if expanded[project.id]}`), plus all related state (`expanded` record, `toggleExpand`, the debug `$effect`, and the destructure-rest cleanup in `performRemove`).
  - Added a gear-icon `settings-btn` in a new right-most column of every project row. Clicking it opens a modal popup (`settingsPopupFor`) with the project's name and path in the header, and the same content that used to live in the foldout: Launch / Open Folder / Kill Unity / More menu (Copy path, Reveal, Refresh, Remove) and the Launch args / Platform intent / Log shortcuts mini-panels.
  - The popup's **Launch** button (`handlePopupLaunch`) closes the popup first, then launches — so launching from the popup behaves the same as clicking the project name.
  - Escape closes the popup (`handleGlobalKeydown`); clicking the overlay also closes it.
  - The grid template now ends with a `2.6rem` settings column instead of a leading `2.2rem` expand column.
  - Added modal styles (`.settings-overlay`, `.settings-modal`, `.settings-modal-header/titles/path/body`, plus `.settings-actions` and `.settings-panels-grid` as the popup-internal replacements for `.expanded-actions`/`.expanded-panels-grid`) and a local `.modal-close-btn` style. Removed `.th-expand`, `.cell-expand`, `.expand-btn`, `.expanded-panel`, `.expanded-actions`, `.expanded-panels-grid` CSS.
  - The settings button fills the entire cell (vertically stretches to the full row height via `align-items: stretch` + `flex: 1; width: 100%; height: 100%`), giving a large click target that runs the full height of the project row. The settings cell carries a `1px` left border (`#24252c`) to visually separate the settings zone from the launch zone.
  - The gear icon is rendered at 32x32 (2x the previous icon size) with `stroke-width: 1.6` so it stays visually balanced at the larger size, and the `<svg>` carries `pointer-events: none` so clicks always reach the button regardless of where on the icon the cursor lands.
  - Simplified the popup guard from a double `{#if settingsPopupFor} {#if popupProject}` chain to a single `{#if popupProject}` driven by a `let popupProject = $derived(settingsPopupFor ? projectsStore.find(settingsPopupFor) ?? null : null)`. Removed the now-unused `settingsPopupProject()` function.
  - [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) — only file touched.
  - `npm run check` → 0 errors, 3 pre-existing-style a11y warnings (already in `ConfirmationModal.svelte`); `npm run build` → clean.

## 2026-06-10 23:40 MSK

- **Fix:** Expand/collapse button on project rows did not respond to clicks (second attempt). Replaced `SvelteSet` (which should have been reactive per Svelte 5 source) with a plain `$state<Record<string, boolean>>({})` that is **reassigned** (`expanded = { ...expanded, [id]: !expanded[id] }`) on every toggle. Variable-level reassignment is the most direct Svelte 5 reactivity trigger and bypasses any proxy / collection-method subtlety. Read sites (`aria-label`, `title`, chevron glyph, `{#if}` guard) updated to `expanded[project.id]`. `handleRemove` cleanup uses destructure-rest to omit the removed id and reassigns.

## 2026-06-10 23:00 MSK

- **Projects tab layout rework:**
  - Project row now shows path below name (instead of separate Path column). Name + path together are hoverable/clickable and trigger project launch.
  - Added expand/collapse button (▸/◂) to left side of each project row. Projects collapsed by default.
  - When expanded: shows Launch, Open Folder, Kill Unity, More menu, plus Launch args, Platform intent, and Log shortcuts panels inline.
  - Bottom detail-strip removed — all content moved into expanded foldout.
  - Renamed "Version" column to "Editor Version".
  - Added "Size" column (folder size excluding Library, Temp, Logs, UserSettings and gitignored dirs). Column header has tooltip explaining exclusions.
  - Added offset above first item in list (padding-top on table-body).
  - Removed VirtualList in favor of regular scrollable list (rows now have variable height due to expand/collapse).
  - [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) rewritten.
- **Rust backend: folder size calculation.** Added [hub/src-tauri/src/config/sizes.rs](hub/src-tauri/src/config/sizes.rs) with `get_project_sizes` command. Excludes Library, Temp, Logs, UserSettings directories and parses .gitignore/ignore.conf patterns. 5 new unit tests. Registered in [hub/src-tauri/src/lib.rs](hub/src-tauri/src/lib.rs).
- **Tools tab simplified.** [hub/src/lib/tabs/ToolsTab.svelte](hub/src/lib/tabs/ToolsTab.svelte) now contains only global tools: Editor logs, Crash logs, Player logs. All project-specific tools (launch args, platform intent, Kill Unity, per-project log shortcuts) moved into the project expand/collapse panel.
- **Sidebar: Settings tab moved to bottom.** [hub/src/lib/components/shell/TopBar.svelte](hub/src/lib/components/shell/TopBar.svelte) now renders Settings tab below a flex spacer, separated from the main navigation tabs.
- **Sidebar: "Unity Versions" renamed to "Installs".**
- **Settings: sections no longer truncate.** Changed `.group` from `overflow: hidden` to `overflow: visible` in [hub/src/lib/tabs/SettingsTab.svelte](hub/src/lib/tabs/SettingsTab.svelte) so collapsible sections render their full content.
- **Verification:** `cargo test` → 90/90 pass (5 new). `npm run check` → 0 errors, 1 pre-existing warning. `npm run build` → clean.

## 2026-06-10 22:00 MSK

- **Shell layout rework:** Moved the section tabs from the top bar to a fixed-width left sidebar and removed the "Unity AI Hub" title from the header. [hub/src/routes/+page.svelte](hub/src/routes/+page.svelte) is now a row (`flex-direction: row`) containing a 11rem-wide sidebar + the `TabPanel`; the `.app` padding/gap was rebalanced to match. [hub/src/lib/components/shell/TopBar.svelte](hub/src/lib/components/shell/TopBar.svelte) is no longer a header — it renders an `<aside class="sidebar">` with a single `<nav role="tablist">` of equal-width vertical tab buttons (left-aligned, full sidebar width, `align-items: stretch` so each tab fills the column). The `APP_NAME` import and the global refresh button were dropped from `TopBar` (the title is no longer rendered; global refresh is now a tab-scoped icon button — see next bullet). [hub/src/lib/components/shell/TabPanel.svelte](hub/src/lib/components/shell/TabPanel.svelte) gained `min-width: 0` so it can shrink alongside the sidebar inside a flex row.
- **Refresh button moved into the Projects toolbar as an icon-only control.** In [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte), the text "Refresh" `Button` next to "Add Project" was replaced with a 2.2rem-square icon button using the same SVG (`M3.51 9a9 9 0 0 1 14.85-3.36L23 10…`) that used to live in the top bar. The button is styled to match the `Button` component's chrome (same border, background, radius, font, hover/focus/disabled states) and uses an explicit `height: 2.2rem` so it lines up with the "Add Project" button height. While `refreshing` is true, the SVG gets a `.icon-spin` class that runs a 0.9s linear `rotate(0→360deg)` keyframe animation. `aria-label` swaps to "Refreshing…" during the in-flight state.
- **Project row height stays uniform.** Rows were already rendered through `VirtualList` with a fixed `ROW_HEIGHT = 38` and `align-items: center` on the grid, so every row is the same height (matching the tallest chip stack). No structural change needed — the new sidebar/row layout preserves the `min-height: 0` chain on `.app → TabPanel → .projects → .table → .table-body → VirtualList`, so virtualization still works.
- **Settings panel:** Widen, collapsible, scrollable.
  - [hub/src/lib/tabs/SettingsTab.svelte](hub/src/lib/tabs/SettingsTab.svelte):
    - Added a `SettingsGroupId` union and an `openGroups: $state<Record<…>>` map (all five groups default to `true`). Each section's `<header>` is now a `<button>` with `aria-expanded` + `aria-controls` that toggles the body in/out. The chevron `▸` is a separate `<span>` that rotates 90° via the `.group-chevron-open` class.
    - Widgets now fit vertically: `.radio-row` wraps the label and description in a `.widget-text` column (label on top, description on its own line below) instead of a cramped horizontal label→desc pair. `.diag-row` switched from `flex-direction: row` to `flex-direction: column` with a dashed bottom divider — label on top, full-width `<code>` path, then the Reveal button on its own line. The check rows keep their inline layout (label-only) but gain a bit more breathing room.
    - The settings container gained `width: 100%; max-width: 56rem; align-self: center; min-width: 0` so the centered column has a wide enough canvas for the vertical widgets without crowding the sidebar.
    - `.body` already had `overflow-y: auto`; reinforced with `min-height: 0` so it actually scrolls when the expanded groups exceed the viewport. The Settings footer (status / version) sits outside `.body` so it stays pinned.
  - The discovery section's `Additional parent folders` hint is long; the new vertical hint sizing (`.group-hint` font-size bumped 0.74 → 0.78rem, line-height 1.4 → 1.5) keeps it readable inside the wider panel.
- **Verification:** `npm run check` → 0 errors, 0 warnings (one pre-existing `a11y_no_noninteractive_element_to_interactive_role` warning on the new `<nav role="tablist">` was silenced with an inline `<!-- svelte-ignore -->`). `npm run build` → clean (adapter-static wrote `build/`). Files touched: `hub/src/routes/+page.svelte`, `hub/src/lib/components/shell/TopBar.svelte`, `hub/src/lib/components/shell/TabPanel.svelte`, `hub/src/lib/tabs/ProjectsTab.svelte`, `hub/src/lib/tabs/SettingsTab.svelte`.

## 2026-06-10 21:27 MSK

- **Branding:** Set custom app icon from `unity-ai-hub-icon.png` — upscaled the 600×595 source to a centered 1024×1024 RGBA master (transparent padding preserved), then regenerated all Tauri bundle icons (`icon.icns`, `icon.ico`, PNG sizes, Appx/Store logos) via `npm run tauri icon`. Master source kept at [hub/src-tauri/icons/icon-source-1024.png](hub/src-tauri/icons/icon-source-1024.png).
- **App name:** Renamed display name from "Unity Agent Hub" to **"Unity AI Hub"** in [hub/src/lib/tokens.ts](hub/src/lib/tokens.ts) (top bar title + footer via `APP_NAME`), [hub/src-tauri/tauri.conf.json](hub/src-tauri/tauri.conf.json) (`productName` for macOS dock tooltip / `.app` bundle name + window `title`), [hub/src/app.html](hub/src/app.html), and [hub/src/routes/+page.svelte](hub/src/routes/+page.svelte) (`aria-label`). Bundle identifier (`com.alexeyperov.unity-agent-hub`) and config dir (`~/.config/unity-agent-hub/`) unchanged.

## 2026-06-10 18:47 MSK

- M1 review follow-up — closed all three high-priority issues per user clarifications:
  - **High #1 (discovery UX):** Renamed the Settings tab section from "Unity discovery" to **"Additional parent folders"** and rewrote the section hint + aria labels to make the additive-scan behavior explicit. The new hint states: OS-default Hub paths (`/Applications/Unity/Hub/Editor` on macOS, `%ProgramFiles%\Unity\Hub\Editor` on Windows) and the `$UNITY_HUB` environment variable are **always scanned** regardless of this list; entries here are extra scan roots layered on top. The empty-state copy and the Remove button aria label were updated to match. Spec updates: [specs/hub/hub-ui.md](specs/hub/hub-ui.md) Settings zones table row + the wireframe ASCII box (`Additional folders` + `Extra scan roots (OS defaults + $UNITY_HUB always on):`) and the mermaid node renamed `UnityDiscoveryGroup` → `AdditionalFoldersGroup`; [specs/hub/hub-data.md](specs/hub/hub-data.md) `settings.json` description updated. [hub/MANUAL_VALIDATION.md](hub/MANUAL_VALIDATION.md) steps 4.3 and 6.7 cross-references updated. The Rust scan logic in [hub/src-tauri/src/config/discovery.rs](hub/src-tauri/src/config/discovery.rs) is unchanged — the additive model is now correctly framed in the UI.
  - **High #2 (unused plugins):** Removed `tauri-plugin-fs` and `tauri-plugin-shell` entirely:
    - [hub/src-tauri/Cargo.toml](hub/src-tauri/Cargo.toml): dropped both `[dependencies]` entries.
    - [hub/src-tauri/src/lib.rs](hub/src-tauri/src/lib.rs): dropped the two `.plugin(tauri_plugin_fs::init())` / `.plugin(tauri_plugin_shell::init())` calls.
    - [hub/src-tauri/capabilities/default.json](hub/src-tauri/capabilities/default.json): dropped `"fs:default"` and `"shell:default"`. The granted permission set is now `core:default`, `dialog:default`, `opener:default`, `opener:allow-open-path`, `opener:allow-open-url` — only what the Hub actually exercises.
    - [hub/package.json](hub/package.json): dropped `@tauri-apps/plugin-fs` and `@tauri-apps/plugin-shell`. Ran `npm install` in `hub/` and `cargo build` to refresh both lockfiles (`hub/package-lock.json` shrank by 22 lines, `hub/src-tauri/Cargo.lock` by 74).
    - Verified zero references in `hub/src` or `hub/src-tauri/src` (`grep` returns nothing). Reduces the runtime attack surface and removes two unused crates from the rebuild graph.
  - **High #3 (per-row Refresh):**
    - **Rust:** Made [hub/src-tauri/src/config/projects.rs::read_dir_mtime_iso](hub/src-tauri/src/config/projects.rs) `pub(crate)`. Extended `VersionRefreshResult` in [hub/src-tauri/src/config/launch.rs](hub/src-tauri/src/config/launch.rs) with a `last_modified_at: Option<String>` field. Rewrote `refresh_project_version` to: resolve the project (return `ProjectNotFound` if missing), check path existence (return `PathInvalid` if missing), re-read `ProjectVersion.txt` via `read_project_version`, re-read the directory mtime via `read_dir_mtime_iso`, update **both** fields on the project entry, persist via `save_projects`, update in-memory state, and return the result. Imports the helper via `use crate::config::projects::read_dir_mtime_iso`.
    - **Tests:** Updated `version_refresh_result_serializes` to assert the new `lastModifiedAt` field appears in the JSON. Added a new `refresh_readers_return_version_and_fresh_mtime` test that exercises the two readers (the unit-testable core of the command) against a `tempfile` directory and asserts the mtime is fresh and not a 1970 sentinel — guards against a future refactor silently returning the stored value instead of re-reading from disk.
    - **Frontend service:** Updated [hub/src/lib/services/config.ts](hub/src/lib/services/config.ts) `VersionRefreshResult` to include `lastModifiedAt?: string`.
    - **UI:** In [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte):
      - Imported `refreshProjectVersion`.
      - Added a `refreshingId: $state<string | null>(null)` per-row busy state.
      - Added `handleRefreshProject(project)`: guards on `refreshingId` and missing-path status, calls `refreshProjectVersion(id)`, then re-checks path existence via `checkPathsExists([project.path])` (with its own error-boundary that uses `appendErrorLog` so a failed recheck doesn't abort the whole refresh), then writes the new `unityVersion` + `lastModifiedAt` through `projectsStore.update`. Drawer logs success at info level and failures at error level (`appendErrorLog`).
      - Added a **Refresh** item to the **More ▾** menu, between the existing filesystem actions and the destructive `Remove from list`, with its own separator on each side. Disabled when the path is missing or while a refresh is in flight (shows "Refreshing…").
      - Added the same **Refresh** item to the right-click context menu (which mirrors the More menu per [specs/hub/hub-ui.md](specs/hub/hub-ui.md)). The two menus stay in lockstep.
  - **Verification:** `cargo test` → 85/85 pass (84 + 1 new). `npm run check` → 0 errors, 0 warnings. `npm run build` → clean. 12 files changed, +141/−119 (excludes the lockfile shrinkage of 22 + 74 = 96 lines). Reverted one stray `hub/README.md` diff that appeared during the session but was unrelated to this work.

## 2026-06-10 17:30 MSK

- M1 review follow-up — fixed all medium + low issues called out in the review (high-priority issues #1–#3 left for a separate pass):
  - **Drawer: only auto-expand on errors.** Split [hub/src/lib/state.svelte.ts](hub/src/lib/state.svelte.ts) so `appendDrawerLog` is info-only (no drawer expand) and the new `appendErrorLog` is the one that flips `drawerExpanded = true`. Updated all error/failure callsites in [ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte), [ToolsTab.svelte](hub/src/lib/tabs/ToolsTab.svelte), [SettingsTab.svelte](hub/src/lib/tabs/SettingsTab.svelte), [UnityVersionsTab.svelte](hub/src/lib/tabs/UnityVersionsTab.svelte), and [TopBar.svelte](hub/src/lib/components/shell/TopBar.svelte) to use `appendErrorLog`. Success/info logs (launch succeeded, folder opened, discovery refreshed, kill succeeded, notFound PID, etc.) no longer pop the drawer open, matching the `hub-ui.md` "auto-opens on launch/tool errors" requirement. `kill: access denied` is the only kill status that expands, since it's a real error.
  - **Deep-link filter is reactive.** Moved the `S.pendingProjectsFilter` consumption out of [ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte)'s `onMount` and into a `$effect`. The filter is now applied whenever it's set, regardless of whether `ProjectsTab` is already mounted — so the "Show projects →" banner on the Unity Versions tab works even if the Projects tab stays alive across the switch.
  - **Diagnostics export uses a directory picker.** [SettingsTab.svelte](hub/src/lib/tabs/SettingsTab.svelte) now calls `openDialog({ directory: true })` to pick a *parent* directory, then constructs the bundle path as `<parent>/<defaultName>`. Removed the `saveDialog` + extension-stripping heuristic that mis-routed "my.bundle" into the parent dir. Also dropped the now-unused `save` import from `@tauri-apps/plugin-dialog`.
  - **Path normalization helper.** Added a small `trimTrailingSeparators` in [SettingsTab.svelte](hub/src/lib/tabs/SettingsTab.svelte) and used it for both the export parent and the new discovery folder, so equality checks against existing entries work and stored paths are clean.
  - **`logs.rs` dead ternary collapsed** ([hub/src-tauri/src/config/logs.rs](hub/src-tauri/src/config/logs.rs)): `editor_dir.join(if cfg!(...) { "Editor.log" } else { "Editor.log" })` → `editor_dir.join("Editor.log")`.
  - **UTF-8 BOM stripped in version readers.** Both [launch.rs::read_project_version](hub/src-tauri/src/config/launch.rs) and [projects.rs::read_unity_version](hub/src-tauri/src/config/projects.rs) now strip a leading `\u{FEFF}` per line before the `m_EditorVersion:` prefix match, so hand-edited `ProjectVersion.txt` files with a BOM still parse.
  - **`seed_from_unity_hub` skip semantics documented.** Added a comment in [seed.rs](hub/src-tauri/src/config/seed.rs) noting that an empty `projects.json` also counts as "already has projects" and pointing to `backlog.md` for the missing re-import action. Also commented the `Option<&String>::cmp` sort to explain the `None`-last ordering in the descending case.
  - **`kill_unity` test rationale expanded.** Replaced the one-line `u32::MAX` comment in [process.rs](hub/src-tauri/src/config/process.rs) with a paragraph explaining why the value is safe on every mainstream OS (`pid_max` ≪ 2^32).
  - **`ProjectsTab.gridTemplate()` hoisted** to a `$derived.by` (recomputed once per column-visibility change, not per row per render).
  - **Single source of truth for `APP_NAME`.** [version.ts](hub/src/lib/version.ts) now re-exports `APP_NAME` from [tokens.ts](hub/src/lib/tokens.ts) — `SettingsTab` still imports from `version.ts`, no callsite churn.
  - **`lastLaunchAt` single-write.** [ProjectsTab.handleLaunch](hub/src/lib/tabs/ProjectsTab.svelte) no longer sets `lastLaunchAt = new Date().toISOString()`; the Rust `launch_project` already records the authoritative value and persists it, and the local-store update only mirrors the new PID + version.
  - **`MANUAL_VALIDATION.md` 0.1** no longer hard-codes `74+`; it points to the latest `specs/changelog.md` entry for the current count.
  - `cargo test`: 84/84 pass (no new tests added; the BOM strip is defensive one-liner code covered by the existing happy-path tests). `npm run check`: 0 errors, 0 warnings. 13 files changed, +111/−72.

## 2026-06-10 15:55 MSK

- Completed M1 Plan 4 Tasks 3, 4, 5 — AI Setup reservation, manual validation checklist, deferrals audit:
  - **Task 3 (M1-17, AI Setup reservation):** added [hub/src/lib/features.ts](hub/src/lib/features.ts) with a single `AI_SETUP_ENABLED = false` constant. The Projects toolbar AI Setup button in [hub/src/lib/tabs/ProjectsTab.svelte](hub/src/lib/tabs/ProjectsTab.svelte) is now gated on this flag (rendered but disabled in M1; flip to `true` in M4 to enable the live control without further code changes). The stub handler now branches on the flag so it stays a no-op in M1 builds. No wizard, MCP merge, or bridge detect logic exists in M1 (verified by `grep` across `hub/src` and `hub/src-tauri/src`); the only matches for those terms are CSS `grid-template-columns`, the explicit "M4 wizard" comment in the AI Setup stub, and the `unity3d` Linux log-path fallback in the cross-platform log shortcuts module (which is in scope for log shortcuts even though Linux is deferred as a platform).
  - **Task 4 (M1-18, manual validation checklist):** authored [hub/MANUAL_VALIDATION.md](hub/MANUAL_VALIDATION.md) covering all V1 done-criteria surfaces: automated pre-flight (cargo test / npm run check / build / cargo check), first-run seed and config recovery, add/remove/missing-path project flows, launch with version match + launch args + `-buildTarget`, Unity Versions discovery + warnings banner + Run Unity, Tools log shortcuts + Kill Unity live + stale PID + access-denied, Settings toggles + diagnostics export + save-failure footer, AI Setup reserved slot, deferrals audit (drag-drop, relink, template, UCP, unity-cli, Linux code paths, etc.), and a repeat-launch / data-loss smoke. Each row is a ☐ checkbox with `Expected` + `Result` columns; a per-platform sign-off section at the end records tester/date and critical-issue links. The checklist is ready for human execution on Windows + macOS.
  - **Task 5 (M1-19, deferrals audit + M1 spec checkboxes):** code audit found no M4/M2 scope leakage (no wizard/MCP/bridge/drag-drop/relink/template/UCP/unity-cli code paths in M1; the only `unity3d` references are the Linux log-path fallback in `logs.rs` and the standard `~/.config/unity3d` directory name). [backlog.md](specs/hub/backlog.md) already covers every deferred item from [hub-requirements.md §Explicitly not required for v1](specs/hub/hub-requirements.md) (Linux, template-based new projects, UCP/unity-cli/Unity official MCP, niche helpers). Updated [M1-hub-launcher.md](specs/execution/M1/M1-hub-launcher.md) — status moved from `pending` to `implementation complete; awaiting cross-platform manual sign-off (2026-06-10)`, all 23 task checkboxes now `[x]`, links added for the new manual validation file and the `AI_SETUP_ENABLED` gate, and a clear "Done when" note that the remaining step is the per-platform sign-off rows in `hub/MANUAL_VALIDATION.md`. Plan 4 exit-criteria checkboxes all flipped to `[x]`.
  - `cargo test`: 84/84 pass (no Rust changes this round). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo check`: clean.
  - Marked Tasks 3, 4, 5 as DONE in [execution-plan-4-settings-validation.md](specs/execution/M1/execution-plan-4-settings-validation.md); Plan 4 is now complete and M1 is awaiting cross-platform manual sign-off.

## 2026-06-10 15:30 MSK

- Completed M1 Plan 4 Task 2 — Settings diagnostics (reveal + export bundle):
  - Added `hub/src-tauri/src/config/diagnostics.rs` — typed `DiagnosticsPaths` / `ExportDiagnosticsResult` / `ExportDiagnosticsError` (camelCase) plus two Tauri commands:
    - `get_diagnostics_paths` returns the config dir + absolute paths to `settings.json` and `projects.json` from the existing `paths` module.
    - `export_diagnostics(target_dir, log_tail)` creates the target directory, copies `settings.json` and `projects.json` if they exist, writes a `version.txt` (app name, version, target arch, build profile, export timestamp — all from `env!("CARGO_PKG_*")` and `cfg!`), and writes a `log.txt` if the frontend supplies a non-empty log tail. Returns the resolved bundle path plus per-file `copied` flags. Errors are typed (`invalidTarget`, `notADirectory`, `targetExists`, `readFailed`, `createFailed`, `copyFailed`, `writeFailed`, `invalidSource`) so the UI can react to "disk not writable" cleanly. Source paths are passed in by the command so the helper is unit-testable against tempdirs without touching the user's real config dir.
  - Registered `get_diagnostics_paths` and `export_diagnostics` in `hub/src-tauri/src/lib.rs`.
  - 10 new unit tests: paths match the `paths` module, full bundle export with log, missing source files are skipped, log file is omitted when the tail is `None`/empty, rejection of empty / file / non-empty target paths, copy failure propagation.
  - Extended `hub/src/lib/services/config.ts` with `DiagnosticsPaths` / `ExportDiagnosticsResult` / `ExportDiagnosticsError` types and `getDiagnosticsPaths()` / `exportDiagnostics()` invokes.
  - Replaced the placeholder Diagnostics section in `SettingsTab.svelte` per `hub-ui.md` §Settings:
    - Loads the config dir / file paths on mount via `getDiagnosticsPaths()` (shows an inline error if the lookup fails, and logs to the status drawer).
    - Renders the config dir path plus per-file rows (`settings.json`, `projects.json`) with a `Reveal` button that calls `revealItemInDir` from `@tauri-apps/plugin-opener` (covered by the existing `opener:default` capability, which already grants `allow-reveal-item-in-dir`).
    - Adds an `Export diagnostics bundle…` primary button. It opens a native save dialog (default name `unity-agent-hub-diagnostics-YYYY-MM-DD_HH-MM-SS`), strips any file extension the OS appends, captures the last ≤200 lines of the status drawer (`S.drawerLogs`) into a header-prefixed `log.txt` payload, and invokes `export_diagnostics`. The resulting bundle path is shown inline below the button and logged to the drawer with per-file summary (`settings: yes/no`, `projects: yes/no`, `log: yes/no`). All failures surface as inline errors plus a drawer log entry.
  - `cargo test`: 84/84 pass (10 new). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo check`: clean.
  - Marked Task 2 as DONE in [execution/M1/execution-plan-4-settings-validation.md](execution/M1/execution-plan-4-settings-validation.md); Plan 4 exit criterion "Diagnostics reveal and export work on both platforms" is now DONE.

## 2026-06-10 15:15 MSK

- Completed M1 Plan 4 Task 1 — Settings tab (prefs, columns, safety, discovery):
  - Added `hub/src/lib/state/settings.svelte.ts` — shared Svelte 5 `$state` settings store. Owns the in-memory `Settings` object loaded via `loadSettings()` and exposes typed mutators (`setLaunchMode`, `setRememberLastSelection`, `setShowPathColumn`, `setShowModifiedColumn`, `setSearchIncludesPath`, `setConfirmKillUnity`, `setConfirmRemoveProject`, `addDiscoveryFolder`, `removeDiscoveryFolder`) that deep-clone, persist via `saveSettings`, and update the in-memory state. `addDiscoveryFolder` / `removeDiscoveryFolder` compare the previous and new `parentFolders` list and trigger `discoveryStore.refresh()` only when the list actually changed, satisfying "Discovery folder changes trigger background rescan (Plan 2 Task 1)". All mutators are no-ops when the value is unchanged, so toggling a checkbox that's already at the right value never round-trips to disk.
  - Refactored `hub/src/lib/state/projects.svelte.ts` to delegate settings ownership to the new store: `settings` is now a getter that returns `settingsStore.current` (so the existing `projectsStore.settings?.projectList.searchIncludesPath` reads in `ProjectsTab.svelte` stay reactive without code changes), and `load()` calls `settingsStore.load()` alongside `loadProjects()`. The store no longer maintains its own `Settings` `$state` field.
  - Added `hub/src/lib/version.ts` with `APP_NAME` and `APP_VERSION` constants (matches `tauri.conf.json` / `package.json` at `0.1.0`) for the Settings tab footer.
  - Built out `SettingsTab.svelte` per `hub-ui.md` §Settings (Plan 4 Task 1 acceptance surface):
    - **Launch** — radio group (Open project scene on launch / Open empty editor only, bound to `settings.launch.mode`) plus a "Remember last selected project on startup" checkbox bound to `settings.launch.rememberLastSelection`.
    - **Project list** — three independent checkboxes (`showPathColumn`, `showModifiedColumn`, `searchIncludesPath`). All apply live to the Projects tab via the existing `projectsStore.settings` reads (no restart needed, no Save button).
    - **Safety** — two checkboxes (`confirmKillUnity`, `confirmRemoveProject`); Projects tab kill/remove flows already consult these.
    - **Unity discovery** — scrollable list of `unityDiscovery.parentFolders` with a Remove button per row, plus an Add Folder button that opens a native folder picker via `@tauri-apps/plugin-dialog` and pipes the picked path through `settingsStore.addDiscoveryFolder`. Removing or adding a folder logs a drawer message and triggers a background discovery rescan.
    - **Diagnostics** — placeholder section noting "coming soon" (Plan 4 Task 2 ships the reveal links and export bundle).
    - **Sticky footer** — "Changes save automatically" / "Saving…" / "Saved ✓" / "Save failed" status with `aria-live="polite"`, and the read-only `Unity Agent Hub v0.1.0 · build` version label.
  - All settings are saved through the existing `save_settings` Tauri command (Plan 1 Task 3), so the round-trip through `settings.json` is covered by the existing persistence unit tests; no backend changes were required.
  - `cargo test`: 74/74 pass. `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo check`: clean.
  - Marked Task 1 as DONE in [execution/M1/execution-plan-4-settings-validation.md](execution/M1/execution-plan-4-settings-validation.md); Plan 4 exit criterion "Settings tab complete with auto-save and live UI effects" is now DONE.

## 2026-06-10 14:30 MSK

- Completed M1 Plan 3 Tasks 3 & 4 — Tools tab log shortcuts + PID-scoped Kill Unity:
  - Added `hub/src-tauri/src/config/logs.rs` — platform-aware log path resolution. macOS uses `~/Library/Logs/Unity` (editor) and `~/Library/Logs/DiagnosticReports` (crash); Windows uses `%LOCALAPPDATA%\Unity\Editor` and `%LOCALAPPDATA%\CrashDumps`; Linux falls back to `~/.config/unity3d` for both. Player logs are always the project's own `Logs/` subfolder. New `LogPaths` struct + `log_paths(project_path)` Tauri command. 6 new unit tests covering player-dir subpath, absolute editor/crash dirs, editor-file-in-editor-dir, command/resolver parity, and empty defaults.
  - Added `hub/src-tauri/src/config/process.rs` — PID-scoped terminate with typed result. `KillUnityStatus` enum (`Killed` | `NotFound` | `AccessDenied`) and `KillUnityResult { pid, status, message }`. macOS/Linux: probes with `kill -0`, sends `SIGTERM` and falls back to `SIGKILL` if needed, then re-probes to distinguish vanished vs. denied. Windows: probes with `tasklist /FI "PID eq <pid>"` and terminates with `taskkill /F /PID <pid>`. PID 0 is rejected outright before any shell-out (would otherwise mean "process group" on Unix and is unused on Windows). New `kill_unity(pid)` Tauri command. 6 new unit tests (unused PID, real running process, second-kill after wait/reap, camelCase serialization for both result and status enum, PID 0 safety).
  - Registered `log_paths` and `kill_unity` in `hub/src-tauri/src/lib.rs` invoke handler.
  - Extended `hub/src/lib/services/config.ts` with `LogPaths`, `KillUnityStatus`, `KillUnityResult` types and `getLogPaths(projectPath)` / `killUnity(pid)` invokes.
  - Extended `ToolsTab.svelte` per `hub-ui.md` §Tools:
    - **Log shortcuts panel** with three folder buttons (Editor / Player / Crash) plus an Editor.log file link. Each button is disabled with an explanatory title when no project is selected or the path is missing. Opening is gated by a single `openingLog` state to prevent duplicate dialogs, with inline error feedback. The Editor.log file link supports middle-click (`onauxclick` with `button === 1`) per the spec. Per-OS path semantics are documented in the panel hint and the Rust comments.
    - **Utilities panel** with destructive `Kill Unity` button (PID-scoped, honouring `settings.safety.confirmKillUnity`), plus `Open project folder` and `Copy project path`. A hint line below the buttons shows the last-recorded `lastLaunchPid` and `lastLaunchAt` (or a "no PID recorded yet" message) so the user always knows what the Kill action will target. Successful and stale-`notFound` results both clear the recorded PID so subsequent Kill actions show the right state.
    - The `$effect` that watches `selected` now also refreshes log paths on project switch; selecting no project (or a project with no path) clears log paths, log errors, and per-field drafts.
  - Wired real `Kill Unity` in `ProjectsTab.svelte`:
    - Replaced the Plan 2 stub in the selection detail strip with a real handler. Button is enabled only when `lastLaunchPid` is present; label switches to `Killing…` while in flight; tooltip explains the missing-PID case.
    - Added a `Kill Unity` item to the right-click context menu between `Copy path` and the destructive separator, matching the new Tasks/Projects/Tools wiring and the spec's "context menu where specified" clause. Same enabled/disabled/tooltip rules as the selection strip.
    - Shared `performKill` / `handleKillUnity` helpers format `KillUnityResult` into a one-line status drawer message and clear `lastLaunchPid` from the project entry on `killed` or `notFound` outcomes, keeping the UI consistent with the recorded state.
  - `cargo test`: 74/74 pass (12 new). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo build`: clean.
  - Marked Tasks 3 and 4 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); the last two Plan 3 exit criteria are now DONE.

## 2026-06-10 14:15 MSK

- Completed M1 Plan 3 Task 2 — Tools tab: launch args + platform intent:
  - Lifted the project selection out of `ProjectsTab.svelte` into the shared `projectsStore` (`projects.svelte.ts`) as `selectedProjectId` + `select(id)`. `add()` auto-selects new projects; `remove()` clears the selection when the removed project was selected. `ProjectsTab` now reads/writes through the store, so the Tools tab inherits the user's Projects selection on first open (or falls back to the first project when the user opens Tools directly).
  - Built out `ToolsTab.svelte` per `hub-ui.md` §Tools and [execution-plan-3 §Task 2](execution/M1/execution-plan-3-versions-tools.md):
    - **Project context bar** — labelled dropdown listing every project (`name · version`) plus the full path; changing it updates the store-wide selection. Disabled (with explanatory copy) when no projects exist.
    - **Launch args panel** — multi-line text input bound to the selected project's `launchArgs` draft, Save and Reset buttons. Save is disabled when the draft is empty (Reset is the way to clear) and when the input contains unsafe characters (regex blocks `\n`, `\r`, `\0`, `` ` ``, `$`, `|`, `&`, `;`, `<`, `>`); an inline error names the offending character. Persists via the existing `projectsStore.update()` round-trip. The hint line shows the currently saved value and reminds the user the args are appended on the **next** launch (matches the existing `launch_project` Tauri command, which whitespace-splits and appends after `-projectPath`).
    - **Platform intent panel** — labelled `<select>` of common `BuildTarget` names (StandaloneWindows64 / StandaloneWindows / StandaloneOSX / StandaloneLinux64 / iOS / Android / WebGL / WSAPlayer / tvOS / VisionOS) plus a "None" option, with a current-value display and Save button. Saving persists the chosen string to `platformIntent` via the same store round-trip; the existing `launch.rs` already appends `-buildTarget <name>` on the next launch, so no backend change was required. The hint copy explicitly states the intent is applied on the next launch only and is not a live switch while the editor is open.
    - Changing the project in the context bar resets both drafts to the new project's stored values via a `$effect` (and clears both the dirty flag and any pending validation error).
    - A dismissible inline error banner covers persist failures from the Tauri side, and the status drawer logs every save / reset / clear.
  - `cargo test`: 62/62 pass (no Rust changes). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo check`: clean.
  - Marked Task 2 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); Plan 3 exit criterion "Tools tab persists launch args and platform intent per project" is now DONE.

## 2026-06-10 13:45 MSK

- Completed M1 Plan 3 Task 1 — Unity Versions tab:
  - Added `hub/src-tauri/src/config/launch.rs::run_unity_install` Tauri command — spawns the resolved Unity executable for a version with no project (opens the editor). Typed errors: `VersionMissing`, `InstallNotFound`, `ExecutableMissing`, `LaunchFailed` with camelCase serialization. Made `get_unity_executable_path` and `resolve_install_for_version` `pub(crate)` for reuse. Registered the new command in `hub/src-tauri/src/lib.rs`.
  - 7 new unit tests: 4 `RunUnityError` variants serialize correctly, `RunUnityResult` camelCase serialization, `get_unity_executable_path` finds macOS bundle, returns `None` for missing directory.
  - Added `opener:allow-open-url` to `hub/src-tauri/capabilities/default.json` (required for release notes link → system browser).
  - Extended `hub/src/lib/services/config.ts` with `RunUnityError` / `RunUnityResult` types and `runUnityInstall(version)` invoke.
  - Added `hub/src/lib/state/discovery.svelte.ts` — shared Svelte 5 `$state` discovery store with `load()` / `refresh()` and helpers for per-version project counts, missing-version buckets, and `ok` / `warn` / `missing` health mapping.
  - Extended `hub/src/lib/state.svelte.ts` with a `pendingProjectsFilter` signal + `requestProjectsFilter()` / `consumeProjectsFilter()` helpers so the Versions tab can deep-link into Projects with the missing-version filter pre-applied.
  - Built out `UnityVersionsTab.svelte` per `hub-ui.md` §Unity Versions:
    - Toolbar: search input (filters version + path) and Refresh button (re-runs discovery via `refreshDiscovery`).
    - Conditional warnings banner listing every `unityVersion` referenced by `projects.json` that has no install, with a "Show projects →" link that switches to the Projects tab and applies the missing-version filter.
    - Installations table (virtualized): colored health dot + status chip on the version cell (`ok` / `warn` / `missing`), source label, install path, project count, installed date.
    - Action bar: **Open Install Folder** (`openPath`), **Reveal** (`revealItemInDir`), **Release Notes ↗** (`openUrl` to `https://unity.com/releases/editor/whats-new/<dashes>`), **Run Unity** (new `runUnityInstall` command). All actions disabled with explanatory titles when no row is selected; Run Unity shows "Running…" while in flight.
    - Keyboard: ↑/↓/Home/End navigate, Enter runs the selected Unity, double-click also runs.
  - `ProjectsTab.svelte` consumes the pending filter on mount (covers the deep-link from the warnings banner). No change to its existing filter preset semantics.
  - Wired the global TopBar Refresh button to refresh both `discoveryStore` and `projectsStore` (logs summary to the status drawer).
  - `cargo test`: 62/62 pass (7 new). `npm run check`: 0 errors, 0 warnings. `cargo check`: clean.
  - Marked Task 1 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); Plan 3 exit criterion "Unity Versions tab shows installs, warnings, and row actions" is now DONE.

## 2026-06-10 13:20 MSK

- Completed M1 Plan 2 Task 5 — Project row actions (launch, folder, reveal, remove):
  - Added `hub/src-tauri/src/config/projects.rs::remove_project` Tauri command — removes a project entry by id from `projects.json` only (does not touch the project folder or Unity Hub registry). Typed errors: `ProjectNotFound`, `PersistFailed` with camelCase serialization. 3 new unit tests (result serialization, both error variants).
  - Registered `remove_project` in `hub/src-tauri/src/lib.rs` invoke handler.
  - Added `opener:allow-open-path` to `hub/src-tauri/capabilities/default.json` (the `opener:default` set does not include `openPath` — required for "Open Folder" via system default app).
  - Extended `hub/src/lib/services/config.ts` with `RemoveProjectError` / `RemoveProjectResult` types and `removeProject(projectId)` invoke.
  - Extended `hub/src/lib/state/projects.svelte.ts` with `remove(id)` store helper that updates in-memory list and persists.
  - Wired `ProjectsTab.svelte` row actions:
    - **Open Folder** uses `openPath()` from `@tauri-apps/plugin-opener`; disabled for missing-path rows with explanatory title; logs success / failure to status drawer and surfaces a dismissible inline error.
    - **Reveal in file manager** uses `revealItemInDir()` from the same plugin; same disabled/visible-tooltip treatment for missing paths. Mirrored on both the More ▾ menu and the right-click context menu (where Launch is now also disabled when not launchable).
    - **Remove from list** invokes the new Tauri command. Honors `settings.safety.confirmRemoveProject` via the existing `S.confirm(...)` modal; if disabled, removes immediately. Disabled while a remove is in flight ("Removing…" label).
    - Missing-path rows are remove-only: Launch, Open Folder, and Reveal are all disabled; only Copy path and Remove from list remain enabled.
    - Selection is cleared automatically when the selected project is removed.
  - `cargo test`: 55/55 pass (3 new). `npm run check`: 0 errors, 0 warnings. `cargo check`: clean.
  - Marked Task 5 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md); Plan 2 exit criterion "Projects tab supports list, search, filter, add, and core row actions" is now DONE.

## 2026-06-10 13:10 MSK

- Completed M1 Plan 2 Task 4 — Search, filter, Add Project folder picker:
  - Added `tauri-plugin-dialog` (Rust crate + `@tauri-apps/plugin-dialog` JS package); registered the plugin in `hub/src-tauri/src/lib.rs` and added `dialog:default` to `hub/src-tauri/capabilities/default.json`.
  - Added `hub/src-tauri/src/config/projects.rs` — project add/refresh logic:
    - `add_project(path)` Tauri command: validates the path is a directory and contains `Assets/` and `ProjectSettings/` (returns typed `AddProjectError::{NotADirectory, NotAUnityProject, Duplicate, PersistFailed}`); rejects duplicates by canonical path; reads `ProjectVersion.txt` and filesystem mtime; generates a new uuid, appends to `projects.json`, and updates in-memory state.
    - `refresh_all_projects()` Tauri command: walks every entry, re-reads `ProjectVersion.txt` and directory mtime for paths that still exist (missing paths are reported in `skipped`); persists updates; re-runs Unity discovery and refreshes the cache.
    - 7 new unit tests (valid root, missing `Assets`, missing `ProjectSettings`, file-not-dir, version parsing, missing version file, name derivation).
  - Extended `hub/src/lib/services/config.ts` with `AddProjectError`, `AddProjectResult`, `RefreshAllResult` types and `addProject()` / `refreshAllProjects()` invokes.
  - Extended `hub/src/lib/state/projects.svelte.ts` with `add()` and `replaceAll()` store helpers.
  - Wired `ProjectsTab.svelte`:
    - Add Project opens a native folder picker via `@tauri-apps/plugin-dialog`, calls `add_project`, inserts the returned entry into the store, re-checks path existence, and selects the new row. Inline error banner appears for non-Unity / duplicate / persist errors and the status drawer logs success and failures.
    - Refresh button calls `refresh_all_projects`, swaps the store list, re-runs path-existence checks, and logs how many entries were updated/skipped.
    - AI Setup button remains disabled/reserved.
  - `cargo test`: 52/52 pass (7 new). `npm run check`: 0 errors, 0 warnings. Frontend and backend build cleanly.
  - Marked Task 4 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md); marked Plan 2 exit criteria 1, 2, and 4 as DONE.

## 2026-06-10 12:30 MSK

- Completed M1 Plan 2 Task 3 — Projects tab (table, columns, selection):
  - Added `hub/src-tauri/src/config/commands.rs::check_paths_exists` Tauri command — returns `HashMap<path, bool>` so the UI can derive missing-path status without per-row IPC chatter. Registered in `hub/src-tauri/src/lib.rs`. 4 new unit tests (existing, missing, empty, mixed).
  - Added `hub/src/lib/state/projects.svelte.ts` — shared Svelte 5 `$state` projects store with `load()`, `find()`, `update()`, `persist()`.
  - Added shared UI primitives in `hub/src/lib/components/`:
    - `StatusChip.svelte` — ok / warn / missing / running / info / muted tones.
    - `RelativeTime.svelte` — compact relative date with full timestamp in `title`.
    - `VirtualList.svelte` — generic typed windowed list with overscan, page-keyboard nav (PageUp/PageDown/Home/End), and graceful fallback to direct rendering below 60 items.
  - Updated `Button.svelte` shell component to forward `title` and arbitrary rest props (e.g. `aria-haspopup`).
  - Built out `ProjectsTab.svelte` per `hub-ui.md` §Projects:
    - Toolbar: search input (respects `settings.projectList.searchIncludesPath`), filter pill group (all / launchable / missing version / missing path), reserved `AI Setup` disabled button, `Add Project` and `Refresh` stubs that log to the status drawer.
    - Table: virtualized list of `ProjectEntry` rows. Columns: Name, Path (`showPathColumn`), Version, Modified (`showModifiedColumn`), Status. Status chip rules: path existence + version presence → `ok` + `launchable` / `warn` (version missing) / `missing path` (Launch disabled).
    - Selection: single-click selects; selection strip mirrors row context with name · version · path and action bar (Launch / Open Folder / Kill Unity / More ▾). More menu and right-click context menu share: Launch, Open folder, Reveal in file manager, Copy path, Remove from list (last two stub-logged for Plan 2 Task 5).
    - Keyboard: ↑/↓ navigate, Home/End jump, Enter launches when launchable, ContextMenu key opens context menu, double-click launches. Esc closes menus.
    - Empty states: separate copy for "no projects yet" vs "no projects match filter".
  - Added `checkPathsExists(paths: string[])` to `hub/src/lib/services/config.ts`.
  - `npm run check`: 0 errors, 0 warnings. `cargo test`: 44/44 pass (4 new). Frontend and backend build cleanly.
  - Marked Task 3 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md).

## 2026-06-10 04:00 MSK

- Completed M1 Plan 2 Task 2 — Launch resolver + Unity process spawn:
  - Added `hub/src-tauri/src/config/launch.rs` — resolves Unity executable for a project and spawns the editor process.
  - Resolve flow: match project `unityVersion` to discovered install, find platform-specific executable (macOS: `Unity.app/Contents/MacOS/Unity`, Windows: `Editor/Unity.exe`).
  - Typed errors: `ProjectNotFound`, `PathInvalid`, `VersionMissing`, `InstallNotFound`, `LaunchFailed` — all with camelCase serialization for frontend.
  - Launch command: `-projectPath <path>` (respects `settings.launch.mode`), per-project `launchArgs` (whitespace-split), `-buildTarget <platformIntent>` when set.
  - Spawns via `std::process::Command`; on success records `lastLaunchPid` and `lastLaunchAt` on project entry, persists to `projects.json`.
  - Refreshes project `unityVersion` from `ProjectSettings/ProjectVersion.txt` before launch.
  - Exposed `launch_project` and `refresh_project_version` Tauri commands.
  - Added 12 unit tests covering: version file parsing (normal, missing, empty, no newline, non-version lines), error variant serialization, result camelCase serialization.
  - All 40 tests pass; builds cleanly.
  - Marked Task 2 as DONE in execution-plan-2-projects-launch.md.

## 2026-06-10 03:00 MSK

- Completed M1 Plan 2 Task 1 — Unity installation discovery service:
  - Added `hub/src-tauri/src/config/discovery.rs` — scans parent folders for versioned Unity Editor installs with three-source precedence: user-configured `settings.unityDiscovery.parentFolders` (source: Manual), OS default Hub Editor paths (source: Hub), `UNITY_HUB` env override (source: Env).
  - Platform-aware detection: macOS checks `Unity.app` bundle, Windows checks `Editor/Unity.exe`, Linux checks `Editor/Unity`.
  - Builds in-memory model: version string, install path, source label, optional install date (from filesystem metadata).
  - Deduplicates installs found through multiple scan roots; sorted by version descending.
  - Scan errors are non-fatal — partial results returned with per-parent error detail for UI.
  - Discovery results cached in `AppState.discovery_cache`; `discover_installations` returns cache, `refresh_discovery` forces rescan.
  - Added `UnityInstallation`, `DiscoveryError`, `DiscoveryResult` types and `discoverInstallations()`, `refreshDiscovery()` to frontend config service.
  - Added 9 unit tests covering: find installs, sort order, skip non-editor dirs, nonexistent parents, unreadable dirs, source labels, deduplication, install dates, empty settings.
  - All 28 tests pass; builds cleanly.

## 2026-06-10 02:00 MSK

- Completed M1 Plan 1 Task 5 — config persistence unit tests:
  - Added 19 Rust unit tests in `hub/src-tauri/src/config/` covering schema defaults, round-trip serialize/deserialize, camelCase serialization, optional field exclusion, corrupt JSON rejection, partial JSON rejection, atomic write (create/overwrite/no leftover tmp), backup_corrupt rename, and corrupt-file recovery to defaults.
  - Added `tempfile` dev-dependency for clean test isolation.
  - All tests pass via `cargo test` without Unity or Hub installed.
  - Marked Task 5 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 01:30 MSK

- Completed M1 Plan 1 Task 4 — first-run Unity Hub seed import:
  - Added `hub/src-tauri/src/config/seed.rs` — reads Unity Hub's `projects-v1.json` (OS-specific paths), maps entries to Hub `ProjectEntry` records with stable UUIDs, ISO 8601 timestamps, and Unity version strings.
  - Seed is non-destructive: skipped when `projects.json` already has projects (second launch does not overwrite user edits).
  - Missing project paths are kept with `skippedPaths` note for missing-path chip (Plan 2).
  - Seed failure (Hub not installed / no projects file) yields empty project list + error message for UI.
  - Added `chrono` dependency for Unix epoch ms → ISO 8601 conversion.
  - Exposed `seed_from_unity_hub` Tauri command returning `SeedResult { projects, seededCount, skippedPaths, error? }`.
  - Added `seedFromUnityHub()` to frontend config service (`src/lib/services/config.ts`).
  - Updated `hub/README.md` with Unity Hub data paths, `projects-v1.json` format, and seed behavior docs.
  - Marked Task 4 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 01:00 MSK

- Completed M1 Plan 1 Task 3 — config directory + split JSON persistence:
  - Added `hub/src-tauri/src/config/` module: `paths.rs` (platform-specific config dir resolver), `schemas.rs` (typed `Settings` and `ProjectsFile` structs with safe defaults), `persistence.rs` (atomic write via temp+rename, idempotent saves, corrupt/missing file recovery with `.json.corrupt` backup), `commands.rs` (Tauri commands: `load_settings`, `save_settings`, `load_projects`, `save_projects`).
  - Added `hub/src/lib/services/config.ts` — frontend TypeScript types and `invoke` wrappers for all four commands.
  - Wired commands and `AppState` into Tauri builder in `lib.rs`.
  - Config dir: macOS/Linux `~/.config/unity-agent-hub/`, Windows `%APPDATA%\unity-agent-hub\`. Auto-created on first access.
  - Updated `hub/README.md` with config directory docs, file descriptions, and manual verification steps.
  - Marked Task 3 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 00:15 MSK

- Completed M1 Plan 1 Task 2 — shell components port:
  - Added `hub/src/lib/components/shell/` with TopBar (pill-tab strip + Refresh button), TabPanel, Button (primary/secondary/destructive), ConfirmationModal (overlay skeleton with promise-based confirm), StatusDrawer (collapsible, empty state, log tail).
  - Added `hub/src/lib/tabs/` with four placeholder panels: ProjectsTab, UnityVersionsTab, ToolsTab, SettingsTab.
  - Added `hub/src/lib/state.svelte.ts` (reactive tab state, modal confirm, drawer log store) and `hub/src/lib/tokens.ts` (brand name, status colors, brand color tokens).
  - Wired full shell layout in `+page.svelte` matching hub-ui wireframe zones (top bar, tab panel, status drawer).
  - Tab switching works without page reload; all four placeholders render.
  - Marked Task 2 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-09 23:15 MSK

- Completed M1 Plan 1 Task 1 scaffold work:
  - Added new `hub/` app scaffold at repo root (Tauri 2 + SvelteKit + Svelte 5 + Vite 6) with versions aligned to `vibe-launcher` pins.
  - Replaced template greet UI with a minimal blank Hub window and updated app metadata to "Unity Agent Hub".
  - Added baseline Tauri permissions/plugins for upcoming file/process operations (`fs`, `shell`, `opener`).
  - Added `hub/README.md` with local dev commands and documented pinned dependency versions.
  - Marked Task 1 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-09 23:30 MSK

- M1 execution planning:
  - Moved [execution/M1-hub-launcher.md](execution/M1/M1-hub-launcher.md) → [execution/M1/](execution/M1/).
  - Added [execution/M1/execution-plan.md](execution/M1/execution-plan.md) — index with assumptions, risks, dependency graph, exit criteria.
  - Added agent sub-plans: [execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md), [execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md), [execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md), [execution-plan-4-settings-validation.md](execution/M1/execution-plan-4-settings-validation.md) — 19 scored tasks with required context and acceptance checklists.
  - Updated [execution/README.md](execution/README.md), [README.md](README.md), [idea.md](idea.md), [questions/questions-0.md](questions/questions-0.md), [questions/questions-1.md](questions/questions-1.md) links.

## 2026-06-09 22:00 MSK

- Resolved [questions/questions-0.md](questions/questions-0.md) and [questions/questions-1.md](questions/questions-1.md):
  - M0: README-only status tracking; decisions folded into target specs; keep absolute `vibe-launcher` path (publish-time sanitization note).
  - M1: all ten recommended answers accepted — Hub seed + owned store, shell copy port, split config, PID Kill Unity, `BuildTarget` launch arg, folder-picker add, missing-path chip, discovery precedence, manual test matrix.
- Added [hub/hub-data.md](hub/hub-data.md) — config schemas, project list source, platform intent, Kill Unity, Unity discovery, stack versions.
- Updated [hub/hub-requirements.md](hub/hub-requirements.md), [hub/hub-ui.md](hub/hub-ui.md), [architecture/monorepo-layout.md](architecture/monorepo-layout.md), [execution/M1-hub-launcher.md](execution/M1-hub-launcher.md), [idea.md](idea.md), [README.md](README.md), [questions/README.md](questions/README.md).

## 2026-06-09 21:00 MSK

- Added `specs/questions/` — per-milestone pre-execution question files `questions-0.md` … `questions-7.md` with answer options and recommended defaults.
- Updated `specs/README.md` and `specs/idea.md` — link questions folder and milestone map column.

## 2026-06-09 20:00 MSK

- Resolved spec contradictions:
  - `specs/idea.md` — M0 marked DONE; wizard removed from M1/v1 baseline (M4 only); M2 tool count clarified (`ping` + 4 meta-tools).
  - `specs/hub/backlog.md` — purpose aligned to M1 launcher-only; removed per-project launch args (already in v1 Tools tab).
  - `specs/hub/hub-wizard.md` — bridge detect uses `com.alexeyperov.unity-agent-bridge`.
  - `specs/architecture/gate-policy.md` — empty `paths_hint` fallback labeled M2 stub (was M1).
- Restructured `specs/` layout:
  - `specs/architecture/` — `gate-policy.md`, `mcp-tools.md`, `monorepo-layout.md` (extracted from idea.md).
  - `specs/packages/` — `bridge.md` (new), `verify.md` (from `verify-package.md`), `mcp-server.md` (new).
  - `specs/agents/` — `agent-skill.md` (new).
  - `specs/execution/` — M0–M7 task plans; M0 marked DONE.
  - `specs/archive/README.md` — relocation map for moved paths.
- Updated `specs/README.md` — reading order, milestone map, manual setup path, full document index.

## 2026-06-09 18:00 MSK

- Added **OpenCode** as a first-class MCP client alongside Cursor and Claude:
  - `specs/idea.md` — architecture diagram, monorepo `skills/` / `templates/` notes, M3/M4 deliverable wording.
  - `specs/mcp-tools.md` — OpenCode `opencode.json` example, optional per-agent tool gating, client config path table.
  - `specs/hub/hub-wizard.md` — Step 1 detection heuristic, Step 4 OpenCode global/project options, Done screen links and actions.

## 2026-06-08 (specs tighten)

- Split roadmap **M6** / **M7** in `specs/idea.md`:
  - **M6** — bring-your-own-bridge (ecosystem docs only).
  - **M7** — MCP Resources and verify cache backing.
- Tightened `specs/mcp-tools.md`:
  - Added verify rule registry table (M3 / M3+ / M5; `regression_trend` not a rule).
  - Removed `unity_agent_scan_category` — use `scan_paths` with one `categories` entry.
  - Trimmed M7 Resources to three URIs; deferred references/categories/dependencies resources.
  - Renamed old combined M6 section into separate M6 (ecosystem) and M7 (resources).
- Updated `specs/verify-package.md` rule-ID milestones and M6/M7 mapping to match.

## 2026-06-09 15:25 MSK

- Updated `specs/hub-wizard.md` to reflect locked strategy decisions:
  - Greenfield Hub implementation (Tauri + Svelte), no LauncherPro fork.
  - Explicit v1 scope (core parity + selected power tools + minimal wizard).
  - Windows + macOS first-class for v1, Linux deferred.
  - Added references to backlog tracking for deferred scope.
- Updated `specs/idea.md` to align product/roadmap language with greenfield Hub:
  - Distribution pillar now describes Hub as Tauri + Svelte app.
  - Monorepo layout no longer describes `hub/` as a LauncherPro fork.
  - M3 deliverable wording updated to greenfield implementation.
  - Added v1 Hub scope baseline decisions and related backlog reference.
- Updated `specs/README.md`:
  - Added `specs/hub/backlog.md` to document index.
  - Updated Unity Agent Hub working-name description to greenfield Tauri + Svelte positioning.
- Added `specs/hub/backlog.md`:
  - Introduced structured deferred scope with priority buckets and milestone windows.
  - Captured UnityLauncherPro parity gaps not included in v1.
  - Added Linux support as explicit post-v1 platform backlog item.

## 2026-06-09 17:30 MSK

- Expanded `specs/hub/hub-ui.md` with full UI layout schemas:
  - Shell wireframe and zone table for the main window.
  - Per-tab wireframes, zone tables, and component hierarchy diagrams for Projects, Unity Versions, Tools, and Settings.

## 2026-06-09 16:53 MSK

- Restructured Hub specs under `specs/hub/` and split former wizard monolith into v1-focused documents:
  - Added `specs/hub/hub-requirements.md` as the main Hub v1 requirements/scope document.
  - Added `specs/hub/hub-ui.md` for tabbed UI architecture, layout schemas, and `vibe-launcher` reuse strategy.
  - Added `specs/hub/hub-wizard.md` as the dedicated wizard and MCP onboarding specification.
  - Removed legacy `specs/hub-wizard.md` after content migration.
- Updated `specs/README.md` document index to reference new Hub spec locations.
- Updated `specs/idea.md` roadmap:
  - Inserted new milestone after M0: **M1 — Hub v1 launcher** (without wizard and without new MCP integration).
  - Shifted subsequent milestones and renumbered sequencing through M6.
  - Updated milestone deliverable sections and related spec links to new Hub docs.
- Normalized milestone numbering in dependent specs to match the new roadmap:
  - Updated `specs/mcp-tools.md` milestone headings/references from M1-M5 to M2-M6 where applicable.
  - Updated `specs/verify-package.md` milestone mapping and references (wizard now M4, batch now M5, ecosystem now M6).
