# MCP Manual Setup

Set up Unity Open MCP **without Unity Hub Pro**: build the MCP server, add Unity packages to your project, configure your AI client, and verify the bridge.

For the guided wizard in Unity Hub Pro, see [wizard-setup.md](wizard-setup.md).

## What you need

| Requirement | Notes |
|---|---|
| Unity 6 (6000.0+) | Required by the bridge package. |
| Node.js 18+ | Runs the MCP server (`mcp-server/dist/index.js`). |
| This repository | Clone or download the `unity-open-mcp` monorepo. |
| An MCP client | Cursor, Claude Desktop, OpenCode, ZCode, or any MCP stdio client. |

## 1. Build the MCP server

From the repository root:

```bash
cd mcp-server
npm install
npm run build
```

Confirm that `mcp-server/dist/index.js` exists.

## 2. Add Unity packages

Edit your project’s `Packages/manifest.json` and add the bridge and verify packages under `dependencies`.

### Option A — Git install (standalone project)

Use the canonical git remote with UPM `path=` and tag pins:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.0.0"
  }
}
```

Replace the tag after `#` if you need a different release pin.

### Option B — Local `file:` install (project inside this monorepo)

If your Unity project lives in this checkout (for example [demo/](../demo/)), use relative paths from the project root:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

Adjust the relative path so it resolves from your project’s `Packages/` folder to `packages/bridge` and `packages/verify`.

Open the project in Unity and let Package Manager resolve the entries. Fix any compile errors before continuing.

## 3. Configure your MCP client

The MCP server is a Node process. Point your client at `mcp-server/dist/index.js` and set the required environment variables.

| Variable | Required | Purpose |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Absolute path to your Unity project root. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Bridge HTTP port override. When unset, the port is derived deterministically from the project path (`20000 + sha256(path) % 10000`) and discovered via the bridge's lock file — see [Multi-instance](#multi-instance-optional). |
| `UNITY_PATH` | no | Unity Editor executable for batch-only tools. |

Use absolute paths. On Windows, use forward slashes or escaped backslashes in JSON.

The examples below pin `UNITY_OPEN_MCP_BRIDGE_PORT` to `19120` for backward compatibility. You can **omit it entirely** — when unset, the MCP server discovers the bridge port automatically from the project path (see [Multi-instance](#multi-instance-optional)). Pin a port only if you want a fixed value across restarts.

### Cursor or Claude Desktop

Edit `~/.cursor/mcp.json` (Cursor) or your Claude Desktop MCP config file. Merge a `unity-open-mcp` entry under `mcpServers`:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/your/unity/project",
        "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

Keep any existing MCP servers in the file; only add or update the `unity-open-mcp` key.

### OpenCode (global)

Edit `~/.config/opencode/opencode.json`. Add under `mcp`:

```json
{
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["node", "/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
      "enabled": true,
      "environment": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/your/unity/project",
        "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

For a project-scoped config, put the same `mcp.unity-open-mcp` block in `opencode.json` at your Unity project root.

### ZCode

Edit `~/.zcode/cli/config.json` (global) — ZCode nests MCP servers three levels deep under `mcp.servers`. Add a `unity-open-mcp` entry:

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "node",
        "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
        "env": {
          "UNITY_PROJECT_PATH": "/absolute/path/to/your/unity/project",
          "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
        }
      }
    }
  }
}
```

For a project-scoped config, put the same `mcp.servers.unity-open-mcp` block in `.zcode/cli/config.json` at your Unity project root.

ZCode also discovers project skills under `.agents/skills/`. Run `unity_agent_generate_skill` with `clients: ["agents"]` (or use the Hub wizard, which writes `.agents/skills/unity-open-mcp/SKILL.md` when ZCode is selected) to give the agent a project-specific Unity skill.

### Claude Code (CLI)

```bash
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/your/unity/project \
  --env UNITY_OPEN_MCP_BRIDGE_PORT=19120 \
  -- node /absolute/path/to/unity-open-mcp/mcp-server/dist/index.js
```

Adjust flags to match your installed Claude Code version if the CLI syntax differs.

## 4. Launch Unity with the bridge

Open your Unity project in the Editor. The bridge package starts an HTTP listener. The port is derived **deterministically from the project path** (`20000 + sha256(path) % 10000`), so two projects running bridges simultaneously get two distinct ports with no configuration. The MCP server discovers the right bridge per project by reading a lock file the bridge writes at `~/.unity-agent/instances/<hash>.json` — no shared config required.

You can override the port (it wins over the deterministic default):

- Environment variable: `UNITY_OPEN_MCP_BRIDGE_PORT=22028`
- Command line: `-UNITY_OPEN_MCP_BRIDGE_PORT=22028`

If you pin a port, use the same value in your MCP client config and when launching Unity. When unset, both sides compute the same hash and meet automatically.

Wait for scripts to compile. The bridge is not useful until compilation finishes.

## 5. Restart your MCP client

Reload or restart the client so it picks up the new MCP server entry (Cursor, Claude Desktop, OpenCode, and most others only read MCP config at startup).

## 6. Verify the connection

### Bridge HTTP check

With Unity running and the project open, find the port the bridge picked (the MCP server logs it on startup as `Bridge port resolved to <port>`), then:

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

To see which port a running bridge is on without launching the MCP server, read its lock file:

```bash
cat ~/.unity-agent/instances/*.json | grep -E '"port"|"projectPath"'
```

Expect JSON with bridge status fields (for example `connected`). If the connection is refused, Unity may still be compiling or the port may not match your config.

See [api/bridge-http.md](api/bridge-http.md) for the full `/ping` response shape.

### MCP client check

In your AI client, use a Unity-related MCP tool or resource. If tools fail:

- Confirm `UNITY_PROJECT_PATH` matches the open project root exactly.
- Confirm `mcp-server/dist/index.js` exists and Node 18+ is on your `PATH`.
- Confirm Unity is running with that project and compilation has finished.
- Confirm no firewall is blocking localhost on the bridge port.

For MCP tool routing and offline fallbacks, see [api/mcp-tools.md](api/mcp-tools.md).

## Optional: install the agent skill

The agent skill (`skills/unity-open-mcp/SKILL.md` in this repo) gives your AI client workflow guidance for the Unity MCP tools — the mutate→gate→fix loop, capabilities-first discovery, and the agent senses (tests, profiler, screenshots). It is optional: the MCP tools work without it, but agents miss the workflow narrative and the `unity_agent_run_tests` verification habit.

Install paths are derived from the single-source manifest at [`skills/client-paths.json`](../skills/client-paths.json). Copy the template to the project-relative folder for your client:

| Client | Skill path |
|---|---|
| Cursor | `.cursor/skills/unity-open-mcp/SKILL.md` |
| Claude Desktop / Claude Code | `.claude/skills/unity-open-mcp/SKILL.md` |
| OpenCode | `.opencode/skills/unity-open-mcp/SKILL.md` |
| ZCode (and other `.agents`-aware clients) | `.agents/skills/unity-open-mcp/SKILL.md` |

For example, for Cursor:

```bash
mkdir -p .cursor/skills/unity-open-mcp
cp /path/to/unity-open-mcp/skills/unity-open-mcp/SKILL.md .cursor/skills/unity-open-mcp/SKILL.md
```

ZCode also discovers project skills under `.agents/skills/` — copy the template there for ZCode.

To generate a **project-specific** skill (Unity version, installed packages, key types) instead of the static template, run `unity_agent_generate_skill` with `{ "write": true, "clients": ["cursor"] }` once the MCP server is connected. Regenerate after package or script changes.

## Multi-instance (optional)

Multiple Unity projects can run bridges concurrently with no port management:

- Each project's bridge binds a **deterministic port** derived from its path: `20000 + (sha256(projectPath) % 10000)`. No two projects share a port.
- Each bridge writes a lock file at `~/.unity-agent/instances/<sha256(projectPath)>.json` with its `pid`, `port`, project path, and current editor state. The file doubles as a heartbeat, rewritten every 0.5s and on every compile / play-mode / domain-reload transition.
- The MCP server reads that lock file to find the right bridge for a given project — no env var needed when `UNITY_PROJECT_PATH` is set correctly.
- Stale locks (Unity crashed) are cleaned up automatically by the next bridge that starts: any lock whose `pid` is no longer alive is deleted.

To run two projects side by side, just open both in Unity with the bridge package installed and point two MCP server instances at the two project paths. Each picks its own port.

`UNITY_OPEN_MCP_BRIDGE_PORT` still overrides the deterministic default when you want to pin a specific port (CI, pinned dev setup). The override applies on both sides — set it in the MCP server env and pass `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>` to Unity.

## Troubleshooting

| Symptom | What to try |
|---|---|
| Package resolve fails in Unity | Check git URL, tag, or `file:` path; ensure Unity 6+. |
| MCP server fails to start | Run `node mcp-server/dist/index.js` manually and read stderr; fix missing `UNITY_PROJECT_PATH`. |
| `/ping` connection refused | Launch Unity for the same project; wait for compile; check the port the bridge picked via `~/.unity-agent/instances/*.json` or the MCP server's startup log (`Bridge port resolved to <port>`). |
| Tools work but live calls fail | Bridge may be disconnected — check `/ping` `connected` and that the Editor has the project focused/open. |
| Wrong project picked up by the MCP server | Confirm `UNITY_PROJECT_PATH` matches the open project root exactly; the deterministic port is derived from this path. |

## Related docs

- [Wizard setup (Unity Hub Pro)](wizard-setup.md) — guided install and verify flow.
- [Architecture](architecture.md) — how bridge, verify, and MCP server fit together.
- [Tools and dependencies](tools.md) — env vars, scripts, and toolchain versions.
