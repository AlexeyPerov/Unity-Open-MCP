# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`. Default bind is `127.0.0.1` (loopback); remote bind (`0.0.0.0`) is opt-in and requires auth — see [Remote bind (M14)](#remote-bind-m14-t54).

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/instance` | `GET` | Live instance lock + heartbeat snapshot (M13). |
| `/events` | `GET` | SSE stream of console logs + editor-state transitions (M13 T4.4). |
| `/events/poll` | `GET` | Drain the event queue as a single JSON envelope (M13 T4.4). |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1` (loopback). Configurable via `bindAddress` in `.unity-open-mcp/settings.json` — see [Remote bind](#remote-bind-m14-t54).
- Default port: **deterministic per project** — `20000 + (sha256(projectPath) % 10000)`. Two Unity projects running bridges simultaneously get two distinct ports with no configuration.
- Port overrides (both win over the deterministic default):
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- The hash formula takes the first 8 bytes of SHA256 of the normalized project path (forward slashes, trailing slash trimmed), interprets them as a big-endian 64-bit unsigned integer, and applies `% 10000`. Both the bridge (`InstancePortResolver.ComputePort`) and the MCP server (`mcp-server/src/instance-discovery.ts`) implement it identically.

## Multi-instance discovery (M13)

Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json`. The file doubles as the heartbeat — it is rewritten every 0.5s and on every forced editor state transition (compile start, play-mode change, domain reload).

Lock / heartbeat fields:
- `pid`, `port`, `projectPath`, `projectHash`
- `authToken` — per-session bearer token (M14); see [Authentication](#authentication-m14)
- `startedAt`, `updatedAt`, `heartbeatAt` (ISO-8601 UTC)
- `state` — `idle` | `compiling` | `reloading` | `entering_playmode` | `playing` | `exiting_playmode`
- `isPlaying`, `isCompiling`
- `bridgeVersion`, `unityVersion`

The MCP server reads this file (no HTTP round-trip needed) to pick the right bridge port per project; stale locks whose `pid` is no longer alive are ignored and cleaned up by the next bridge that starts. `GET /instance` returns the same JSON the bridge just wrote, for clients that want to verify the live bridge against the on-disk lock.

## Authentication (M14)

The bridge mints a 256-bit per-session bearer token into the instance lock (`authToken` field above) on every start. Whether the HTTP layer *enforces* it is controlled by `authMode` in `.unity-open-mcp/settings.json`:

| `authMode` | Behavior |
|---|---|
| `none` (default) | Every request is accepted. The token is still minted into the lock so flipping to `required` needs no restart. |
| `required` | Every request must carry `Authorization: Bearer <token>` matching the live instance's token. Mismatched/missing → `401 {"error":{"code":"unauthorized", ...}}`. |

All endpoints are gated equally — there are no exempt paths. The MCP server auto-discovers the token from the lock file (see [Manual setup](../manual-setup.md#authentication-m14)), so no client-side configuration is needed; a project can be flipped from `none` to `required` purely on the bridge side.

The comparison is constant-time and unknown `authMode` values fail closed (treated as `required`), so a corrupt settings file cannot silently disable auth. `required` is **mandatory** for [remote bind](#remote-bind-m14-t54) and recommended on shared machines where another local process might otherwise reach the listener.

## Remote bind (M14 T5.4)

The bridge binds loopback (`127.0.0.1`) by default. Remote bind (`0.0.0.0`) — for a shared dev machine, a CI runner accessed over the network, or a remote pair-programming setup — is opt-in via `bindAddress` in `.unity-open-mcp/settings.json` and is **refused at listener start unless `authMode` is `required`**:

```json
{
  "bindAddress": "0.0.0.0",
  "authMode": "required"
}
```

The listener checks the pair via `BridgeBindAddress.Decide` before opening the socket, so a misconfigured project fails fast with an actionable log line instead of exposing an unauthenticated listener. Unknown `bindAddress` values coerce to `127.0.0.1`. The effective bind is logged on every start (`Listening on http://0.0.0.0:<port>/ (remote — authMode required)`).

### Threat model

- **Loopback (default).** The trust boundary is the local OS account. Any process owned by the same user (or with loopback access) can reach the bridge. `authMode: "none"` is acceptable here; `required` is extra defense for shared dev machines.
- **Remote (`0.0.0.0`).** The bridge is reachable from every host that can route to this machine — LAN, VPN, and anything routable on the interface. Token auth is mandatory because the network is untrusted. **The bearer token is sent in cleartext over HTTP** — the bridge does not terminate TLS, so an attacker on the path could sniff it. Treat remote bind as: "convenient for a trusted LAN/VPN behind a reverse proxy or ssh tunnel that provides TLS." For anything stronger, terminate TLS upstream and do not expose the bridge directly. The token also grants full editor control (any tool call), so compromise is equivalent to arbitrary code execution in the editor.
- **TLS.** The bridge itself does not support TLS and there are no plans to add it. Use a reverse proxy (nginx, Caddy) or an `ssh -L` tunnel for transport encryption. This keeps the bridge dependency-free.

## Power-tool deny lists (M14 T5.2 / T5.3)

`execute_csharp` and `execute_menu` can do anything — quit the editor, delete assets, build the player. The gate verifies *new project errors* after a mutation, but several destructive ops produce no verify signal (the editor just closes) or are themselves the threat (bulk asset delete). The deny heuristic refuses these **before** the mutation runs.

### Built-in defaults

| Tool | Blocked patterns (regex, case-sensitive) |
|---|---|
| `execute_csharp` | `EditorApplication\.Exit`, `Application\.Quit`, `AssetDatabase\.DeleteAsset`, `BuildPipeline\.BuildPlayer`, `Directory\.Delete\s*\([^)]*Assets` |
| `execute_menu` | `^File/Quit# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`. Default bind is `127.0.0.1` (loopback); remote bind (`0.0.0.0`) is opt-in and requires auth — see [Remote bind (M14)](#remote-bind-m14-t54).

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/instance` | `GET` | Live instance lock + heartbeat snapshot (M13). |
| `/events` | `GET` | SSE stream of console logs + editor-state transitions (M13 T4.4). |
| `/events/poll` | `GET` | Drain the event queue as a single JSON envelope (M13 T4.4). |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1` (loopback). Configurable via `bindAddress` in `.unity-open-mcp/settings.json` — see [Remote bind](#remote-bind-m14-t54).
- Default port: **deterministic per project** — `20000 + (sha256(projectPath) % 10000)`. Two Unity projects running bridges simultaneously get two distinct ports with no configuration.
- Port overrides (both win over the deterministic default):
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- The hash formula takes the first 8 bytes of SHA256 of the normalized project path (forward slashes, trailing slash trimmed), interprets them as a big-endian 64-bit unsigned integer, and applies `% 10000`. Both the bridge (`InstancePortResolver.ComputePort`) and the MCP server (`mcp-server/src/instance-discovery.ts`) implement it identically.

## Multi-instance discovery (M13)

Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json`. The file doubles as the heartbeat — it is rewritten every 0.5s and on every forced editor state transition (compile start, play-mode change, domain reload).

Lock / heartbeat fields:
- `pid`, `port`, `projectPath`, `projectHash`
- `authToken` — per-session bearer token (M14); see [Authentication](#authentication-m14)
- `startedAt`, `updatedAt`, `heartbeatAt` (ISO-8601 UTC)
- `state` — `idle` | `compiling` | `reloading` | `entering_playmode` | `playing` | `exiting_playmode`
- `isPlaying`, `isCompiling`
- `bridgeVersion`, `unityVersion`

The MCP server reads this file (no HTTP round-trip needed) to pick the right bridge port per project; stale locks whose `pid` is no longer alive are ignored and cleaned up by the next bridge that starts. `GET /instance` returns the same JSON the bridge just wrote, for clients that want to verify the live bridge against the on-disk lock.

## Authentication (M14)

The bridge mints a 256-bit per-session bearer token into the instance lock (`authToken` field above) on every start. Whether the HTTP layer *enforces* it is controlled by `authMode` in `.unity-open-mcp/settings.json`:

| `authMode` | Behavior |
|---|---|
| `none` (default) | Every request is accepted. The token is still minted into the lock so flipping to `required` needs no restart. |
| `required` | Every request must carry `Authorization: Bearer <token>` matching the live instance's token. Mismatched/missing → `401 {"error":{"code":"unauthorized", ...}}`. |

, `^File/Exit# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`. Default bind is `127.0.0.1` (loopback); remote bind (`0.0.0.0`) is opt-in and requires auth — see [Remote bind (M14)](#remote-bind-m14-t54).

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/instance` | `GET` | Live instance lock + heartbeat snapshot (M13). |
| `/events` | `GET` | SSE stream of console logs + editor-state transitions (M13 T4.4). |
| `/events/poll` | `GET` | Drain the event queue as a single JSON envelope (M13 T4.4). |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1` (loopback). Configurable via `bindAddress` in `.unity-open-mcp/settings.json` — see [Remote bind](#remote-bind-m14-t54).
- Default port: **deterministic per project** — `20000 + (sha256(projectPath) % 10000)`. Two Unity projects running bridges simultaneously get two distinct ports with no configuration.
- Port overrides (both win over the deterministic default):
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- The hash formula takes the first 8 bytes of SHA256 of the normalized project path (forward slashes, trailing slash trimmed), interprets them as a big-endian 64-bit unsigned integer, and applies `% 10000`. Both the bridge (`InstancePortResolver.ComputePort`) and the MCP server (`mcp-server/src/instance-discovery.ts`) implement it identically.

## Multi-instance discovery (M13)

Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json`. The file doubles as the heartbeat — it is rewritten every 0.5s and on every forced editor state transition (compile start, play-mode change, domain reload).

Lock / heartbeat fields:
- `pid`, `port`, `projectPath`, `projectHash`
- `authToken` — per-session bearer token (M14); see [Authentication](#authentication-m14)
- `startedAt`, `updatedAt`, `heartbeatAt` (ISO-8601 UTC)
- `state` — `idle` | `compiling` | `reloading` | `entering_playmode` | `playing` | `exiting_playmode`
- `isPlaying`, `isCompiling`
- `bridgeVersion`, `unityVersion`

The MCP server reads this file (no HTTP round-trip needed) to pick the right bridge port per project; stale locks whose `pid` is no longer alive are ignored and cleaned up by the next bridge that starts. `GET /instance` returns the same JSON the bridge just wrote, for clients that want to verify the live bridge against the on-disk lock.

## Authentication (M14)

The bridge mints a 256-bit per-session bearer token into the instance lock (`authToken` field above) on every start. Whether the HTTP layer *enforces* it is controlled by `authMode` in `.unity-open-mcp/settings.json`:

| `authMode` | Behavior |
|---|---|
| `none` (default) | Every request is accepted. The token is still minted into the lock so flipping to `required` needs no restart. |
| `required` | Every request must carry `Authorization: Bearer <token>` matching the live instance's token. Mismatched/missing → `401 {"error":{"code":"unauthorized", ...}}`. |

, `^Assets/Reimport All# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`. Default bind is `127.0.0.1` (loopback); remote bind (`0.0.0.0`) is opt-in and requires auth — see [Remote bind (M14)](#remote-bind-m14-t54).

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/instance` | `GET` | Live instance lock + heartbeat snapshot (M13). |
| `/events` | `GET` | SSE stream of console logs + editor-state transitions (M13 T4.4). |
| `/events/poll` | `GET` | Drain the event queue as a single JSON envelope (M13 T4.4). |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1` (loopback). Configurable via `bindAddress` in `.unity-open-mcp/settings.json` — see [Remote bind](#remote-bind-m14-t54).
- Default port: **deterministic per project** — `20000 + (sha256(projectPath) % 10000)`. Two Unity projects running bridges simultaneously get two distinct ports with no configuration.
- Port overrides (both win over the deterministic default):
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- The hash formula takes the first 8 bytes of SHA256 of the normalized project path (forward slashes, trailing slash trimmed), interprets them as a big-endian 64-bit unsigned integer, and applies `% 10000`. Both the bridge (`InstancePortResolver.ComputePort`) and the MCP server (`mcp-server/src/instance-discovery.ts`) implement it identically.

## Multi-instance discovery (M13)

Each running bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json`. The file doubles as the heartbeat — it is rewritten every 0.5s and on every forced editor state transition (compile start, play-mode change, domain reload).

Lock / heartbeat fields:
- `pid`, `port`, `projectPath`, `projectHash`
- `authToken` — per-session bearer token (M14); see [Authentication](#authentication-m14)
- `startedAt`, `updatedAt`, `heartbeatAt` (ISO-8601 UTC)
- `state` — `idle` | `compiling` | `reloading` | `entering_playmode` | `playing` | `exiting_playmode`
- `isPlaying`, `isCompiling`
- `bridgeVersion`, `unityVersion`

The MCP server reads this file (no HTTP round-trip needed) to pick the right bridge port per project; stale locks whose `pid` is no longer alive are ignored and cleaned up by the next bridge that starts. `GET /instance` returns the same JSON the bridge just wrote, for clients that want to verify the live bridge against the on-disk lock.

## Authentication (M14)

The bridge mints a 256-bit per-session bearer token into the instance lock (`authToken` field above) on every start. Whether the HTTP layer *enforces* it is controlled by `authMode` in `.unity-open-mcp/settings.json`:

| `authMode` | Behavior |
|---|---|
| `none` (default) | Every request is accepted. The token is still minted into the lock so flipping to `required` needs no restart. |
| `required` | Every request must carry `Authorization: Bearer <token>` matching the live instance's token. Mismatched/missing → `401 {"error":{"code":"unauthorized", ...}}`. |

 |

`execute_menu` additionally hard-blocks `File/Quit` (not configurable away) as a last-resort guard.

### Configuring

Override the defaults in `.unity-open-mcp/settings.json`. A non-empty array replaces the defaults for that tool; `null`/unset/empty array ⇒ built-in defaults.

```json
{
  "csharpDenyPatterns": ["DangerousApi\\.Foo", "ClassifiedStuff"],
  "menuDenyPatterns": ["^Edit/Clear$"]
}
```

Invalid regexes and whitespace-only entries are dropped at settings-load time (logged once to the console). The Settings tab shows the active count per tool. To run an otherwise-blocked op without disabling the list project-wide, use the per-request bypass below.

### Bypass (audited)

A request bypasses the deny heuristic only when it passes **both**:

- `gate: "off"` — opt out of post-mutation verification, AND
- `confirm_bypass: true` — explicit acknowledgement.

A single flag is not enough, so a careless agent can't talk its way past the list. Bypasses and refusals are recorded in the [audit log](#on-disk-audit-log-m14-t55) (when enabled) and always in the in-memory activity log with `gate.mode = off`. A denied request returns:

```json
{
  "mutation": {
    "success": false,
    "output": null,
    "error": {
      "code": "denied_by_policy",
      "message": "execute_csharp matched the configured deny pattern 'EditorApplication.Exit'. ... Suggestion: ... Matched pattern: EditorApplication.Exit."
    }
  },
  "gate": { "mode": "enforce", "skipped": true, "validation": null, "delta": null },
  "agentNextSteps": [ "..." ]
}
```

(`execute_menu` returns the same shape with `code: "menu_blocked"`.)

## On-disk audit log (M14 T5.5)

The in-memory activity log (`BridgeActivityLog`, capacity 100) is session-scoped and cleared on domain reload. For security-sensitive contexts, opt in to a persistent on-disk audit log via `auditLogEnabled` in `.unity-open-mcp/settings.json` (Settings tab → On-disk audit log).

When enabled, every gate mutation (pass / fail / warn) and every deny-list refusal/bypass is appended as one JSON-lines record to a rolling file:

```
~/.unity-open-mcp/audit/audit-<projectHash>.jsonl
```

Rotation: when the active file exceeds 5 MiB it is renamed to `.1`, `.1` → `.2`, …, and the oldest of 5 retained files is dropped. Records survive domain reload and editor restart (the file is reopened on each write, not held open).

Record shape (one JSON object per line):

```json
{
  "ts": "2026-06-17T12:00:00.0000000Z",
  "projectHash": "a1b2...",
  "tool": "unity_open_mcp_execute_csharp",
  "gate": "enforce",
  "pathsHint": null,
  "outcome": "denied",
  "gateRan": false,
  "newErrors": 0,
  "newWarnings": 0,
  "resolvedErrors": 0,
  "resolvedWarnings": 0,
  "checkpointId": null,
  "totalGateMs": 0,
  "mutationError": "denied_by_policy",
  "bypassedDenyList": false,
  "deniedPattern": "EditorApplication.Exit"
}
```

`outcome` is one of `passed` | `warned` | `failed` | `skipped` | `denied`. `bypassedDenyList` is `true` when the request used the `gate: "off"` + `confirm_bypass: true` escape hatch. `deniedPattern` carries the matched regex when the deny heuristic refused the request. Best-effort: an I/O failure is logged once and the record dropped — audit logging never breaks the dispatch path.

## `/ping` response

Success payload fields:
- `connected`
- `projectPath`
- `unityVersion`
- `bridgeVersion`
- `mode`
- `compiling`
- `isPlaying`

If the bridge session is not initialized yet, endpoint may return `503` with a fallback payload where `connected` is `false`.

## `/instance` response (M13)

Returns the live lock JSON described under [Multi-instance discovery](#multi-instance-discovery-m13). Returns `503` with `{"error":{"code":"no_instance", ...}}` when the bridge has not acquired a lock yet (listener started but lock write failed — e.g. the `~/.unity-open-mcp/instances/` directory could not be created).

## `/events` SSE & `/events/poll` (M13 T4.4)

Streaming notification channel for console logs and editor-state transitions. Backed by an in-memory ring buffer (`BridgeEventSource`, 1024-event capacity) fed by `Application.logMessageReceived` and editor callbacks (compile start/stop, play-mode changes, before-assembly-reload). Two drain surfaces:

### `GET /events` — Server-Sent Events

Long-lived connection that emits incremental events. Query params:

| Param | Default | Purpose |
|---|---|---|
| `subscriber` | (minted) | Opaque id so a reconnecting client keeps its cursor and doesn't replay events it already saw. |
| `max_per_poll` | `100` | Cap events drained per tick. Bounds burst replay after a reconnect. |

Wire format: standard SSE — one `event:` line, one or more `data:` lines (long payloads like stacks are split across `data:` lines and reassembled by the client), blank-line separator.

Control events:
- `ready` — emitted immediately on connect with `{"subscriber":"<id>"}` so the client knows the stream is live before the first event.
- `missed` — emitted when the ring evicted events this subscriber never saw: `{"missed":N}`. Never silent.
- `close` — emitted before the connection closes (10-minute timeout or bridge shutdown): `{"reason":"timeout_or_shutdown"}`.

Event types:
- `log` — `{"seq":N,"ts":"...","type":"log","logType":"error|warning|log|exception|assert","message":"...","stack":"..."}`. `stack` omitted when empty.
- `editor_state` — `{"seq":N,"ts":"...","type":"editor_state","state":"idle|compiling|reloading|entering_playmode|playing|exiting_playmode","isCompiling":bool,"isPlaying":bool}`. State vocabulary matches the heartbeat file (`BridgeInstanceLock.State*`).

The connection lives on a ThreadPool worker thread for up to 10 minutes; the listener's `finally` clause skips the automatic `Response.Close()` for SSE so the stream isn't aborted when `HandleRequest` returns.

### `GET /events/poll` — single JSON drain

Returns the events buffered since the caller's last poll as one JSON envelope. Use this when the client is request/response only (the MCP server does, because it lives behind a stdio transport). Query params:

| Param | Default | Purpose |
|---|---|---|
| `subscriber` | (minted) | Opaque id; reused across polls to keep a cursor. |
| `max_events` | `100` | Cap events returned; extras stay buffered for the next poll. |

Response shape:

```json
{
  "subscriberId": "abc123",
  "events": [ /* BridgeEvent objects, same shape as SSE `data` payloads */ ],
  "count": 3,
  "missed": 0,
  "totalEmitted": 42
}
```

`missed > 0` means the ring evicted events between this subscriber's cursor and the oldest entry still in the buffer — the loss is reported, never silent.

## `/tools/{toolName}` behavior

### Dispatch flow

1. Validate HTTP method (`POST` required).
2. Check tool exists in built-in tool set or `BridgeToolRegistry`.
3. Execute on Unity main thread via dispatcher.
4. Return JSON payload envelope.

### Common tool-level error codes

- `tool_not_found`
- `method_not_allowed`
- `paths_hint_required` (mutating tool without scope hints)
- `scene_dirty` (disruptive op refused because a loaded scene has unsaved changes — see [Scene dirty guard](#active-scene-dirty-guard))
- `denied_by_policy` (`execute_csharp` refused by the deny heuristic — see [Power-tool deny lists](#power-tool-deny-lists-m14-t52--t53))
- `menu_blocked` (`execute_menu` refused by the deny heuristic or the hard blocklist — see [Power-tool deny lists](#power-tool-deny-lists-m14-t52--t53))
- `timeout`
- `execution_error`
- `bridge_internal_error`

### Lifecycle policy

Every dispatched tool declares a **lifecycle policy** that tells the dispatcher how long to wait before returning and whether the op may survive a domain reload. The policy is surfaced in the gate response envelope as `lifecycle` (snake_case token) and `settleMs` (milliseconds the bridge blocked waiting for the editor to finish compiling).

| Policy | Token | Behaviour |
|---|---|---|
| `None` | `none` | Read-only, returns immediately. No settle wait. |
| `EditorSettle` | `editor_settle` | Mutating; the bridge waits for asset refresh/serialization to finish (cap ~5s) before returning. |
| `RestartThenSettle` | `restart_then_settle` | Mutating; may trigger a domain reload. The bridge blocks until the editor finishes compiling (cap ~60s) so the caller never observes a half-compiled state. The HTTP listener survives the reload, so a follow-up `/ping` reflects the post-reload state automatically. |
| `CustomConfirmation` | `custom_confirmation` | Async; returns immediately and the result arrives via an external completion signal (e.g. `run_tests` file-handoff poll on the MCP server). |

Classification lives in two places that never drift: the `[BridgeTool(Lifecycle = ...)]` attribute for registry-discovered tools, and `ToolLifecycle.Map` for the legacy hardcoded meta-tools. Unknown tools default to `None` (read-only safe default).

Tool → policy assignment:

| Policy | Tools |
|---|---|
| `none` | `ping`, `find_members`, `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`, `read_asset`, `search_assets`, `list_assets`, `editor_status`, `read_console`, `screenshot`, `profiler_capture`, `profiler_memory`, `profiler_rendering`, `spatial_query` |
| `editor_settle` | `apply_fix`, `reserialize` |
| `restart_then_settle` | `execute_csharp`, `invoke_method`, `execute_menu`, `compile_check` |
| `custom_confirmation` | `run_tests` |

### Active-scene dirty guard

Before any `restart_then_settle` op, the bridge preflights the loaded scenes via `EditorSceneManager.GetSceneManagerSetup()`. If any scene has unsaved changes (`isDirty`), the call is **refused** so Unity's native save modal never interrupts the flow:

```json
{
  "mutation": {
    "success": false,
    "output": null,
    "error": {
      "code": "scene_dirty",
      "message": "Active scene has unsaved changes (dirty): Assets/Scenes/Main.unity. ..."
    }
  },
  "gate": { "mode": "enforce", "skipped": true, "validation": null, "delta": null },
  "lifecycle": "restart_then_settle",
  "settleMs": 0,
  "dirtyScenes": ["Assets/Scenes/Main.unity"],
  "agentNextSteps": [
    "Save or discard changes to the dirty scene(s) before retrying: Assets/Scenes/Main.unity.",
    "To save via the bridge: unity_open_mcp_execute_csharp with EditorSceneManager.SaveScene(...).",
    "To discard: EditorSceneManager.RestoreSavedSceneState(), or retry with ignore_scene_dirty: true."
  ]
}
```

Recover by saving the scene first, discarding, or passing `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` to proceed and accept the risk of a native save prompt. The guard is **not** applied to `apply_fix` / `reserialize` (they never trigger the native save modal).

### Gate envelope behavior

Mutating tools return a gate-aware envelope with:
- `mutation` (success/output/error)
- `gate` (mode/checkpoint/validation/delta/skipped flags)
- `lifecycle` — the resolved lifecycle policy token (see [Lifecycle policy](#lifecycle-policy))
- `settleMs` — milliseconds the bridge blocked waiting for the editor to finish compiling (0 when no settle wait ran)
- `dirtyScenes` — present (array of scene paths) only when the active-scene dirty guard refused the op; `null` otherwise
- `agentNextSteps` (actionable guidance)

Non-mutating direct-response tools return tool payloads directly (or direct error JSON); they do not carry the gate/lifecycle envelope.

## Known built-in tool names

- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`
- `unity_open_mcp_find_members`
- `unity_open_mcp_validate_edit`
- `unity_open_mcp_checkpoint_create`
- `unity_open_mcp_delta`
- `unity_open_mcp_find_references`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_apply_fix`
- `unity_open_mcp_reserialize`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`
- `unity_senses_run_tests` (test runner; requires Unity Test Framework)
- `unity_senses_screenshot` (scene/game/isolated screenshots)
- `unity_senses_read_console` (console log reader)
- `unity_senses_profiler_capture` (profiler frame hierarchy)
- `unity_senses_profiler_memory` (memory allocator stats)
- `unity_senses_profiler_rendering` (rendering environment stats)
- `unity_senses_spatial_query` (physics raycast / overlap / bounds / ground / nearest)

Typed tools discovered via `BridgeToolRegistry` are also callable through `/tools/{toolName}`.

### Test runner (`unity_senses_run_tests`)

Direct-response tool that starts an async Unity test run and returns `{ "status": "started", "runId": "...", "mode": "EditMode|PlayMode" }`.

- Results are written to `~/.unity-open-mcp/test-results-<runId>.json` when the run completes.
- PlayMode runs survive domain reload: a pending marker file (`test-pending-<runId>.json`) is written before the run, and `TestRunnerState` re-attaches callbacks after reload.
- The MCP server polls the results file and returns structured pass/fail counts to the caller.
- Lives in a separate assembly (`com.alexeyperov.unity-open-mcp-bridge.TestRunner.Editor`) that is conditionally compiled only when `com.unity.test-framework` is installed.

### Screenshots (`unity_senses_screenshot`)

Direct-response tool that captures a PNG screenshot and returns the saved file path.

- `view: "scene"` — renders the last active Scene view camera.
- `view: "game"` — renders the main game camera (or first camera found).
- `view: "isolated"` — renders a single GameObject in a 2×2 composite (Front/Right/Back/Top) with layer culling, configurable background (transparent/solid/skybox), and guaranteed state restore.
- Output is written to `~/.unity-open-mcp/screenshots/screenshot-<view>-<timestamp>.png`.
- Parameters: `view`, `width` (default 1280), `height` (default 720), `object_path` (required for isolated), `background` (default skybox).

### Console reader (`unity_senses_read_console`)

Direct-response tool that reads Unity console entries via reflection on internal `LogEntries`.

- Returns structured entries: `{ type, message, stack }` with summary counts.
- Filter by `type`: `error` | `warning` | `log` | `all` (default).
- `include_unity_frames` (default false) controls whether UnityEngine/UnityEditor/System stack frames are included.
- `max_entries` (default 100) caps the returned entry count; `max_stack_frames` (default 20) truncates long stack traces.
- `clear: true` empties the console after reading.

### Profiler capture (`unity_senses_profiler_capture`)

Direct-response tool that reads the Unity Profiler frame hierarchy via `ProfilerDriver.GetHierarchyFrameDataView` (requires the Profiler to be enabled and to have captured frames).

- Single-frame mode (default): returns `{ itemId, name, totalMs, selfMs, calls, children? }` for the root level.
- Drill-down: `parent` (an `itemId` from a previous response, same frame only) or `root` (recursive case-insensitive name substring).
- Averaging: set `from_frame`/`to_frame` or `frames` (last N) to switch to averaged flat-by-name mode with `avgTotalMs`/`avgSelfMs`/`avgCalls`/`appearedIn`.
- Token-bounded via `depth` (1 = one level, 0 = unlimited), `min_ms`, `max_items` (default 30), and `sort` (`total`/`self`/`calls`).
- Returns `profiler_empty` / `frame_out_of_range` / `no_frame_data` / `root_not_found` / `no_frames_in_range` error codes as appropriate.

### Profiler memory (`unity_senses_profiler_memory`)

Direct-response tool that snapshots live memory allocator stats.

- Returns raw byte counts (`allocatedBytes`, `reservedBytes`, `unusedReservedBytes`, `tempAllocatorBytes`, `managedHeapBytes`) plus a `humanReadable` block.
- `gc_collect: true` runs a full GC (with finalizers) before sampling.

### Profiler rendering (`unity_senses_profiler_rendering`)

Direct-response tool that snapshots the rendering environment (no parameters).

- `system` — GPU name/vendor/version, device type, VRAM (MB), processor, OS.
- `renderPipeline` — active `RenderPipelineAsset` type name (URP/HDRP) or `Built-in Render Pipeline`.
- `screen` — width/height/dpi/fullScreen, current resolution + refresh rate.
- `quality` — quality level/name, vSync, pixel lights, anti-aliasing, shadow cascades, soft shadows.
- `application` — target frame rate, run in background, is playing, Unity version.
- `time` — frame count, rendered frame count, time scale, realtime since startup.

### Spatial query (`unity_senses_spatial_query`)

Direct-response tool that runs physics-based spatial queries against the current scene (live-only; requires a loaded scene).

- `action: raycast` — `origin` + `direction` (`"x,y,z"` strings); returns `hit`, and on hit `point`/`normal`/`distance` plus the hit object's `instanceId`/`gameObject`/`path`/`collider`.
- `action: overlap` — volume query by `shape` (`sphere`/`box`/`capsule`) centered on `center`; returns `hits[]` with `instanceId`/`gameObject`/`path`/`collider`/`distance`. `half_extents` (box) and `end` (capsule) configure the shape.
- `action: bounds` — combined world AABB of a target object; returns `center`/`extents`/`size`/`min`/`max` (and `empty` if no renderers/colliders).
- `action: ground_check` — cast downward (override with `direction`) from a target object or a `point` to find the surface below; returns `hit` and on hit `point`/`normal`/`distance` plus `surface`/`surfaceId`/`surfacePath`.
- `action: nearest` — closest objects to a target object or a `point`; returns `objects[]` sorted by distance with `instanceId`/`name`/`path`/`distance`/`position`. Filter via `component` (type name) and `tag`; cap via `max` (default 5).
- Targets are addressed in priority order: `instance_id`, `path` (`"Root/Child"`), `name` (first match).
- Physics queries (`raycast`/`overlap`/`ground_check`) hit Colliders only; `bounds`/`nearest` also see render-only objects. `layer` (name) restricts physics to one layer; `query_triggers` includes trigger colliders.
- Error codes: `unknown_action`, `missing_parameter`, `bad_parameter`, `target_not_found`.

## `/resources` and `/resources/{route}`

- `GET /resources` returns list of registered resource metadata: `name`, `route`, `mimeType`, `description`.
- `GET /resources/{route}` executes the mapped resource provider and returns content with provider mime type.
- Resource errors use JSON envelope with codes such as `resource_not_found` and `execution_error`.

## Object handles

Live `UnityEngine.Object` values returned by `invoke_method` / `execute_csharp` are emitted as serializable handles (not reflected into JSON) so they survive the LLM round-trip. Each handle carries a canonical instance ID plus redundant fallback locators:

```json
{
  "objectId": 12345,
  "type": "UnityEngine.GameObject",
  "name": "Player",
  "path": "Root/Player",
  "assetPath": "Assets/Prefabs/Player.prefab",
  "assetGuid": "a1b2c3..."
}
```

- **GameObjects** include `path` (hierarchy path).
- **Components** include `gameObjectPath` and `gameObjectId` (parent locators).
- **Assets** include `assetPath` and `assetGuid`.
- Objects that are neither assets nor scene objects emit only `objectId`, `type`, `name`.

### Passing handles back to tools

- `invoke_method` accepts `object_id` (instance ID) to target a live object for instance methods instead of creating a new instance. Args that are handle JSON are auto-resolved when the target parameter type is `UnityEngine.Object`.
- `execute_csharp` accepts `object_ids` (array of instance IDs or handle JSON). Resolved objects are injected as `Snippet.Refs[index]` / `Snippet.Ref<T>(index)` in the snippet body.

### Domain-reload safety

Instance IDs are invalidated by domain reload (recompilation, enter/exit Play Mode). Resolution uses a priority fallback chain: `objectId` → `assetPath` → `assetGuid` → `path` → component-on-parent → `name`. When all locators fail, the error includes guidance to re-acquire the object via `unity_open_mcp_scene_get_data`, `unity_senses_spatial_query`, or `unity_open_mcp_search_assets`.

## Source-of-truth files

- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/BridgeEventSource.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeToolRegistry.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeResourceRegistry.cs`
