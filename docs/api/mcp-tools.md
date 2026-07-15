# MCP tools API

`unity-open-mcp` exposes **250+ tools** for Unity editor workflows. This page is
the overview and index; focused pages own session visibility, routing/lifecycle,
and CLI automation.

> **Install / connect.** See [Manual setup](../setup/manual-setup.md) for the
> Unity packages and [MCP client configuration](../setup/client-configuration.md)
> for client paths, envelopes, and environment variables.

For exact runtime schemas, call `unity_open_mcp_capabilities`. Source
definitions live in `mcp-server/src/tools/`.

| ![Bridge status](../screenshots/bridge-status.png) | ![Bridge tools](../screenshots/bridge-tools.png) |
|---|---|

## Focused references

| Document | Owns |
|---|---|
| [Tool groups and session visibility](tool-groups.md) | Default groups, `manage_tools`, availability vs activation, reset/restart, and auto-activation. |
| [Routing, offline, and lifecycle contracts](routing-lifecycle.md) | Live/batch/offline/local selection, offline coverage, lifecycle recovery, batch behavior, errors, and multi-agent scheduling. |
| [CLI and automation](cli-automation.md) | CLI commands, options, JSON output, and links to canonical CI behavior. |
| [CI templates](../ci/README.md) | Pipeline shape, CLI exit codes, baselines, and provider templates. |
| [MCP resources](resources.md) | Resource URIs, payloads, and resource routing. |

## Tool families

- **Core runtime** — ping, C# execution, method invocation, menu calls,
  reflection, compile checks, editor status, and live batch execution.
- **Gate and validation** — validation, checkpoints, deltas, references,
  dependencies, scans, baselines, regression checks, and targeted fixes.
- **Asset intelligence** — reserialize plus structured asset read/search/list.
- **Agent senses** — tests, screenshots, Frame Debugger, console, profiler,
  memory/rendering snapshots, spatial queries, and event pull.
- **Typed editor** — assets, materials, shaders, prefabs, GameObjects,
  components, scenes, packages, selection, undo, tags/layers, scripts,
  ScriptableObjects, asmdefs, build, and project settings.
- **Embedded domains** — navigation, input, ProBuilder, particles, animation,
  splines, lighting, audio, UI, constraints, terrain, Cinemachine, Timeline,
  Tilemap, Shader Graph, VFX Graph, Memory Profiler, and 2D art.
- **Discovery** — capabilities, rules, skill generation, and tool-group
  management.
- **Unity Hub control** — editor discovery, release listing, installs, modules,
  and install-path management without a running bridge.

The canonical embedded-domain dependency and activation table is
[Extension domains](../extensions.md).

## Discover tools programmatically

Call `unity_open_mcp_capabilities` first:

```json
{
  "kind": "tools",
  "include_planned": true
}
```

Important response fields:

- `tools[].name`, `category`, `group`, and `inputSchema`
- `tools[].routePolicy` and `batchCapable`
- `tools[].lifecycle` and optional `lifecycleNote`
- `toolGroups[]` for defaults, activation guidance, and compiled availability
- `routing` for the current routing narrative
- `costHints` for output profiles, page sizes, and recommended tool chains
- `lifecycleBlock` for recovery policy

The response is the authoritative current catalog. Public documentation uses
`250+` instead of a hand-maintained exact total.

## Mutation and gate contract

Mutating tools require a non-empty `paths_hint` scoped to the project assets
they may touch. A live mutation normally runs:

```text
checkpoint → mutate → validate → delta
```

Read `gate.delta`, inline `logs`, and `agentNextSteps` even when the mutation
reports success. Gate modes are:

- `enforce` — fail when validation introduces blocking issues;
- `warn` — report issues without blocking;
- `off` — skip the gate when explicitly supported.

`unity_open_mcp_apply_fix` defaults to `dry_run: true`. Review the preview before
applying. Unsafe fixes require an explicit replacement target. A top-level
non-dry-run fix can restore touched files when application fails or introduces
new enforced errors; non-dry-run fixes are refused inside `batch_execute`
because that rollback snapshot is unavailable there. Applying a fix with
`gate: "off"` commits without rollback protection; the response carries
`rollbackDisabled: true` so the mutation is visible and the asset health must
be verified manually afterward.

Every issue can carry:

- `rootCause` — stable code for programmatic branching;
- `evidence` — instance-specific details;
- `fixCandidates` — available fixes and their safety flags;
- `remediation` — human-readable next action.

Copy `issue_id` verbatim from a scan response when applying a fix.

## Output shaping

Heavy tools share:

- `profile`: `compact` (default), `balanced`, or `full`;
- `page_size` to bound a response;
- `cursor` from the previous `pagination.next_cursor`.

Profile-aware tools include `read_asset`, `search_assets`, `scene_get_data`,
`find_references`, `validate_edit`, `scan_paths`, and `component_get`.
`capabilities.costHints` provides recommended starting page sizes and expected
cost bands.

Legacy `detail` and per-tool caps remain aliases when `page_size` is omitted.
Prefer `profile` plus uniform paging for new callers.

Mutating responses include compact per-call Unity console entries in `logs[]`.
Use `unity_senses_read_console` only when the global console buffer and stack
traces are needed.

## Selected tool contracts

### `unity_open_mcp_batch_execute`

Runs multiple typed tools sequentially in one request to an already-open
Editor. It uses one checkpoint/validation/delta cycle and one undo group.
`commands` and the union `paths_hint` are required; `fail_fast` defaults to
`true`; `gate` defaults to `enforce`. Successful earlier steps are not
automatically rolled back when a later step fails. Nested steps that resolve
to the `restart_then_settle` lifecycle (`scene_open` Single mode, `package_add`
/ `package_remove`, `asmdef_create` / `asmdef_modify`, `build_set_target` /
`build_set_defines`, `settings_set_player`, `reimport_package`) are refused
with `batch_nested_reload_unsafe` — a domain reload or scene switch mid-batch
would silently abort every later step. `batch_execute` itself and `compile_check`
are also refused as nested steps.

This is live request batching, not headless Unity fallback. See
[Routing and lifecycle](routing-lifecycle.md).

### `unity_open_mcp_dependencies`

Returns forward and reverse asset edges, broken forward GUIDs, and dependency
cycles. Use `include_impact=true` for the transitive reverse closure. The impact
closure is offline-routed; other forms prefer live Unity and fall back where
supported.

### Scene tools

`scene_set_active`, `scene_unload`, `scene_save`, and `scene_get_data` resolve
opened scenes by asset `path` first and display `name` second. Prefer paths.
For `scene_save`, a path that does not identify an open scene is a save-as
destination.

### `gameobject_modify`

In addition to legacy flat fields, the tool accepts:

- `gameObjectDiffs` for the target root;
- `pathPatchesPerGameObject` for descendants;
- `jsonPatchesPerGameObject` for component merge patches.

Application order is component JSON patches, descendant path patches, then
root diffs.

### Unity Hub control

The `unity_open_mcp_hub_*` family is local-routed and does not need a running
Editor. Install calls open Unity Hub through its deep link and return after the
request is accepted, not after download completion. Poll `hub_list_editors` to
confirm completion. System-level mutations are gate-free because they do not
modify project assets.

## Source references

- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/capabilities/tool-groups.ts`
- `mcp-server/src/tool-session-state.ts`
- `mcp-server/src/cli/`
