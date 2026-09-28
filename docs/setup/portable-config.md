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
override a committed config from their own environment.

`--unity-subpath` tolerates a teammate who opens the client on the Unity
folder instead of the repository root: when `<cwd>/<subpath>` is not a Unity
project but the working directory itself is, the working directory is used
(source `cwd`). The resolved path is printed on stderr at startup:

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
| ZCode | `<workspace>/.zcode/config.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
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
      "args": ["-y", "unity-open-mcp@1.4.0"],
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
        "unity-open-mcp@1.4.0",
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

To let Claude Code write that file, run this from the repository root and
commit the resulting `.mcp.json`:

```bash
claude mcp add --scope project unity-open-mcp -- npx -y unity-open-mcp@1.4.0 --project-from-cwd --unity-subpath Client
```

Without `--scope project` the entry lands in your user-level Claude Code
config and is not shared.

### ZCode

`<repo>/.zcode/config.json` (the workspace file; `~/.zcode/cli/config.json` is
your personal one):

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@1.4.0", "--project-from-cwd", "--unity-subpath", "Client"],
        "env": {}
      }
    }
  }
}
```

ZCode starts stdio servers in the session's working directory — the repository
root it was opened on — and does not expand `${...}` in config files, so the
args form is the portable one. Drop the last two arguments for Layout A.
`setup --client zcode --unity-subpath Client`, run from the repository root,
writes this entry. A same-named server in `~/.zcode/cli/config.json` overrides
the workspace one, so remove an old personal `unity-open-mcp` entry there.

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@1.4.0", "--project-from-cwd", "--unity-subpath", "Client"],
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

The bridge window writes the script for you (see
[From the Unity Editor](#from-the-unity-editor)), or write it with the setup CLI
so its version pin matches the package you actually installed:

```bash
cd my-game
npx -y unity-open-mcp@latest setup --client codex --unity-subpath Client
```

It lands at `scripts/mcp/unity-open-mcp.sh` for a monorepo, or
`.unity-open-mcp/mcp-wrapper.sh` when the Unity project is the repository. The
script resolves the Unity root from its own location, exports
`UNITY_PROJECT_PATH`, and execs the pinned package — so the client's working
directory does not matter. Override the Unity subfolder for one run with
`UNITY_SUBPATH=OtherClient`.

For Codex this writes the script and the `[mcp_servers.unity-open-mcp]` table in
`.codex/config.toml`, leaving the rest of that file byte for byte. The wrapper
is a bash script, so on Windows it needs Git Bash or WSL; there setup writes the
absolute Codex entry unless you pass `--portable`.

## Write it with the setup CLI

```bash
cd my-game
npx -y unity-open-mcp@latest setup --client cursor --unity-subpath Client
```

- Run it from the repository root: `--unity-subpath` names the Unity folder
  below the current directory. For Layout A, run it in the Unity project with no
  subpath. `--project <path>` (absolute or relative) names the Unity project
  from anywhere else.
- Client config and the agent skill are written under the **workspace root**
  (`my-game/`); the Unity package pins always go to the **Unity project**
  (`my-game/Client/Packages/manifest.json`).
- The portable form is the default for a monorepo and for a Unity project that
  is itself a git repository root. Pass `--no-portable` to force the absolute
  path, or `--portable` to force the committable form.
- Run inside `Client/` of a larger repository, setup configures `Client/` itself
  and warns with the command to run from the repository root instead.
- `--workspace <path>` names the repository root explicitly; without it, setup
  derives it from the project minus `--unity-subpath`.
- `--dry-run` prints the exact snippet without writing anything — use it to
  confirm no machine path appears before you commit.

Re-running with different flags rewrites the entry in place: an absolute entry
becomes portable and vice versa, and sibling MCP servers are preserved.

## From the Unity Editor

**Tools → Unity Open MCP Bridge → Status → Configure AI client** has a
**Commit-safe config** toggle. It is on by default when the Unity project is
inside a git repository (a subfolder of it, or the repository itself) and
names the target at the repository root. With it on, the snippet carries no
machine path and no bridge port:

- `${workspaceFolder}` clients (Cursor project config, VS Code, Visual Studio)
  get the interpolated `UNITY_PROJECT_PATH`.
- Args clients, ZCode (project) included, get `--project-from-cwd
  [--unity-subpath Client]`; for Claude Code the panel shows the
  `claude mcp add --scope project …` command.
- Codex gets the wrapper form, plus a **Write** button that creates the wrapper
  script at the path shown.

Clients that only read a machine-wide config (Claude Desktop, Cline,
Antigravity, the global Cursor/OpenCode/ZCode rows) keep the absolute form, and
the panel says so. The Hub setup wizard's **Commit-safe config** option follows
the same default when it detects a repository at or above the Unity project.

## Keeping the pin current

The committed entry still pins a version (`unity-open-mcp@1.4.0`), and it must
move together with the bridge and verify pins in `Packages/manifest.json`. The
bridge window's **Updates** flow resolves portable entries against the
directory the config was found under — the workspace root — and rewrites the
ones that name this project: a `${workspaceFolder}` or relative
`UNITY_PROJECT_PATH`, `--project-from-cwd [--unity-subpath …]`, and the
committed wrapper script (`scripts/mcp/unity-open-mcp.sh` or
`.unity-open-mcp/mcp-wrapper.sh`) with its baked default subfolder. An entry
that names another Unity project of the same repository is left alone, like
any foreign entry. Commit the updated files together.

## After a teammate clones

Nothing machine-specific needs editing. Each teammate:

1. Installs Node.js 18 or newer.
2. Opens the Unity project (`Client/` in a monorepo) so Package Manager
   resolves the bridge and verify pins from `Packages/manifest.json`.
3. Opens the AI client on the repository root and approves the project's MCP
   servers when asked — Claude Code and Cursor ask once for servers defined in
   the repository, and Codex reads `.codex/config.toml` only for a project it
   trusts.

A teammate who already had a local, uncommitted copy of the same config file
must remove or rename it before pulling: Git refuses to overwrite an untracked
file with a tracked one.

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
npx -y unity-open-mcp@1.4.0 ping --project-from-cwd --unity-subpath Client
```

The startup line shows which input won and which absolute path was resolved. A
non-zero exit before the bridge is running is expected; the resolution line is
what you are checking.

For per-client paths and the absolute snippets, see [MCP client
configuration](client-configuration.md). For first-time installation, see
[Agent setup](agent-setup.md) or [Manual setup](manual-setup.md).
