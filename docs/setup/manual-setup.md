# Manual Setup

Set up Unity Open MCP without Unity Hub Pro.

## Who is this for

This is the **do-it-yourself** path. It's a good fit if you:

- already edit Unity `manifest.json` and MCP-client config files by hand, or
- prefer to control exactly what gets installed and pinned, or
- are on a headless/CI machine where the Hub GUI isn't an option.

Prefer letting an AI agent do the install? See [agent-setup.md](agent-setup.md)
(paste the README prompt into your AI client).

If you've never opened a terminal or edited JSON, the **wizard** path is much
easier — see [wizard-setup.md](wizard-setup.md). It does everything below
automatically and explains each step.

## How Unity Open MCP fits together (two halves)

Unity Open MCP has two independent halves that talk to each other over your
machine's loopback network:

| Half | Lives in | Installed from | What it does |
|---|---|---|---|
| **AI side** | a small Node program (the MCP server) | **npm** (`npx`) | Your AI client (Cursor, Claude, …) launches it; it exposes the MCP tools. |
| **Unity side** | two Unity Editor packages (bridge + verify) | **Unity Package Manager** (Git URL) | Runs inside Unity and carries out each tool call. |

You need **both**. The AI side never touches Unity directly — it asks the Unity
side over HTTP. The steps below install each half in turn.

For working on the packages themselves (local checkout, building the MCP server,
contributor and maintainer workflows), see [development-setup.md](development-setup.md).

## Requirements

- **Unity 2022.3 LTS or newer**.
- **Node.js 18 or newer** — this is *only* needed because the MCP server is a
  small Node program your AI client launches in the background. You will **not**
  be writing JavaScript or running it interactively.
  - Don't have Node? Install it from <https://nodejs.org/> (the **LTS** button is
    fine). Restart your terminal afterwards so the `node`/`npx` commands are on
    your PATH. Verify with `node --version`.
- **An MCP client that supports stdio MCP servers** — Cursor, Claude Desktop,
  Claude Code, OpenCode, ZCode, Cline, Codex, VS Code Copilot, Gemini CLI, or
  any compatible MCP client. The Hub wizard configures all of these in one
  click; the snippets below cover the most common manual shapes.

## 1) Add the Unity packages (Unity side)

Open `Packages/manifest.json` in your Unity project (it's at the root of the
project folder, e.g. `MyGame/Packages/manifest.json`) and add two entries to the
`dependencies` object.

### Git install (recommended)

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.6.1",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.6.1"
  }
}
```

### Optional Unity domain dependencies

Domain tools are bundled with the bridge. Some need a matching Unity package
before they compile in, and most need explicit session activation. Use
[Extensions](../extensions.md) for the canonical dependency catalog,
activation table, and package examples.

### Optional dependencies (in-Editor)

Once the bridge is installed, you can add or remove Unity domain dependencies without editing the manifest by hand. Open **Tools → Unity Open MCP Bridge → Extensions** and use the **Optional Unity dependencies** panel: one row per domain shows installed / missing status, with a one-click **Install…** / **Remove…** action for each UPM package. Unity re-imports the manifest and recompiles; the embedded tools register (or stop compiling) on the next domain reload. The Unity Hub Pro project settings modal mirrors this as a read-only status panel.

## 2) Configure your MCP client (AI side)

Now point your AI client at the MCP server. Use
[MCP client configuration](client-configuration.md) for the canonical
client-specific path and JSON/TOML/CLI envelope. Merge the documented
`unity-open-mcp` entry into your client without replacing unrelated settings.

What that means:

- `npx -y unity-open-mcp@0.6.1` downloads and launches that exact MCP server
  version from npm. The `-y` accepts the first-run prompt; pinning the version
  keeps the server in lockstep with the bridge and verify packages, which share
  the same number. To move to a newer release, bump the version here and in your
  Unity `manifest.json` together — see [Versioning](../versioning.md).
- **First run can take 10–60 seconds** while the package is downloaded — that's
  normal, not a hang. Subsequent launches are fast.
- Prefer a one-time install instead? `npm install -g unity-open-mcp` puts it on
  your PATH, then use `"command": "unity-open-mcp"` (no `npx`/`args`). You update
  manually with `npm update -g unity-open-mcp`.

> ⚠️ **`UNITY_PROJECT_PATH` is the #1 setup gotcha.** It must be the **absolute**
> path to your Unity project root (the folder that contains `Assets/`,
> `Packages/`, and `ProjectSettings/`). The server exits immediately if it's
> missing, and a wrong path means the AI can drive a different Unity project
> than the one you have open.

Core project, port, and batch environment variables are summarized in the
shared client reference. The complete startup-dialog environment-variable
matrix and safety policy live only in [Dialog policy](../dialog-policy.md).

## 3) Launch Unity and verify

1. Open the **same** Unity project (the one `UNITY_PROJECT_PATH` points at) in the Editor.
2. Wait for scripts to compile — watch the status bar in the bottom-right corner.
3. Restart your MCP client (so it re-reads the config from step 2).
4. The easiest check: in Unity, open **Tools → Unity Open MCP Bridge** — the
   window should show a **connected** status. If it does, you're done.
5. Prefer the terminal? Confirm the bridge is reachable:

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

The port is derived from your project path automatically; if you set
`UNITY_OPEN_MCP_BRIDGE_PORT`, use that. You can also read it from the bridge
window in step 4. A successful ping returns JSON with `"connected": true`.

Finally, ask your AI client to run any Unity Open MCP tool (for example, have it
list the available capabilities) — if it responds with Unity data, the two halves
are talking.

## 4) Optional CLI (CI and automation)

```bash
npx -y unity-open-mcp@0.6.1 wait-for-ready --project /path/to/MyGame
npx -y unity-open-mcp@0.6.1 run-tool unity_open_mcp_capabilities --project /path/to/MyGame --json
```

On unattended machines, configure startup modal handling via
[Dialog policy](../dialog-policy.md) (`UNITY_OPEN_MCP_DIALOG_POLICY` and related env vars).

## Troubleshooting

For this path, first confirm Unity is open on the same absolute
`UNITY_PROJECT_PATH`, compilation finished, and the MCP client was restarted.
For connection, listener, modal, and dead-bridge recovery, follow the complete
[Troubleshooting](../troubleshooting.md) guide. Modal policy and macOS
Accessibility details live in [Dialog policy](../dialog-policy.md).

## Related docs

- [Agent setup](agent-setup.md) — let an AI agent perform this install
- [MCP client configuration](client-configuration.md) — client paths and envelopes
- [Troubleshooting](../troubleshooting.md) — bridge start failures and connectivity recovery
- [Dialog policy](../dialog-policy.md)
- [Wizard setup](wizard-setup.md)
- [Development setup](development-setup.md) — local checkout, contributor and maintainer workflows.
- [Unity Hub Pro](../unity-hub-pro.md)
- [Bridge HTTP API](../api/bridge-http.md)
- [MCP tools API](../api/mcp-tools.md)
