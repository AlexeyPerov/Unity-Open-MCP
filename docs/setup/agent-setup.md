# Agent Setup

**Audience: AI agents.** Follow this procedure to install Unity Open MCP into a
Unity project. Do every agent step yourself. Stop and tell the human only when a
**USER ACTION** block says so.

Humans: this path is **experimental**. Prefer [Manual setup](manual-setup.md) or
[Wizard setup](wizard-setup.md). To try agent install anyway, paste the prompt
from the repo [README](../../README.md#quick-setup) into your AI client.

## Hard rules (do not skip)

1. **Never invent version numbers.** Do not recall `0.x.y` from training data or
   prior chats. The only allowed version is the one you resolve in
   [Step 0](#step-0--resolve-the-release-version).
2. **Fetch this procedure fresh** (or read it from a local monorepo checkout).
   Do not improvise an alternate install from memory.
3. **Copy the skill by bytes** (`curl` / `cp`). Do not rewrite, summarize,
   expand, or “improve” `SKILL.md`. Do **not** call
   `unity_open_mcp_generate_skill` during setup.
4. **Write one client skill path** (the detected client). Do not fan out to
   every client folder unless the human asks.

## Goal

Install both halves of Unity Open MCP for **one** Unity project:

| Half | What you install |
|---|---|
| **Unity side** | Bridge + verify packages in `Packages/manifest.json` (Git URL pins) |
| **AI side** | MCP client config that launches `npx -y unity-open-mcp@<VERSION>` with `UNITY_PROJECT_PATH` |

Then copy the core agent skill (byte-for-byte), hand off restarts to the human,
and best-effort verify if tools become available.

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

## Step 0 — Resolve the release version

Read the release pin **before** any manifest or MCP edit.

1. Fetch (or read locally if this monorepo is open):

   `https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/version.json`

   Local checkout: `version.json` at the repo root.
2. Parse JSON and set `VERSION` to the `version` field (e.g. `"0.7.0"` →
   `VERSION=0.7.0`).
3. Optionally confirm published npm matches:

   ```bash
   npm view unity-open-mcp version
   ```

   If npm’s latest and `VERSION` differ, **prefer `VERSION` from `version.json`**
   (that is the documented release pin for this procedure) and tell the human.
4. From here on, every pin uses that same `VERSION`:

   | Artifact | Pin |
   |---|---|
   | npm MCP server | `unity-open-mcp@<VERSION>` |
   | Bridge UPM | `…/packages/bridge#bridge-v<VERSION>` |
   | Verify UPM | `…/packages/verify#verify-v<VERSION>` |

**Stop** if you cannot read `version.json`. Do not guess.

## Step 1 — Merge Unity packages (`Packages/manifest.json`)

Read `Packages/manifest.json`. Under `dependencies`, set (create or overwrite
these two keys only — leave every other dependency untouched), substituting the
`VERSION` from Step 0:

```json
"com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v<VERSION>",
"com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v<VERSION>"
```

Example when `VERSION=0.7.0`:

```json
"com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.7.0",
"com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.7.0"
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

Follow [MCP client configuration](client-configuration.md): find the detected
client in the table, copy its snippet, replace `/absolute/path/to/project` with
the absolute path from Preconditions, and replace every npm version in the
snippet with **`VERSION` from Step 0** (the shared doc may lag a release; your
resolved `VERSION` wins). If you fetched this procedure remotely, fetch
`https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/client-configuration.md`.

Use: `npx -y unity-open-mcp@<VERSION>`.

**Idempotent merge rules when `unity-open-mcp` already exists:**

1. Update `command` / `args` (or equivalent) to `npx -y unity-open-mcp@<VERSION>`.
2. Set `UNITY_PROJECT_PATH` to **this** project’s absolute path.
3. Preserve any other env keys already on the entry.
4. Leave every other MCP server / unrelated config key untouched.
5. Create parent directories if missing. If the file does not exist, create it with
   the correct top-level envelope for that client.

Do not guess a config shape from memory: the shared reference owns client paths
and JSON/TOML/CLI snippets. If Claude Desktop's OS-global file cannot be
located, ask the human for its path.

## Step 3 — Copy the core skill (bytes only)

Install the shipped playbook for the **one** detected client. This file is large
on purpose; agents must **not** author it.

1. Create the destination directory if needed (table below).
2. Copy with a shell tool — do **not** paste or regenerate the markdown:

   ```bash
   # Remote (typical):
   curl -fsSL \
     "https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/skills/unity-open-mcp/SKILL.md" \
     -o "<PROJECT_ROOT>/<client-skill-path>"

   # Local monorepo checkout instead:
   # cp skills/unity-open-mcp/SKILL.md "<PROJECT_ROOT>/<client-skill-path>"
   ```

3. Overwrite if the file already exists.
4. Confirm the written file is non-trivial (roughly hundreds of lines / tens of
   KB). If you only wrote a short stub, delete it and re-run the `curl`/`cp`.
5. **Forbidden during setup:** rewriting the skill, summarizing it, appending
   project inventory by hand, or calling `unity_open_mcp_generate_skill`.
   Project-specific regeneration is optional later (see [Skills](../skills.md)).

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

If the client mapping is still ambiguous after asking once, write **only** the
`.agents/skills/unity-open-mcp/SKILL.md` path (generic agents folder), not every
client.

## Step 3b — Pre-handoff report (required)

Before the USER ACTION checklist, print exactly:

1. `VERSION` resolved from `version.json`
2. The two UPM dependency strings you wrote
3. The MCP launch command you wrote (`npx -y unity-open-mcp@…`)
4. The single skill path written, plus `wc -l` / file size of that file
5. Confirmation that you did **not** invent the version and did **not** rewrite
   the skill

If any pin is not equal to Step 0’s `VERSION`, fix it before continuing.

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
  MCP). Optionally they can run (substitute `VERSION` and the project path):

```bash
npx -y unity-open-mcp@<VERSION> wait-for-ready --project /absolute/path/to/project
npx -y unity-open-mcp@<VERSION> run-tool unity_open_mcp_capabilities --project /absolute/path/to/project --json
```

## Short troubleshooting

- **bridge unavailable / connection refused:** Unity must be open on the same
  absolute project path as `UNITY_PROJECT_PATH`.
- **`npx` / `node` not found:** Node not on `PATH` — reinstall Node LTS, restart
  the AI client.
- **Tools missing after config edit:** restart the MCP client.
- **Wrong project driven:** `UNITY_PROJECT_PATH` must be absolute and point at
  the folder that contains `Assets/`, `Packages/`, `ProjectSettings/`.
- **Wrong / old version installed:** re-read Step 0 `version.json` and rewrite
  the UPM pins + MCP `@<VERSION>`; do not keep a memorized older pin.

More detail: [Troubleshooting](../troubleshooting.md), [Dialog policy](../dialog-policy.md).

## Related docs

- [MCP client configuration](client-configuration.md) — client paths and copy-paste snippets
- [Manual setup](manual-setup.md) — human DIY installation flow
- [Development setup](development-setup.md) — local checkout / contributor path
- [Skills](../skills.md) — what the playbook covers after install
- [Extensions](../extensions.md) — optional domain packages (skipped by this flow)
- [Versioning](../versioning.md) — how pins stay in sync with releases
