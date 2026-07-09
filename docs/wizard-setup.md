# Wizard Setup (Unity Hub Pro)

Use the AI Setup wizard in Unity Hub Pro to connect a Unity project to `unity-open-mcp`.

## Who is this for

This is the **simplest** path — no terminal, no hand-editing JSON, no Git URLs.
It's ideal if you're an artist or designer prototyping a game with AI, or a
developer who hasn't used Node/npm before. The wizard walks you through every
step, checks your environment, edits `manifest.json` and your MCP-client config
for you, and verifies the connection at the end.

For the do-it-yourself path (no Hub app), see [manual-setup.md](manual-setup.md).

## Requirements

- **Unity 2022.3 LTS or newer** (Unity 6 recommended).
- **Node.js 18 or newer** — only needed because the MCP server is a small Node
  program. Install it from <https://nodejs.org/> (the **LTS** button) if you
  don't have it. The wizard checks this and tells you if it's missing or too old.
- **Unity Hub Pro** — install it first. See [unity-hub-pro.md](unity-hub-pro.md)
  (download the installer for your OS from the GitHub Releases page).
- **An MCP client** (Cursor, Claude Desktop, Claude Code, OpenCode, ZCode,
  Cline, Codex, VS Code Copilot, Gemini CLI, or one of the other supported
  agents) — the AI tool you'll actually drive Unity from.

## Quick flow

1. **Install Unity Hub Pro** if you haven't (see [unity-hub-pro.md](unity-hub-pro.md)).
2. Open Unity Hub Pro and add your Unity project.
3. Click the **AI** action for that project. The button turns **green** when the agent is already installed and configured for that project; otherwise it is amber/blue and opens the wizard.
4. Pick a setup preset (or **Custom / skip**) on the preset step.
5. Complete the wizard — the Recommended preset can do everything in one click via **Express setup**.
6. Restart your MCP client.
7. Run a Unity MCP call to confirm connectivity.

Wizard choices (preset, MCP client, package toggles, bridge port, and other form fields) are remembered **per project** when you reopen the wizard; you always start again at the preset picker.

![plot](../screenshots/hub-ai-buttons.png)

## Wizard steps

The wizard shows a clickable progress strip at the top. Segments turn **green** when their checks already pass, so you can see at a glance which steps still need attention. You can click any segment to jump to that step; Back/Next still move sequentially. The recommended path needs no **Advanced (optional)** expansion on any step.

### Preset — Setup preset (optional)

Pick a preset to pre-fill the rest of the wizard, or choose **Custom / skip** to configure every step manually. Presets are starting points, not locks — you can change any field on later steps. The first viewport shows the three common choices; niche presets are behind **More presets**.

| Preset | Best for | Pre-fills |
|---|---|---|
| **Regular user (npm)** *(recommended)* | Developers who want the published npm package, no monorepo checkout | `npx -y unity-open-mcp@0.5.0`; bridge + verify from published sources; domain deps off; skill on |
| **Contributor (local checkout)** | Monorepo contributors hacking on bridge / verify / MCP server | Local checkout + `file:` packages from the clone; domain deps off; skill on. Build `mcp-server/` first (see [Development setup](development-setup.md)) |
| **Custom / skip** | Anyone who wants the wizard's built-in defaults | No pre-fills — identical to the manual flow |

**More presets** (behind the disclosure):

| Preset | Best for | Pre-fills |
|---|---|---|
| **Team CI** | Headless CI automation | Global npm install; **Manual / CLI snippet** client; skill skipped; configure token auth on the bridge for CI |
| **Secure / remote** | Non-localhost bridge access with restricted mutations | Published sources; skill on. Token auth, remote bind, and restricted tool groups are bridge-side controls — configure them from the bridge window after onboarding |

### Preflight — environment gate

Preflight checks your environment and is the gate for everything else. Checks split into two groups:

- **Blocking — must pass to continue:** valid Unity project layout, Unity version (minimum 2022.3 LTS; Unity 6+ recommended), Node.js 18+, and a writable `Packages/manifest.json`. These must all pass before Next is enabled.
- **Setup status — handled on later steps:** whether the bridge / verify packages, an MCP client config, and an agent skill are already installed. These read "Not yet" (not as failures) and turn green as you complete the later steps.

A single **Re-check** button re-runs project detection and the Node probe. Detection and the Node probe run off the UI thread and are bounded by timeouts, so a slow disk or a hung `node --version` spawn surfaces a real error instead of freezing the wizard. You can always close the wizard with **Cancel**, **Escape**, or the **×** button — detection stops in the background.

**Already configured?** If the bridge, verify, and an MCP client are all already set up for the project, Preflight offers a **You're ready** banner with a **Go to Verify** shortcut that skips the apply steps.

**Express setup:** when the environment checks pass, Preflight offers **Express setup** — one **Set up** click runs package install → MCP client write → launch/verify with a live progress list. The full step-by-step path stays available via the progress strip.

![plot](../screenshots/hub-wizard-1.png)

### MCP server source (optional / advanced)

Choose how the `unity-open-mcp` server is launched. The default (`npx`) needs no configuration here and the wizard auto-skips this step on the Recommended path — you only land here via the progress strip or the **Custom / skip** / **Contributor** preset.

- **npx (published npm)** *(default)* — fetches the latest `unity-open-mcp` from npm on first spawn. No repo clone needed.
- **Global install** — launches the bare `unity-open-mcp` binary (assumes `npm i -g unity-open-mcp`). Stable path for CI images.
- **Local checkout** — points at a cloned `unity-open-mcp` monorepo. Packages and skill copy use the toolkit root.
- **Custom entrypoint (advanced)** — a specific `mcp-server/dist/index.js` path for builds outside the toolkit root.

Pick one source; only the inputs for your choice are shown. Local checkout and custom entrypoint both require a validated toolkit root. If you use local checkout, build first:

```bash
cd mcp-server
npm install
npm run build
```

![plot](../screenshots/hub-wizard-2.png)

### Unity packages

Install or upgrade the bridge and verify packages in `Packages/manifest.json`. The required toggles are in the default section; a live diff preview re-computes as you change them.

**Advanced (optional)** (collapsed by default — the recommended path never needs it):

- **Use local packages** — install via `file:` paths relative to the toolkit root (typical for projects inside the monorepo).
- **Package version pin** — override the tag both packages pin to. Leave empty to install the version matching this Hub build.
- **Custom git URL** — replaces the toolkit root's git remote (for testing against a fork).
- **Unity domain dependencies (optional)** — NavMesh, Input System, ProBuilder. Domain tools are **bundled with the bridge**; they compile in automatically once the matching Unity package is present. Toggle the ones you want the wizard to add. Built-in modules (Particle System, Animation) ship with the Editor and need no manifest entry.

After onboarding, you can add or remove Unity domain dependencies at any time from the bridge window (**Tools → Unity Open MCP Bridge → Extensions → Optional Unity dependencies**) — one click per domain, no manifest editing. The Hub's project settings modal also shows a read-only installed / missing status per domain.

For the contributor / community-pack `file:` workflow, see [Development setup](development-setup.md).

![plot](../screenshots/hub-wizard-3.png)

### Configure AI client

Pick the AI client to connect. The first viewport shows a short **Popular** list (Cursor, Claude Desktop, VS Code Copilot, Claude Code, Manual); the full catalog is behind **Show all clients** with a search box.

Each option shows whether it writes a config file, is CLI-only, or copies a JSON snippet, and has a tooltip describing the config format and target path:

- Cursor / Claude Desktop / Cline / most editor agents: `mcpServers` JSON
- VS Code Copilot / Visual Studio Copilot: `servers` JSON (project `.vscode/mcp.json` / `.vs/mcp.json`)
- OpenCode: `mcp` + `$schema` JSON
- ZCode: `mcp.servers` + `type:stdio` JSON with skills under `.agents/skills/`
- Codex: TOML `[mcp_servers.unity-open-mcp]` table in `.codex/config.toml`
- Claude Code: CLI-only — renders a `claude mcp add` command (no file)

Review the generated config preview (JSON or TOML, or a CLI command for Claude Code), then write it. Writes are merge-safe: unrelated keys and sibling MCP servers are preserved, and a `.bak` backup is left next to the original file.

**Advanced (optional):** the **Bridge HTTP port** override lives here (collapsed by default; the port auto-derives from the project path).

![plot](../screenshots/hub-wizard-4.png)

### Agent skill (optional)

The agent skill gives your AI client workflow guidance for the Unity MCP tools — the mutate→gate→fix loop, capabilities-first discovery, and the agent senses (tests, profiler, screenshots). Two options write to the same project-relative skill folder(s) for your selected client (ZCode → `.agents/skills/`, Cursor → `.cursor/skills/`, etc.):

- **Copy skill** — installs the template playbook (`skills/unity-open-mcp/SKILL.md`). The same workflow guidance for every project; no build required.
- **Generate project skill** — produces a project-specific `SKILL.md` that merges the template playbook with this project's inventory (Unity version, installed packages, key MonoBehaviour / ScriptableObject types). Requires the built MCP server (`mcp-server/dist/index.js`).

Both honor an explicit overwrite checkbox; existing files are backed up to `*.bak` before they are replaced. You can copy only, generate only, or both (generate overwrites the same path the copy writes, so confirm the overwrite).

The **Team CI** preset auto-skips this step — CI agents typically don't need a desktop skill file.

![plot](../screenshots/hub-wizard-5.png)

### Launch and verify

- Launch Unity
- Wait for compile and bridge readiness
- Finish when health checks pass

While waiting, the MCP server auto-dismisses common Unity startup modals (Safe
Mode, version mismatch, and similar) per `UNITY_OPEN_MCP_DIALOG_POLICY`. If this
step stalls on a modal, see [Dialog policy](dialog-policy.md).

![plot](../screenshots/hub-wizard-6.png)

## Clear AI Setup

The wizard footer has a yellow **Clear AI Setup** button (bottom-right). It removes every artifact the wizard wrote for the current project, after a confirmation prompt:

- the bridge + verify entries from `Packages/manifest.json`
- the `unity-open-mcp` entry from every known MCP client config (project-scoped configs unconditionally; global configs only the entry whose project path matches this one)
- the copied agent-skill `SKILL.md` files

A `.bak` backup is created next to each changed file. Per-target failures are reported inline rather than aborting the whole pass. This cannot be undone.

## Troubleshooting

- **AI action missing on a project row:** re-check the project path and that
  Unity version detection passed on the Preflight step.
- **Package install disabled (Next greyed out):** resolve the Preflight blocking
  checks first — the project must meet the minimum Unity version, have Node.js
  18+, and a writable `Packages/manifest.json`.
- **Tools unavailable in the client after finishing:** restart the client. Most
  MCP clients only read their config at startup.
- **Bridge unavailable on Launch and verify:** verify the project path is right,
  Unity actually launched, and finished compiling. The health check runs while
  Unity is running. If a startup modal blocks progress (Safe Mode, project
  upgrade), see [Dialog policy](dialog-policy.md).
- **Re-check does nothing:** it refreshes the on-disk snapshot only — bridge
  reachability is checked on Launch and verify while Unity is running.
- **npx first run looks slow:** expected — the server downloads on first launch.

## Related docs

- [Dialog policy](dialog-policy.md)
- [Manual setup](manual-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
