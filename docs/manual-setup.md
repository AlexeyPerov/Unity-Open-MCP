# MCP Manual Setup

Set up Unity Open MCP **without Unity Hub Pro**: install the MCP server from npm, add Unity packages to your project, configure your AI client, and verify the bridge.

For the guided wizard in Unity Hub Pro, see [wizard-setup.md](wizard-setup.md).

## What you need

| Requirement | Notes |
|---|---|
| Unity 2022.3 LTS+ (Unity 6 recommended) | Required by the bridge package. See [Unity version compatibility](unity-version-compat.md). |
| Node.js 18+ | Runs the MCP server. Your AI client spawns it via `npx`, so no manual install is required. |
| An MCP client | Cursor, Claude Desktop, OpenCode, ZCode, or any MCP stdio client. |

You do **not** need to clone this repository for a normal install — the MCP server is published to npm as [`unity-open-mcp`](https://www.npmjs.com/package/unity-open-mcp). Clone the repo only if you want to contribute or run from a local checkout (see [Contributor / local checkout](#contributor--local-checkout)).

## 1. Install the MCP server

The MCP server is a Node process your AI client spawns. The default install path uses `npx` and fetches the latest published version on first spawn — no manual install step. If you prefer a one-time install:

```bash
npm install -g unity-open-mcp
```

Then use `unity-open-mcp` as the command in your client config (see step 3) instead of `npx -y unity-open-mcp@latest`.

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

The MCP server is a Node process. Point your client at it via `npx` (default) and set the required environment variables.

| Variable | Required | Purpose |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Absolute path to your Unity project root. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | no | Bridge HTTP port override. When unset, the port is derived deterministically from the project path (`20000 + sha256(path) % 10000`) and discovered via the bridge's lock file — see [Multi-instance](#multi-instance-optional). |
| `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS` | no | Set to `1` to disable auto-dismissal of Unity's "compile errors at launch" / Safe Mode dialog. Default: auto-dismiss **enabled** — see [Launch dialog auto-dismiss](#launch-dialog-auto-dismiss). |
| `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` | no | Overall budget (ms) for a single launch-dialog dismiss pass. Default `30000`. |
| `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` | no | Poll interval (ms) between launch-dialog probes. Default `1500`. |
| `UNITY_PATH` | no | Unity Editor executable for batch-only tools. |

Use absolute paths. On Windows, use forward slashes or escaped backslashes in JSON.

The examples below pin `UNITY_OPEN_MCP_BRIDGE_PORT` to `19120` for backward compatibility. You can **omit it entirely** — when unset, the MCP server discovers the bridge port automatically from the project path (see [Multi-instance](#multi-instance-optional)). Pin a port only if you want a fixed value across restarts.

### Cursor or Claude Desktop

Edit `~/.cursor/mcp.json` (Cursor) or your Claude Desktop MCP config file. Merge a `unity-open-mcp` entry under `mcpServers`:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@latest"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/your/unity/project",
        "UNITY_OPEN_MCP_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

If you installed globally (`npm i -g unity-open-mcp`), use `"command": "unity-open-mcp", "args": []` instead.

Keep any existing MCP servers in the file; only add or update the `unity-open-mcp` key.

### OpenCode (global)

Edit `~/.config/opencode/opencode.json`. Add under `mcp`:

```json
{
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@latest"],
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
        "command": "npx",
        "args": ["-y", "unity-open-mcp@latest"],
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

ZCode also discovers project skills under `.agents/skills/`. Run `unity_open_mcp_generate_skill` with `clients: ["agents"]` (or use the Hub wizard, which writes `.agents/skills/unity-open-mcp/SKILL.md` when ZCode is selected) to give the agent a project-specific Unity skill.

### Claude Code (CLI)

```bash
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/your/unity/project \
  --env UNITY_OPEN_MCP_BRIDGE_PORT=19120 \
  -- npx -y unity-open-mcp@latest
```

Adjust flags to match your installed Claude Code version if the CLI syntax differs.

## 4. Launch Unity with the bridge

Open your Unity project in the Editor. The bridge package starts an HTTP listener. The port is derived **deterministically from the project path** (`20000 + sha256(path) % 10000`), so two projects running bridges simultaneously get two distinct ports with no configuration. The MCP server discovers the right bridge per project by reading a lock file the bridge writes at `~/.unity-open-mcp/instances/<hash>.json` — no shared config required.

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
cat ~/.unity-open-mcp/instances/*.json | grep -E '"port"|"projectPath"'
```

Expect JSON with bridge status fields (for example `connected`). If the connection is refused, Unity may still be compiling or the port may not match your config.

See [api/bridge-http.md](api/bridge-http.md) for the full `/ping` response shape.

### MCP client check

In your AI client, use a Unity-related MCP tool or resource. If tools fail:

- Confirm `UNITY_PROJECT_PATH` matches the open project root exactly.
- Confirm Node 18+ is on your `PATH`. On the local-checkout path, also confirm `mcp-server/dist/index.js` exists (run `npm run build` in `mcp-server/`).
- Confirm Unity is running with that project and compilation has finished.
- Confirm no firewall is blocking localhost on the bridge port.

For MCP tool routing and offline fallbacks, see [api/mcp-tools.md](api/mcp-tools.md).

## CLI for CI / automation

The MCP server ships a thin CLI for scripting and CI pipelines. It shares the
same routing layer as the stdio server, so a CLI invocation returns the same
JSON an MCP client would receive. The published binary is invocable via `npx`:

```bash
npx -y unity-open-mcp@latest <command> [options]
```

Commands:

| Command | Purpose | Exit code |
|---|---|---|
| `ping` | One `/ping` against the resolved bridge. | `0` if ready, `1` otherwise. |
| `wait-for-ready` | Poll `/ping` until the bridge is connected and idle (compile-aware). Fails fast on a dead-bridge assembly. | `0` on ready, `1` on timeout / dead bridge. |
| `status` | Print resolved port, instance lock classification, auth token presence, and a cheap readiness probe. Always `0`. | `0`. |
| `run-tool <name>` | Invoke any MCP tool by name and print its JSON result. | `0` on success, `1` on tool error, `2` on unknown tool. |
| `--help` / `-h` | Show help. | `0`. |
| `--version` / `-V` | Print the package version. | `0`. |

Every command accepts `--json` to emit machine-readable JSON instead of the
human-readable summary.

Options:

| Option | Purpose |
|---|---|
| `--project <path>` / `-P <path>` | Unity project path. Defaults to `UNITY_PROJECT_PATH`. |
| `--port <n>` / `-p <n>` | Bridge port override. Defaults to `UNITY_OPEN_MCP_BRIDGE_PORT`, then deterministic discovery. |
| `--timeout-ms <n>` | Ping / wait-for-ready overall timeout (ms). |
| `--interval-ms <n>` | wait-for-ready poll interval (ms). |
| `--args '<json>'` | JSON object of tool args (`run-tool`). |
| `--arg key=value` | One tool arg (`run-tool`, repeatable; JSON-parsed when the value is valid JSON). |

Examples:

```bash
# Block until the bridge is ready (typical CI gate before running tools).
npx -y unity-open-mcp@latest wait-for-ready --project /path/to/MyGame

# Probe and exit — useful as a health check.
npx -y unity-open-mcp@latest ping --project /path/to/MyGame --json

# Show the resolved bridge port + instance state without waiting.
npx -y unity-open-mcp@latest status --project /path/to/MyGame --json

# Run a tool exactly like an MCP client would; parse the JSON downstream.
npx -y unity-open-mcp@latest run-tool unity_open_mcp_capabilities \
  --project /path/to/MyGame --json --arg include_planned=false

# Offline-only tools (no bridge required) also work via the CLI.
npx -y unity-open-mcp@latest run-tool unity_open_mcp_list_assets \
  --project /path/to/MyGame --arg folder=Assets --arg max_per_folder=10
```

`wait-for-ready` is compile-aware: a 503 response or `compiling: true` body
keeps the wait alive, and a stale-heartbeat + live-PID signature (the bridge
assembly failed to recompile) fails fast with a pointer at
`run-tool unity_open_mcp_read_compile_errors` instead of spinning to the
timeout. See [Bridge HTTP API](api/bridge-http.md) for the `/ping` body.

> **Pinning.** `npx -y unity-open-mcp@latest` always fetches the newest
> published version on first spawn. To pin a release, swap `@latest` for a
> specific version (`unity-open-mcp@0.2.0`) or install that version globally.

## Contributor / local checkout

Contributors who run from a cloned checkout can point the MCP client at the
local `mcp-server/dist/index.js` instead of `npx`. Build once:

```bash
cd mcp-server
npm install
npm run build
```

Then use `node` with the absolute path in your client config:

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

From the CLI, use `node mcp-server/dist/index.js <command>` against your
checkout. The Hub wizard's Step 2 **"Use local checkout"** toggle selects
this path (it auto-enables when a toolkit root is already saved).

## Maintainer publish flow

The `unity-open-mcp` npm package is published from `mcp-server/`. There are
two publish channels — use CI for releases, the Hub maintainer panel for
local workflows:

| Channel | When | Owner |
|---|---|---|
| **CI** (`.github/workflows/npm-publish.yml` on `v*` tags) | Production releases | push a `vX.Y.Z` tag whose version matches `mcp-server/package.json` |
| **Hub maintainer panel** | First manual publish, emergency republish, local dry-runs | the maintainer's own `npm login` |

### CI publish (tag-triggered)

```bash
cd mcp-server
npm version patch   # or minor / major — bumps mcp-server/package.json
git add mcp-server/package.json mcp-server/package-lock.json
git commit -m "Bump unity-open-mcp to vX.Y.Z"
git tag vX.Y.Z
git push origin vX.Y.Z
```

The `npm-publish.yml` workflow runs `npm ci && npm publish --access public`
on the tag (the `prepublishOnly` script rebuilds `dist/` first). It refuses
to publish when the tag version does not match `mcp-server/package.json`.
The `NPM_TOKEN` repository secret (an automation token on the publishing
account) authenticates the publish.

### Hub maintainer panel

When a folder is tracked as an Open-MCP repository (the Hub detects
`mcp-server/` + a root `package.json`), the settings popup becomes a
maintainer panel:

- **Info header** — package name + local version from `mcp-server/package.json`,
  the published version from `npm view`, and the `npm whoami` result.
- **Build / Test** — `npm run build` / `npm test` with cwd `mcp-server/`.
- **Version bump** — `npm version patch|minor|major --no-git-tag-version` (the
  Hub never creates git tags).
- **Publish dry-run** — `npm publish --dry-run --access public` preflight.
- **Publish** — real publish behind a confirmation dialog.

The Hub does **not** store npm credentials — authenticate with `npm login`
on the machine (or the env vars npm already reads). Both channels ship only
the `files` whitelist (`dist/`, `README.md`, `LICENSE`).

### Publishing a fork

To publish under your own name (a fork): change `name` in
`mcp-server/package.json`, `npm login` with your account, and run the Hub
publish flow or `npm publish --access public` from `mcp-server/`. The CI
workflow is keyed off your fork's tags + `NPM_TOKEN` secret, so a fork with
its own token publishes on its own tags with no further change.

## Optional: install the agent skill

The agent skill (`skills/unity-open-mcp/SKILL.md` in this repo) gives your AI client workflow guidance for the Unity MCP tools — the mutate→gate→fix loop, capabilities-first discovery, and the agent senses (tests, profiler, screenshots). It is optional: the MCP tools work without it, but agents miss the workflow narrative and the `unity_senses_run_tests` verification habit.

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

To generate a **project-specific** skill (Unity version, installed packages, key types) instead of the static template, run `unity_open_mcp_generate_skill` with `{ "write": true, "clients": ["cursor"] }` once the MCP server is connected. Regenerate after package or script changes.

## Multi-instance (optional)

Multiple Unity projects can run bridges concurrently with no port management:

- Each project's bridge binds a **deterministic port** derived from its path: `20000 + (sha256(projectPath) % 10000)`. No two projects share a port.
- Each bridge writes a lock file at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` with its `pid`, `port`, project path, and current editor state. The file doubles as a heartbeat, rewritten every 0.5s and on every compile / play-mode / domain-reload transition.
- The MCP server reads that lock file to find the right bridge for a given project — no env var needed when `UNITY_PROJECT_PATH` is set correctly.
- Stale locks (Unity crashed) are cleaned up automatically by the next bridge that starts: any lock whose `pid` is no longer alive is deleted.

To run two projects side by side, just open both in Unity with the bridge package installed and point two MCP server instances at the two project paths. Each picks its own port.

`UNITY_OPEN_MCP_BRIDGE_PORT` still overrides the deterministic default when you want to pin a specific port (CI, pinned dev setup). The override applies on both sides — set it in the MCP server env and pass `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>` to Unity.

## Authentication (M14)

The bridge mints a per-session bearer token into the same lock file described above, and the MCP server reads it automatically — so **no configuration is needed for the default (`authMode: "none"`) or for `required` mode**. The MCP server always sends `Authorization: Bearer <token>` when it can discover one, and the bridge ignores it unless the project asks for enforcement.

To require the token (recommended on shared machines where another local process might reach the loopback listener):

1. In Unity, open the bridge window → **Settings** tab → **Bridge auth** → set **Auth mode** to `required`. Or edit `.unity-open-mcp/settings.json` directly:

   ```json
   { "authMode": "required" }
   ```

2. Reload the bridge. The MCP server picks up the token from the lock file on its next start — no env var, no client-side change.

A request without the header (or with the wrong token) gets `401 {"error":{"code":"unauthorized", ...}}`. The bridge binds `127.0.0.1` by default; `required` is an extra layer for shared machines and **mandatory** for remote bind. See [Bridge HTTP API → Authentication](api/bridge-http.md#authentication-m14) for the full policy.

> Note: when you pin `UNITY_OPEN_MCP_BRIDGE_PORT` on both sides, the MCP server skips reading the lock file, so it has no token to send — in that pinned-port setup, keep `authMode` as `none`.

## Remote bind & power-tool deny lists (M14)

Three opt-in security surfaces round out M14 — all controlled from the bridge **Settings** tab or `.unity-open-mcp/settings.json`:

- **Remote bind** — set `bindAddress` to `0.0.0.0` to expose the bridge beyond loopback (shared dev machine, CI runner reached over the network). **Requires `authMode: "required"`** — the bridge refuses to start on a non-loopback interface without token auth. The bearer token travels in cleartext over HTTP, so terminate TLS upstream (reverse proxy / `ssh -L` tunnel). See [Bridge HTTP API → Remote bind](api/bridge-http.md#remote-bind-m14-t54) for the full threat model.
- **Power-tool deny lists** — `execute_csharp` and `execute_menu` are blocked from destructive patterns (editor exit, bulk asset delete, unbounded builds, `File/Quit`) by default. Override via `csharpDenyPatterns` / `menuDenyPatterns` (non-empty regex arrays replace the defaults; null/empty = defaults). Bypass per-request with `gate: "off"` + `confirm_bypass: true` (audited). See [Bridge HTTP API → Power-tool deny lists](api/bridge-http.md#power-tool-deny-lists-m14-t52--t53).
- **On-disk audit log** — set `auditLogEnabled: true` to persist every gate mutation and deny-list refusal to a rolling JSON-lines file under `~/.unity-open-mcp/audit/`. Survives domain reload and editor restart. Off by default.

## Launch dialog auto-dismiss

When Unity starts with compile errors, it blocks behind a native modal — the "compile errors at launch" / "Enter Safe Mode?" prompt. The MCP server would then stall on `/ping` and compile-wait loops with no way to recover, which matters most in unattended / CI flows.

By default the MCP server **auto-dismisses** that dialog by clicking **Ignore** while it waits for the bridge to become ready. It probes the OS desktop on the same cadence as the compile/bridge poll (not only at process spawn) and logs each dismissal to stderr for auditability.

- **Enabled by default.** Set `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` to preserve the pre-feature behavior (no OS clicks).
- Tune the budget with `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` (default `30000`) and the poll interval with `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` (default `1500`).
- **macOS:** uses AppleScript (`osascript`). Grant Accessibility permission to your terminal / `node` binary once in System Settings → Privacy & Security → Accessibility, or the dismiss will report a permission error (logged once) and continue without clicking.
- **Windows:** uses Win32 `BM_CLICK` via PowerShell (no focus stealing).
- **Linux:** uses `xdotool` (X11). Wayland is not supported — install `xdotool` (e.g. `sudo apt-get install xdotool`) to enable it.

Only the launch-errors / Safe Mode dialog is auto-dismissed. Destructive startup dialogs (project upgrade, version mismatch) are intentionally left for a human — see the backlog for a future per-dialog policy.

## Troubleshooting

| Symptom | What to try |
|---|---|
| Package resolve fails in Unity | Check git URL, tag, or `file:` path; ensure Unity 2022.3 LTS or newer (Unity 6 recommended) — see [Unity version compatibility](unity-version-compat.md). |
| MCP server fails to start | Run `npx -y unity-open-mcp@latest` manually and read stderr; fix missing `UNITY_PROJECT_PATH`. On the local-checkout path, run `node mcp-server/dist/index.js` and confirm the file exists. |
| `/ping` connection refused | Launch Unity for the same project; wait for compile; check the port the bridge picked via `~/.unity-open-mcp/instances/*.json` or the MCP server's startup log (`Bridge port resolved to <port>`). |
| Tools work but live calls fail | Bridge may be disconnected — check `/ping` `connected` and that the Editor has the project focused/open. |
| Wrong project picked up by the MCP server | Confirm `UNITY_PROJECT_PATH` matches the open project root exactly; the deterministic port is derived from this path. |
| `npx` first run is slow | Expected — `npx -y` downloads and extracts the package on first spawn. Subsequent runs hit the npm cache. Use `npm i -g unity-open-mcp` for a one-time install if this matters. |
| MCP server stalls on first call after a compile-error launch | Expected if the Safe Mode dialog is up. Auto-dismiss clicks **Ignore** by default; if you disabled it (`UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1`) or hit a permission error (macOS Accessibility), dismiss the dialog manually. Dismissals and errors are logged to the MCP server's stderr. |

## Related docs

- [Wizard setup (Unity Hub Pro)](wizard-setup.md) — guided install and verify flow.
- [Architecture](architecture.md) — how bridge, verify, and MCP server fit together.
- [Tools and dependencies](tools.md) — env vars, scripts, and toolchain versions.
