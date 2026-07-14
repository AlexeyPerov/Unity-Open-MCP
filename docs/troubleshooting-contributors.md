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

## `Main` scene modal after full test (Band C)

**Symptom:** After a full test run, Unity shows **"Scene(s) Have Been Modified"**
for **`Main`** (not `InitTestScene*`).

**Why:** When additive `FT_Scene` creation or activation fails, the Band C
GameObject chain runs in **`Main.unity`**. Failed or incomplete destroy steps
leave fixture objects (`FT_*`, `GateTestCube`, Cinemachine `CM *`) in the
hierarchy and Main stays **dirty in memory**. Finalize used to call
`saveAllDirtyScenes`, which could try to persist that junk — or leave Main dirty
if save failed or a modal blocked the bridge.

**Recovery:**

1. Click **Don't Save** (usual choice for the demo project).
2. Re-run the suite — finalize now calls **`revertMainSceneIfDirty`** (removes
   `FT_*` / `GateTestCube` / `CM *` roots from Main and clears the dirty flag
   without saving to disk).
3. If the hierarchy still looks wrong after dismissing the modal, quit Unity and
   `git checkout -- demo/Assets/Scenes/Main.unity` — do **not** revert scene
   files while Unity has them open (external-modification modal).

**Note:** `--auto-revert` only restores tracked **ProjectSettings** — it does
**not** revert scene files by design.

## Full test suite (`scripts/mcp-full-test.mjs`)

- Requires `mcp-server/dist/index.js` built and a live Unity Editor with the
  target project open.
- Pass the project as an **absolute path** (relative paths hash to a different
  bridge port).
- Sets `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` and
  `UNITY_OPEN_MCP_DIALOG_POLICY=cancel` in the child env.
- **Preflight** dismisses modals, closes `InitTestScene*`, and discards dirty
  **Main** state from a prior wedged run.
- **Finalize** (after Band C or G) repeats that hygiene; Main is excluded from
  auto-save unless you pass **`--save-main`** (debug only).
- Band G (`run_tests`) is isolated last — it blocks the test runner and can
  leave the bridge in a brief post-run degraded window. Its per-step timeout is
  120s so a genuinely slow runner isn't a false "tolerated timeout."
- JSON reports include an **`isolation`** block:
  `ft_scene_created`, `ft_scene_active`, `active_scene_at_teardown`,
  `main_dirty_at_finalize`.
- **Tolerate scope.** The suite `tolerate`s only genuine ongoing limitations
  (screenshot/base64 serialization, post-domain-reload body truncation on
  asmdef/reserialize). The profiler `note`, `asmdef_list` bool, and
  `component_get` serialization bugs are fixed — those families now assert a
  clean response, so a `bridge_response_unparsable` there is a real regression.

### macOS Accessibility (automation hosts)

Dialog auto-dismiss runs in the **`node` subprocess** that executes
`run-tool`, not necessarily the Cursor IDE process. Grant Accessibility to the
**host that actually runs the test command**:

| How you run the suite | Grant Accessibility to |
|---|---|
| Cursor integrated terminal / agent | **Cursor** (and sometimes **Cursor Helper**) |
| External Terminal / iTerm | **Terminal** or **iTerm** |
| CI / scripted `node` | The CI runner binary or **`node`** itself |

See [Dialog policy → macOS Accessibility](dialog-policy.md#macos-accessibility-required-for-auto-dismiss).

```bash
node scripts/mcp-full-test.mjs --project /absolute/path/to/demo --json-out /tmp/mcp-full-test-report.json
```

## MCP test suite catalog

The full test surface is split into seven suites. Each owns a distinct
environment + pass-criteria tier; together they cover every registered tool
with at least one strict owner. The per-tool → suite mapping lives in the
generated coverage matrix (re-run `node scripts/gen-mcp-coverage-matrix.mjs`
when tools ship).

| ID | Script | Environment | Role |
|---|---|---|---|
| **S0** | `scripts/mcp-full-test.mjs` | Live Editor + bridge; `demo/` | Reachability smoke — every tool called once |
| **S1** | `scripts/mcp-behavior.mjs` | Live Editor + bridge; `Assets/MCP_BehaviorTest/` | Strict success paths, gate semantics, checkpoint/fix chains |
| **S2** | `scripts/mcp-headless.mjs` | Editor **closed** on target project | Batch spawn, offline reads, batch meta-tools |
| **S3** | `scripts/mcp-protocol.mjs` | MCP stdio server process | `tools/list`, `list_changed` notification, route spot-checks |
| **S4** | `scripts/mcp-extensions.mjs` | Live Editor + bridge | Embedded-domain success chains when groups compile in |
| **S5** | `scripts/mcp-sandbox.mjs` | Temp project clone (never mutates `demo/`) | Package lifecycle, Hub mutators, destructive build |
| **S6** | Validation Suite scenarios under `validation-suite/scenarios/unity/` | Validation Suite app + human/agent steps | Onboarding flows, `batch_execute`, client auto-config |

All `scripts/mcp-*.mjs` suites share `scripts/mcp-test-lib.mjs` (arg parsing,
expect classifier, CLI runner, scene hygiene, cleanup). Common flags:
`--project <abs>` (required), `--list`, `--json-out`, `--only`, `--band`,
`--no-cleanup`, `--timeout-ms`. Each suite documents its prerequisites in its
header comment.

**S0** is the fast reachability layer — it proves every tool is registered and
routed, tolerating known tool bugs. **S1–S5** are the strict behavioral layers:
a failure there is a real regression. **S6** covers flows that need a human or
MCP client (Hub UI, Validation Suite app). Run order + CI tiers are wired by
the orchestrator (`scripts/mcp-test-all.mjs`).

## Related docs

- [Troubleshooting](troubleshooting.md) — user-facing recovery
- [Development setup](setup/development-setup.md) — local checkout and build
- [Dialog policy](dialog-policy.md) — dismiss env vars and macOS Accessibility
- [Bridge HTTP API](api/bridge-http.md)
