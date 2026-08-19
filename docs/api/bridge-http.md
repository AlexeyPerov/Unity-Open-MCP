# Bridge HTTP API

Unity bridge endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`.

Default bind is loopback (`127.0.0.1`).

## Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge and editor health snapshot. |
| `/instance` | `GET` | Runtime instance metadata snapshot. |
| `/events` | `GET` | SSE event stream (console/editor-state events). |
| `/events/poll` | `GET` | Pull-style event drain endpoint. |

Subscriber lifecycle on both event endpoints: a client-supplied `subscriber`
id persists across polls/reconnects and keeps its cursor; an id the bridge
mints (no `subscriber` param) is retired with the request/stream so anonymous
clients don't accumulate subscriber state.
| `/tools` | `GET` | Compiled-state tool inventory + group→tools map (used by capabilities / manage_tools for per-group availability). |
| `/tools/{toolName}` | `POST` | Execute one bridge tool. |
| `/resources` | `GET` | List bridge resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Port and discovery

- Default port is deterministic per project path.
- Optional override:
  - `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- Running bridge writes an instance lock file under `~/.unity-open-mcp/instances/`.
- MCP server uses that lock for project-to-port resolution.

## Authentication and bind mode

- `authMode: "none"` (default): requests accepted without bearer token.
- `authMode: "required"`: requests must send `Authorization: Bearer <token>`.
- Remote bind (`bindAddress: "0.0.0.0"`) is intended for controlled environments and should be paired with `authMode: "required"`.
- If remote access is needed, terminate TLS in front of the bridge (reverse proxy or tunnel).

## Tool execution envelopes

`POST /tools/{toolName}` responses typically include:

- `ok`: success marker
- `result`: tool payload (on success)
- `error`: structured error object (on failure)
- optional lifecycle/settle metadata for mutating tools

## Request limits

`POST /tools/{toolName}` request bodies are capped at 64 MB. A body that
exceeds the cap — whether declared via `Content-Length` or streamed (chunked) —
is rejected with HTTP `413` and the standard error envelope:

```json
{ "error": { "code": "request_too_large", "message": "Request body of N bytes exceeds the 67108864-byte limit." } }
```

Legitimate JSON tool arguments are kilobytes; the cap exists so a caller cannot
OOM the Editor with an unbounded body. In `authMode: "none"` (local dev) any
caller on the bind address can reach this path.

## Gate policy

For a mutating tool, the bridge selects the effective gate mode in this order:

1. A valid request-level `gate` value (`"enforce"`, `"warn"`, or `"off"`).
2. The project default in `.unity-open-mcp/settings.json`.

An omitted, malformed, or unknown request value falls back to the project
default. The gate value declared by a registry tool is catalog/recommendation
metadata; it does not override that project default during dispatch.

`GatePolicy.Execute` then applies the selected mode to the
checkpoint → mutate → validate → delta flow. Changing this precedence or its
fallback behavior is a bridge API contract change.

Two honesty rules govern the gate's response:

- `gate.delta` is `null` (never a zeroed object) whenever no delta was
  computed — the checkpoint failed, the mutation failed before validation, or
  the validate scan could not complete. A zeroed-looking delta always means
  "computed and clean".
- When a verify rule **throws** during the checkpoint or validate scan, the
  gate reports `gate.outcome: "validate_scan_failed"` with
  `gate.rulesFailed: [<ruleId>, …]` and withholds the delta: a failed rule
  contributes no issues, so its pre-existing problems would read as phantom
  "new"/"resolved" counts. The mutation itself still committed — verify health
  manually with `validate_edit` / `scan_paths`.

`paths_hint` is mandatory for every mutating call and is **not** waived by
`gate: "off"`. It is the declared mutation scope recorded in the audit trail,
not only the gate's validation scope, and there is no whole-project fallback.
The check runs before dispatch, so a missing `paths_hint` returns
`error.code: "paths_hint_required"` with `gate.skippedReason:
"request_rejected"` — nothing ran and no gate was evaluated. Tools that expose
`read_only` (today: `execute_csharp`) waive the requirement when it is `true`,
and a read-only `execute_menu` path waives it too.

Rule auto-selection maps the `paths_hint` extensions to rule families. Only
**registered** rule families are ever auto-selected: image (`.png`, `.jpg`,
`.jpeg`, `.tga`) and audio (`.wav`, `.mp3`, `.ogg`) paths fall back to
`missing_references` + `dependencies`, because the `textures`,
`sprite_2d_analysis`, and `audio_analysis` families are planned-but-not-shipped
and selecting them would run zero rules (a vacuous gate pass).

## Health check example

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

Use `/ping` to confirm:

- bridge reachability
- compile/play state
- readiness before running mutating tools

## Related docs

- [Routing and lifecycle](routing-lifecycle.md)
- [MCP client configuration](../setup/client-configuration.md)
- [Architecture](../architecture.md)
