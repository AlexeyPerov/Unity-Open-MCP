# Bridge HTTP API

Formal HTTP contract for the Unity Agent Bridge (`packages/bridge`). The MCP server (`mcp-server/`) is the primary client; Hub wizard Step 5 polls `/ping` after launch.

See also: [../packages/bridge.md](../packages/bridge.md), [../packages/mcp-server.md](../packages/mcp-server.md), [mcp-tools.md](mcp-tools.md), [gate-policy.md](gate-policy.md).

## Transport

| Property | Value |
|---|---|
| Protocol | HTTP/1.1 |
| Bind address | `127.0.0.1` only (localhost) |
| Default port | `19120` |
| Port override | `UNITY_AGENT_BRIDGE_PORT` env var or `-UNITY_AGENT_BRIDGE_PORT=<port>` CLI arg |
| Content-Type | `application/json; charset=utf-8` (all request and response bodies) |
| Security | No auth token (localhost trust). Remote/token options deferred — see [../packages/backlog.md](../packages/backlog.md) |

All mutations and Editor API calls are dispatched to the Unity main thread via an internal queue. HTTP handler threads never touch Unity APIs directly.

---

## Endpoints

### `GET /ping`

Health check. Returns bridge session state: connection status, project info, and editor mode.

**Request:** no body, no query parameters.

**Response (200 OK):**

```json
{
  "connected": true,
  "projectPath": "/Users/dev/MyUnityProject",
  "unityVersion": "6000.0.23f1",
  "bridgeVersion": "0.1.0",
  "mode": "live",
  "compiling": false,
  "isPlaying": false
}
```

**Fields:**

| Field | Type | Always present | Description |
|---|---|---|---|
| `connected` | `boolean` | yes | `true` when bridge listener is running and session is initialized |
| `projectPath` | `string \| null` | yes | Absolute path to the Unity project root |
| `unityVersion` | `string \| null` | yes | Unity Editor version string |
| `bridgeVersion` | `string` | yes | Bridge package version (semver) |
| `mode` | `string` | yes | Always `"live"` in M2. Batch mode reserved for M5 |
| `compiling` | `boolean` | yes | `true` while Unity is compiling scripts (domain reload) |
| `isPlaying` | `boolean` | yes | `true` when the editor is in play mode |

**Response (503 Service Unavailable):** returned during domain reload when the HTTP listener is up but `BridgeSession` has not re-initialized yet.

```json
{
  "connected": false,
  "projectPath": null,
  "unityVersion": null,
  "bridgeVersion": "0.1.0",
  "mode": "live",
  "compiling": true,
  "isPlaying": false
}
```

**Compile-wait behavior:** MCP server clients should poll `/ping` when `compiling === true` and retry tool calls only after `compiling` becomes `false` and `connected` becomes `true`.

---

### `POST /tools/{tool_name}`

Dispatch an MCP tool call. The request body is the tool's input arguments (matching the schema in [mcp-tools.md](mcp-tools.md)).

**URL parameters:**

| Parameter | Description |
|---|---|
| `tool_name` | MCP tool identifier, e.g. `unity_agent_execute_csharp` |

**Common request body fields** (accepted by all mutating tools):

| Field | Type | Default | Constraints | Description |
|---|---|---|---|---|
| `timeout_ms` | `integer` | `30000` | `1000`–`300000` | Max wait time in milliseconds for tool execution |
| `gate` | `string` | `"enforce"` | `"enforce" \| "warn" \| "off"` | Gate validation mode |
| `paths_hint` | `string[]` | — | **required** non-empty for mutating tools | Asset paths likely touched; drives scoped gate validation |

Read-only tools (`unity_agent_find_members`) accept `timeout_ms` but ignore `gate` and `paths_hint`.

**Known tool names (M2):**

- `unity_agent_execute_csharp`
- `unity_agent_invoke_method`
- `unity_agent_execute_menu`
- `unity_agent_find_members`

See [mcp-tools.md](mcp-tools.md) for per-tool input schemas and descriptions.

---

## Response shapes

### Tool success (mutation envelope)

All tool calls return a combined envelope containing mutation result and gate state:

```json
{
  "mutation": {
    "success": true,
    "output": null,
    "error": null
  },
  "gate": {
    "mode": "enforce",
    "skipped": true,
    "validation": null,
    "delta": null
  },
  "agentNextSteps": []
}
```

**`mutation` fields:**

| Field | Type | Description |
|---|---|---|
| `success` | `boolean` | `true` if tool executed without exception |
| `output` | `any \| null` | Serialized tool return value |
| `error` | `object \| null` | Present when `success` is `false` |

**`mutation.error` fields (when present):**

| Field | Type | Description |
|---|---|---|
| `code` | `string` | Machine-readable error code (see error codes below) |
| `message` | `string` | Human-readable error description |

**`gate` fields:**

| Field | Type | Description |
|---|---|---|
| `mode` | `string` | The gate mode used: `"enforce"`, `"warn"`, or `"off"` |
| `skipped` | `boolean` | `true` in M2 (gate stub); `false` when full gate runs (M3+) |
| `validation` | `object \| null` | `null` when skipped. Full validation result in M3+ |
| `delta` | `object \| null` | `null` when skipped. Before/after delta in M3+ |

**`agentNextSteps`** is a `string[]` of 0–3 short hints for the AI agent (e.g. suggested fixes, follow-up tool calls).

### Tool timeout

Returned when tool execution exceeds `timeout_ms`:

```json
{
  "mutation": {
    "success": false,
    "output": null,
    "error": {
      "code": "timeout",
      "message": "Tool 'unity_agent_execute_csharp' timed out after 30000ms"
    }
  },
  "gate": {
    "mode": "enforce",
    "skipped": true,
    "validation": null,
    "delta": null
  },
  "agentNextSteps": [
    "Tool execution timed out. Consider increasing timeout_ms or simplifying the operation."
  ]
}
```

### Tool execution error

Returned when tool execution throws an unhandled exception:

```json
{
  "mutation": {
    "success": false,
    "output": null,
    "error": {
      "code": "execution_error",
      "message": "<exception message>"
    }
  },
  "gate": {
    "mode": "enforce",
    "skipped": true,
    "validation": null,
    "delta": null
  },
  "agentNextSteps": [
    "Tool execution failed with an unexpected error."
  ]
}
```

---

## HTTP status codes

| Status | When | Body |
|---|---|---|
| `200` | Successful tool dispatch (including mutation failures and timeouts — these are in-envelope) | Mutation envelope |
| `200` | Successful `/ping` | Ping JSON |
| `404` | Unknown tool name | Error JSON |
| `404` | Unknown endpoint path | Error JSON |
| `405` | Non-POST method on `/tools/*` | Error JSON |
| `500` | Unhandled bridge exception | Error JSON |
| `503` | `/ping` during domain reload (bridge not initialized) | Ping JSON with `connected: false`, `compiling: true` |

**Important:** mutation-level failures (tool error, timeout) return HTTP 200 with `mutation.success: false` in the envelope. The MCP server maps this to `isError: true` in the MCP protocol. HTTP-level errors (404, 405, 500) indicate a problem with the request itself, not the tool execution.

### Error response format (HTTP errors)

```json
{
  "error": {
    "code": "<error_code>",
    "message": "<human-readable description>"
  }
}
```

### Error codes

| Code | HTTP status | Description |
|---|---|---|
| `tool_not_found` | 404 | Unknown `tool_name` in URL |
| `not_found` | 404 | Unknown endpoint path |
| `method_not_allowed` | 405 | Non-POST request to `/tools/*` |
| `bridge_internal_error` | 500 | Unhandled exception in bridge request handling |
| `timeout` | 200 (envelope) | Tool execution exceeded `timeout_ms` |
| `execution_error` | 200 (envelope) | Tool threw an unhandled exception |
| `paths_hint_required` | 200 (envelope) | Mutating tool called without non-empty `paths_hint` |

---

## `isError` mapping (MCP server responsibility)

The MCP server translates the HTTP response into the MCP protocol `isError` flag. Rules per [gate-policy.md](gate-policy.md):

```text
isError = (mutation.success == false)
       OR (gate.mode == "enforce" AND gate.delta.newErrors > 0)
```

When `gate.mode == "warn"`: always `isError: false`, but `gate.delta` is populated.
When `gate.mode == "off"`: only `mutation.success == false` sets `isError`.

---

## M2 scope notes

- **Gate is stubbed:** `gate.skipped` is always `true`; `validation` and `delta` are `null`. Full gate with `VerifyGateAdapter` lands in M3.
- **`paths_hint`** is strictly enforced for mutating tools (`execute_csharp`, `invoke_method`, `execute_menu`). Missing or empty `paths_hint` returns `mutation.success: false` with error code `paths_hint_required`. Read-only tools (`find_members`) do not require `paths_hint`. There is no whole-project fallback in M2 — agents must always provide explicit asset paths for mutating operations.
- **Tool implementations are scaffolds:** `DispatchTool` returns a generic success result. Per-tool logic is implemented in Plan 2.
- **Batch mode** is not supported in M2. `mode` is always `"live"`.

---

## Request examples

### Ping

```http
GET /ping HTTP/1.1
Host: 127.0.0.1:19120
```

### Execute C# snippet

```http
POST /tools/unity_agent_execute_csharp HTTP/1.1
Host: 127.0.0.1:19120
Content-Type: application/json

{
  "code": "return UnityEditor.Selection.activeGameObject?.name ?? \"(none)\";",
  "usings": ["UnityEditor"],
  "paths_hint": [],
  "gate": "enforce",
  "timeout_ms": 10000
}
```

### Invoke method

```http
POST /tools/unity_agent_invoke_method HTTP/1.1
Host: 127.0.0.1:19120
Content-Type: application/json

{
  "type_name": "UnityEditor.EditorApplication",
  "method_name": "isPlaying",
  "is_static": true,
  "paths_hint": [],
  "gate": "off",
  "timeout_ms": 5000
}
```

### Execute menu

```http
POST /tools/unity_agent_execute_menu HTTP/1.1
Host: 127.0.0.1:19120
Content-Type: application/json

{
  "menu_path": "Assets/Refresh",
  "paths_hint": [],
  "gate": "enforce"
}
```

### Find members

```http
POST /tools/unity_agent_find_members HTTP/1.1
Host: 127.0.0.1:19120
Content-Type: application/json

{
  "query": "AssetDatabase",
  "kind": "method",
  "max_results": 20,
  "timeout_ms": 10000
}
```
