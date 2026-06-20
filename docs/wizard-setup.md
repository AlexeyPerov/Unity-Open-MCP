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
2. Click the **AI** action for that project.
3. Complete wizard steps 1–6.
4. Restart your MCP client.
5. Run a Unity MCP call to confirm connectivity.

[[SCREENSHOT:WIZARD-OPEN]]

## Wizard steps

### Step 1 — Project check

- Valid Unity project layout
- Unity version detection
- Existing bridge/verify package check
- Existing MCP config check

[[SCREENSHOT:WIZARD-STEP1-PROJECT-CHECK]]

### Step 2 — Environment

- Node.js check
- MCP launch mode:
  - default: `npx -y unity-open-mcp@latest`
  - optional: global install
  - optional: local checkout path

If you use local checkout, build first:

```bash
cd mcp-server
npm install
npm run build
```

[[SCREENSHOT:WIZARD-STEP2-ENVIRONMENT]]

### Step 3 — Unity packages

- Install or upgrade bridge and verify packages
- Review manifest diff before apply

[[SCREENSHOT:WIZARD-STEP3-PACKAGES-DIFF]]

### Step 4 — MCP client config

- Choose client preset
- Review generated config preview
- Write config to target location

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

## Troubleshooting

- AI action missing: re-check project path and Unity version detection.
- Package install disabled: resolve environment validation first.
- Tools unavailable in client: restart client after writing config.
- Bridge unavailable: verify project path, Unity runtime state, and Node path.

## Related docs

- [Manual setup](manual-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
