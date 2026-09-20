# Batch and gates

## Core loop: mutate → gate → fix

1. **Discover** — `capabilities`, then `find_members` for reflection targets.
2. **Declare scope** — `paths_hint` for every asset path you intend to touch.
3. **Mutate** — typed tools preferred over `execute_csharp` / `invoke_method` / `execute_menu`; default `gate: "enforce"`.
4. **Read the gate** — on `isError: true`, inspect `gate.delta.newIssues` + `agentNextSteps`.
5. **Fix** — address the top error; `apply_fix` with `dry_run: true` first when a `fixId` is present. Each issue carries `rootCause` (machine-readable, branch on it) + `evidence` (the specific broken ref/value) + `fixCandidates` (every fix option with its `safe` flag) + `remediation` (the human-readable next step) — read these before choosing a fix.
6. **Retry** — confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.

### Gate modes


| Mode                | When                                                    |
| ------------------- | ------------------------------------------------------- |
| `enforce` (default) | Normal edits — fail fast on new errors                  |
| `warn`              | Exploratory — read `gate.delta` but call does not error |
| `off`               | Trusted admin scripts only — no checkpoint/validate     |


### Gate failure (canonical shape)

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "validation": {
      "passed": false,
      "issues": [{
        "severity": "Error", "code": "MISSING_SCRIPT",
        "assetPath": "Assets/Prefabs/Player.prefab",
        "fixId": "remove_missing_script", "fixSafe": true
      }]
    },
    "delta": {
      "newErrors": 1, "resolvedErrors": 0,
      "newIssues": ["missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"]
    }
  },
  "logs": [
    { "severity": "warning", "message": "...", "source": "unity" }
  ],
  "agentNextSteps": [
    "New error: missing_references MISSING_SCRIPT on Assets/Prefabs/Player.prefab",
    "Fix available: use unity_open_mcp_apply_fix with fix_id=\"remove_missing_script\""
  ]
}
```

Issue keys in `gate.delta.newIssues` are `ruleId|severity|assetPath|issueCode` (severity is `ERROR` / `WARN` / `INFO`). The `newIssues` / `resolvedIssues` arrays are **bounded** (first 25 keys) with a `…Truncated` count and a `newIssuesByRule` / `resolvedIssuesByRule` histogram — branch on `gate.outcome` + the counts (`newErrors`, `newWarnings`); fetch the full list via `validate_edit` / `scan_paths` when you need it. On success, `validation.passed: true`, empty `issues`, `agentNextSteps: []`. The `logs` array carries Unity console entries emitted *during this call* (always present, `[]` when none) — read it inline instead of polling `read_console` after a mutation. Stacks are omitted here; `read_console` stays the verbose path.

**`gate.delta` can be `null`** — never a zeroed object — when no delta was computed. Checkpoint failure and mutation failure report `gate.outcome: "failed"` with `mutation.success: false`; a validate-scan failure (the scan threw, or a verify rule threw on either side of the delta) reports `gate.outcome: "validate_scan_failed"` with the mutation committed. All three carry `isError: true`: the mutation's health is **unknown** (or it failed outright). When a verify rule threw, `gate.rulesFailed` lists the rule ids whose findings are missing. Do NOT retry the mutation (it already applied) and do NOT treat the missing delta as a clean pass — run `validate_edit` / `scan_paths` on the touched paths to confirm health. `scan_paths` / `validate_edit` responses carry the sibling signal as `rulesFailed` + `scanIncomplete: true` when any selected rule threw.

### Verify rules and issue codes

Authoritative via `capabilities` (call for the live list). Implemented:

- `**missing_references**` — per-PPtr-field view. Codes: `missing_guid` (Error, fix `relink_broken_guid` — unsafe, needs `target_guid`), `missing_fileid` (Error), `missing_script` (Error, fix `remove_missing_script`), `missing_local_fileid` (Warning), `empty_local_ref` (Warning on **user-script** fields — the real bugs; **Info** on built-in empty-by-default fields like `m_SelectOn*`, sprite swaps, TMP material/style — the ~98% noise floor), `missing_method` / `type_mismatch` / `duplicate_component` / `invalid_layer` (Warning, full-scan only). `empty_local_ref` keys are `<transformPath>:<property>` (content-addressed, survives prefab rebuilds); use `fail_on_severity` / `profile` to control what surfaces.
- `**scene_prefab_health**` — structural health. Codes: `broken_reference` (Error), `high_risk_bootstrap` / `scene_object_count` / `component_hotspot` / `inactive_expensive` / `inactive_heavy` / `deep_nesting` / `override_explosion` (Warning).
- `**dependencies**` — forward dependency graph. Codes: `broken_dependency` (Error — asset-graph edge to a missing asset; fix `relink_broken_guid` — unsafe), `dependency_cycle` (Warning).
- `**asmdef_audit**` — assembly definition health. Codes: `broken_asmdef_reference` (Error — reference that does not resolve to a compiled assembly or known asmdef), `asmdef_missing_name` (Error — no `name` field), `malformed_asmdef` (Error — JSON failed to parse), `asmdef_duplicate_name` (Error, full-scan — name shared by 2+ asmdefs), `asmdef_circular_reference` (Error, full-scan — DFS cycle over the name-based reference graph), `asmdef_editor_in_runtime` (Warning — runtime assembly referencing an editor assembly), `asmdef_auto_referenced_orphan` (Warning, full-scan — `autoReferenced=false` and unreferenced), `asmdef_platform_filter_broad` (Warning — no platform filters), `asmdef_platform_filter_contradict` (Warning — simultaneous include + exclude platforms), `asmdef_version_define_invalid` (Warning — version define references a `com.*` package).
- `**project_health**` — whole-project integrity, full-scan only (does not fire on a scoped validate_edit). Codes: `orphan_meta` (Warning — `.meta` with no companion asset, fix `remove_orphan_meta` safe), `duplicate_guid` (Error — GUID shared by 2+ assets, fix `fix_duplicate_guid` unsafe), `missing_project_setting` (Error — required ProjectSettings file missing), `project_empty_folder` / `project_meta_only_folder` (Warning), `project_deep_nesting` (Warning — depth > 8), `project_large_folder` (Warning — > 200 files), `project_broken_asset` (Error — asset failed to load), `project_empty_scene` (Warning — zero root objects).
- `**materials**` — material reference + performance health. Per-asset codes: `missing_shader` (Error — null shader or InternalErrorShader, i.e. the original shader failed to compile/is missing), `builtin_shader` (Warning — Standard/Legacy/Mobile builtin), `builtin_texture` (Warning — unity_builtin texture), `render_queue_override` (Warning), `unable_to_load` (Error). Note: there is no `missing_texture` code — a null texture slot is usually legitimate (most optional slots like `_BumpMap` are intentionally empty); a genuinely-broken texture PPtr surfaces via the `missing_references` rule's `missing_guid` instead. Full-scan-only codes: `duplicate_material` (Warning — SHA-256 fingerprint match), `unused_material` (Warning — unreferenced + not in Resources), `variant_parent_invalid` (Error), `variant_deep_chain` / `variant_heavy_overrides` (Warning), `gpu_instancing_off` / `srp_batcher_incompatible` (Warning), `null_material` / `null_material_slot` / `builtin_material` (Warning, renderer-side).
- `**animation_analysis**` — animator controller + clip health. Per-asset codes: `missing_clip` (Error — state with no motion assigned), `empty_clip` (Warning — `.anim` declaring no curves), `unreachable_state` (Warning — not reachable from entry/default/any-state via BFS), `complexity_over_threshold` (Warning — > 50 states), `anystate_overuse` (Warning — > 5 any-state transitions), `parameter_mismatch` (Warning — script references a param not in the controller), `expensive_curves_density` / `expensive_curves_count` (Warning). Full-scan-only: `duplicate_clip` (Warning — byte-size match).
- `**shader_analysis**` — shader compile + variant health. Per-asset codes: `shader_compile_error` (Error — InternalErrorShader), `missing_shader_asset` (Error — `.shader` failed to load), `variant_explosion` (Warning — `2^keywords × passes` over threshold), `pass_count_exceeded` (Warning), `fallback_shader` (Warning — has a `Fallback` directive), `expensive_feature_platform` (Warning — mobile-expensive keywords; fires only when the scan runs with the mobile profile — `scan_all`/`baseline_create`/`regression_check` with `platform_profile: "mobile"`; live `validate_edit` / `scan_paths` always use desktop), `platform_keyword_mismatch` (Warning — HDRP shader on mobile profile, same mobile-profile gating). Full-scan-only: `duplicate_keyword_profiles` (Warning — materials sharing a keyword set).

### Issue explainability

Every issue in a `scan_paths` / `validate_edit` response carries optional explainability fields (beyond `ruleId` / `severity` / `code` / `assetPath` / `description`). Two code-shaped fields carry different things — match the right one:

- **`code`** — the BARE catalog code (`missing_script`, `invalid_layer`, …). This is what `capabilities` / `list_rules` / the table above advertise. Branch on `code === "<bare>"`.
- **`issueCode`** — the full key-discriminator form (`missing_script:<guid>`, `invalid_layer:7`, `duplicate_component:<type>:<gameObject>`). Used internally for gate-delta tracking (so two broken GUIDs on one asset do not collapse to one key) and by `apply_fix` to target the exact instance. Pass `issueCode` (or the whole `issue_id`) back to `apply_fix`, but branch recovery logic on the bare `code` / `rootCause`.

- **`rootCause`** — a stable machine-readable code identifying *why* the issue class happens. Branch recovery on this, not on free-text. Values: `missing_guid_reference`, `missing_fileid_reference`, `missing_script_class`, `missing_dependency`, `orphaned_meta`, `duplicate_guid`, `structural_complexity`, `configuration_mismatch`, `resource_missing`, `build_blocker`. The same code is declared on the issue descriptor in `capabilities` / `list_rules` (`issues[].rootCause`).
- **`evidence`** — the per-instance payload that fired *this* issue (the broken reference's GUID / fileID / line, the duplicate group's paths, the count vs threshold, etc.). Keys are issue-class-specific, always string-valued. Absent when the rule has no per-instance detail.
- **`fixCandidates`** — every fix that can resolve the issue, each `{fixId, safe}`. Use this over the legacy single `fixId` / `fixSafe` pair (kept for backwards compat): it lists safe **and** unsafe options so you pick deliberately. Absent when no fix exists.
- **`remediation`** — a short, clean, user-visible playbook for the issue class (the human-readable next step). Pair with `rootCause`: branch on the code, surface the remediation text.

### Fixes

`apply_fix` defaults to `dry_run: true` (the dry-run short-circuits the gate entirely — returns description/candidates without checkpoint+validate):

- `**remove_missing_script**` (safe) — strips `MonoBehaviour` whose script GUID no longer resolves. Works on `.prefab` / `.unity`.
- `**remove_orphan_meta**` (safe) — deletes a `.meta` whose companion asset was deleted. Producer: `project_health` (live) / `offline_integrity` (offline). No asset data lost.
- `**relink_broken_guid**` (unsafe) — rewrites a broken external GUID reference. Dry-run advertises candidate targets; apply requires `target_guid`. Never auto-applied.
- `**fix_duplicate_guid**` (unsafe) — regenerates the GUID of one colliding asset. Re-GUIDing silently rewires the asset graph, so pick the less-referenced asset deliberately; apply on that asset's issue id. Producer: `project_health` / `offline_integrity`. Never auto-applied.
- `**reassign_missing_shader**` (unsafe) — assigns a shader to a material whose shader is null / the error shader. Dry-run advertises candidate shaders; apply requires `target_shader` (shader name e.g. `Standard`, or asset path). Producer: `materials`. Never auto-applied.

If `fix_id` omitted, the response lists every fix that can resolve the given `issue_id`.

**Safe auto-fix rollback.** A non-dry-run `apply_fix` runs checkpoint → apply → validate → delta, and if the fix fails to apply **or** introduces new errors under `enforce`, the touched files are restored to their pre-fix state and the response carries a top-level `rollback` block: `{rolledBack: true, reason, restoredPaths[]}`. Read `gate.delta.newErrors` + `rollback` together — a rolled-back fix left no project change, so inspect the issue manually before retrying. Rollback is **not** triggered by new warnings (informational) or under `warn`/`off` gate modes (report-only). Applying with `gate: "off"` commits without rollback protection; the response carries `rollbackDisabled: true` so the unguarded mutation is visible — verify asset health with `validate_edit` afterward.

## Gate intelligence: plan before, explain after

Three read-only, gate-free tools compose gate foundations into agent-actionable shapes. They do **not** run a rule scan or mutate — treat outputs as guidance (every response carries a `heuristicNote`).

- **Before mutating** — `unity_open_mcp_impact_preview` (`paths_hint`): resolves the auto-selected rule set, classifies each path, reports coarse `risk.band` (`low`/`moderate`/`high`) with `confidence`. Size risk before paying for a checkpoint.
- **Before mutating** — `unity_open_mcp_gate_budget_estimate` (`paths_hint`, `mode: "cache"` | `"sample"`): forecasts `estimatedDurationMs` (lower bound) + `estimatedIssueBudget` (upper bound) with `basis` + `confidence`. `sample` runs a cheap checkpoint scan (grounded); `cache` inspects the latest VerifyCacheService snapshot (cheap, coarse).
- **After mutating** — `unity_open_mcp_mutation_explain` (`checkpoint_id?`, `tool_name?`): projects the most recent gate run into a `narrative` + structured `summary` (outcome, new/resolved counts, durations, `agentNextSteps`).

Typical sequence: `impact_preview` (size) → `gate_budget_estimate` `mode: "sample"` (cost) → mutate → `mutation_explain` (narrate).

### Checkpoint → mutate → delta (large refactors)

`checkpoint_create` with scoped paths → run mutations (`gate: off` for bulk, or `enforce` per call) → `delta` against the checkpoint for a single verification pass.

- **Session-scoped.** Checkpoints live in an in-memory ring buffer (capacity 20, LRU-evicted on access recency) and are cleared on script recompile, domain reload, or editor restart — they are never persisted to disk. Capture immediately before mutating and delta right after; do not hold a checkpoint id across a recompile.
- **Missing checkpoint is non-blocking.** If `delta` (or `mutation_explain` with a `checkpoint_id`) references a checkpoint that is gone (evicted, or lost to a reload), the call returns success with `"unavailable": true` + `agentNextSteps` rather than an error. Treat it as "no baseline to delta against" and fall back to `validate_edit` / `scan_paths` on the relevant paths. It does not block the workflow.
- **Clear from the editor.** The Bridge window → Gate tab → Checkpoint history has a "Clear history" button (two-click confirm) that empties the session ring buffer. It touches nothing on disk and leaves gate-run history intact.

### Batch multiple mutations into one call (`batch_execute`)

When you need to set up many things at once (spawn N objects + materials + assignments), one `**unity_open_mcp_batch_execute`** call runs the whole sequence in a single HTTP round trip, cutting latency and token cost. The batch is **strictly safer than sequential calls**: the WHOLE sequence shares ONE gate cycle (one checkpoint → all steps → one validate/delta) and ONE undo group.

- **When to use.** Multi-step setup where each step is independent and typed (create 3 cubes + 3 materials + assign). Prefer the per-tool array surfaces (`component_modify.fields[]`, `gameobject_modify` multi-surface) when a *single* tool already batches — `batch_execute` is for bundling *different* tools.
- **Always pass `paths_hint`.** The batch is mutating; scope `paths_hint` to the union of every path the nested steps touch (scene paths, asset paths). Required.
- **Prefer typed tools inside the batch.** Each `commands[i]` carries a full tool id + its `params` (omit `paths_hint`/`gate` per step — the batch owns those for the sequence). Nested meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`), `batch_execute` itself, `compile_check`, and any `restart_then_settle` tool (`scene_open` Single mode, `package_add`/`remove`, `asmdef_create`/`modify`, `build_set_*`, `settings_set_player`, `reimport_package`) are **blocked** in v1 — a domain reload or scene switch mid-batch would silently abort every later step. The refusal is `batch_nested_reload_unsafe`. Use these as single top-level calls.
- **`run_tests` is blocked too, for a different reason** (`batch_step_requires_server_poll`): its terminal result comes from the server polling a results file, which only the top-level route does. Nested, the step would report `success` with a non-terminal `{status:"started"}` body and you would never see the outcome.
- **Nestability is NOT the `batchCapable` flag.** That flag answers "is there a headless batch-spawn fallback when the bridge is down?" — a different axis. Read the refusals above.
- **Every refusal is pre-flight and names a reachable alternative.** Nothing ran, `batch.results[]` is empty, nothing to undo. Where a meta-tool equivalent exists the message gives it (`recompile_scripts` → `CompilationPipeline.RequestScriptCompilation()` via `execute_csharp` + poll `editor_status`; `run_tests` → `invoke_method` on the test method) — useful when your client did not refresh its tool list after `manage_tools` activated a group, so the "single top-level call" it points at is not visible to you.
- **`fail_fast: true` (default) stops on the first failure**; later entries are `skipped` (not executed). Set `fail_fast: false` to run every step and collect per-step errors.
- **Read `gate.delta` ONCE at the end** — the batch-level gate reports new issues introduced by the whole sequence. Partial-failure note: v1 does NOT roll back successful steps when a later step fails (same as the competitor); undo the whole batch with a single `editor_undo` if needed.
- **Limits.** 25 commands default, 100 hard max (`batchExecuteMaxCommands` in `.unity-open-mcp/settings.json`). `parallel: true` is accepted but ignored (Unity API is main-thread; sequential only).
- **Response shape:** `batch.results[]` carries `{ index, tool, status }` per step (`success`/`failed`/`skipped`) with `output` on success and `error` on failure.

### Read-only requests and batch preflight

Use `execute_csharp(read_only: true)` only for inspection without writes,
compile/refresh, scene switching, or play transitions. Pure reads run against
dirty scenes without saving them, skip gates even with `paths_hint`, and return
lifecycle `none`. This assertion does not sandbox indirect C# calls. Known
explicit disruptive calls retain scope and dirty-scene protection.

All-read `batch_execute` calls, including screenshots, need no `paths_hint` and
create no checkpoint or undo group. Mixed/mutating batches require the explicit
union scope. Every step is schema/lifecycle checked before dispatch; fix all
reported errors before retrying. A failed mutation before validation reports
`gate.outcome: "skipped"` and `skippedReason: "mutation_failed"`. Read
`mutation.success` separately: committed work in a partial batch still receives
validation, so a passed gate does not mean every step succeeded.
