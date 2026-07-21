# Manual Setup

Set up Unity Open MCP without Unity Hub Pro.

## Who is this for

This is the most typical way to set up Unity Open MCP from the terminal: add the
Unity packages, then point your MCP client at the server.

Prefer a GUI? Use the **wizard** path in [wizard-setup.md](wizard-setup.md).
Want an experimental AI-agent install instead? See [agent-setup.md](agent-setup.md).
Working on this repository itself? See [development-setup.md](development-setup.md).

Unity Open MCP has two halves you must install: the **Unity side** (bridge +
verify packages in the Editor) and the **AI side** (a small Node MCP server your
client launches). The steps below cover each in turn.

## Requirements

- **Unity 2022.3 LTS or newer**.
- **Node.js 18 or newer** — required only so your MCP client can launch the
  server (`npx`). Install from <https://nodejs.org/> (LTS), restart the
  terminal, and verify with `node --version`.
- **An MCP client that supports stdio MCP servers** — Cursor, Claude Desktop,
  Claude Code, OpenCode, ZCode, Cline, Codex, VS Code Copilot, Gemini CLI, or
  any compatible client. Copy-paste snippets live in
  [MCP client configuration](client-configuration.md).

## 1) Add the Unity packages

Open `Packages/manifest.json` in your Unity project (for example
`MyGame/Packages/manifest.json`) and add these entries to `dependencies`:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.7.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.7.0"
  }
}
```

Pin the same version on the MCP server side (step 2). See
[Versioning](../versioning.md) when upgrading.

Optional domain packages (NavMesh, Input System, …) and the in-Editor install
panel are covered in [Extensions](../extensions.md) — not required for a basic
install.

## 2) Configure your MCP client

1. Open [MCP client configuration](client-configuration.md).
2. Find your client in the table and note the config file path.
3. Copy that client’s snippet.
4. Replace `/absolute/path/to/project` with the absolute path to your Unity
   project root (the folder with `Assets/`, `Packages/`, `ProjectSettings/`).
5. Save the file (if it already has other MCP servers, add only the
   `unity-open-mcp` entry).

## 3) Open Unity and verify

1. Open the **same** Unity project (`UNITY_PROJECT_PATH`) in the Editor.
2. Wait for scripts to compile (status bar, bottom-right).
3. Restart your MCP client so it re-reads the config from step 2.
4. In Unity, open **Tools → Unity Open MCP Bridge** — it should show
   **connected**. If it does, you are done.

Ask your AI client to run any Unity Open MCP tool (for example, list
capabilities). If it returns Unity data, both halves are talking.

## Optional next steps

- Domain packages and activation — [Extensions](../extensions.md)
- CI / CLI automation — [CLI and automation](../api/cli-automation.md)
- Startup modals on unattended machines — [Dialog policy](../dialog-policy.md)

## Troubleshooting

Confirm Unity is open on the same absolute `UNITY_PROJECT_PATH`, compilation
finished, and the MCP client was restarted. Then follow
[Troubleshooting](../troubleshooting.md). Modal policy and macOS Accessibility
details are in [Dialog policy](../dialog-policy.md).

## Related docs

- [Agent setup](agent-setup.md)
- [MCP client configuration](client-configuration.md)
- [Wizard setup](wizard-setup.md)
- [Development setup](development-setup.md)
- [Unity Hub Pro](../unity-hub-pro.md)
