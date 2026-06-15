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

- `BridgeHttpServer` is localhost-only (`127.0.0.1`). Do not bind non-localhost without a security review and the token-auth work (backlog).
- All Unity API calls happen on the main thread via `MainThreadDispatcher`. Never call `UnityEditor`/`UnityEngine` Editor APIs from the listener worker thread.
- JSON is hand-rolled via `StringBuilder` (no Newtonsoft dependency in the bridge). Follow the existing `EscapeString`/`Build*Envelope` patterns; do not introduce a JSON serializer dependency.

## UI

- `Editor/UI/UnityOpenMcpBridgeWindow.cs` is the single EditorWindow (5 tabs). Add new surfaces as tab sections, not separate windows, unless there is a strong reason.
- Settings persist in `.unity-open-mcp/settings.json` via `BridgeProjectSettings`. Follow the existing v1 schema; extend in place.

## Verification

- C# changes: add or update the narrowest EditMode test in `Tests/Editor/`.
- Tool contract changes: update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task so schemas stay in sync.
- Gate flow changes: verify the delta math (new/resolved errors+warnings) in the integration tests.
