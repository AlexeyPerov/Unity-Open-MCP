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
- **An MCP client** (Cursor, Claude Desktop, Claude Code, OpenCode, ZCode, or
  similar) — the AI tool you'll actually drive Unity from.

## Quick flow

1. **Install Unity Hub Pro** if you haven't (see [unity-hub-pro.md](unity-hub-pro.md)).
2. Open Unity Hub Pro and add your Unity project.
3. Click the **AI** action for that project. The button turns **green** when the agent is already installed and configured for that project; otherwise it is amber/blue and opens the wizard.
4. Pick a setup preset (or **Custom / skip**) on Step 1.
5. Complete the wizard steps.
6. Restart your MCP client.
7. Run a Unity MCP call to confirm connectivity.

Wizard choices (preset, MCP client, package toggles, bridge port, and other form fields) are remembered **per project** when you reopen the wizard; you always start again at the preset picker.

![plot](../screenshots/hub-ai-buttons.png)

## Wizard steps

The wizard shows a clickable progress strip at the top. Segments turn **green** when their checks already pass, so you can see at a glance which steps still need attention. You can click any segment to jump to that step; Back/Next still move sequentially. The wizard has seven steps plus a Done screen.

### Step 1 — Setup preset (optional)

Pick a preset to pre-fill the rest of the wizard, or choose **Custom / skip** to configure every step manually. Presets are starting points, not locks — you can change any field on later steps.

| Preset | Best for | Pre-fills |
|---|---|---|
| **Regular user (npm)** *(recommended)* | Developers who want the published npm package, no monorepo checkout | `npx -y unity-open-mcp@latest`; bridge + verify from published sources; domain deps off; skill on |
| **Contributor (local checkout)** | Monorepo contributors hacking on bridge / verify / MCP server | Local checkout + `file:` packages from the clone; domain deps off; skill on. Build `mcp-server/` first (see [Development setup](development-setup.md)) |
| **Solo dev** | Fastest fully-tooled local agent loop | `npx`; bridge + verify + NavMesh + Input System + ProBuilder; **Cursor** pre-selected; skill on |
| **Team CI** | Headless CI automation | Global npm install; **Manual / CLI snippet** client; skill skipped; configure token auth on the bridge for CI |
| **Secure / remote** | Non-localhost bridge access with restricted mutations | Published sources; skill on. Token auth, remote bind, and restricted tool groups are bridge-side controls — configure them from the bridge window after onboarding |
| **Custom / skip** | Anyone who wants the wizard's built-in defaults | No pre-fills — identical to the manual flow |

### Step 2 — Project detection + diagnostics

- Valid Unity project layout
- Unity version detection (minimum 2022.3 LTS; Unity 6+ recommended)
- Node.js check (18+) — required to launch the MCP server
- Writable `Packages/manifest.json`
- Existing bridge/verify package check
- Existing MCP config check

A **Diagnostics** panel at the top runs the same checks in a one-click-remediation list (green check / red cross). Each failing row carries a short hint on how to fix it. Use **Run diagnostics** to re-run project detection + the Node probe on demand.

This step is the environment gate: the **Next** button is disabled until the project is valid, meets the minimum Unity version, has a writable manifest, and Node.js 18+ is detected. **Re-detect** refreshes the snapshot from disk and shows a confirmation when it completes.

![plot](../screenshots/hub-wizard-1.png)

### Step 3 — MCP server source

Choose how the `unity-open-mcp` server is launched. Not sure? Leave it on the
default — `npx` downloads and runs the latest version automatically, no extra
install step.

- default: `npx -y unity-open-mcp@latest` ← recommended for most users
- optional: global install (`npm i -g unity-open-mcp`) — installs once, then the client launches it directly.
- optional: local checkout path (only if you cloned the `unity-open-mcp` monorepo to hack on it).

If you use local checkout, build first:

```bash
cd mcp-server
npm install
npm run build
```

![plot](../screenshots/hub-wizard-2.png)

### Step 4 — Unity packages

- Install or upgrade bridge and verify packages
- Optional: install Unity domain dependencies (NavMesh, Input System, ProBuilder) that activate the bundled domain tools
- Review manifest diff before apply

Domain tools (NavMesh, Input System, ProBuilder, Particle System, Animation) are **bundled with the bridge** — there is no separate extension-pack install. They compile in automatically once the matching Unity package is present in the project. Toggle the Unity domain dependencies you want the wizard to add to `Packages/manifest.json`; the bridge's embedded tools register after Unity re-imports the manifest. Built-in Unity modules (Particle System, Animation) ship with the Editor and need no manifest entry — the wizard lists them for visibility only.

After onboarding, you can add or remove Unity domain dependencies at any time from the bridge window (**Tools → Unity Open MCP Bridge → Extensions → Optional Unity dependencies**) — one click per domain, no manifest editing. The Hub's project settings modal also shows a read-only installed / missing status per domain.

For the contributor / community-pack `file:` workflow, see [Development setup](development-setup.md).

![plot](../screenshots/hub-wizard-3.png)[mcp-tools.md](api/mcp-tools.md)

### Step 5 — MCP client config

- Choose a client preset. Each option has a tooltip describing the config format and other popular agents that share it (for example, Cursor and Claude Desktop share the `mcpServers` JSON shape; OpenCode uses `mcp` + `$schema`; ZCode uses `mcp.servers` + `type:stdio` with skills under `.agents/skills/`).
- Review the generated config preview
- Write config to the target location (or copy a CLI command / JSON snippet for Claude Code / Manual)

![plot](../screenshots/hub-wizard-4.png)

### Step 6 — Agent skill (optional)

- Copy `SKILL.md` for your selected client
- Optional overwrite with backup

The **Team CI** preset auto-skips this step — CI agents typically don't need a desktop skill file.

![plot](../screenshots/hub-wizard-5.png)

### Step 7 — Launch and verify

- Launch Unity
- Wait for compile and bridge readiness
- Finish when health checks pass

While waiting, the MCP server auto-dismisses common Unity startup modals (Safe
Mode, version mismatch, and similar) per `UNITY_OPEN_MCP_DIALOG_POLICY`. If
Step 7 stalls on a modal, see [Dialog policy](dialog-policy.md).

![plot](../screenshots/hub-wizard-6.png)

## Clear AI Setup

The wizard footer has a yellow **Clear AI Setup** button (bottom-right). It removes every artifact the wizard wrote for the current project, after a confirmation prompt:

- the bridge + verify entries from `Packages/manifest.json`
- the `unity-open-mcp` entry from every known MCP client config (project-scoped configs unconditionally; global configs only the entry whose project path matches this one)
- the copied agent-skill `SKILL.md` files

A `.bak` backup is created next to each changed file. Per-target failures are reported inline rather than aborting the whole pass. This cannot be undone.

## Troubleshooting

- **AI action missing on a project row:** re-check the project path and that
  Unity version detection passed in Step 2.
- **Package install disabled (Next greyed out):** resolve the Step 2 environment
  checks first — the project must meet the minimum Unity version, have Node.js
  18+, and a writable `Packages/manifest.json`.
- **Tools unavailable in the client after finishing:** restart the client. Most
  MCP clients only read their config at startup.
- **Bridge unavailable in Step 7:** verify the project path is right, Unity
  actually launched, and finished compiling. The health check runs while Unity is
  running. If a startup modal blocks progress (Safe Mode, project upgrade),
  see [Dialog policy](dialog-policy.md).
- **Re-detect does nothing:** it refreshes the on-disk snapshot only — bridge
  reachability is checked in Step 7 while Unity is running.
- **npx first run looks slow:** expected — the server downloads on first launch.

## Related docs

- [Dialog policy](dialog-policy.md)
- [Manual setup](manual-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
