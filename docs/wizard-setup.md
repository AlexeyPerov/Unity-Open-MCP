# MCP Setup with Unity Hub Pro

Step-by-step guide to connect an AI client (Cursor, Claude Desktop, OpenCode, and others) to a Unity project using the **AI Setup wizard** in Unity Hub Pro.

For setup without Unity Hub Pro, see [manual-setup.md](manual-setup.md).

## What you need

| Requirement | Notes |
|---|---|
| Unity 6 (6000.0+) | Required by the bridge package. |
| Node.js 18+ | Runs the MCP server (`mcp-server/dist/index.js`). |
| This repository | Clone or download the `unity-open-mcp` monorepo. |
| Unity Hub Pro | Desktop app in [hub/](../hub/). |
| An MCP client | Cursor, Claude Desktop, OpenCode, or any client that supports MCP stdio servers. |

## 1. Build the MCP server

From the repository root:

```bash
cd mcp-server
npm install
npm run build
```

Confirm that `mcp-server/dist/index.js` exists. The wizard checks for this file when validating the toolkit root.

## 2. Run Unity Hub Pro

From the `hub/` directory:

```bash
npm install
npm run tauri dev
```

See [hub/README.md](../hub/README.md) for platform-specific notes.

## 3. Add your Unity project

1. Open the **Projects** tab.
2. Click **Add Project** and choose your Unity project folder (must contain `Assets/` and `ProjectSettings/`).
3. Optionally add the bundled [demo](../demo/) project to try the flow without a separate checkout.

## 4. Open the AI Setup wizard

1. In the project list, click **AI** on the row for your project (to the left of the gear **Settings** button).
2. The wizard opens for that project only.

You can also select a project and use the toolbar **AI Setup** button, or right-click a project row and choose **Configure Agent Bridge…**.

## 5. Walk through the wizard

### Step 1 — Project check

The wizard confirms:

- Valid Unity project layout
- Unity version (6000+)
- Whether bridge/verify packages are already in `Packages/manifest.json`
- Whether an MCP client config already mentions `unity-open-mcp`

Fix any hard blocks (missing version file, invalid path) before continuing.

### Step 2 — Environment

1. **Node.js** — must report version 18 or higher.
2. **AI toolkit root** — pick the root of this repository (the folder that contains `packages/`, `mcp-server/`, and `hub/`).
3. Click **Validate** and resolve any failed fingerprint checks (missing `mcp-server/dist/index.js` usually means you skipped step 1).
4. Ensure the project’s `Packages/manifest.json` is writable.

### Step 3 — Install Unity packages

1. Leave **Install Unity Open MCP Bridge** and **Install Unity Open MCP Verify** enabled unless you already manage those packages yourself.
2. Review the manifest diff preview.
3. If packages are already installed (for example in the demo project with local `file:` paths), click **Next** or **Skip to Step 4** — no rewrite is required.
4. If an upgrade is proposed, check **I understand the manifest will be upgraded** before clicking **Install** or **Upgrade manifest**.

When the demo project lives inside this monorepo, the wizard uses local `file:` package paths instead of git URLs.

### Step 4 — MCP client config

1. Choose your MCP client (Cursor, Claude Desktop, OpenCode, Claude Code, or Manual).
2. Review the live preview (JSON entry, CLI command, or copyable snippet).
3. Click **Write config** (or follow the CLI instructions for Claude Code).
4. Optional: enable advanced options such as a custom `mcp-server/dist/index.js` path if you built the server elsewhere.

The wizard writes a `unity-open-mcp` server entry with at least:

- `command`: `node`
- `args`: path to `mcp-server/dist/index.js`
- `env.UNITY_PROJECT_PATH`: your project’s absolute path

See [tools.md](tools.md) for optional environment variables.

### Step 5 — Launch and verify

1. Click **Launch Unity and verify** (or use your usual launch flow with the bridge port shown).
2. Wait for compile + bridge `/ping` checks (up to about two minutes).
3. When `/ping` succeeds, finish the wizard.

If verification times out, you can **Skip to Done** and retry after Unity finishes compiling.

### Done screen

The summary lists manifest changes, MCP config path, copied skill files (when applicable), and bridge status. Use **Open in Cursor** or **Open in OpenCode** if your client is configured.

## 6. Restart your MCP client

Most clients load MCP servers only at startup:

- **Cursor** — restart Cursor or reload MCP servers from settings after the config file is written.
- **Claude Desktop** — quit and reopen the app.
- **OpenCode** — restart the TUI or reload global/project MCP config as you normally would.

Config file locations depend on the client and scope you chose in Step 4. The wizard shows the exact target path in the preview.

## 7. Confirm the connection

1. Open the Unity project in the Editor (bridge listens on the configured port, default `19120`).
2. In your AI client, invoke an MCP tool or resource tied to Unity (for example a bridge `/ping`-backed check or a project resource).
3. If tools fail with connection errors, check:
   - Unity is running with the project open
   - `UNITY_PROJECT_PATH` in the MCP config matches the project root
   - `mcp-server/dist/index.js` exists and Node 18+ is on your `PATH`
   - No firewall is blocking localhost on the bridge port

For HTTP-level bridge behavior, see [api/bridge-http.md](api/bridge-http.md). For MCP tool routing, see [api/mcp-tools.md](api/mcp-tools.md).

## Troubleshooting

| Symptom | What to try |
|---|---|
| **AI** button missing on a project row | Path must exist, project must have a detected Unity version, and must not be marked stale. |
| Toolkit validation fails | Run `npm run build` in `mcp-server/`. Point toolkit root at the monorepo root, not `hub/` or `mcp-server/` alone. |
| Step 3 buttons disabled | Validate toolkit root in Step 2; ensure at least one package is selected; for upgrades, confirm the upgrade checkbox. |
| MCP tools unavailable in the client | Restart the client after Step 4; confirm the config file path matches your OS and client. |
| Bridge `/ping` fails | Launch Unity for the same project; wait for compilation; confirm port env vars match between MCP config and Unity. |

## Related docs

- [Manual setup (no Hub)](manual-setup.md) — install packages and MCP config by hand.
- [Architecture](architecture.md) — how hub, bridge, verify, and MCP server fit together.
- [API index](api.md) — contracts and protocol surfaces.
- [Tools and dependencies](tools.md) — scripts, env vars, and toolchain versions.
