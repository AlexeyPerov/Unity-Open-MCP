# Agent Setup

**Audience: AI agents.** Use the setup CLI to install Unity Open MCP into one
Unity project. Complete the agent steps yourself. Stop only at the **USER
ACTION** handoff.

Humans: this path is experimental. [Manual setup](manual-setup.md) and [Wizard
setup](wizard-setup.md) remain available as fallbacks.

If the project is already installed and open in Unity, do not rerun first-time
setup just to change versions. Use **Tools → Unity Open MCP Bridge → Status →
Updates**, or preview `unity_open_mcp_upgrade` with its default `dry_run: true`;
both update the existing package, config, and agent-prose pins as one reviewed
operation.

## Hard rules

1. Fetch this procedure fresh, or read it from a local checkout. Do not install
   from remembered snippets.
2. Let the running `unity-open-mcp` package choose every version pin. Do not
   invent a version or hand-author pins before trying the CLI.
3. The CLI copies the core `SKILL.md` bytes shipped in the npm package. Do not
   rewrite the skill or call `unity_open_mcp_generate_skill` during setup.
4. Install for one detected client. Do not populate every client directory.
5. Do not add optional domain packages unless the human asks for them.

## 1) Resolve the project and client

Find the absolute Unity project root: the directory containing `Assets/`,
`Packages/`, and `ProjectSettings/`. Remove any trailing slash. Pass it with
`--project` even though setup defaults to the current directory: your shell's
working directory is not guaranteed to be the project, and setup ignores
`UNITY_PROJECT_PATH`.

Also note whether that root is the folder the human's AI client is opened on.
If the Unity project is a subfolder of a larger repository (`<repo>/Client`),
this is a monorepo — use the committed-config command in step 2 instead of the
default one.

Choose one setup client id:

| Client | `--client` | Project config written | Skill |
|---|---|---|---|
| Cursor | `cursor` | `.cursor/mcp.json` | `.cursor/skills/` |
| Claude Code / project-scoped Claude | `claude` | `.mcp.json` | `.claude/skills/` |
| VS Code Copilot | `vscode` | `.vscode/mcp.json` | `.vscode/skills/` |
| Codex | `codex` | `.codex/config.toml` | `.agents/skills/` |
| OpenCode | `opencode` | `opencode.json` | `.opencode/skills/` |
| Generic agents | `agents` | `.mcp.json` | `.agents/skills/` |

If the client is unclear, ask the human once. Other skill ids are recognized,
but this command deliberately refuses them because it cannot safely write their
MCP configuration; use the [manual configuration catalog](client-configuration.md)
for those clients.

Confirm Node.js 18 or newer with `node --version`. If Node is missing or too
old, ask the human to install Node LTS and restart the terminal/client.

## 2) Run setup

Run exactly one command, substituting the resolved absolute project and client:

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/UnityProject \
  --client cursor
```

The downloaded package pins all three components to **its own version**. It:

- merges bridge and verify Git pins into `Packages/manifest.json`;
- merges the `unity-open-mcp` server into the project-local MCP config while
  preserving sibling servers and extra environment variables (for Codex it
  edits only the `[mcp_servers.unity-open-mcp]` tables and leaves every other
  byte of `config.toml` alone);
- copies its bundled core skill byte-for-byte to the selected client path;
- does not start Unity, connect to a bridge, or install domain packages.

### Monorepo: write a committed config

When the Unity project is a subfolder of the repository, name the layout. The
config and skill then go to the repository root (where the client is opened),
the Unity pins still go to the Unity project, and the written entry carries no
machine-specific path — so the team commits it once:

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/repo/Client \
  --client cursor \
  --layout monorepo --unity-subpath Client
```

This is the happy path for a monorepo: prefer it over asking every developer to
edit an absolute path. When the Unity project IS the repository (a `.git` next
to `Assets/`), the default command already writes the committable form. Details
and the per-client matrix: [Portable MCP config](portable-config.md).

If the report warns that the project is inside a larger repository, the
human's client is probably opened on that root and will not read the file just
written. Ask once, then rerun with the command the warning prints.

Useful options:

- `--dry-run` reports every target and intended value without writing files.
- `--skip-skill` installs packages and MCP config without touching a skill.
- `--json` returns a stable report containing the package version, project,
  client, target paths, pins, byte count, and warnings.
- `--layout monorepo --unity-subpath <rel>` writes the committed form described
  above. It is also the default when the workspace is a git repository root;
  `--portable` forces it elsewhere and `--no-portable` forces the absolute path.
- `--workspace <path>` names the repository root explicitly instead of deriving
  it from `--project` minus `--unity-subpath`.
- `--wrapper` also writes the committed wrapper script, for clients that expand
  neither a workspace variable nor an environment variable.
- `setup --help` works without the required setup flags.

Exit `0` means success (including dry-run), `2` means a project/client usage
error, and `1` means a file read, parse, or write failure. Exit `1` also covers a
config setup will not rewrite — a `.vscode/mcp.json` with comments, or a Codex
entry spelled with dotted keys; the message then contains the entry to add by
hand. On failure, report the exact message. Do not silently switch to guessed
pins.

## 3) Check the report

Before handoff, confirm the report contains:

- `VERSION` from the running package;
- bridge and verify pins ending in that same version;
- the MCP config target and `unity-open-mcp@VERSION` launch entry;
- one skill path and its byte count, unless `--skip-skill` was requested;
- a dry-run marker when applicable;
- a `Config:` line: `portable — safe to commit` or `absolute path — this machine
  only`. For a portable run the workspace root and layout are listed too. If a
  config meant for the repository still contains an absolute path, report that
  instead of committing it;
- any `Warning:` lines, relayed to the human.

## 4) USER ACTION

Tell the human to:

1. Open Unity with the same project path and wait for compilation to finish.
2. Restart the MCP / AI client so it reloads the configuration.
3. Optionally open **Tools → Unity Open MCP Bridge** and confirm it is healthy.

The first `npx` launch can take 10–60 seconds while npm downloads the package.

After the human confirms, use `unity_open_mcp_capabilities` or
`unity_open_mcp_ping` if the tools are visible. Otherwise explain that the
files are installed and a restarted/new client session must load them.

## Manual fallback

If setup reports an unsupported client, use [MCP client
configuration](client-configuration.md) and [Manual setup](manual-setup.md), or
use the [Hub wizard](wizard-setup.md). Keep every npm/bridge/verify pin equal;
never invent a version, and copy the skill bytes rather than rewriting it.

For failures after installation, see [Troubleshooting](../troubleshooting.md).
