# unity-open-mcp

[![npm version](https://img.shields.io/npm/v/unity-open-mcp.svg)](https://www.npmjs.com/package/unity-open-mcp)
[![license](https://img.shields.io/npm/l/unity-open-mcp.svg)](./LICENSE)

MCP server (stdio) that routes Model Context Protocol tool calls to a **live Unity
Editor** via the [Unity Open MCP Bridge](https://github.com/AlexeyPerov/unity-open-mcp),
with batch (headless Unity) and offline (disk-parse) fallbacks. Works with Cursor,
Claude Desktop, Claude Code, OpenCode, ZCode, and any MCP stdio client.

## Install

Node.js 18+ is required (the server is a Node process spawned by your MCP client).

You do not need to install this package yourself — your MCP client spawns it via
`npx`. The sections below show both the zero-install `npx` path and the optional
global install.

### Configure your MCP client

Pick your client and merge the matching config. Replace `/path/to/your/unity/project`
with the absolute path to your Unity project root.

#### Cursor or Claude Desktop

Edit `~/.cursor/mcp.json` (Cursor) or your Claude Desktop MCP config file:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@latest"],
      "env": {
        "UNITY_PROJECT_PATH": "/path/to/your/unity/project",
        "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

#### OpenCode (global)

Edit `~/.config/opencode/opencode.json`:

```json
{
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@latest"],
      "enabled": true,
      "environment": {
        "UNITY_PROJECT_PATH": "/path/to/your/unity/project",
        "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

For a project-scoped config, put the same `mcp.unity-open-mcp` block in
`opencode.json` at your Unity project root.

#### Claude Code (CLI)

```bash
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/path/to/your/unity/project \
  --env UNITY_OPEN_MCP_BRIDGE_PORT=19120 \
  -- npx -y unity-open-mcp@latest
```

### Optional: global install

If you prefer a one-time install over `npx` re-resolving on each spawn:

```bash
npm install -g unity-open-mcp
```

Then use `"command": "unity-open-mcp", "args": []` (Cursor / Claude Desktop) or
`"command": ["unity-open-mcp"]` (OpenCode) in your client config. Update with
`npm update -g unity-open-mcp`.

## Environment variables

| Variable | Required | Purpose |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Absolute path to your Unity project root. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Bridge HTTP port override. When unset, the port is derived deterministically from the project path and discovered via the bridge's lock file. |
| `UNITY_PATH` | no | Unity Editor executable for batch-only (headless) tools. |

## How it works

1. The Unity Open MCP Bridge package runs inside the Unity Editor and exposes an
   HTTP listener on `127.0.0.1`.
2. This MCP server connects to that bridge, routes tool calls to it, and returns
   the results over MCP stdio to your AI client.
3. When the live Editor is unavailable, supported tools fall back to headless
   Unity (batch) or offline disk parsing.

The bridge package must be installed in your Unity project. See the
[manual setup guide](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/manual-setup.md)
for the `Packages/manifest.json` entries.

## CLI

The same binary is a thin CLI for scripting and CI:

```bash
npx unity-open-mcp wait-for-ready --project /path/to/MyGame
npx unity-open-mcp ping --project /path/to/MyGame --json
npx unity-open-mcp status --project /path/to/MyGame --json
npx unity-open-mcp run-tool unity_open_mcp_capabilities \
  --project /path/to/MyGame --json --arg include_planned=false
```

Commands: `ping`, `wait-for-ready`, `status`, `run-tool`, `--help`, `--version`.
All accept `--json` for machine-readable output. See the
[manual setup guide](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/manual-setup.md#cli-for-ci--automation)
for the full option reference.

## Version pinning

The default `npx -y unity-open-mcp@latest` always fetches the latest published
version on first run. To pin a specific version, change the version suffix in
your client config (for example `unity-open-mcp@0.2.0`) or install a specific
version globally.

## Documentation

- [Full manual setup](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/manual-setup.md)
- [Unity Hub Pro wizard walkthrough](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/wizard-setup.md)
- [MCP tool catalog and routing](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/api/mcp-tools.md)
- [Bridge HTTP API](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/api/bridge-http.md)
- [Architecture](https://github.com/AlexeyPerov/unity-open-mcp/blob/main/docs/architecture.md)

## License

[MIT](./LICENSE)
