# Bridge package rules

## Scope

Rules for `packages/bridge/` — the Unity Editor HTTP bridge (`com.alexeyperov.unity-open-mcp-bridge`). Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- Editor-only Unity package. All code lives under `Editor/` and uses the `UnityOpenMcpBridge` namespace.
- Hard dependency on `com.alexeyperov.unity-open-mcp-verify` — the gate flow calls into verify for checkpoint/validate/delta. Do not break this dependency direction.
- `Tests/Editor/` holds EditMode tests. No playmode tests.

## Tool registration

- Registry tools are discovered via `[BridgeToolType]` on a class + `[BridgeTool]` on methods (`Editor/Bridge/Attributes/`). The HTTP server (`BridgeHttpServer.cs`) also has a hardcoded `KnownTools` set for legacy tools — prefer the registry path for new tools.
- Every new tool must declare:
  - A unique `Name` (the MCP tool name, `unity_open_mcp_*` / `unity_agent_*`).
  - `IsMutating` — true if the tool changes Unity state.
  - `Gate` — the default gate mode for mutating tools (`Enforce` / `Warn` / `Off`).
- Mutating tools must accept and honor the request-level `gate` value. Read-only tools set `Gate = Off` and `ReadOnlyHint = true`.
- When adding/removing/renaming a tool, update the hardcoded `KnownTools` / `DirectResponseTools` / `MutatingTools` sets in `BridgeHttpServer.cs` **only if** the tool is not registry-discovered. Registry tools are picked up automatically.

## Gate policy

- The gate flow (checkpoint → mutate → validate → delta) is the bridge's core safety contract. Do not add a mutating dispatch path that bypasses `GatePolicy.Execute`.
- `paths_hint` is mandatory for mutating tool calls — there is no whole-project fallback. Do not add one.
- Gate precedence: request `gate` → project default (`BridgeGateDefaultPolicy`) → tool attribute default. Do not reorder this without updating `docs/architecture.md` and the bridge-http docs.

## Transport

- `BridgeHttpServer` is localhost-only (`127.0.0.1`). Do not bind non-localhost without a security review — token auth (M14) is loopback defense, not a substitute for binding.
- All Unity API calls happen on the main thread via `MainThreadDispatcher`. Never call `UnityEditor`/`UnityEngine` Editor APIs from the listener worker thread.
- JSON is hand-rolled via `StringBuilder` (no Newtonsoft dependency in the bridge). Follow the existing `EscapeString`/`Build*Envelope` patterns; do not introduce a JSON serializer dependency.

## Auth (M14)

- A 256-bit per-session bearer token (`BridgeAuthToken.Generate`) is minted into the instance lock on every `Acquire` and mirrored as `authToken` in the lock JSON. The TS-side `InstanceLock` interface (`mcp-server/src/instance-discovery.ts`) must carry the same field; `resolveAuthToken` reads it.
- Enforcement is opt-in via `authMode` in `.unity-open-mcp/settings.json` (`BridgeAuthPolicy`: `"none"` default | `"required"`). The token is always minted regardless, so flipping to `required` needs no restart.
- The HTTP gate is `BridgeAuthCheck.IsAuthorized` (pure, unit-tested) called from `BridgeHttpServer.CheckAuth` before routing. No endpoint is exempt. Unknown `authMode` values fail closed (`required`).
- Token comparison is constant-time (`BridgeAuthToken.EqualsConstantTime`). Do not replace it with `==`/`string.Equals`.

## Multi-instance port + discovery (M13)

- The bridge port is **deterministic per project**: `20000 + (sha256(projectPath) % 10000)`, implemented in `InstancePortResolver`. The formula must stay byte-for-byte identical to the MCP server mirror at `mcp-server/src/instance-discovery.ts` (`computePort`); cross-side consistency is pinned by `InstancePortResolverTests.cs` and `instance-discovery.test.ts`. If either side changes, update both in the same task.
- `UNITY_OPEN_MCP_BRIDGE_PORT` (env) and `-UNITY_OPEN_MCP_BRIDGE_PORT=<n>` (Unity arg) override the deterministic default — override always wins, so existing pinned-port configs keep working.
- Each running bridge writes a lock file at `~/.unity-agent/instances/<sha256(projectPath)>.json` via `BridgeInstanceLock`. The file doubles as the heartbeat (`BridgeHeartbeat` rewrites it every 0.5s + on forced state transitions: compile, play-mode change, domain reload).
- Stale locks (crashed Unity) are swept on `Acquire` by PID-liveness (`Process.GetProcessById` — throws on dead pid). The MCP server is read-only on the lock and falls back to the hash when a lock's pid is dead; do not add lock mutation on the MCP side.
- **Lock retention on domain reload.** `BridgeHttpServer.Stop(releaseLock: false)` is called from `beforeAssemblyReload`; the lock is only deleted on graceful quit (`EditorApplication.quitting` → `Stop(releaseLock: true)`). This is deliberate: when the bridge assembly itself fails to compile, `[InitializeOnLoad]` never re-runs and the heartbeat stops advancing, leaving a lock whose PID is still alive (Unity is running, just stuck) but whose `heartbeatAt` is frozen. That stale-heartbeat + live-PID signature is the ONLY out-of-band signal the MCP server has to detect a dead bridge and fail fast instead of hanging on `/ping` (see `classifyInstance` in `mcp-server/src/instance-discovery.ts` and the `bridge_compile_failed` error in `live-client.ts`). Do not re-introduce a lock release on the reload path.
- New editor states go through `BridgeInstanceLock.State*` constants so both sides agree on the vocabulary (`idle`/`compiling`/`reloading`/`entering_playmode`/`playing`/`exiting_playmode`).
- `InstancePortResolver.InstancesDirOverride` is a test-only hook for sandboxing lock I/O — never set it in production code.

## UI

- `Editor/UI/UnityOpenMcpBridgeWindow.cs` is the single EditorWindow (5 tabs). Add new surfaces as tab sections, not separate windows, unless there is a strong reason.
- Settings persist in `.unity-open-mcp/settings.json` via `BridgeProjectSettings`. Follow the existing v1 schema; extend in place.

## Verification

- C# changes: add or update the narrowest EditMode test in `Tests/Editor/`.
- Tool contract changes: update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task so schemas stay in sync.
- Gate flow changes: verify the delta math (new/resolved errors+warnings) in the integration tests.
