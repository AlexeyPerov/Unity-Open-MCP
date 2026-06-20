# Unity Hub Pro

Unity Hub Pro is the desktop companion app for Unity Open MCP. It helps you manage projects, run the AI Setup wizard, and handle maintainer workflows from one UI.

## What it does

- Adds and classifies folders as Unity projects, Open MCP repositories, packages, or custom entries.
- Runs the AI Setup wizard for MCP onboarding and verification.
- Manages per-project launch options and environment variables.
- Shows git status and line-count views in project settings.
- Provides maintainer actions for Open MCP repositories (build, test, version bump, publish dry-run, publish).

[[SCREENSHOT:UNITY-HUB-PRO-PROJECTS-TAB]]

## Main workflows

### 1) Project management

- Add projects from disk.
- Open project-specific settings.
- Launch Unity with per-project configuration.

[[SCREENSHOT:UNITY-HUB-PRO-PROJECT-SETTINGS]]

### 2) AI setup

- Click the **AI** action on a Unity project row.
- Follow wizard steps for packages, MCP client config, and verification.
- Re-run verification when Unity or dependencies change.

[[SCREENSHOT:UNITY-HUB-PRO-AI-SETUP-BUTTON]]

For full setup steps, see [wizard-setup.md](wizard-setup.md).

### 3) Maintainer panel (Open MCP repositories)

- Run `npm run build` and `npm test` from the `mcp-server/` workspace.
- Bump package version.
- Run publish dry-run and publish with confirmation.

[[SCREENSHOT:UNITY-HUB-PRO-MAINTAINER-PANEL]]

## Installation

From `hub/`:

```bash
npm install
npm run tauri dev
```

For implementation-level details, see [`hub/README.md`](../hub/README.md).
