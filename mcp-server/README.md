# unity-open-mcp

[![npm version](https://img.shields.io/npm/v/unity-open-mcp.svg)](https://www.npmjs.com/package/unity-open-mcp)
[![license](https://img.shields.io/npm/l/unity-open-mcp.svg)](./LICENSE)

MCP server (stdio) that routes Model Context Protocol tool calls to a **live Unity
Editor** via the [Unity Open MCP Bridge](https://github.com/AlexeyPerov/Unity-Open-MCP),
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
      "args": ["-y", "unity-open-mcp@0.4.1"],
      "env": {
        "UNITY_PROJECT_PATH": "/path/to/your/unity/project"
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
      "command": ["npx", "-y", "unity-open-mcp@0.4.1"],
      "enabled": true,
      "environment": {
        "UNITY_PROJECT_PATH": "/path/to/your/unity/project"
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
  -- npx -y unity-open-mcp@0.4.1
```

> **Bridge port.** The bridge HTTP port is **derived from the project path**
> (`20000 + sha256(path) % 10000`, so two projects never collide), and the
> server discovers it via the bridge's lock file — so you usually do **not**
> need to set a port at all. The Unity Hub Pro wizard computes it for you.
> Set `UNITY_OPEN_MCP_BRIDGE_PORT` only when you want to pin a specific port.

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
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Bridge HTTP port override. When unset, the port is derived deterministically from the project path (`20000 + sha256(path) % 10000`) and discovered via the bridge's lock file. Set only to pin a specific port. |
| `UNITY_PATH` | no | Unity Editor executable for batch-only (headless) tools. |

## How it works

1. The Unity Open MCP Bridge package runs inside the Unity Editor and exposes an
   HTTP listener on `127.0.0.1`.
2. This MCP server connects to that bridge, routes tool calls to it, and returns
   the results over MCP stdio to your AI client.
3. When the live Editor is unavailable, supported tools fall back to headless
   Unity (batch) or offline disk parsing.

The bridge package must be installed in your Unity project. See the
[manual setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/manual-setup.md)
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
[manual setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/manual-setup.md#cli-for-ci--automation)
for the full option reference.

## Version pinning

The snippets above pin the server to `unity-open-mcp@0.4.1` so it stays in
lockstep with the bridge and verify packages, which share the same version
number. To move to a newer release, bump the version in your client config and
your Unity `manifest.json` together — see
[Versioning](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/versioning.md).
If you prefer to always run the newest published server, replace the pinned
version with `@latest`.

## Documentation

- [Full manual setup](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/manual-setup.md)
- [Unity Hub Pro wizard walkthrough](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/wizard-setup.md)
- [MCP tool catalog and routing](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/api/mcp-tools.md)
- [Bridge HTTP API](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/api/bridge-http.md)
- [Architecture](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/architecture.md)

## License

[MIT](./LICENSE)
