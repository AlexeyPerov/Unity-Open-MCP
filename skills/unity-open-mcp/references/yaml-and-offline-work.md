# YAML and offline work

### Reserialize after direct YAML edits

When you edit `.prefab` / `.unity` / `.asset` / `.mat` / `.controller` / `.anim` directly as YAML text, run `**unity_open_mcp_reserialize`** with the touched `paths` (the `paths` array doubles as gate scope). Round-trip rewrites canonically so missing fields, wrong indentation, and stale `fileID` references surface in `gate.delta`. Whole-project reserialize is intentionally unsupported — enumerate assets you edited. Default targets asset YAML only (no `.meta` diff on body-only edits); pass `include_meta: true` only for upgrade/importer-change workflows.

Prefer a typed live tool while the Editor is reachable. Never directly edit an open or dirty scene or asset YAML. If direct editing is necessary, explicitly identify the offline-file route and confirm the target is not open/dirty. Offline work remains supported when the Editor is unavailable or the user chooses file-level repair. Preserve GUID/fileID relationships and verify the intended scene: wrong-scene edits can silently damage a different asset. Reimport/reserialize the touched files and run scoped validation before trusting the result.

### read_asset: map, not dump

Raw Unity YAML is enormous. `read_asset` returns counts, a `cmp` table declaring repeated component sets once (referenced by `c1`/`c2` codes), and a folded `tree`. Drill down with `field_limit` + `component` / `path` / `profile=full` (or `detail=verbose`) instead of re-reading raw YAML. Session cache reuses the parsed model (`_cache: "hit"`). `profile: full` disables render-only folding; `field_limit: 0` (default) returns names only — bump it before `component` drill-down so fields are available. `page_size`/`cursor` page the `tree` rows.

Use `**search_assets`** to locate prefabs/components/GUIDs; each result tags *why* it matched so you know which `read_asset` drill-down to run next.

### Reads that save tokens

Raw Unity data is large. Prefer the cheap, structured reads before reaching for verbose output:

- **`read_asset`** returns a folded `tree` + `cmp` table + counts, not raw YAML. Drill into a subtree with `component` / `path` + `field_limit` instead of re-reading the whole asset; the parsed model is session-cached (`_cache: "hit"`). `field_limit: 0` (default) returns field names only — bump it only for a `component` drill-down where you need values.
- **`manage_tools(action="list_groups")`** — sessions start with two default-on groups (`core` + `gate-and-verify`); activate only the other group you need so the full **250+ tool** surface stays out of the prompt. Prefer `action="activate_for"` with a free-text `intent` (or `action="suggest"` to preview) over hand-picking ids — it brings the right groups online in one call.
- **`read_console`** with `detail: "summary"` returns messages only; reserve `detail: "verbose"` for when you need Unity-internal stack frames.
- **`search_assets`** tags *why* each result matched, so you skip broad reads and go straight to the right drill-down.
- **Output profiles + paging.** The heavy tools (`read_asset`, `search_assets`, `scene_get_data`, `find_references`, `validate_edit`, `scan_paths`, `component_get`) take `profile: compact|balanced|full` (`compact` is the default — the cheap folded/counts shape). Raise to `balanced`/`full` to expand. Page large results with `page_size` + the `pagination.next_cursor` token (omit `page_size` to get the whole payload at once). For `component_get`, start compact then use `property_path` or paging to expand one subtree. For `find_references` / `validate_edit` / `scan_paths`, the compact default returns counts/groupings only — pass `profile: "balanced"` to get the per-asset / per-issue lists those tools previously returned by default. (The legacy `detail` / `max_results` / `max_nodes` caps remain as aliases.)
- **`capabilities`** before assuming tool names/schemas/route policy — cheaper than discovering a tool's real signature by trial and error.
- **Prefer typed tools** (`component_modify`, `scene_get_data`, …) over `execute_csharp` / `invoke_method` — explicit schemas, smaller request/response envelopes, same gate.

### find_references before delete

Before deleting or moving an asset, call `**find_references*`* to see who depends on it. Offline-first (no live bridge needed for text-serialized assets).

### dependencies: both directions in one call

When you need **both** directions (what this asset depends on AND what depends on it), or the broken-edge / cycle view, call `**dependencies*`*. It returns the forward + reverse edge sets plus the same `broken_dependency` GUIDs and `dependency_cycle` trails the `dependencies` verify rule computes — no second dependency graph is built (it reuses the verify scanner + the `find_references` reverse walker). Live bridge only (the scanners call AssetDatabase); pass `detail: "summary"` for counts only when you just need the magnitude.
