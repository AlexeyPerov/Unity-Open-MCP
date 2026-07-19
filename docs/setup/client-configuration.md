# MCP client configuration

Connect an MCP client to one Unity project: find your client, copy the snippet,
set your project path, save the file, restart the client.

## Do this

1. Find your client in the [table](#where-to-put-it) and note the config file path.
2. Copy the matching snippet from [Copy these](#copy-these).
3. Replace `/absolute/path/to/project` with the absolute Unity project root
   (the folder that contains `Assets/`, `Packages/`, and `ProjectSettings/`).
4. Write that content to the file. If the file already has other MCP servers,
   add only the `unity-open-mcp` entry — do not wipe siblings.
5. Restart the MCP client so it reloads the config.

Pin the same server version as your bridge/verify packages (`0.7.0` below).
See [Versioning](../versioning.md) when upgrading. The first `npx` launch can
take 10–60 seconds while the package downloads; later launches are fast.

## Where to put it

| Client | Config file | Snippet |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Claude Desktop | OS global config | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Claude Code | CLI (no file) | [`Claude Code`](#claude-code) |
| VS Code Copilot | `<project>/.vscode/mcp.json` | [VS Code](#vs-code-and-visual-studio-copilot) |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | [VS Code](#vs-code-and-visual-studio-copilot) |
| OpenCode | `<project>/opencode.json` | [OpenCode](#opencode) |
| ZCode | `<project>/.zcode/cli/config.json` | [ZCode](#zcode) |
| Codex | `<project>/.codex/config.toml` | [Codex](#codex) |
| Cline | Client global MCP settings | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Gemini CLI | `<project>/.gemini/settings.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| GitHub Copilot CLI | `<project>/.mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Kilo Code | `<project>/.kilocode/mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Unity AI | `<project>/UserSettings/mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| ZooCode | `<project>/.roo/mcp.json` | [`mcpServers`](#mcpservers-cursor-and-most-clients) |
| Antigravity | Global Antigravity MCP config | [`mcpServers`](#mcpservers-cursor-and-most-clients) |

Prefer the project-local path when the client supports it.

## Copy these

### `mcpServers` (Cursor and most clients)

Use for Cursor, Claude Desktop, Cline, Gemini CLI, GitHub Copilot CLI, Kilo
Code, Rider, Unity AI, ZooCode, and Antigravity:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.7.0"],
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
      "args": ["-y", "unity-open-mcp@0.7.0"],
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
      "command": ["npx", "-y", "unity-open-mcp@0.7.0"],
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
        "args": ["-y", "unity-open-mcp@0.7.0"],
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
args = ["-y", "unity-open-mcp@0.7.0"]

[mcp_servers.unity-open-mcp.env]
UNITY_PROJECT_PATH = "/absolute/path/to/project"
```

### Claude Code

```sh
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/project \
  -- npx -y unity-open-mcp@0.7.0
```

If the server is already registered, remove and re-add it when the command,
version pin, or project path must change.

## Optional

| Variable | Required | Purpose |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Absolute Unity project root. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Pin a bridge port instead of path-based discovery. |
| `UNITY_PATH` | no | Explicit Unity executable for batch fallback. |

Startup modal env vars: [Dialog policy](../dialog-policy.md).

**Global install** (instead of `npx`): `npm install -g unity-open-mcp`, then use
`"command": "unity-open-mcp"` with no `args`. Update with
`npm update -g unity-open-mcp`.

**Local checkout:** build `mcp-server/` and point at
`node /absolute/path/to/unity-open-mcp/mcp-server/dist/index.js` — see
[Development setup](development-setup.md).

For connection problems after setup, see [Troubleshooting](../troubleshooting.md).
