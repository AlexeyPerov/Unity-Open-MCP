# Troubleshooting

Common bridge and MCP connectivity issues when using Unity Open MCP with an AI
agent in your game project.

## Bridge won't start: "Address already in use"

**Symptom:** **Tools → Unity Open MCP Bridge** (Status tab) logs:

```text
[Unity Open MCP Bridge] Failed to start listener: Address already in use
```

The Status tab shows **Stopped**, but your agent may report timeouts or odd
bridge states.

### What it means

Each project gets a **deterministic bridge port** from its path
(`20000 + hash(projectPath) % 10000`). See **Bind URL** on the bridge Status
tab for yours. Something is already listening on that port — usually a stale
listener after a domain reload, quitting Unity with an unsaved scene, or
toggling **Stop** / **Start** too quickly.

### Recovery

The bridge **automatically** retries when it sees this error. If **Start** still
fails:

1. **Quit Unity completely** — **File → Exit** or **Cmd+Q** (macOS) /
   **Alt+F4** (Windows).
2. **Confirm the port is free** (replace `<port>` with **Bind URL**):

   **macOS / Linux:** `lsof -i :<port>`

   **Windows:** `netstat -ano | findstr :<port>`

3. **Optional — remove a stale lock** if the agent still reports `dead_bridge`
   after restart. Path is on the Status tab under **Project (diagnostics)**, or
   `~/.unity-open-mcp/instances/<project-hash>.json`. Delete it **only when
   Unity is fully quit** and nothing is listening on the port.
4. **Reopen the project.** The bridge auto-starts unless disabled in Settings.
5. **Verify:** Status tab → **Ping**, or `curl -s "http://127.0.0.1:<port>/ping"`.

### Pin a different port

- Environment: `UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- Unity arg: `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`

Valid range: **20000–29999**. Restart Unity after changing.

## Live tools hang or time out (`dead_bridge`, `main_thread_blocked`)

**Symptom:** Offline tools (`list_assets`, `read_compile_errors`) work, but
live tools time out. Errors mention **`dead_bridge`** or **`main_thread_blocked`**.

### Common causes

- **Unsaved scene modals** — Unity's "Save scene?" / **"Scene(s) Have Been
  Modified"** dialog blocks the main thread. Save or revert dirty scenes, or
  click **Don't Save** for throwaway temp scenes.
- **Stale bridge after recompile** — see [Address already in use](#bridge-wont-start-address-already-in-use) above.
- **Compile in progress** — wait until the Editor finishes; Status shows
  **Running (compiling)** when applicable.

### Recovery

1. Dismiss any Unity modal sitting in front of the Editor (especially save
   prompts after automated edits).
2. Save or revert open scenes.
3. If the bridge looks stuck, follow the [Address already in use](#bridge-wont-start-address-already-in-use) steps.
4. For unattended agent runs, enable bridge **Auto-save dirty scenes** (Settings
   tab) and read [Dialog policy](dialog-policy.md) for opt-in auto-dismiss env
   vars. On **macOS**, auto-dismiss also requires a one-time **Accessibility**
   grant for the app that runs `node` — see
   [Dialog policy → macOS Accessibility](dialog-policy.md#macos-accessibility-required-for-auto-dismiss).

## MCP client cannot connect

1. Confirm the bridge Status tab shows **Running** and **Ping** succeeds.
2. Check your MCP client config uses the **same absolute project path** as the
   open Unity project (`UNITY_PROJECT_PATH`).
3. See [MCP client configuration](setup/client-configuration.md) for client
   paths and snippets,
   [Agent setup](setup/agent-setup.md) for the AI-driven install path, and
   [Wizard setup](setup/wizard-setup.md) for guided verification.

If tools are still missing after a config edit, restart the MCP client. Most
clients read MCP configuration only at startup.

### Status tab says the client is not configured

The **Client** stage in the connection strip is a file heuristic. It searches the
Unity project and up to four parent folders for a project-scoped client config,
so a Unity project nested in a larger repository (`<repo>/Client` configured by
`<repo>/.cursor/mcp.json`) reads as configured — the panel's **Found in** field
names the file it matched. The heuristic still cannot see Claude Code (CLI-only,
no file) or a config in a location outside that search, so a warning here is
advisory: if tools work, the client is configured.

## Node or `npx` problems

- **`node` / `npx` not found:** install Node.js 18 or newer from
  <https://nodejs.org/>, then restart the terminal and MCP client so they get
  the updated `PATH`.
- **First `npx` launch looks stuck:** allow up to a minute for npm to download
  the pinned server package. Later launches use npm's cache.
- **Server exits immediately:** verify `UNITY_PROJECT_PATH` is present,
  absolute, and points to the folder containing `Assets/`, `Packages/`, and
  `ProjectSettings/`.

## Startup dialog blocks Unity

If Unity is waiting on Safe Mode, project upgrade, version mismatch, or another
native modal, follow [Dialog policy](dialog-policy.md). It owns the supported
policy values, destructive-operation opt-ins, and timeout controls.

On macOS, auto-dismiss also requires Accessibility permission for the app that
actually launches `node` (for example the terminal or IDE). Grant it in
**System Settings → Privacy & Security → Accessibility**, then restart the MCP
client.

## `execute_csharp` reports `roslyn_unavailable` (Unity 6000.x)

### What it means

`execute_csharp` and script validation compile snippets with Roslyn loaded
from the Unity installation. Unity 6000.x ships every Roslyn copy
(`DotNetSdkRoslyn/`, the ApiUpdater copy) as ReadyToRun (R2R) crossgen'd
images, which the editor's Mono runtime cannot load — the Unity console shows
`Could not load image … due to Invalid data directory 3` if such an image is
probed. The editor locations that used to carry a Mono-loadable Roslyn
(`MonoBleedingEdge/.../Roslyn`, `Tools/roslyn`) no longer exist in 6000.x.

**This is not a corrupted install — reinstalling Unity does not help.**

### Recovery

Install the bridge's IL-only Roslyn fallback (Roslyn 4.8.0 + its dependency
closure, ~15 MB, downloaded from nuget.org as SHA-256-pinned packages into
`~/.unity-open-mcp/roslyn/4.8.0/`). Either surface works:

- **From your agent:** call `execute_csharp` with `{"setup_roslyn": true}`.
  Re-call to poll; when the status is `installed`, snippets compile
  immediately (you can pass `code` in the same call).
- **From the editor:** menu **Tools → Unity Open MCP Bridge - Install Roslyn
  Fallback**, or the **Roslyn (execute_csharp)** row in the bridge window's
  Extensions tab.

The install is shared across projects and editor versions on the machine; a
domain reload or editor restart picks it up automatically. On Unity 2022.3,
where the editor still ships a Mono-loadable Roslyn, the fallback is never
used.

### Manual / offline install

On a machine without nuget.org access, copy the whole
`~/.unity-open-mcp/roslyn/4.8.0/` directory (DLLs plus
`install-manifest.json`) from a machine where the install succeeded. The
directory is self-contained and portable.

## `read_compile_errors` reports errors from a different Unity

### What it means

`read_compile_errors` reads the tail of `Editor.log`. When a `-batchmode` run of
a **different** Unity version (e.g. a CI build on 6000.5) wrote the log, its
compiler errors — including API deprecations that are only errors on that
version — can look like the live editor is broken. The response carries
`logUnityVersion`, `logBatchMode`, and `liveUnityVersion` so you can tell who
wrote the log: on a version mismatch it adds
`logAuthorshipMismatch: true` + `logAuthorshipHint` warning that the errors
**cannot all occur** in the live editor.

### Recovery

When `logAuthorshipMismatch` is set (or `logUnityVersion` differs from
`liveUnityVersion`), do **not** act on the reported errors as if the live editor
produced them. Confirm the live editor's health with `bridge_status`
(`unityVersion`) and `editor_status` (`isCompiling`). If the live editor is the
project you care about, its own log (`~/Library/Logs/Unity/Editor.log` on
pre-6000.5, `<project>/Logs/Editor.log` on 6000.5+) holds the authoritative
errors; `read_compile_errors` resolves that path automatically when the project
is the live one.

## `read_compile_errors` reports a `stale_log` status

### What it means

When the log is stale (a cited source file is newer than the log's last write)
**or** authored by a different Unity (a version mismatch), the response
**downgrades** the top-level `status` to `"stale_log"` and suppresses
`unhealthy`. The `errors[]` are still attached as evidence, and the literal log
verdict is preserved in `logVerdict`, but the headline makes clear the errors
**may not apply** to the running editor. This stops an agent from "fixing" a
burst of phantom errors that were already resolved on disk (e.g. 31 obsolete-API
warnings from a session that ended hours earlier).

### Recovery

Do not act on the errors until a genuine recompile confirms them. Call
`unity_open_mcp_recompile_scripts` (deterministic force-recompile) and re-read
compile errors. If the status returns to `no_errors_found` or `compile_failed`
without the `stale_log` downgrade, the errors are current.

### Why the wrong log file is read

Unity 6000.5+ writes a project-relative log (`<project>/Logs/Editor.log`) and
stops touching the global per-user log. A project opened in 6000.5+ at some point
leaves a `<project>/Logs/Editor.log` on disk; if the same project is later
opened under a pre-6000.5 Unity, the live editor writes the **global** log while
the project log lingers. `read_compile_errors` now picks the **freshest** of the
two (newest mtime) rather than preferring the project log on existence alone —
reported as `logSource: "global_log_fresher"` when the global log wins.

## `restart_editor` refuses with `restart_signature_absent`

### What it means

`restart_editor` only terminates the Editor when the fd-exhaustion signature
(`Could not register to wait for file descriptor N`) is present. It first scans
the resolved `Editor.log` tail; when that log does not carry the signature **but
the bridge is still reachable**, it falls back to scanning the live Unity
console (`unity_senses_read_console`) — the Bee driver exception is raised into
the console independently of whichever log file the resolver picked. The
response reports `signatureSource: "editor_log"` or `"live_console"` so you can
tell which source confirmed it.

### Recovery

If the signature is genuinely absent, the Editor is slow/stuck for a different
reason (a fixable compile failure, a modal, an unresponsive main thread) — re-run
`read_compile_errors` to confirm the diagnosis before killing the Editor. If the
bridge is down and the log is stale, start the Editor from the Hub and use
`resource_pressure` to monitor fd headroom proactively.

## Related docs

- [Dialog policy](dialog-policy.md) — startup and steady-state modal handling
- [Agent setup](setup/agent-setup.md) — AI-driven install procedure
- [MCP client configuration](setup/client-configuration.md) — client paths and copy-paste snippets
- [Bridge HTTP API](api/bridge-http.md) — `/ping` and listener contract
- [Routing and lifecycle](api/routing-lifecycle.md) — route classes and recovery
