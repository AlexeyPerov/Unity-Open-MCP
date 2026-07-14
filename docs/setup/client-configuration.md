# MCP client configuration

This is the canonical reference for connecting an MCP client to one Unity
project. Setup guides link here for client-specific file locations and config
envelopes.

## Before you configure a client

- Install Node.js 18 or newer.
- Resolve the absolute Unity project root: the folder containing `Assets/`,
  `Packages/`, and `ProjectSettings/`.
- Prefer project-local configuration when the client supports it.
- Use the server version that matches the bridge and verify package pins. See
  [Versioning](../versioning.md) before changing one side independently.

The standard server entry is:

```json
{
  "command": "npx",
  "args": ["-y", "unity-open-mcp@0.6.1"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

`npx` downloads and launches that exact version. The first launch can take
10–60 seconds; later launches use npm's cache. To upgrade, change the npm pin
and the bridge/verify package pins together.

## Client files and envelopes

| Client | Preferred config | Envelope |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | `mcpServers` |
| Claude Desktop | OS global config | `mcpServers` |
| Claude Code | CLI registration | `claude mcp add` |
| VS Code Copilot | `<project>/.vscode/mcp.json` | `servers` with `type: "stdio"` |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | `servers` with `type: "stdio"` |
| OpenCode | `<project>/opencode.json` | `mcp`; command array; `environment` |
| ZCode | `<project>/.zcode/cli/config.json` | `mcp.servers` with `type: "stdio"` |
| Codex | `<project>/.codex/config.toml` | TOML `mcp_servers` table |
| Cline | Client global MCP settings | `mcpServers` |
| Gemini CLI | `<project>/.gemini/settings.json` | `mcpServers` |
| GitHub Copilot CLI | `<project>/.mcp.json` | `mcpServers` |
| Kilo Code | `<project>/.kilocode/mcp.json` | `mcpServers` |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | `mcpServers` |
| Unity AI | `<project>/UserSettings/mcp.json` | `mcpServers` |
| ZooCode | `<project>/.roo/mcp.json` | `mcpServers` |
| Antigravity | Global Antigravity MCP config | `mcpServers` |

If a config already exists, merge the `unity-open-mcp` entry without replacing
unrelated settings or sibling MCP servers.

### `mcpServers` clients

Use this shape for Cursor, Claude Desktop, Cline, Gemini CLI, GitHub Copilot
CLI, Kilo Code, Rider, Unity AI, ZooCode, and Antigravity:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.6.1"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

### VS Code and Visual Studio Copilot

```json
{
  "servers": {
    "unity-open-mcp": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.6.1"],
      "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@0.6.1"],
      "enabled": true,
      "environment": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### ZCode

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@0.6.1"],
        "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
      }
    }
  }
}
```

### Codex

```toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "npx"
args = ["-y", "unity-open-mcp@0.6.1"]

[mcp_servers.unity-open-mcp.env]
UNITY_PROJECT_PATH = "/absolute/path/to/project"
```

### Claude Code

```sh
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/project \
  -- npx -y unity-open-mcp@0.6.1
```

If the server is already registered, remove and re-add it when the command,
version pin, or project path must change.

## Environment

| Variable | Required | Purpose |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Absolute Unity project root. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Pin a bridge port instead of path-based discovery. |
| `UNITY_PATH` | no | Explicit Unity executable for batch fallback when auto-discovery is unavailable. |

Startup and steady-state modal handling has additional environment variables.
The complete list, defaults, safety opt-ins, and policy matrix live in
[Dialog policy](../dialog-policy.md).

## Alternative server commands

For a global install:

```bash
npm install -g unity-open-mcp
```

Use `"command": "unity-open-mcp"` with no arguments (or the equivalent command
array for OpenCode). Update it explicitly with
`npm update -g unity-open-mcp`.

For a local checkout, build `mcp-server/` and replace the `npx` entry with:

```json
{
  "command": "node",
  "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

See [Development setup](development-setup.md) for the complete contributor
workflow.

## After editing configuration

Restart the MCP client so it reloads the file, open the same Unity project, and
wait for compilation to finish. Then call `unity_open_mcp_ping` or
`unity_open_mcp_capabilities`.

For connection and bridge recovery, use [Troubleshooting](../troubleshooting.md).
