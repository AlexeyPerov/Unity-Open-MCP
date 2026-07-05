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
3. See [Manual setup](manual-setup.md) for client `env` blocks and
   [Wizard setup](wizard-setup.md) for guided verification.

## Related docs

- [Dialog policy](dialog-policy.md) — startup and steady-state modal handling
- [Manual setup](manual-setup.md) — MCP client configuration
- [Bridge HTTP API](api/bridge-http.md) — `/ping` and listener contract
- [MCP tools API](api/mcp-tools.md) — route classes and recovery hints per tool family
