# Wizard Setup (Unity Hub Pro)

Use the AI Setup wizard in Unity Hub Pro to connect a Unity project to `unity-open-mcp`.

For non-wizard setup, see [manual-setup.md](manual-setup.md).

## Requirements

- Unity 2022.3 LTS or newer (Unity 6 recommended)
- Node.js 18 or newer
- Unity Hub Pro
- MCP client (Cursor, Claude Desktop, OpenCode, ZCode, or similar)

## Quick flow

1. Open Unity Hub Pro and add your Unity project.
2. Click the **AI** action for that project. The button turns **green** when the agent is already installed and configured for that project; otherwise it is amber/blue and opens the wizard.
3. Complete the wizard steps.
4. Restart your MCP client.
5. Run a Unity MCP call to confirm connectivity.

[[SCREENSHOT:WIZARD-OPEN]]

## Wizard steps

The wizard shows a clickable progress strip at the top. Segments turn **green** when their checks already pass, so you can see at a glance which steps still need attention. You can click any segment to jump to that step; Back/Next still move sequentially.

### Step 1 — Project detection

- Valid Unity project layout
- Unity version detection (minimum 2022.3 LTS; Unity 6+ recommended)
- Node.js check (18+) — required to launch the MCP server
- Writable `Packages/manifest.json`
- Existing bridge/verify package check
- Existing MCP config check

This step is the environment gate: the **Next** button is disabled until the project is valid, meets the minimum Unity version, has a writable manifest, and Node.js 18+ is detected. **Re-detect** refreshes the snapshot from disk and shows a confirmation when it completes.

[[SCREENSHOT:WIZARD-STEP1-PROJECT-CHECK]]

### Step 2 — MCP server source

Choose how the `unity-open-mcp` server is launched:

- default: `npx -y unity-open-mcp@latest`
- optional: global install (`npm i -g unity-open-mcp`)
- optional: local checkout path (cloned `unity-open-mcp` monorepo)

If you use local checkout, build first:

```bash
cd mcp-server
npm install
npm run build
```

[[SCREENSHOT:WIZARD-STEP2-ENVIRONMENT]]

### Step 3 — Unity packages

- Install or upgrade bridge and verify packages
- Optional: install Unity domain dependencies (NavMesh, Input System, ProBuilder) that activate the bundled domain tools
- Review manifest diff before apply

Domain tools (NavMesh, Input System, ProBuilder, Particle System, Animation) are **bundled with the bridge** — there is no separate extension-pack install. They compile in automatically once the matching Unity package is present in the project. Toggle the Unity domain dependencies you want the wizard to add to `Packages/manifest.json`; the bridge's embedded tools register after Unity re-imports the manifest. Built-in Unity modules (Particle System, Animation) ship with the Editor and need no manifest entry — the wizard lists them for visibility only.

After onboarding, you can add or remove Unity domain dependencies at any time from the bridge window (**Tools → Unity Open MCP Bridge → Extensions → Optional Unity dependencies**) — one click per domain, no manifest editing. The Hub's project settings modal also shows a read-only installed / missing status per domain.

For the contributor / community-pack `file:` workflow, see [Manual setup](manual-setup.md#contributor--community-pack-workflow).

[[SCREENSHOT:WIZARD-STEP3-PACKAGES-DIFF]]

### Step 4 — MCP client config

- Choose a client preset. Each option has a tooltip describing the config format and other popular agents that share it (for example, Cursor and Claude Desktop share the `mcpServers` JSON shape; OpenCode uses `mcp` + `$schema`; ZCode uses `mcp.servers` + `type:stdio` with skills under `.agents/skills/`).
- Review the generated config preview
- Write config to the target location (or copy a CLI command / JSON snippet for Claude Code / Manual)

[[SCREENSHOT:WIZARD-STEP4-MCP-CONFIG-PREVIEW]]

### Step 5 — Agent skill (optional)

- Copy `SKILL.md` for your selected client
- Optional overwrite with backup

[[SCREENSHOT:WIZARD-STEP5-SKILL]]

### Step 6 — Launch and verify

- Launch Unity
- Wait for compile and bridge readiness
- Finish when health checks pass

[[SCREENSHOT:WIZARD-STEP6-VERIFY]]

## Clear AI Setup

The wizard footer has a yellow **Clear AI Setup** button (bottom-right). It removes every artifact the wizard wrote for the current project, after a confirmation prompt:

- the bridge + verify entries from `Packages/manifest.json`
- the `unity-open-mcp` entry from every known MCP client config (project-scoped configs unconditionally; global configs only the entry whose project path matches this one)
- the copied agent-skill `SKILL.md` files

A `.bak` backup is created next to each changed file. Per-target failures are reported inline rather than aborting the whole pass. This cannot be undone.

## Troubleshooting

- AI action missing: re-check project path and Unity version detection.
- Package install disabled: resolve the Step 1 environment checks first (Unity version, Node.js, writable manifest).
- Tools unavailable in client: restart client after writing config.
- Bridge unavailable: verify project path, Unity runtime state, and Node path.
- Re-detect does nothing: it refreshes the on-disk snapshot only — bridge reachability is checked in Step 6 while Unity is running.

## Related docs

- [Manual setup](manual-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
