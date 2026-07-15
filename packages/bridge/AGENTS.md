# Bridge package rules

## Scope

Rules for `packages/bridge/` — Unity Editor HTTP bridge
(`com.alexeyperov.unity-open-mcp-bridge`). Root `AGENTS.md` also applies.
Owns registration, transport, gate execution, embedded domains, the
deterministic-port formula, and Editor UI. MCP owns session visibility,
routing, and schemas — [`mcp-server/AGENTS.md`](../../mcp-server/AGENTS.md).

## Package shape

- Editor-only under `Editor/`, namespace `UnityOpenMcpBridge`.
- Hard dependency on `com.alexeyperov.unity-open-mcp-verify` (do not reverse).
- EditMode tests only, under `Tests/Editor/`.

## Tool registration

1. Prefer `[BridgeToolType]` + `[BridgeTool]` discovery.
2. Legacy typed tools still need `BridgeToolClassification`
   (`KnownTools` / `DirectResponseTools` / `MutatingTools`) and a
   `BridgeHttpServer.DispatchTool` case; registry tools skip those sets.
3. `BridgeToolCatalog.Build()` unions meta-tools, registry, and `KnownTools` so
   the Tools tab matches `GET /tools`.
4. Keep `Scan()` excluding `nunit.framework` assemblies; the
   `Scan(includeTestAssemblies: true)` overload stays `internal`.
5. Declare unique `Name`, `IsMutating`, `Gate`, and `Group` (null for
   always-visible meta-tools; otherwise an exact `tool-groups.ts` id).
6. Mutating tools honor request `gate`; read-only set `Gate = Off` +
   `ReadOnlyHint = true`.

## Tool-group compiled state

Bridge reports compiled inventory only — no MCP session state.
`GET /tools` returns KnownTools ∪ registry plus `GroupToTools()`.
`[BridgeTool(Group)]` ids must match `tool-groups.ts` exactly.

## Embedded domain tools

1. Shipped domains live under `Editor/TypedTools/Extensions/<Domain>/`, not
   `packages/extensions/`. See
   [Embedded domain model](../../docs/contributing/extensions.md#embedded-domain-model).
2. Optional deps: self-contained sub-asmdef (`versionDefines` + matching
   `defineConstraints` + package ref). Same `UNITY_OPEN_MCP_EXT_<DOMAIN>` on
   source and gated tests.
3. No runtime reflection probing (except documented version-split APIs such as
   Cinemachine). No manual Player Settings writes for `UNITY_OPEN_MCP_EXT_*`.
4. Follow the Navigation template and the
   [end-to-end domain checklist](../../docs/contributing/extensions.md#end-to-end-domain-checklist).
5. Name tool-group ID, tool prefix, and source/skill folder separately when
   they differ (e.g. `input-system` / `inputsystem` / `InputSystem`).

## Gate policy

- Every mutating path goes through `GatePolicy.Execute`.
- `paths_hint` is mandatory — no whole-project fallback.
- Valid request `gate` wins; omitted/malformed/unknown →
  `BridgeGateDefaultPolicy`. `[BridgeTool].Gate` is catalog metadata only.
  Keep [Gate policy](../../docs/api/bridge-http.md#gate-policy) aligned.

## Transport and JSON

- Default bind `127.0.0.1`. Non-loopback requires `authMode: "required"` via
  `BridgeBindAddress.Decide` ([Remote bind](#remote-bind-and-authentication)).
- Unity APIs only on the main thread (`MainThreadDispatcher`).
- Hand-rolled JSON via shared `BridgeJson` appenders only — no serializer
  dependency, no third escape helper. `BridgeJsonTests` pins the contract.
  Extension sub-assemblies keep their own escape helper (`BridgeJson` is
  internal).

## Auth, deny list, remote bind, audit

- Mint lock `authToken` on `Acquire`; keep MCP `InstanceLock` /
  `resolveAuthToken` aligned. `authMode` unknown → fail closed as `required`.
  Compare with `EqualsConstantTime` only.
- `execute_csharp` / `execute_menu` run `BridgeDenyList` before mutation.
  Bypass needs `gate: "off"` + `confirm_bypass: true`. `File/Quit` is
  hard-blocked. Denials: `denied_by_policy` / `menu_blocked`.
- No TLS in-bridge — terminate upstream
  ([Authentication and bind mode](../../docs/api/bridge-http.md#authentication-and-bind-mode)).
- Opt-in audit JSONL is best-effort and must never break dispatch.
  `AuditDirOverride` is test-only.

## Multi-instance port and discovery

This file owns the three-way deterministic-port contract:

1. Normalize: `\` → `/`, trim trailing `/`, do not lowercase.
2. SHA-256 UTF-8; first 8 bytes big-endian `uint`; port =
   `20000 + (value % 10000)`.
3. Keep byte-identical:
   `InstancePortResolver.ComputePort`,
   `mcp-server/src/instance-discovery.ts` `computePort`,
   `hub/src-tauri/src/config/bridge_port.rs` `compute_port`.
4. Same-task update all three implementations + fixtures
   (`InstancePortResolverTests.cs`, `instance-discovery.test.ts`,
   `bridge_port.rs` `#[cfg(test)]`) when hashing, normalization, range, or
   fallback changes.
5. `UNITY_OPEN_MCP_BRIDGE_PORT` env/arg always wins.
6. Bridge writes
   `~/.unity-open-mcp/instances/<sha256(projectPath)>.json`; MCP is
   read-only. Retain lock across domain reload
   (`ForceStopListener(releaseLock: false)`); delete only on graceful quit.
   Skip listener in worker/batch. Never kill foreign PIDs on port recovery.
   See [Contributor troubleshooting](../../docs/troubleshooting-contributors.md#stale-heartbeat-vs-live-pid).
7. States use `BridgeInstanceLock.State*`. `InstancesDirOverride` is test-only.

## UI

- Single EditorWindow with peer tabs; new surfaces are tab sections. Tab enum
  changes update `MigrateSelectedTab` + `BridgeWindowTabIaContractTests`.
- Tooltip (`GUIContent`) for: mutability/read-only, gate mode/run fields,
  registry/legacy source, activity filters/status, compiled/activation state,
  extension-pack status. Reuse `Tooltip*`; extend contract tests for new
  categories.
- Pagination: change `ToolsPageSize` and `ToolsShowAllThreshold` together.
- Token estimates: generated `BridgeToolTokenEstimates.cs` via
  `node scripts/generate-token-estimates.mjs` — never hand-edit. Totals reflect
  bridge toggles, not MCP session activation.
- Bulk enable/disable: `BridgeToolTogglePolicy` on the filtered view.
- Settings: `.unity-open-mcp/settings.json` via `BridgeProjectSettings`;
  extend v1 in place.

## Verification

1. C# → narrowest `Tests/Editor/` EditMode test.
2. Tool contracts → MCP schema/handler + domain/tool array + `ALL_TOOLS`;
   owning page under `docs/api/mcp-tools.md`; root/domain skills when workflow
   changes.
3. Schema/catalog/group changes → regenerate token estimates.
4. Routed tools → S0 + narrowest S1–S5
   (`docs/troubleshooting-contributors.md#mcp-test-suite-catalog`).
5. Instance IDs →
   [convention](../../docs/code-conventions.md#instance-ids) via `InstanceId`
   helpers (no `int`/JSON-number assumptions).
6. Gate changes → verify delta math in integration tests.
