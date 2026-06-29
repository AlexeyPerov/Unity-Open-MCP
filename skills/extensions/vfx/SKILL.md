# Unity Open MCP — VFX Graph Extension

Skill for AI agents driving the Unity Visual Effect Graph package
(`com.unity.visualeffectgraph`) in a Unity project through the `unity-open-mcp`
MCP server.

> This domain is **embedded** in the bridge, **compile-gated** on
> `com.unity.visualeffectgraph`, and **auto-activating**. Its tools compile in
> only when the project has `com.unity.visualeffectgraph` installed (the bridge
> sets the `UNITY_OPEN_MCP_EXT_VFX` define automatically). When the package is
> present, the `vfx` group **activates automatically** for the session — its
> tools appear in `ListTools` with no manual `manage_tools` call.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.visualeffectgraph` installed (requires URP or HDRP).
  If `capabilities` reports the `vfx` group as `available: false`, install the
  package (and a Scriptable Render Pipeline) and let the bridge recompile.
- The `vfx` group is **auto-activated** when the package is present — check
  `unity_open_mcp_manage_tools(action="list_groups")`; the group should show
  `activationSource: "auto"`. If you deactivated it manually, re-activate with
  `manage_tools(action="activate", group="vfx")`.

## Tool prefix

All tools in this pack use `unity_open_mcp_vfx_*`.

## Vocabulary

A **VisualEffectGraph** is a `.vfx` asset — the authored effect graph. It holds
**contexts** (Spawn / Init / Update / Output) which contain **blocks** (the
per-particle operations). It exposes **properties** (named, agent-addressable
parameters) that can be set per-instance at runtime.

## Version-stability note

VFX Graph's editor graph model (`UnityEditor.VFX`: `VFXGraph`, `VFXContext`,
`VFXBlock`, `VFXSlot`) is largely **internal/unstable** — more so than Shader
Graph's. This pack's tools split cleanly along that boundary:

- `vfx_list` and `vfx_open` work over the public **runtime**
  `UnityEngine.VFX.VisualEffectAsset` type and the serialized file format —
  **always work**, stable across versions.
- `vfx_block_edit` reflects over the editor graph model, which **requires the
  VFX Graph window to be open** (there is no stable public headless entry point
  to load + edit + save a graph). When the window is closed, the tool returns a
  structured `vfx_block_edit_requires_editor_window` error — open the graph in
  the window (`vfx_open`) and retry, or edit the block manually.

## Canonical workflow: discover + inspect

1. **List effects** — `unity_open_mcp_vfx_list` enumerates every `.vfx` under
   `Assets/`. Optional `filter` substring + `max_results` cap. Returns each
   asset's path, name, and file size.

2. **Open + inspect** — `unity_open_mcp_vfx_open` brings up the VFX Graph
   editor window for a `.vfx` and returns a structured summary: context count,
   block count, exposed property count, and property names. Read-only, no
   `paths_hint`.

3. **Patch a block** (when the window is open) — `unity_open_mcp_vfx_block_edit`
   patches a single property on a block. `block_selector` is a type-name
   fragment (e.g. `SetVelocity`, `SetColor`) — the first block whose type name
   contains the fragment is patched. `property` is the field; `value_json` is
   the new value. Mutating: `paths_hint` is the `.vfx` asset path.

## Common recipes

### Inspect an effect

1. `vfx_list` → find the `.vfx` path.
2. `vfx_open` on the path → read the context/block counts and property names.

### Tune a property on a block

1. `vfx_open` to bring up the window (block_edit requires the window open).
2. `vfx_block_edit` with `block_selector: "SetVelocity"`, `property: "<field>"`,
   `value_json: "<new value>"`. If the tool returns
   `vfx_block_edit_requires_editor_window`, the graph window was not open in
   the Editor — open it (the agent cannot open it headlessly) and retry.
3. `vfx_open` again to confirm.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "game") visually confirms the effect on a
  `VisualEffect` component after assignment.
- `unity_senses_profiler_memory` / `unity_open_mcp_profiler_get_script_stats`
  give CPU/frame context — pair with the broader profiler family to profile a
  heavy effect.
- `unity_open_mcp_execute_csharp` is the fallback for advanced operations the
  reflection layer can't reach (custom blocks, Subgraph wiring, event setup).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `vfx_list` | no (read-only) | none | Enumerate `.vfx` assets under `Assets/`. Gate = Off. |
| `vfx_open` | no (read-only) | none | Window bring-up + structured context/block/property summary. Gate = Off. |
| `vfx_block_edit` | yes | editor_settle | Patch one property on one block. Requires the VFX Graph window open. |

Address every graph by `asset_path` (`Assets/.../*.vfx`). The mutating tool
requires a non-empty `paths_hint` scoped to the graph asset path — the gate has
no whole-project fallback. When `vfx_block_edit` returns
`vfx_block_edit_requires_editor_window`, the editor graph model could not be
reached — open the graph in the VFX Graph window and retry, or edit the block
manually.
