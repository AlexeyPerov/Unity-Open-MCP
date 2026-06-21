# Unity Open MCP (Compact v2)

Practical skill for agents operating Unity projects via `unity-open-mcp`.
Focus: discover capabilities, choose the right route, mutate safely, and recover fast when Unity or bridge state is unhealthy.

## Preconditions

- Unity Editor is open for the target project when using live-only tools.
- `unity_open_mcp_ping` should report `connected: true` for live bridge flows.
- Offline reads (`list_assets`, `search_assets`, `find_references`, `read_asset`) can work without a live bridge.

## Non-negotiable rules

1. **Discover first**: call `unity_open_mcp_capabilities` before assuming tool names, schemas, route policy, or fixes.
2. **No hardcoded bridge port**: never assume a fixed port. Use instance lock `port` as authority.
3. **Always scope mutations**: mutating tools require non-empty `paths_hint`.
4. **One test run at a time**: never start a second `unity_senses_run_tests` before the first resolves.
5. **Mutation success is not enough**: always read gate output (`gate.delta`, `agentNextSteps`).

## Fast start sequence

1. `unity_open_mcp_capabilities`
2. `unity_open_mcp_ping`
3. `unity_open_mcp_find_members` (before reflection-heavy calls)
4. Mutate with `gate: "enforce"` + scoped `paths_hint`
5. On gate failure, prefer `unity_open_mcp_apply_fix` with `dry_run: true` first
6. Re-run mutation and confirm gate delta improved

## Unity state triage (before edits/tests, and on `bridge_offline`)

Use the instance lock at:
`~/.unity-open-mcp/instances/<sha256(projectPath)>.json`

Check lock presence, `pid` liveness (`kill -0 <pid>`), `state`, and `heartbeatAt`.

- **No lock + no Unity process**: Unity not running. Start Unity or use offline/batch-capable tools.
- **PID dead**: stale lock. Reopen Unity or use batch fallback.
- **PID alive + fresh heartbeat + `idle`**: healthy; proceed.
- **PID alive + `compiling` or `isCompiling: true`**: wait; do not run tests or additional mutations.
- **PID alive + `reloading` + stale heartbeat**: likely Safe Mode / bridge assembly compile failure.
  - Call `unity_open_mcp_read_compile_errors` first.
- **PID alive + fresh heartbeat, but tools still report `bridge_offline`**: port mismatch.
  - Use lock `port`; remove stale forced port config or align environment.

## Compile failure recovery

When C# edits are involved and bridge goes offline:

1. Call `unity_open_mcp_read_compile_errors` (offline diagnostic; survives dead bridge).
2. Fix reported `errors[].file` / `line` / `code`.
3. Trigger recompile.
4. Confirm no compile errors and lock returns to fresh-heartbeat `idle`.
5. Resume normal tool flow.

Use `unity_open_mcp_compile_check` for deliberate clean batch compile verification, not as first-line recovery for every bridge failure.

## Core loop: mutate -> gate -> fix

1. **Discover**
   - `unity_open_mcp_capabilities`
   - `unity_open_mcp_find_members` for reflection targets
2. **Plan scope**
   - provide `paths_hint` for intended touch-set
3. **Mutate**
   - `execute_csharp`, `invoke_method`, `execute_menu`, typed mutators
   - default `gate: "enforce"`
4. **Read gate**
   - inspect `gate.validation.issues`, `gate.delta`, `agentNextSteps`
5. **Repair**
   - apply available fix via `unity_open_mcp_apply_fix` (`dry_run: true` first)
6. **Retry**
   - rerun mutation and verify `newErrors == 0` or `resolvedErrors > 0`

## Gate modes

- `enforce` (default): fail-fast on new errors
- `warn`: collect diagnostics without hard failing
- `off`: trusted/admin flows only (no validation safety net)

Prefer `enforce` unless you have a clear reason not to.

## Routing rules (condensed)

- **Live default**: most tools run against the active Editor bridge.
- **Batch fallback**: only for tools marked `batchCapable` and only when live bridge is unavailable.
- **Offline-first reads**: asset graph/text reads that do not require live Unity.
- **Senses are live-only**: tests, console streaming, screenshot/profiler/spatial tools require running Editor.
- **`compile_check` is always batch**.

Treat `capabilities.routePolicy` + `batchCapable` as source of truth.

## Tool selection by intent

- **Need to inspect project/tool surface**
  - `unity_open_mcp_capabilities`, `unity_open_mcp_list_rules`
- **Need safe scripted mutation**
  - prefer typed mutators first; use `execute_csharp`/`invoke_method` when typed tool is missing
- **Need reference integrity checks**
  - gate output from mutators, `validate_edit`, `scan_paths`
- **Need direct asset investigation**
  - `search_assets` -> `read_asset` drill-down -> `find_references`
- **Need tests/runtime feedback**
  - `unity_senses_run_tests`, `unity_senses_read_console`, profiler/screenshot/spatial senses

## Safety and lifecycle notes

- Some operations trigger domain reload (`restart_then_settle`) and may be blocked by dirty-scene guard.
- If refused with `scene_dirty`, save or intentionally bypass only when safe.
- Destructive/power patterns are deny-listed by default; explicit bypass requires deliberate opt-in.

## Local package source (`packages/`) recompile caveat

Edits under local `file:` packages may not auto-recompile like `Assets/` edits.
Before concluding a fix failed:

1. Trigger recompilation (package resolution/focus/import strategy).
2. Verify affected assembly timestamp is newer than source edit.
3. Then run tests once.

If results are identical to pre-fix failures, suspect stale DLL before assuming logic is wrong.

## Read-only helpers (common)

- `unity_open_mcp_capabilities`
- `unity_open_mcp_ping`
- `unity_open_mcp_find_members`
- `unity_open_mcp_validate_edit`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_find_references`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`
- `unity_open_mcp_list_assets`

## Optional project-specific skill generation

Run `unity_open_mcp_generate_skill` with `{ "write": true }` to generate project-specific guidance based on current Unity version, packages, rules, and source types.

---

### Agent checklist (minimal)

Before mutating:
- Capabilities refreshed
- Unity state classified
- `paths_hint` prepared

After mutating:
- Gate delta reviewed
- Fixes applied/retried as needed
- Compile/test verification completed

