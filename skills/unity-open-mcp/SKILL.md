# Unity Open MCP

Skill for AI agents driving a Unity project through the `unity-open-mcp` MCP server. Covers the gate + verify workflow, capabilities-first discovery, and the agent senses.

> Tool prefixes: `unity_open_mcp_*` (bridge-routed) and `unity_agent_*` (standalone senses). The prefix signals routing — see below.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- If you only need offline reads (asset search, find_references, read_asset), the bridge is optional — those routes parse the project from disk.

## Discover first

Call **`unity_open_mcp_capabilities`** (no args) before guessing which tools, verify rules, or fixes exist. It returns, in one local call:

- every tool with its **input schema**, **route policy** (`live` / `offline` / `offline-first` / `compressible`), **category**, and a `batchCapable` flag;
- every verify rule with its issue codes, severities, and fix ids;
- every available fix;
- a top-level **`routing`** summary (batch requirements, blocked meta-tools, live-only categories);
- planned-but-unbuilt items with `status: "planned"` and a fallback hint — they tell you what to use *today* instead of failing.

Make this your first step on a fresh project. Re-call it if tool/rule behavior seems to have changed.

## Routing (brief)

The per-tool `routePolicy` and `batchCapable` fields on the capabilities response are authoritative. In short:

- **Live is the default.** When the bridge is connected, most tools route to `POST /tools/{name}` on the Editor.
- **Batch fallback** spawns a headless Unity (`-batchmode`) **only** when a tool has `batchCapable: true` AND the live bridge is unavailable. Batch requires `UNITY_PATH` + `UNITY_PROJECT_PATH` env vars. Mutating meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) are blocked in batch — they need a live Editor.
- **Agent senses are live-only.** `unity_agent_run_tests`, screenshots, profiler, console, and spatial queries have no batch form — they need a running Editor.
- **Offline reads** (`list_assets`, `find_references`, `read_asset`, `search_assets`) parse the project from disk and never need Unity.
- **`compile_check` is always batch.** It spawns a fresh headless Unity that recompiles from scratch, even when the live bridge is up — running it against an Editor that already compiled would never surface a broken build. Use it to self-diagnose compile state.

Full route-policy and batch tables live in `docs/api/mcp-tools.md` (human/contributor docs). Do not copy them here — read them from `routing` + per-tool fields on the capabilities response.

## Core loop: mutate → gate → fix

1. **Discover** — `unity_open_mcp_capabilities`, then `unity_open_mcp_find_members` before blind reflection.
2. **Declare scope** — pass `paths_hint` with every asset path you intend to touch. Empty `paths_hint` fails with `paths_hint_required`.
3. **Mutate** — `unity_open_mcp_execute_csharp` / `invoke_method` / `execute_menu` with default `gate: enforce`.
4. **Read the gate** — on `isError: true`, inspect `gate.delta.newIssues` and `agentNextSteps`.
5. **Fix** — address the top error; `unity_open_mcp_apply_fix` with `dry_run: true` first when a `fixId` is present.
6. **Retry** — re-run the mutation; confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.

**Principle: mutation success ≠ project safe.** A successful C# compile can still break prefab references. The gate is the safety net.

### Gate modes

| Mode | When |
|---|---|
| `enforce` (default) | Normal edits — fail fast on new errors (`isError: true`) |
| `warn` | Exploratory — read `gate.delta` but the call does not error |
| `off` | Trusted admin scripts only — no checkpoint/validate |

### Gate failure (canonical shape)

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "validation": {
      "passed": false,
      "issues": [
        {
          "severity": "Error",
          "code": "MISSING_SCRIPT",
          "assetPath": "Assets/Prefabs/Player.prefab",
          "fixId": "remove_missing_script",
          "fixSafe": true,
          "agentHint": "Remove the missing script component"
        }
      ]
    },
    "delta": {
      "newErrors": 1, "newWarnings": 0,
      "resolvedErrors": 0, "resolvedWarnings": 0,
      "newIssues": ["missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"],
      "resolvedIssues": []
    }
  },
  "agentNextSteps": [
    "New error: missing_references MISSING_SCRIPT on Assets/Prefabs/Player.prefab",
    "Fix available: use unity_open_mcp_apply_fix with fix_id=\"remove_missing_script\""
  ]
}
```

On success the same envelope returns `validation.passed: true`, empty `issues`, and `agentNextSteps: []`.

### Verify rules and issue codes

The capabilities response is authoritative (call `unity_open_mcp_capabilities` for the live list). The implemented rules and their issue codes:

- **`missing_references`** — per-PPtr-field view. Codes: `missing_guid` (Error), `missing_fileid` (Error), `missing_script` (Error, fix `remove_missing_script`), `missing_local_fileid` (Warning), `empty_local_ref` (Warning), `missing_method` / `type_mismatch` / `duplicate_component` / `invalid_layer` (Warning, full-scan only).
- **`scene_prefab_health`** — structural health. Codes: `broken_reference` (Error), `high_risk_bootstrap`, `scene_object_count`, `component_hotspot`, `inactive_expensive`, `inactive_heavy`, `deep_nesting`, `override_explosion` (Warning).
- **`dependencies`** — forward-graph view of what each scoped asset depends on. Codes: `broken_dependency` (Error — an asset-graph edge to a missing asset; complements `missing_references` which scans PPtr fields), `dependency_cycle` (Warning — the scoped asset participates in a forward cycle).

Issue keys in `gate.delta.newIssues` are `ruleId|severity|assetPath|issueCode` (severity is `ERROR` / `WARN`).

## Key workflows

### Reserialize after direct YAML edits

When you edit a `.prefab` / `.unity` / `.asset` / `.mat` / `.controller` / `.anim` file directly as YAML text, run **`unity_open_mcp_reserialize`** with the touched `paths` (the `paths` array doubles as the gate scope). The round-trip rewrites the file canonically so missing fields, wrong indentation, and stale `fileID` references surface in `gate.delta`. Supported extensions: `.prefab`, `.unity`, `.asset`, `.mat`, `.controller`, `.anim`. Whole-project reserialize is intentionally unsupported — enumerate the assets you edited.

**Principle: edit freely, but always reserialize before trusting a direct YAML change.**

### read_asset: map, not dump

Raw Unity YAML is enormous. `unity_open_mcp_read_asset` returns counts, a `cmp` table that declares repeated component sets once (referenced by `c1`/`c2` codes), and a folded `tree`. Drill down with `field_limit` + `component` / `path` / `detail=verbose` instead of re-reading raw YAML. The session cache reuses the parsed model (`_cache: "hit"`). `detail: verbose` disables render-only folding; `field_limit: 0` (default) returns names only — bump it before `component` drill-down so fields are available.

Use **`unity_open_mcp_search_assets`** to locate prefabs/components/GUIDs; each result tags *why* it matched so you know which `read_asset` drill-down to run next.

### checkpoint → mutate → delta

For large refactors: `unity_open_mcp_checkpoint_create` with scoped paths → run mutations (`gate: off` for bulk, or `enforce` per call) → `unity_open_mcp_delta` against the checkpoint for a single verification pass.

### find_references before delete

Before deleting or moving an asset, call **`unity_open_mcp_find_references`** to see who depends on it. Offline-first (no live bridge needed for text-serialized assets).

### Diagnose a broken build (compile_check)

When a C# edit breaks compilation so badly the bridge assembly won't load, the live Editor enters a bad state and every live tool refuses — including the usual `unity_agent_read_console` health check. **`unity_open_mcp_compile_check`** is the recovery path: it spawns a fresh headless Unity, recompiles from scratch, and returns structured compiler errors (`status`, `errorCount`, `errors[]` with `code`/`file`/`line`/`message`) without needing the live bridge.

- Call it when `unity_open_mcp_ping`/`editor_status` say the bridge is down after a C# change.
- Read `errors[].code` (e.g. `CS0246`) and `file`/`line` to locate the break, fix, then re-check.
- If the break is in the bridge assembly itself, the tool's JSON markers never print; the MCP server then extracts `error CSxxxx` lines from the Unity log and surfaces them in the error so you still see what failed.

## Agent senses (live-only)

These give you direct project feedback and are **live-only** (no batch fallback):

- **`unity_agent_run_tests`** — EditMode + PlayMode test runner with per-test pass/fail. Filter by assembly / namespace / class / method. Use this to verify your changes — e.g. after a C# edit, run the affected assembly's EditMode tests. PlayMode is domain-reload-safe via a file handoff. Set `include_passes: false` on large suites to avoid truncation.
- **`unity_agent_read_console`** — Unity console entries via reflection. Filter `type: "error"` to confirm a clean compile after edits.
- **`unity_agent_screenshot`** — Scene / Game / isolated 2×2 composite of one GameObject.
- **`unity_agent_profiler_capture`** / **`profiler_memory`** / **`profiler_rendering`** — frame hierarchy, memory allocators, rendering env.
- **`unity_agent_spatial_query`** — physics-based raycast / overlap / bounds / ground_check / nearest against the live scene.

**Verification habit:** after any C# change, run `unity_agent_read_console` with `type: "error"` (or `unity_agent_run_tests` on the affected assembly) to confirm the change compiled and tests pass before declaring done.

## Mutating tools (gate-aware)

All accept `gate` (`enforce` / `warn` / `off`, default `enforce`) and require a non-empty `paths_hint`:

- `unity_open_mcp_execute_csharp` — compile + run a C# snippet.
- `unity_open_mcp_invoke_method` — call a method via reflection.
- `unity_open_mcp_execute_menu` — run a Unity Editor menu item.
- `unity_open_mcp_apply_fix` — apply a verify rule fix (e.g. `remove_missing_script`).
- `unity_open_mcp_reserialize` — round-trip text assets through Unity's serializer.

### Return serialization (execute_csharp / invoke_method)

Results are walked by a depth-limited reflective serializer before becoming `mutation.output`:

- Structs/POCOs → JSON objects with public fields/props (`return new Vector3(1,2,3)` → `{"$type":"Vector3","x":1,"y":2,"z":3}`).
- Lists truncate to 100 items (configurable via `max_items`); truncated arrays report `{"items":[...],"truncated":N}`.
- Recursion caps at depth 4 (configurable via `max_depth`).
- Cycles / `UnityEngine.Object` refs never infinite-loop — back-edges become `{"$ref":"TypeName"}`, Unity objects become `{"$type":...,"name":...,"instanceId":...}`.

## Read-only tools (no gate)

- `unity_open_mcp_capabilities` — discover the surface (call first).
- `unity_open_mcp_ping` — bridge health.
- `unity_open_mcp_find_members` — types, methods, properties.
- `unity_open_mcp_validate_edit` — scoped health scan, no mutation (pre-commit check).
- `unity_open_mcp_find_references` — reverse dependency lookup (offline-first).
- `unity_open_mcp_scan_paths` — run specific verify rules over scoped paths.
- `unity_open_mcp_read_asset` — compact drill-down asset read.
- `unity_open_mcp_search_assets` — compact asset search.
- `unity_open_mcp_list_assets` — offline asset listing.
- `unity_open_mcp_checkpoint_create` / `unity_open_mcp_delta` — manual checkpoint + delta.

## Project-specific skill (optional)

Call **`unity_agent_generate_skill`** with `{ "write": true }` to generate a project-specific SKILL.md reflecting the actual project — Unity version, installed packages, available verify rules, and key MonoBehaviour/ScriptableObject types from source. The `clients` parameter writes to the project-relative skill folder(s) declared in `skills/client-paths.json` (`cursor` / `claude` / `opencode` / `agents`). Regenerate after package or script changes.

For routing details, see the `routing` object on the capabilities response — not this file.
