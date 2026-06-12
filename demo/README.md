# Unity Agent Demo Project

Minimal Unity project for testing the Unity Agent Bridge and MCP tools locally.

## Requirements

- **Unity 6** (6000.0.23f1 or later)
- **Node.js** 18+ (for MCP server)

## Quick Start

### 1. Open in Unity

Open this `demo/` folder as a Unity project. Unity will resolve local `file:` package references automatically:

- `com.alexeyperov.unity-agent-bridge` → `../../packages/bridge`
- `com.alexeyperov.unity-agent-verify` → `../../packages/verify`

The bridge HTTP listener starts automatically on `127.0.0.1:19120` when the Editor finishes loading.

### 2. Verify bridge is running

```bash
curl http://127.0.0.1:19120/ping
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
cd /path/to/Unity-AI-Hub/mcp-server
npm run build
node dist/index.js
```

Set environment variables:

| Variable | Value |
|---|---|
| `UNITY_PROJECT_PATH` | Absolute path to this `demo/` directory |
| `UNITY_AGENT_BRIDGE_PORT` | `19120` (default) |

### 4. Connect an AI client

Configure your MCP client (Cursor, Claude Desktop, or OpenCode) to use the MCP server. See [mcp-server.md](../specs/packages/mcp-server.md) §Client config for configuration examples.

## Sample Assets

| Asset | Purpose |
|---|---|
| `Assets/Prefabs/GateTestCube.prefab` | Simple cube prefab for gate validation tests |
| `Assets/Scenes/Main.unity` | Minimal scene with a GateTestCube instance |

These assets are designed for controlled broken/fixed reference checks:

- **Break a reference**: Remove or rename the prefab file while the scene references it → `missing_references` rule detects the broken reference.
- **Fix a reference**: Restore the prefab file → gate delta shows `resolvedErrors: 1`.

## Package References

The demo uses local `file:` references in `Packages/manifest.json`:

```json
{
  "com.alexeyperov.unity-agent-bridge": "file:../../packages/bridge",
  "com.alexeyperov.unity-agent-verify": "file:../../packages/verify"
}
```

Changes to bridge or verify source are reflected after Unity recompiles (domain reload).

## Port Override

Set `UNITY_AGENT_BRIDGE_PORT` environment variable or pass `-UNITY_AGENT_BRIDGE_PORT=<port>` as a Unity launch argument to override the default port `19120`.
