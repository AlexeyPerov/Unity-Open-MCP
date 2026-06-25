# Hexa Sort — Setup and Reset (v1)

How to prepare a clean Unity project for a Hexa Sort benchmark run, and how to
reset between runs. Follows the **mixed manual + scripted** approach (per
`benchmarks/docs/methodology.md` §Mixed setup): scripted where it is stable and
repeatable, manual where the editor forces human interaction.

The aim is that every run starts from the **same project state**, so that
differences between runs reflect Unity Open MCP and the agent, not environment
drift.

## Scripted setup (automated, deterministic)

Run these every time, in order. They prepare a known baseline.

1. **Fresh project prerequisites.** Start from a known-clean Unity project with
   the Unity Open MCP bridge installed and compiled. The project must:
   - have **zero compile errors** at baseline (verify in step 4);
   - have the bridge reachable on its configured port;
   - have no leftover `Assets/_Benchmark/` from a prior run (see Reset).

2. **Fixture folder + baseline.** Create the benchmark workspace under the
   project:
   - `Assets/_Benchmark/HexaSort/` — where the agent will place scripts.
   - `Assets/_Benchmark/Run/` — where run evidence goes
     (`run-log.jsonl`, `A-0X-*.md`, `feedback.md`).
   These start **empty** (no scripts, no prior evidence) so each run authors
   them fresh.

3. **MCP preflight (the A-00 calls).** Before timed work, confirm the surface
   is wired:
   - `unity_open_mcp_ping` → `connected: true`.
   - `unity_open_mcp_capabilities` → non-empty; groups `core`, `gate-and-verify`,
     `typed-editor` report `available: true`.
   - `unity_open_mcp_manage_tools` (`list_groups`) → those groups are visible to
     the session.
   - `unity_open_mcp_read_compile_errors` → zero errors at baseline.
   Any failure here stops the run with a `transport_failure` /
   `project_state_issue` tag (see [`triage.md`](triage.md)).

4. **Preflight report.** A-00 writes `Assets/_Benchmark/Run/A-00-preflight.md`
   and opens `run-log.jsonl` with a run-start event. Do not begin A-01 until
   preflight Overall is PASS.

## Manual setup (operator-driven, one-time-per-session)

These cannot be scripted reliably; do them once when opening the editor, then
leave the editor alone for the run.

- **Open Unity once** and let it finish the initial import / asset database
  refresh. Wait for the spinner to stop.
- **Dismiss first-time dialogs** if they appear: license, first-time tips,
  platform module warnings, package manager prompts.
- **Confirm the bridge is running** (the hub / launcher shows it connected, or
  the editor console shows the bridge-ready log).
- **Confirm the game view renders** (only needed if a later step does a visual
  acceptance check; for v1 the benchmark drives the board-model API, so this is
  optional).

## Reset between runs

Run this before each new run to restore the baseline.

1. **Remove benchmark artifacts** from the project under test:
   - delete `Assets/_Benchmark/HexaSort/` (all authored scripts);
   - delete `Assets/_Benchmark/Run/` (all evidence + `run-log.jsonl`);
   - delete any Hexa Sort scene created during the run.
2. **Reapply the known baseline** (recreate the empty fixture folders from
   Scripted setup step 2).
3. **Confirm editor returns to idle:**
   - `unity_open_mcp_editor_status` → not compiling, not in play mode, no dirty
     benchmark scene;
   - `unity_open_mcp_console_clear` if you want a clean console for the next run;
   - `unity_open_mcp_read_compile_errors` → zero errors.
4. **Operator sign-off.** Only start the next run once the editor is confirmed
   idle and the baseline is clean. If the editor is stuck (import loop,
   unresolved errors not caused by this run), tag as `project_state_issue` and
   stop rather than running over a dirty baseline.

## Why mixed (not pure-scripted or pure-manual)

- **Manual-only** is too noisy: first-time dialogs and import timing vary, which
  poisons cross-version comparison.
- **Fully automated** (headless, scripted dialogs) is expensive for v1 and
  brittle against Unity version differences.
- **Mixed** keeps the parts that must be deterministic (baseline state, preflight,
  reset) scripted, while letting a human handle the parts Unity forces into
  interactive territory — once, at session open.

## Prerequisites checklist (operator)

- [ ] Clean Unity project with Unity Open MCP bridge compiled and reachable.
- [ ] Editor opened once; first-time dialogs dismissed; idle confirmed.
- [ ] `Assets/_Benchmark/` empty (no prior run).
- [ ] A-00 preflight PASS.
- [ ] Model/client/temperature recorded for `feedback.md`.
