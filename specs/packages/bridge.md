# Unity Agent Bridge Package

`packages/bridge` — Unity Editor package that exposes a live HTTP connector, meta-tool dispatch, and GatePolicy.

See also: [../architecture/gate-policy.md](../architecture/gate-policy.md), [../architecture/mcp-tools.md](../architecture/mcp-tools.md), [../architecture/bridge-http-api.md](../architecture/bridge-http-api.md), [verify.md](verify.md), [mcp-server.md](mcp-server.md).

## UPM identity

| Field | Value |
|---|---|
| Package id | `com.alexeyperov.unity-agent-bridge` |
| Unity minimum | 6000.0 (Unity 6) |
| Lives in monorepo | `packages/bridge/` |

## Responsibilities

| Component | Location | Role |
|---|---|---|
| HTTP listener | `Editor/Bridge/` | Accept MCP server requests on main-thread queue |
| Meta-tool dispatch | `Editor/MetaTools/` | `execute_csharp`, `invoke_method`, `execute_menu`, `find_members` |
| GatePolicy | `Editor/Gate/GatePolicy.cs` | Checkpoint → mutate → validate → delta state machine |
| Verify adapter | `Editor/Gate/VerifyGateAdapter.cs` | Thin facade over `packages/verify` (M3+) |
| Session state | `Editor/Bridge/BridgeSession.cs` | Checkpoint ring buffer, compile/play status |

GatePolicy lives in bridge, not verify — keeps check logic neutral and reusable.

## HTTP API (live mode)

Default port: `19120` (override via `UNITY_AGENT_BRIDGE_PORT` env or `-UNITY_AGENT_BRIDGE_PORT` launch arg).

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | GET | Health: version, compile state, play mode, project path |
| `/tools/{tool_name}` | POST | Dispatch MCP tool; returns combined mutation + gate envelope |

MCP server (`unity-agent-mcp`) is the primary client. Hub wizard Step 5 polls `/ping` after launch.

### Main-thread dispatch

All mutation and Editor API work runs on the Unity main thread via an internal queue. HTTP handler enqueues work and awaits completion with timeout (tool-level `timeout_ms`).

Compile-wait: bridge reports `compiling: true` in `/ping`; MCP may retry or block until compile finishes.

## Meta-tools (M2)

Four meta-tools plus one health tool (see [mcp-tools.md](../architecture/mcp-tools.md) §M2):

| Tool | Gate | Notes |
|---|---|---|
| `unity_agent_ping` | no | Health check only — not counted as a meta-tool |
| `unity_agent_execute_csharp` | yes | Roslyn compile + run in Editor |
| `unity_agent_invoke_method` | yes | Reflection dispatch |
| `unity_agent_execute_menu` | yes | Menu path execution; deny-list for destructive menus |
| `unity_agent_find_members` | no | Read-only discovery |

### `paths_hint` strict validation (M2)

All mutating tools (`execute_csharp`, `invoke_method`, `execute_menu`) require a non-empty `paths_hint` array. When `paths_hint` is missing or empty:

- The bridge returns `mutation.success: false` with error code `paths_hint_required` before dispatching to the main thread.
- The error message includes guidance on providing asset paths.
- There is no whole-project scan fallback — agents must always declare which assets they intend to affect.
- Read-only tools (`find_members`) do not require `paths_hint`.

## Gate behavior by milestone

| Milestone | Gate behavior |
|---|---|
| **M2** | Stub: checkpoint + fixed `missing_references` on `paths_hint`; empty/missing `paths_hint` on mutating tools returns `paths_hint_required` error (strict mode — no whole-project fallback) |
| **M3** | Full GatePolicy via `VerifyGateAdapter` and `packages/verify` |

See [gate-policy.md](../architecture/gate-policy.md) for state machine and response contract.

## Package layout (sketch)

```
packages/bridge/
├── package.json
└── Editor/
    ├── Bridge/
    │   ├── BridgeHttpServer.cs
    │   ├── BridgeSession.cs
    │   └── MainThreadDispatcher.cs
    ├── MetaTools/
    │   ├── ExecuteCSharpTool.cs
    │   ├── InvokeMethodTool.cs
    │   ├── ExecuteMenuTool.cs
    │   └── FindMembersTool.cs
    └── Gate/
        ├── GatePolicy.cs
        └── VerifyGateAdapter.cs
```

## Install

**Distribution:**

```json
"com.alexeyperov.unity-agent-bridge": "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v1.0.0"
```

**Local dev:**

```json
"com.alexeyperov.unity-agent-bridge": "file:../../packages/bridge"
```

## Milestones

| Phase | Deliverable |
|---|---|
| **M2** | HTTP listener, 4 meta-tools, gate stub, `/ping` |
| **M3** | Full GatePolicy + `VerifyGateAdapter` integration |
| **M4** | Hub wizard installs bridge package |
| **M5** | Batch entry points for headless Editor (limited meta-tool parity) |
