# Unity Open MCP Demo Project

Minimal Unity project for testing the Unity Open MCP Bridge and MCP tools locally.

## Requirements

- **Unity 6** (6000.0.23f1 or later) — the demo project pins Unity 6 URP packages. The bridge and verify packages themselves support 2022.3 LTS+; this demo is just pinned to Unity 6 for its render pipeline.
- **Node.js** 18+ (for MCP server)

## Quick Start

### 1. Open in Unity

Open this `demo/` folder as a Unity project. Unity will resolve local `file:` package references automatically:

- `com.alexeyperov.unity-open-mcp-bridge` → `../../packages/bridge`
- `com.alexeyperov.unity-open-mcp-verify` → `../../packages/verify`

The bridge HTTP listener starts automatically on a **per-project port**
derived from this project's path (`20000 + sha256(path) % 10000`, so two
projects never collide) when the Editor finishes loading.

### 2. Verify bridge is running

The port for this demo project is printed by the bridge window's status line;
discover it (and confirm the bridge is up) with the MCP server CLI:

```bash
cd ../mcp-server && node dist/index.js status --project "$(pwd)/.." --json
```

Or ping it directly once you know the port (the bridge window shows it):

```bash
curl http://127.0.0.1:<port>/ping
```

Expected response:

```json
{
  "connected": true,
  "projectPath": "/path/to/demo",
  "unityVersion": "6000.0.23f1",
  "bridgeVersion": "0.1.0",
  "mode": "live",
  "compiling": false,
  "isPlaying": false
}
```

### 3. Start MCP server

```bash
cd /path/to/unity-open-mcp/mcp-server
npm run build
node dist/index.js
```

Set environment variables:

| Variable | Value |
|---|---|
| `UNITY_PROJECT_PATH` | Absolute path to this `demo/` directory |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | Optional — the port is derived from the project path automatically. Set only to pin a specific port. |

### 4. Connect an AI client

Configure your MCP client (Cursor, Claude Desktop, or OpenCode) to use the MCP server. See the MCP server README (`mcp-server/README.md`) for configuration examples.
