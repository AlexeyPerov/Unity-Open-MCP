# Manual Setup

Set up Unity Open MCP without Unity Hub Pro.

For guided setup, see [wizard-setup.md](wizard-setup.md).

## Requirements

- Unity 2022.3 LTS or newer (Unity 6 recommended)
- Node.js 18 or newer
- MCP client that supports stdio MCP servers

## 1) Add Unity packages

Edit `Packages/manifest.json` in your Unity project.

### Git install (recommended for external projects)

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.0.0"
  }
}
```

### Local `file:` install (monorepo projects)

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

## 2) Configure your MCP client

Use `npx` by default:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@latest"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

[[SCREENSHOT:MANUAL-SETUP-MCP-CONFIG]]

### Environment variables

- Required: `UNITY_PROJECT_PATH`
- Optional: `UNITY_OPEN_MCP_BRIDGE_PORT`
- Optional: `UNITY_PATH` (batch fallback)
- Optional launch-dialog handling:
  - `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1`
  - `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS`
  - `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS`

## 3) Launch Unity and verify

1. Open the same Unity project in the Editor.
2. Wait for scripts to compile.
3. Restart your MCP client.
4. Validate bridge status:

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

## 4) Optional CLI (CI and automation)

```bash
npx -y unity-open-mcp@latest wait-for-ready --project /path/to/MyGame
npx -y unity-open-mcp@latest run-tool unity_open_mcp_capabilities --project /path/to/MyGame --json
```

## Troubleshooting

- Connection refused: confirm Unity is open with the same project path.
- Tools missing in client: restart client after config changes.
- Compile-error launch dialog blocks startup: dismiss manually or keep auto-dismiss enabled.
- Slow first run with `npx`: expected package download behavior.

## Related docs

- [Wizard setup](wizard-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
