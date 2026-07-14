# Agent Setup

**Audience: AI agents.** Follow this procedure to install Unity Open MCP into a
Unity project. Do every agent step yourself. Stop and tell the human only when a
**USER ACTION** block says so.

Humans: paste the prompt from the repo [README](../../README.md#quick-setup) into
your AI client, or follow [Manual setup](manual-setup.md) yourself.

## Goal

Install both halves of Unity Open MCP for **one** Unity project:

| Half | What you install |
|---|---|
| **Unity side** | Bridge + verify packages in `Packages/manifest.json` (Git URL pins) |
| **AI side** | MCP client config that launches `npx -y unity-open-mcp@0.6.1` with `UNITY_PROJECT_PATH` |

Then install/update the core agent skill, hand off restarts to the human, and
best-effort verify if tools become available.

Do **not** install optional Unity domain packages (NavMesh, Input System, …)
unless the human asks. Point them at [Extensions](../extensions.md) instead.

## Preconditions checklist

Before editing anything:

1. **Resolve the Unity project root** — a directory that contains `Assets/`,
   `Packages/`, and `ProjectSettings/`. Walk up from the workspace cwd if needed.
   If you cannot find one, **stop** and ask the human for the absolute project path.
2. **Resolve the absolute project path** — no trailing slash. This becomes
   `UNITY_PROJECT_PATH`.
3. **Check Node.js** — run `node --version`. Require **18+**.
   - If missing or too old → **USER ACTION:** install Node LTS from
     <https://nodejs.org/>, restart the terminal / AI client so `node`/`npx` are
     on `PATH`, then ask the human to re-run this setup.
4. **Detect the MCP client** (auto-detect; ask once only if ambiguous):
   - Cursor → `.cursor/` present, or Cursor-specific env/UI cues
   - Claude Code → `claude` CLI / Claude Code session
   - Claude Desktop → Claude Desktop app config paths
   - VS Code Copilot → `.vscode/` + Copilot MCP
   - Visual Studio Copilot → `.vs/`
   - OpenCode → `opencode` / `opencode.json`
   - ZCode → `.zcode/` / `.agents/`
   - Codex → `.codex/`
   - Otherwise ask: “Which AI client should I configure?”

## Step 1 — Merge Unity packages (`Packages/manifest.json`)

Read `Packages/manifest.json`. Under `dependencies`, set (create or overwrite
these two keys only — leave every other dependency untouched):

```json
"com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.6.1",
"com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.6.1"
```

**Idempotent rules:**

- If the keys are missing → add them.
- If they already exist with a different URL/tag/`file:` path → **update** them
  to the pins above (published Git install is the agent-setup path).
- Never remove unrelated packages.
- Write valid JSON (preserve formatting if practical; prefer a minimal diff).

## Step 2 — Configure the MCP client (idempotent merge)

**Prefer project-local config** whenever the client supports it. Use a global
config only if the human explicitly asks.

Read [MCP client configuration](client-configuration.md), select the detected
client's target and envelope, and replace `/absolute/path/to/project` with the
absolute path from Preconditions. If you fetched this procedure remotely,
fetch
`https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/client-configuration.md`.
Use the pinned `npx -y unity-open-mcp@0.6.1` command shown there.

**Idempotent merge rules when `unity-open-mcp` already exists:**

1. Update `command` / `args` (or equivalent) to the pinned `npx -y unity-open-mcp@0.6.1` form.
2. Set `UNITY_PROJECT_PATH` to **this** project’s absolute path.
3. Preserve any other env keys already on the entry.
4. Leave every other MCP server / unrelated config key untouched.
5. Create parent directories if missing. If the file does not exist, create it with
   the correct top-level envelope for that client.

Do not guess an envelope from memory: the shared reference is the owner for
client paths and JSON/TOML/CLI shapes. If Claude Desktop's OS-global file cannot
be located, ask the human for its path.

## Step 3 — Install / update the core skill

Always install or overwrite the shipped core playbook for the detected client(s).

1. Fetch the template:
   `https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/skills/unity-open-mcp/SKILL.md`
   (If this monorepo is already open locally, copy from
   `skills/unity-open-mcp/SKILL.md` instead.)
2. Write it to the client skill path(s) below (create directories as needed).
   Overwrite the stock path if a file already exists.

| Client family | Skill path (under project root) |
|---|---|
| Cursor | `.cursor/skills/unity-open-mcp/SKILL.md` |
| Claude (Desktop / Code) | `.claude/skills/unity-open-mcp/SKILL.md` |
| OpenCode | `.opencode/skills/unity-open-mcp/SKILL.md` |
| ZCode / Codex / generic agents | `.agents/skills/unity-open-mcp/SKILL.md` |
| Cline | `.cline/skills/unity-open-mcp/SKILL.md` |
| Gemini | `.gemini/skills/unity-open-mcp/SKILL.md` |
| Kilo Code | `.kilocode/skills/unity-open-mcp/SKILL.md` |
| ZooCode (Roo) | `.roo/skills/unity-open-mcp/SKILL.md` |
| Antigravity | `.agent/skills/unity-open-mcp/SKILL.md` |
| Rider (Junie) | `.junie/skills/unity-open-mcp/SKILL.md` |
| VS Code | `.vscode/skills/unity-open-mcp/SKILL.md` |
| Visual Studio | `.vs/skills/unity-open-mcp/SKILL.md` |
| GitHub Copilot CLI | `.github/skills/unity-open-mcp/SKILL.md` |

If the client mapping is ambiguous, write Cursor + Claude + OpenCode + agents
paths (the safe default set).

## Step 4 — USER ACTION handoff (required)

Print this checklist to the human and **stop mutating** until they confirm:

1. **Open Unity** with the **same** project (`UNITY_PROJECT_PATH`).
2. Wait until scripts finish compiling (Editor status bar).
3. **Restart your MCP / AI client** so it reloads the config from Step 2.
   (Most clients only read MCP config at startup.)
4. Optional: in Unity open **Tools → Unity Open MCP Bridge** and confirm it looks healthy.

First `npx` launch can take 10–60 seconds while npm downloads the package — that
is normal.

## Step 5 — Best-effort verify

After the human confirms Unity is open and the client was restarted:

- If Unity Open MCP tools are visible in this session, call
  `unity_open_mcp_capabilities` and/or `unity_open_mcp_ping`.
  - Success → report setup complete.
  - Failure → print the short troubleshooting list below; do not loop endlessly.
- If tools are **not** visible yet → tell the human the config/skill/manifest are
  in place and they still need a client restart (or a new agent chat that loads
  MCP). Optionally they can run:

```bash
npx -y unity-open-mcp@0.6.1 wait-for-ready --project /absolute/path/to/project
npx -y unity-open-mcp@0.6.1 run-tool unity_open_mcp_capabilities --project /absolute/path/to/project --json
```

## Short troubleshooting

- **bridge unavailable / connection refused:** Unity must be open on the same
  absolute project path as `UNITY_PROJECT_PATH`.
- **`npx` / `node` not found:** Node not on `PATH` — reinstall Node LTS, restart
  the AI client.
- **Tools missing after config edit:** restart the MCP client.
- **Wrong project driven:** `UNITY_PROJECT_PATH` must be absolute and point at
  the folder that contains `Assets/`, `Packages/`, `ProjectSettings/`.

More detail: [Troubleshooting](../troubleshooting.md), [Dialog policy](../dialog-policy.md).

## Related docs

- [MCP client configuration](client-configuration.md) — client paths and envelopes
- [Manual setup](manual-setup.md) — human DIY installation flow
- [Development setup](development-setup.md) — local checkout / contributor path
- [Skills](../skills.md) — what the playbook covers after install
- [Extensions](../extensions.md) — optional domain packages (skipped by this flow)
- [Versioning](../versioning.md) — how pins stay in sync with releases
