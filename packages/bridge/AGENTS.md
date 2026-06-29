# Bridge package rules

## Scope

Rules for `packages/bridge/` — the Unity Editor HTTP bridge (`com.alexeyperov.unity-open-mcp-bridge`). Inherits root `AGENTS.md`; deeper rules win on overlap.

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

- Shipped domain tools (Nav, Input, ProBuilder, Particles, Animation) live under `Editor/TypedTools/Extensions/<Domain>/`, **not** in `packages/extensions/`. See `docs/extensions.md` §Embedded domain model.
- **Compile-gating is mandatory.** The bridge root asmdef (`com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`) declares one `versionDefines` entry per shipped domain that maps the Unity package/module to `UNITY_OPEN_MCP_EXT_<DOMAIN>`. Each domain lives in its own sub-asmdef with `defineConstraints: ["UNITY_OPEN_MCP_EXT_<DOMAIN>"]` and a reference to the domain package — the sub-asmdef only compiles when the dependency is present, so a project with zero optional Unity domain packages still compiles cleanly.
- **No runtime reflection probing** for shipped domains. When the dependency is absent, the tools are simply not compiled in; the capability surface reports `available: false (dependency missing: <package>)`. Reflection is reserved for version-split APIs only (Cinemachine 2.x/3.x in M18 Plan 7) and must document the split + minimum version.
- **No manual Player Settings scripting-define writes.** The wizard must not write `UNITY_OPEN_MCP_EXT_*` symbols — they come exclusively from the asmdef `versionDefines`.
- New first-party domains follow the Navigation reference template (`Editor/TypedTools/Extensions/Navigation/`): sub-asmdef with `defineConstraints` + package ref, source files wrapped in `#if UNITY_OPEN_MCP_EXT_<DOMAIN>`, `[BridgeToolType]` + `[BridgeTool]` discovery (registry scans all loaded assemblies), stable `unity_open_mcp_<domain>_<action>` IDs, gate contracts on mutating tools, and a gated EditMode test asmdef under `Tests/Editor/TypedTools/<Domain>/`.
- Tool IDs and JSON response schemas are stable across the embed migration — do not change them when moving a domain out of `packages/extensions/`.

## Gate policy

- The gate flow (checkpoint → mutate → validate → delta) is the bridge's core safety contract. Do not add a mutating dispatch path that bypasses `GatePolicy.Execute`.
- `paths_hint` is mandatory for mutating tool calls — there is no whole-project fallback. Do not add one.
- Gate precedence: request `gate` → project default (`BridgeGateDefaultPolicy`) → tool attribute default. Do not reorder this without updating `docs/architecture.md` and the bridge-http docs.

## Transport

- `BridgeHttpServer` binds `127.0.0.1` by default. Remote bind (`0.0.0.0`) is opt-in via `bindAddress` and requires `authMode: "required"` (see §Remote bind below). Do not add a non-localhost binding path that skips the `BridgeBindAddress.Decide` check.
- All Unity API calls happen on the main thread via `MainThreadDispatcher`. Never call `UnityEditor`/`UnityEngine` Editor APIs from the listener worker thread.
- JSON is hand-rolled via `StringBuilder` (no Newtonsoft dependency in the bridge). Follow the existing `EscapeString`/`Build*Envelope` patterns; do not introduce a JSON serializer dependency.

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

## Remote bind (M14 T5.4)

- The listener bind address is configurable (`bindAddress`: `"127.0.0.1"` default | `"0.0.0.0"`). The bridge refuses to start on a non-loopback interface unless `authMode` is `"required"` — `BridgeBindAddress.Decide` is the pure decision, called from `BridgeHttpServer.Start` before opening the socket.
- The bridge does not terminate TLS. Remote access is documented as "convenient for a trusted LAN/VPN behind a reverse proxy / ssh tunnel" — see `docs/api/bridge-http.md` §Remote bind. Do not add TLS termination to the bridge; terminate upstream.

## On-disk audit log (M14 T5.5)

- Opt-in via `auditLogEnabled` in `.unity-open-mcp/settings.json`. When on, every gate mutation (pass / fail / warn) and deny-list refusal is appended to a rolling JSON-lines file at `~/.unity-open-mcp/audit/audit-<projectHash>.jsonl` (5 MiB active, 5 retained rotations).
- Writes are serialized through a lock and best-effort: an I/O failure is logged once and the record dropped — audit logging never breaks the dispatch path. `BridgeAuditLog.AuditDirOverride` is a test-only hook (mirrors `InstancePortResolver.InstancesDirOverride`); never set it in production.
- The audit record is built in `BridgeHttpServer.RecordAudit`, called from `RecordGateRun` alongside the in-memory history. The outcome vocabulary is `passed` | `warned` | `failed` | `skipped` | `denied`; `bypassedDenyList` flags the gate=off+confirm escape hatch.

## Multi-instance port + discovery (M13)

- The bridge port is **deterministic per project**: `20000 + (sha256(projectPath) % 10000)`, implemented in `InstancePortResolver`. The formula must stay byte-for-byte identical to the MCP server mirror at `mcp-server/src/instance-discovery.ts` (`computePort`); cross-side consistency is pinned by `InstancePortResolverTests.cs` and `instance-discovery.test.ts`. If either side changes, update both in the same task.
- `UNITY_OPEN_MCP_BRIDGE_PORT` (env) and `-UNITY_OPEN_MCP_BRIDGE_PORT=<n>` (Unity arg) override the deterministic default — override always wins, so existing pinned-port configs keep working.
- Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` via `BridgeInstanceLock`. The file doubles as the heartbeat (`BridgeHeartbeat` rewrites it every 0.5s + on forced state transitions: compile, play-mode change, domain reload).
- Stale locks (crashed Unity) are swept on `Acquire` by PID-liveness (`Process.GetProcessById` — throws on dead pid). The MCP server is read-only on the lock and falls back to the hash when a lock's pid is dead; do not add lock mutation on the MCP side.
- **Lock retention on domain reload.** `BridgeHttpServer.Stop(releaseLock: false)` is called from `beforeAssemblyReload`; the lock is only deleted on graceful quit (`EditorApplication.quitting` → `Stop(releaseLock: true)`). This is deliberate: when the bridge assembly itself fails to compile, `[InitializeOnLoad]` never re-runs and the heartbeat stops advancing, leaving a lock whose PID is still alive (Unity is running, just stuck) but whose `heartbeatAt` is frozen. That stale-heartbeat + live-PID signature is the ONLY out-of-band signal the MCP server has to detect a dead bridge and fail fast instead of hanging on `/ping` (see `classifyInstance` in `mcp-server/src/instance-discovery.ts` and the `bridge_compile_failed` error in `live-client.ts`). Do not re-introduce a lock release on the reload path.
- New editor states go through `BridgeInstanceLock.State*` constants so both sides agree on the vocabulary (`idle`/`compiling`/`reloading`/`entering_playmode`/`playing`/`exiting_playmode`).
- `InstancePortResolver.InstancesDirOverride` is a test-only hook for sandboxing lock I/O — never set it in production code.

## UI

- `Editor/UI/UnityOpenMcpBridgeWindow.cs` is the single EditorWindow (8 tabs: Status, Tools, Gate, Activity, Batch, Settings, Extensions, Info). Add new surfaces as tab sections, not separate windows, unless there is a strong reason.
- **Tooltips over help boxes.** Internal concepts surfaced in the UI (mutating/read-only, gate modes, registry/hardcoded source, gate-run fields like Delta/Durations/Categories, activity filters, extension pack status) carry hover tooltips via `GUIContent(text, tooltip)`. Add a tooltip for any new cell value or detail label that names a bridge-internal term. Do NOT duplicate a tooltip on a widget that already has a help box immediately next to it (the Settings tab sections are the reference for help-box-prefixed fields). Shared tooltip wording lives in `const string Tooltip*` fields at the top of the window — reuse them so the same term reads identically across tabs.
- **Tools-tab pagination.** The Tools list paginates at `ToolsPageSize = 20` rows per page (mirrors Unity-Dependencies-Hunter's page selector). `BuildFilteredToolList` narrows by filter + search once per frame; `DrawToolPagination` renders numbered page buttons plus an "All" affordance when the filtered set is ≤ `ToolsShowAllThreshold` (150). When changing page-size semantics, update both constants together.
- **Tools-tab token estimates.** Each catalog row shows a per-tool `~{N} tokens` chip, the filters header shows the active-set total, and a collapsible per-group breakdown shows each group's active vs total cost. The numbers come from the generated `Editor/UI/BridgeToolTokenEstimates.cs` (per-tool `EstimateFor(name)`, group `GroupFor(name)`, K-formatting via `Format(int)`), **not** a hand-maintained table. It is regenerated from the MCP-server tool schemas by `scripts/generate-token-estimates.mjs` (import `mcp-server/src/tools/index.ts` + `tool-groups.ts` via `--experimental-strip-types`; chars/4 heuristic over the tool's MCP wire JSON). The CI drift gate (`.github/workflows/version-sync.yml` `check-token-estimates`) runs `--check` on every PR touching the tool schemas or the codegen script. When adding/removing a tool, the table is regenerated the same way — never edit `BridgeToolTokenEstimates.cs` by hand. The active total reflects the **bridge toggle policy** (per-tool enable/disable), not per-session `manage_tools` activation (which lives in the MCP server).
- Settings persist in `.unity-open-mcp/settings.json` via `BridgeProjectSettings`. Follow the existing v1 schema; extend in place.

## Verification

- C# changes: add or update the narrowest EditMode test in `Tests/Editor/`.
- Tool contract changes: update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task so schemas stay in sync.
- Gate flow changes: verify the delta math (new/resolved errors+warnings) in the integration tests.
