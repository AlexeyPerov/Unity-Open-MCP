# Agent Setup

**Audience: AI agents.** Use the setup CLI to install Unity Open MCP into one
Unity project. Complete the agent steps yourself. Stop only at the **USER
ACTION** handoff.

Humans: this path is experimental. [Manual setup](manual-setup.md) and [Wizard
setup](wizard-setup.md) remain available as fallbacks.

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
`Packages/`, and `ProjectSettings/`. Remove any trailing slash.

Choose one setup client id:

| Client | `--client` | Project config written |
|---|---|---|
| Cursor | `cursor` | `.cursor/mcp.json` |
| Claude Code / project-scoped Claude | `claude` | `.mcp.json` |
| OpenCode | `opencode` | `opencode.json` |
| Generic agents | `agents` | `.mcp.json` |

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
  preserving sibling servers and extra environment variables;
- copies its bundled core skill byte-for-byte to the selected client path;
- does not start Unity, connect to a bridge, or install domain packages.

Useful options:

- `--dry-run` reports every target and intended value without writing files.
- `--skip-skill` installs packages and MCP config without touching a skill.
- `--json` returns a stable report containing the package version, project,
  client, target paths, pins, byte count, and warnings.
- `setup --help` works without the required setup flags.

Exit `0` means success (including dry-run), `2` means a project/client usage
error, and `1` means a file read, JSON parse, or write failure. On failure,
report the exact message. Do not silently switch to guessed pins.

## 3) Check the report

Before handoff, confirm the report contains:

- `VERSION` from the running package;
- bridge and verify pins ending in that same version;
- the MCP config target and `unity-open-mcp@VERSION` launch entry;
- one skill path and its byte count, unless `--skip-skill` was requested;
- a dry-run marker when applicable.

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
