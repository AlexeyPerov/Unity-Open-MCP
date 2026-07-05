# Troubleshooting

Common bridge and MCP connectivity issues and how to recover.

## Bridge won't start: "Address already in use"

**Symptom:** Clicking **Start** in **Tools → Unity Open MCP Bridge** (Status tab) logs:

```text
[Unity Open MCP Bridge] Failed to start listener: Address already in use
```

The Status tab shows **Stopped**, but MCP clients may still report odd states (`dead_bridge`, timeouts on live tools, or a successful `ping` with hung mutations).

### What it means

Each Unity project gets a **deterministic bridge port** derived from the project path (`20000 + hash(projectPath) % 10000`). The demo project in this repository typically lands on port **27916** — your project will have its own port; see the **Bind URL** field on the bridge Status tab.

This error means **something is already listening on that port** — usually a **zombie listener** inside the same Unity Editor process after:

- a domain reload / script recompile (bridge code changed),
- quitting Unity with an unsaved scene (listener did not shut down cleanly), or
- toggling **Stop** / **Start** while the old `HttpListener` socket was still bound.

The HTTP socket can stay open even though the bridge thinks it is **not running** (`Listener: Stopped`). Clicking **Start** tries to bind the same port again and fails.

The instance lock at `~/.unity-open-mcp/instances/<project-hash>.json` may show a **stale heartbeat** while the port is still held.

### Why this happens often during heavy automation (test suites, CI)

Unity spawns **child worker processes** for asset import and background work (visible as `Unity -batchMode -name AssetImportWorkerHW* -parentPid <editor-pid>`). Because those workers load the same editor assemblies, the bridge's `[InitializeOnLoad]` initializer used to run in them too and bind the project's deterministic port — so the **main Editor** could not bind its own port. The symptom was particularly confusing: `/ping` succeeded (the worker's listener answered) while the MCP server classified the instance as `dead_bridge` (the main Editor's heartbeat was frozen). Frequent domain reloads (recompile-heavy tool runs, the Unity Test Runner) made the workers respawn often, so the collision recurred throughout a `scripts/mcp-full-test.mjs` run.

The bridge now **detects worker / batch-mode processes and skips starting the listener there** (`[Unity Open MCP Bridge] Skipping listener in batch/worker process (...)` in `Editor.log`). Only the interactive Editor binds the port. The deliberate headless batch path (`unity_open_mcp_compile_check` / `BridgeBatchEntry`) is unaffected — it is `-executeMethod`-driven and never relied on the listener.

### Recovery

The bridge **automatically** force-stops any in-process listener and retries the bind several times when it sees “address already in use”. If Start still fails after those retries, recover manually:

1. **Quit Unity completely** — use **File → Exit** or **Cmd+Q** (macOS) / **Alt+F4** (Windows). Do not rely on closing the project window alone if another Unity instance might be open.
2. **Confirm the port is free** (replace `<port>` with the number from **Bind URL** on the Status tab):

   **macOS / Linux:**

   ```bash
   lsof -i :<port>
   ```

   **Windows (PowerShell or cmd):**

   ```text
   netstat -ano | findstr :<port>
   ```

   No output means the port is free. If a process still holds it after Unity quit, terminate that process, then recheck.

3. **Optional — remove a stale lock file** if the MCP server still reports `dead_bridge` after restart. On the bridge Status tab, expand **Project (diagnostics)** and note the **Instance lock** path, or use:

   ```text
   ~/.unity-open-mcp/instances/<project-hash>.json
   ```

   Delete that file **only when Unity is fully quit** and nothing is listening on the port.

4. **Reopen the project.** The bridge auto-starts on Editor load unless disabled in Settings.
5. **Do not click Start** if the Status tab already shows **Running** — the listener is up.
6. **Verify:** on the Status tab, click **Ping**, or from a terminal:

   ```bash
   curl -s "http://127.0.0.1:<port>/ping"
   ```

   You should get a JSON response with `"status":"ok"`.

### Pin a different port (escape hatch)

Set an explicit port override when the deterministic port collides with another service:

- Environment variable: `UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- Unity command line: `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`

Valid range: **20000–29999** (unless your tooling allocates otherwise). Restart Unity after changing the override.

## Live tools hang or time out (`dead_bridge`, `main_thread_blocked`)

**Symptom:** Offline tools (`list_assets`, `read_compile_errors`) work, but live tools time out. `bridge_status` may classify the instance as **`dead_bridge`**, or errors mention **`main_thread_blocked`**.

### Common causes

- **Unsaved scene modals** — Unity's native "Save scene?" dialog blocks the main thread. Save or revert dirty scenes before long agent runs or test suites.
- **Stale bridge after recompile** — heartbeat and listener state diverged (see [Address already in use](#bridge-wont-start-address-already-in-use) above).
- **Compile in progress** — wait until the Editor finishes recompiling; the Status tab shows **Running (compiling)** when applicable.

### Recovery

1. Save or revert open scenes (especially after automated mutations).
2. Follow the [Address already in use](#bridge-wont-start-address-already-in-use) steps if Start fails or the listener looks stuck.
3. For unattended runs, see [Dialog policy](dialog-policy.md) — including `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` (destructive; opt-in only) and bridge setting **Auto-save dirty scenes** in **Settings**.

## MCP client cannot connect

1. Confirm the bridge Status tab shows **Running** and **Ping** succeeds.
2. Check your MCP client config points at the same project path the Unity Editor has open.
3. See [Manual setup](manual-setup.md) for client `env` blocks and [Wizard setup](wizard-setup.md) for guided verification.

## Related docs

- [Dialog policy](dialog-policy.md) — startup and steady-state modal handling
- [Manual setup](manual-setup.md) — MCP client configuration
- [Bridge HTTP API](api/bridge-http.md) — `/ping` and listener contract
- [MCP tools API](api/mcp-tools.md) — route classes and recovery hints per tool family
