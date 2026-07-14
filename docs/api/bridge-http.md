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
