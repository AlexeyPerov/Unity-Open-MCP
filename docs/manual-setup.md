# Manual Setup

Set up Unity Open MCP without Unity Hub Pro.

For guided setup, see [wizard-setup.md](wizard-setup.md).

## Requirements

- Unity 2022.3 LTS or newer (Unity 6 recommended)
- Node.js 18 or newer
- MCP client that supports stdio MCP servers

## 1) Add Unity packages

Edit `Packages/manifest.json` in your Unity project.

### Git install (recommended for external projects)

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.0.0"
  }
}
```

### Local `file:` install (monorepo projects)

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

### Optional Unity domain dependencies

Domain tools (NavMesh, Input System, ProBuilder, Particle System, Animation) are **bundled with the bridge** — they activate automatically once the matching Unity package is present. Add the Unity dependencies you want under `dependencies`:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.0.0",
    "com.unity.ai.navigation": "2.0.0",
    "com.unity.inputsystem": "1.7.0",
    "com.unity.probuilder": "6.0.9"
  }
}
```

Particle System and Animation are built-in Unity modules — no manifest entry is needed, the tools compile in as soon as the module is enabled in the Editor. See [extensions.md](extensions.md) for the full domain catalog and the define-symbol model.

### Optional dependencies (in-Editor)

Once the bridge is installed, you can add or remove Unity domain dependencies without editing the manifest by hand. Open **Tools → Unity Open MCP Bridge → Extensions** and use the **Optional Unity dependencies** panel: one row per domain shows installed / missing status, with a one-click **Install…** / **Remove…** action for each UPM package. Unity re-imports the manifest and recompiles; the embedded tools register (or stop compiling) on the next domain reload. The Unity Hub Pro project settings modal mirrors this as a read-only status panel.

## 2) Configure your MCP client

Use `npx` by default:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@latest"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

[[SCREENSHOT:MANUAL-SETUP-MCP-CONFIG]]

### Environment variables

- Required: `UNITY_PROJECT_PATH`
- Optional: `UNITY_OPEN_MCP_BRIDGE_PORT`
- Optional: `UNITY_PATH` (batch fallback)
- Optional launch-dialog handling:
  - `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1`
  - `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS`
  - `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS`

## 3) Launch Unity and verify

1. Open the same Unity project in the Editor.
2. Wait for scripts to compile.
3. Restart your MCP client.
4. Validate bridge status:

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

## 4) Optional CLI (CI and automation)

```bash
npx -y unity-open-mcp@latest wait-for-ready --project /path/to/MyGame
npx -y unity-open-mcp@latest run-tool unity_open_mcp_capabilities --project /path/to/MyGame --json
```

## Troubleshooting

- Connection refused: confirm Unity is open with the same project path.
- Tools missing in client: restart client after config changes.
- Compile-error launch dialog blocks startup: dismiss manually or keep auto-dismiss enabled.
- Slow first run with `npx`: expected package download behavior.

## Contributor / community-pack workflow

The default install path uses the bridge plus optional Unity domain dependencies (above). Two advanced workflows exist for contributors and third-party pack authors:

### Local monorepo checkout (contributors)

Clone `unity-open-mcp` side-by-side with your Unity project and point the manifest at the local package folders via `file:` URLs. The Hub wizard's **Use local checkout** (Step 2) + **Use local packages** (Step 3) toggles automate this; the manual equivalent is:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../unity-open-mcp/packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../unity-open-mcp/packages/verify"
  }
}
```

Edit source under `packages/bridge/Editor/` and Unity recompiles on focus. Build the MCP server once with `npm run build` in `mcp-server/` so the wizard's Step 2 fingerprint check passes.

### Community domain packs (third-party)

`packages/extensions/` is the home for **third-party / community** domain packs that are not shipped with the bridge. The shipped domains (Nav, Input, ProBuilder, Particles, Animation) are embedded in the bridge and **must not** also be installed as separate `com.alexeyperov.unity-open-mcp-ext-*` packs — that would double-register tool IDs. See [extensions.md](extensions.md#legacy--community-domain-packs-advanced-path) for the community-pack contract.

To install a community pack, add its UPM id under `dependencies`:

```json
{
  "dependencies": {
    "com.example.my-mcp-ext": "file:../../my-mcp-ext"
  }
}
```

## Related docs

- [Wizard setup](wizard-setup.md)
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
