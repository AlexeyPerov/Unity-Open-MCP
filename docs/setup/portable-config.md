# Portable MCP config (committable)

Put one MCP client config in your repository and let every teammate use it —
no per-developer absolute path, no "copy the example file and edit line 6".

This page covers the committable form. For the per-machine catalog of client
paths and snippets, see [MCP client configuration](client-configuration.md).

## Two layouts

**Layout A — the Unity project is the repository.** Your AI client opens the
folder that contains `Assets/`, `Packages/`, and `ProjectSettings/`.

```
my-game/                    <- AI client opened here
  .cursor/mcp.json          <- committed
  Assets/
  Packages/
  ProjectSettings/
```

**Layout B — monorepo.** Your AI client opens the repository root; the Unity
project is a subfolder.

```
my-game/                    <- AI client opened here, config lives here
  .cursor/mcp.json          <- committed, names Client/
  Client/                   <- Unity project (Assets/, Packages/)
  Server/
```

Everything below is the same for both, except that Layout B names the Unity
subfolder (`Client`).

## How the project path is resolved

The MCP server needs one absolute Unity project root. It takes the first of
these that is set:

| Priority | Input | Result |
|---|---|---|
| 1 | `--project <path>` (CLI only) | absolute, or resolved against the working directory |
| 2 | `UNITY_PROJECT_PATH` | absolute, or resolved against the working directory |
| 3 | `--project-from-cwd` + optional `--unity-subpath <rel>` | working directory, plus the subfolder |
| 4 | none | startup error listing these options |

`UNITY_PROJECT_PATH` wins over `--project-from-cwd`, so a developer can still
override a committed config from their own environment. The resolved path is
printed on stderr at startup:

```
[unity-open-mcp] Unity project resolved to /Users/dev/my-game/Client (source: cwd+subpath)
```

If the resolved folder is not a Unity project, the server says so and exits
instead of connecting to the wrong bridge.

## Client matrix

Three portable shapes, depending on what the client can do:

| Shape | How it works | Use when |
|---|---|---|
| **Interpolation** | the client expands `${workspaceFolder}` inside the config | the client supports it (Cursor, VS Code) |
| **Args** | the server resolves the path from the directory the client spawns it in | the client spawns MCP servers at the workspace root |
| **Wrapper** | a committed shell script resolves the path from its own location | neither of the above works |

| Client | Config file | Layout A | Layout B |
|---|---|---|---|
| Cursor | `<workspace>/.cursor/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Claude Code | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| VS Code Copilot | `<workspace>/.vscode/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Visual Studio Copilot | `<workspace>/.vs/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| OpenCode | `<workspace>/opencode.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| GitHub Copilot CLI | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Gemini CLI | `<workspace>/.gemini/settings.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Kilo Code | `<workspace>/.kilocode/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Rider (Junie) | `<workspace>/.junie/mcp/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| ZooCode | `<workspace>/.roo/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Unity AI | `<unity-project>/UserSettings/mcp.json` | `--project-from-cwd` | — (config lives in the Unity project) |
| Codex | `<workspace>/.codex/config.toml` | [wrapper](#wrapper-script) | [wrapper](#wrapper-script) |
| ZCode | `<workspace>/.zcode/cli/config.json` | [wrapper](#wrapper-script) | [wrapper](#wrapper-script) |
| Claude Desktop | OS global config | absolute path only | absolute path only |
| Cline | client global MCP settings | absolute path only | absolute path only |
| Antigravity | global Antigravity config | absolute path only | absolute path only |

A global config has no workspace to resolve against, so it always carries the
absolute path. That is the intended use of the absolute form; treat it as the
per-machine fallback, not the team default.

## Copy these

### Cursor — `${workspaceFolder}`

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@1.2.3"],
      "env": { "UNITY_PROJECT_PATH": "${workspaceFolder}/Client" }
    }
  }
}
```

Drop `/Client` for Layout A.

### Claude Code and other args-only clients

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": [
        "-y",
        "unity-open-mcp@1.2.3",
        "--project-from-cwd",
        "--unity-subpath",
        "Client"
      ],
      "env": {}
    }
  }
}
```

Drop the last two arguments for Layout A.

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@1.2.3", "--project-from-cwd", "--unity-subpath", "Client"],
      "enabled": true,
      "environment": {}
    }
  }
}
```

### Wrapper script

For clients that expand neither a workspace variable nor an environment
variable, commit a small script and point the client at it:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "bash",
      "args": ["scripts/mcp/unity-open-mcp.sh"]
    }
  }
}
```

```toml
# .codex/config.toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "bash"
args = ["scripts/mcp/unity-open-mcp.sh"]
```

Write the script with the setup CLI so its version pin matches the package you
actually installed:

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/my-game/Client \
  --client cursor \
  --layout monorepo --unity-subpath Client --wrapper
```

It lands at `scripts/mcp/unity-open-mcp.sh` for a monorepo, or
`.unity-open-mcp/mcp-wrapper.sh` when the Unity project is the repository. The
script resolves the Unity root from its own location, exports
`UNITY_PROJECT_PATH`, and execs the pinned package — so the client's working
directory does not matter. Override the Unity subfolder for one run with
`UNITY_SUBPATH=OtherClient`.

## Write it with the setup CLI

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/my-game/Client \
  --client cursor \
  --layout monorepo --unity-subpath Client
```

- Client config and the agent skill are written under the **workspace root**
  (`my-game/`); the Unity package pins always go to the **Unity project**
  (`my-game/Client/Packages/manifest.json`).
- `--layout monorepo` implies a portable config. Pass `--no-portable` to force
  the absolute path, or `--portable` to get the committable form for a
  Layout A project as well.
- `--workspace <abs>` names the repository root explicitly; without it, setup
  derives it from `--project` minus `--unity-subpath`.
- `--dry-run` prints the exact snippet without writing anything — use it to
  confirm no machine path appears before you commit.

Re-running with different flags rewrites the entry in place: an absolute entry
becomes portable and vice versa, and sibling MCP servers are preserved.

## What must not be committed

- **`UNITY_OPEN_MCP_BRIDGE_PORT`.** The default port is derived from the
  absolute project path, so it differs per machine. Leave it out and let the
  server discover the running bridge. Pin it only in a local, uncommitted
  override.
- **`UNITY_PATH`.** The Unity Editor location is per machine.
- **Secrets.** Other servers in the same file should read theirs from the
  environment (`${env:…}`), not from the committed JSON.

## Verify without an Editor

From the repository root:

```bash
npx -y unity-open-mcp@1.2.3 ping --project-from-cwd --unity-subpath Client
```

The startup line shows which input won and which absolute path was resolved. A
non-zero exit before the bridge is running is expected; the resolution line is
what you are checking.

For per-client paths and the absolute snippets, see [MCP client
configuration](client-configuration.md). For first-time installation, see
[Agent setup](agent-setup.md) or [Manual setup](manual-setup.md).
