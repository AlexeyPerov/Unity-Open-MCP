# Manual Setup

Set up Unity Open MCP without Unity Hub Pro.

## Who is this for

This is the **do-it-yourself** path. It's a good fit if you:

- already edit Unity `manifest.json` and MCP-client config files by hand, or
- prefer to control exactly what gets installed and pinned, or
- are on a headless/CI machine where the Hub GUI isn't an option.

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
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.0.0"
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

Particle System and Animation are built-in Unity modules — no manifest entry is needed, the tools compile in as soon as the module is enabled in the Editor. See [extensions.md](extensions.md) for the domain catalog and activation steps.

### Optional dependencies (in-Editor)

Once the bridge is installed, you can add or remove Unity domain dependencies without editing the manifest by hand. Open **Tools → Unity Open MCP Bridge → Extensions** and use the **Optional Unity dependencies** panel: one row per domain shows installed / missing status, with a one-click **Install…** / **Remove…** action for each UPM package. Unity re-imports the manifest and recompiles; the embedded tools register (or stop compiling) on the next domain reload. The Unity Hub Pro project settings modal mirrors this as a read-only status panel.

## 2) Configure your MCP client (AI side)

Now point your AI client at the MCP server. With **`npx`** it is fully automatic
— your client downloads the server on first run and keeps it up to date, so you
never install or update it manually:

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

What that means:

- `npx -y unity-open-mcp@latest` downloads and launches the latest MCP server
  from npm. The `-y` accepts the first-run prompt; `@latest` means every launch
  checks for a newer version.
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

### Environment variables

- **Required:** `UNITY_PROJECT_PATH` — absolute path to the Unity project root.
- Optional: `UNITY_OPEN_MCP_BRIDGE_PORT` (override the auto-derived port).
- Optional: `UNITY_PATH` (batch fallback when the Editor can't auto-discover).
- Optional startup-dialog handling (see [Dialog policy](dialog-policy.md)):
  - `UNITY_OPEN_MCP_DIALOG_POLICY=auto|manual|ignore|recover|safe-mode|cancel` (default `ignore`)
  - `UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1` (opt in to auto-confirming the irreversible Project Upgrade dialog; off by default)
  - `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` (opt in to auto-dismissing the "Unsaved changes to scene" modal — destructive under every policy, off by default)
  - `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` (kill-switch — disables all OS clicks)
  - `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` (default 30000)
  - `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` (default 1500)

### Per-client config snippets

The `mcpServers` JSON shape above works for Cursor, Claude Desktop, Cline,
Gemini CLI, GitHub Copilot CLI, Kilo Code, Rider (Junie), Unity AI, ZooCode,
and Antigravity. A few clients use a different envelope:

**VS Code Copilot / Visual Studio Copilot** use a `servers` key (not
`mcpServers`) in a project-local file:

```json
{
  "servers": {
    "unity-open-mcp": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "unity-open-mcp@latest"],
      "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

Place it at `.vscode/mcp.json` (VS Code) or `.vs/mcp.json` (Visual Studio).

**OpenCode** nests under `mcp` with `command` as an array and env under
`environment`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@latest"],
      "enabled": true,
      "environment": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

**ZCode** nests under `mcp.servers` with `type: "stdio"`:

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@latest"],
        "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
      }
    }
  }
}
```

**Codex** uses TOML in `.codex/config.toml`:

```toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "npx"
args = ["-y", "unity-open-mcp@latest"]

[mcp_servers.unity-open-mcp.env]
UNITY_PROJECT_PATH = "/absolute/path/to/project"
```

**Claude Code** is CLI-only — run this instead of writing a file:

```sh
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/project \
  -- npx -y unity-open-mcp@latest
```

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
npx -y unity-open-mcp@latest wait-for-ready --project /path/to/MyGame
npx -y unity-open-mcp@latest run-tool unity_open_mcp_capabilities --project /path/to/MyGame --json
```

On unattended machines, configure startup modal handling via
[Dialog policy](dialog-policy.md) (`UNITY_OPEN_MCP_DIALOG_POLICY` and related env vars).

## Troubleshooting

- **Connection refused / "bridge unavailable":** confirm Unity is **open** with
  the **same project path** that `UNITY_PROJECT_PATH` points to. The bridge runs
  *inside* Unity, so Unity must be running for the two halves to talk.
- **`npx` first run looks stuck:** that's the package downloading — give it up to
  a minute on the first launch. It's fast afterwards.
- **`npx` / `node` not found:** Node isn't on your PATH. Reopen your terminal
  after installing Node from <https://nodejs.org/>, or restart your AI client so
  it picks up the new PATH.
- **Tools missing in the client after editing config:** restart the client. Most
  MCP clients only read the config at startup.
- **Compile-error launch dialog blocks startup:** the server auto-dismisses it
  by default (clicking Ignore). If it keeps reappearing, set
  `UNITY_OPEN_MCP_DIALOG_POLICY=manual` and dismiss it by hand, or check
  `unity_open_mcp_read_compile_errors` for the underlying CS error. See
  [Dialog policy](dialog-policy.md).
- **"nothing happens" but no error:** double-check `UNITY_PROJECT_PATH` is an
  **absolute** path to the project root and contains no trailing slash or typos.
- **macOS — modal auto-dismiss does nothing:** grant **Accessibility** to the
  app that runs `node` (Terminal, your IDE, etc.) in **System Settings →
  Privacy & Security → Accessibility**, then restart the MCP client. Required
  for any agent host — not Cursor-specific. See
  [Dialog policy → macOS Accessibility](dialog-policy.md#macos-accessibility-required-for-auto-dismiss).

## Related docs

- [Troubleshooting](troubleshooting.md) — bridge start failures and connectivity recovery
- [Dialog policy](dialog-policy.md)
- [Wizard setup](wizard-setup.md)
- [Development setup](development-setup.md) — local checkout, contributor and maintainer workflows.
- [Unity Hub Pro](unity-hub-pro.md)
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
