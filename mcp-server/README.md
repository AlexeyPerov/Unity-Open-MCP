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

For a first installation, the setup command merges the Unity package pins,
project MCP config, and bundled core skill without requiring a running Editor:

```bash
cd MyGame
npx -y unity-open-mcp@latest setup --client cursor
```

Run it in the Unity project folder, or pass `--project <path>`. When the Unity
project is a subfolder of the repository your AI client opens (a monorepo), run
it from the repository root and name the folder:

```bash
cd my-repo
npx -y unity-open-mcp@latest setup --client cursor --unity-subpath Client
```

Supported project-config ids are `cursor`, `claude`, `zcode`, `vscode`, `codex`,
`opencode`, and `agents`. Every npm and Unity package pin it writes is the
version of the package that runs it.
Run `npx -y unity-open-mcp@latest setup --help` for dry-run, skill-skip, JSON,
portable/absolute, and exit-code details.

### Configure your MCP client

`setup` writes the entry for you. To write it yourself, prefer the committable
form: it names no machine path, because the server resolves the Unity project
from the directory the client starts it in. For Claude Code (`.mcp.json`) and
other clients that start servers in the workspace root:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@1.6.0", "--project-from-cwd", "--unity-subpath", "Client"]
    }
  }
}
```

Drop `--unity-subpath Client` when the client opens the Unity project itself, and
keep the version equal to your bridge and verify packages.
[Portable MCP config](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/portable-config.md)
has the per-client matrix (`${workspaceFolder}` for Cursor and VS Code, the
wrapper script for Codex).

For a machine-wide config (Claude Desktop, Cline), or to override a committed
config locally, set `UNITY_PROJECT_PATH` to the absolute Unity project root
instead. The
[MCP client configuration reference](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/client-configuration.md)
owns the client-specific paths and JSON/TOML/CLI envelopes.

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

Then use `"command": "unity-open-mcp", "args": ["--project-from-cwd"]` (Claude
Code and other `mcpServers` clients that start servers in the workspace root) or
`"command": ["unity-open-mcp", "--project-from-cwd"]` (OpenCode) in your client
config. Add `--unity-subpath Client` for a monorepo; in a machine-wide config
such as Claude Desktop, use `"args": []` with an absolute `UNITY_PROJECT_PATH`.
Update with `unity-open-mcp update` (or `npm update -g unity-open-mcp`).

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

The bridge and verify packages must be installed in your Unity project; `setup`
adds both pins to `Packages/manifest.json`. To add them by hand instead, see the
[manual setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/manual-setup.md); for an AI-driven install, see the
[agent setup guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/agent-setup.md).

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
automation, plus bridge-independent project setup. All accept `--json` for
machine-readable output. See the
[CLI and automation reference](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/cli-automation.md)
for the full option reference.

Check or apply an MCP server update without a Unity project path:

```bash
unity-open-mcp update --check
unity-open-mcp update
```

The command updates global and project-local npm installs. When launched by
`npx`, it prints the pin to change instead of modifying the npx cache. It never
changes Unity package pins or MCP client configuration; use the repository's
[Updating guide](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/updating.md)
for the coordinated and air-gapped paths.

## Version pinning

The client configuration reference pins the server so it stays in lockstep
with the bridge and verify packages. To move to a newer release, bump the
client and Unity package pins together — see
[Version compatibility](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/versioning.md).

## Documentation

- [Agent setup](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/agent-setup.md)
- [Full manual setup](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/manual-setup.md)
- [MCP client configuration](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/client-configuration.md)
- [Portable, committable MCP configuration](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/portable-config.md)
- [Unity Hub Pro wizard walkthrough](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/setup/wizard-setup.md)
- [Updating and air-gapped installs](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/updating.md)
- [MCP tool catalog and routing](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/mcp-tools.md)
- [Project-defined commands](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/project-commands.md)
- [Asynchronous jobs](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/jobs.md)
- [Bridge HTTP API](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/api/bridge-http.md)
- [Architecture](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/master/docs/architecture.md)

## License

[MIT](./LICENSE)
