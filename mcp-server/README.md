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

Pick your client and merge a `unity-open-mcp` entry using the canonical
[MCP client configuration reference](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/client-configuration.md).
It owns the client-specific paths and JSON/TOML/CLI envelopes. Set
`UNITY_PROJECT_PATH` to the absolute Unity project root.

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

Core environment variables and modal-policy options are documented in the
[client configuration reference](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/client-configuration.md)
and [Dialog policy](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/dialog-policy.md).

## How it works

1. The Unity Open MCP Bridge package runs inside the Unity Editor and exposes an
   HTTP listener on `127.0.0.1`.
2. This MCP server connects to that bridge, routes tool calls to it, and returns
   the results over MCP stdio to your AI client.
3. When the live Editor is unavailable, supported tools fall back to headless
   Unity (batch) or offline disk parsing.

The bridge package must be installed in your Unity project. See the
[agent setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/agent-setup.md)
(paste the README prompt into your AI client) or the
[manual setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/manual-setup.md)
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

Commands also include event streaming, verify, baseline, and regression
automation. All accept `--json` for machine-readable output. See the
[CLI and automation reference](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/cli-automation.md)
for the full option reference.

## Version pinning

The client configuration reference pins the server so it stays in lockstep
with the bridge and verify packages. To move to a newer release, bump the
client and Unity package pins together — see
[Version compatibility](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/versioning.md).

## Documentation

- [Agent setup](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/agent-setup.md)
- [Full manual setup](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/manual-setup.md)
- [MCP client configuration](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/client-configuration.md)
- [Unity Hub Pro wizard walkthrough](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/wizard-setup.md)
- [MCP tool catalog and routing](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/mcp-tools.md)
- [Bridge HTTP API](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/bridge-http.md)
- [Architecture](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/architecture.md)

## License

[MIT](./LICENSE)
