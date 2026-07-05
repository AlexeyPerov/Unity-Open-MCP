# Contributor troubleshooting

Bridge and automation issues when working on the **unity-open-mcp** repository
itself — local checkouts, test suites, CI, and bridge package development.

For game-project users, see [Troubleshooting](troubleshooting.md).

## Bridge won't start: "Address already in use" (deep dive)

Everything in the [user troubleshooting → Address already in use](troubleshooting.md#bridge-wont-start-address-already-in-use) section applies. The notes below are maintainer-specific.

### Worker / batch listener collision (historical + regression guard)

Unity spawns **child worker processes** for asset import and background work
(visible as `Unity -batchMode -name AssetImportWorkerHW* -parentPid <editor-pid>`).
Before the worker guard landed, those processes could bind the project's
deterministic port, producing a confusing split brain: `/ping` succeeded (worker's
listener) while the MCP server classified the instance as `dead_bridge` (main
Editor heartbeat frozen). Heavy automation — `scripts/mcp-full-test.mjs`,
recompile-heavy tool bands, Unity Test Runner — made this recur.

The bridge now **skips the listener in worker / batch-mode processes** (`[Unity
Open MCP Bridge] Skipping listener in batch/worker process (...)` in
`Editor.log`). Only the interactive Editor binds the port. The headless batch
path (`unity_open_mcp_compile_check` / `BridgeBatchEntry`) never relied on the
listener.

If you see "address already in use" during a full test run, check `Editor.log`
for a worker that incorrectly started a listener (regression) and verify only
one interactive Editor holds the project lock.

### Stale heartbeat vs live PID

The instance lock at `~/.unity-open-mcp/instances/<project-hash>.json` may show
a **stale heartbeat** while the port is still held. The MCP server uses
stale-heartbeat + live-PID to detect `bridge_compile_failed` (bridge assembly
failed to reload). See `packages/bridge/AGENTS.md` §Instance discovery and
`mcp-server/src/instance-discovery.ts` (`classifyInstance`).

## `InitTestScene*` modal after test runs

**Symptom:** After `scripts/mcp-full-test.mjs` (Band G /
`unity_senses_run_tests`) or manual EditMode test runs, Unity shows **"Scene(s)
Have Been Modified"** for `InitTestScene<uuid>`.

**Why:** The Unity Test Runner creates temporary scenes with **no on-disk path**.
Bridge **Auto-save dirty scenes** skips them, so closing the scene surfaces
Unity's save prompt. Until dismissed, live tools return **`main_thread_blocked`**.

**Recovery:**

1. Click **Don't Save** in the Unity dialog.
2. Or grant **Accessibility** to the process running `node` so
   `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` + `UNITY_OPEN_MCP_DIALOG_POLICY=cancel`
   can auto-click **Don't Save** — see
   [Dialog policy → macOS Accessibility](dialog-policy.md#macos-accessibility-required-for-auto-dismiss).
3. Re-run the suite — `mcp-full-test.mjs` runs a **finalize** step (dismiss +
   unload `InitTestScene*`) after Band G and **preflights** editor reachability
   before Band A.

## Full test suite (`scripts/mcp-full-test.mjs`)

- Requires `mcp-server/dist/index.js` built and a live Unity Editor with the
  target project open.
- Pass the project as an **absolute path** (relative paths hash to a different
  bridge port).
- Sets `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` and
  `UNITY_OPEN_MCP_DIALOG_POLICY=cancel` in the child env.
- Band G (`run_tests`) is isolated last — it blocks the test runner and can
  leave the bridge in a brief post-run degraded window.

```bash
node scripts/mcp-full-test.mjs --project /absolute/path/to/demo
```

## Related docs

- [Troubleshooting](troubleshooting.md) — user-facing recovery
- [Development setup](development-setup.md) — local checkout and build
- [Dialog policy](dialog-policy.md) — dismiss env vars and macOS Accessibility
- [Bridge HTTP API](api/bridge-http.md)
