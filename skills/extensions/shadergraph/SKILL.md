# Unity Open MCP — Shader Graph Extension

Skill for AI agents driving the Unity Shader Graph package
(`com.unity.shadergraph`) in a Unity project through the `unity-open-mcp` MCP
server.

> This domain is **embedded** in the bridge, **compile-gated** on
> `com.unity.shadergraph`, and **auto-activating**. Its tools compile in only
> when the project has `com.unity.shadergraph` installed (the bridge sets the
> `UNITY_OPEN_MCP_EXT_SHADERGRAPH` define automatically). When the package is
> present, the `shadergraph` group **activates automatically** for the session
> — its tools appear in `ListTools` with no manual `manage_tools` call. This is
> the first domain to ship with package-detection auto-activation.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.shadergraph` installed. If `capabilities` reports
  the `shadergraph` group as `available: false`, install the package (and a
  Scriptable Render Pipeline — URP or HDRP) and let the bridge recompile.
- The `shadergraph` group is **auto-activated** when the package is present —
  check `unity_open_mcp_manage_tools(action="list_groups")`; the group should
  show `activationSource: "auto"`. If you deactivated it manually, re-activate
  with `manage_tools(action="activate", group="shadergraph")`.

## Tool prefix

All tools in this pack use `unity_open_mcp_shader_graph_*` (note the
underscore split — `shader_graph`, matching the catalog word-boundary
convention).

## Vocabulary

A **Shader Graph** is a `.shadergraph` asset — the authored graph that
compiles into a shader. It holds an ordered list of **nodes**, each exposing
input and output **slots** (identified by integer slot ids). **Edges** connect
an output slot of one node to an input slot of another. The graph has one or
more **output nodes** (Vertex / Fragment / etc.) that the rest of the graph
feeds into.

Node **ids** are stable string GUIDs assigned by Shader Graph — read them via
`shader_graph_open` or the return value of `shader_graph_node_add`. Slot ids
are integers; an `open` summary lists each node's slots with `{ id, name }`.

## Version-stability note

Shader Graph's editing API (`UnityEditor.ShaderGraph`: `GraphData`,
`AbstractMaterialNode`, slots) is internal to the package. The tools reach it
by reflection and work on the graph the way the package does: they load it as
the importer does, edit it through `GraphData`, and write it with the
package's own serializer (verified on Shader Graph 17 / Unity 6). `create`
never runs the `Assets/Create` menu items (those need an interactive rename
and can raise a modal preference dialog).

- When the installed version exposes a different surface, a tool returns a
  structured `shadergraph_api_unavailable` (or `graph_load_failed`) error
  rather than throwing. Do the edit manually in the graph window, then call
  `shader_graph_open` to read the result.

## Canonical workflow: build a graph

1. **Create the asset** — `unity_open_mcp_shader_graph_create` writes a new
   `.shadergraph` at `asset_path` (an `Assets/.../*.shadergraph` path; parent
   folder must exist). `shader_type` selects the template: `Unlit` (default),
   `Lit`, `Decal`, `Fullscreen`, `Blank`. The graph gets the active render
   pipeline's target for the template (the response's `target`, e.g.
   `UniversalTarget/UniversalUnlitSubTarget`; `null` for Blank) and the
   target's master-stack blocks. A template the pipeline has no target for
   answers `shader_type_unsupported`; an unknown name answers
   `unknown_shader_type`.

2. **Open + inspect** — `unity_open_mcp_shader_graph_open` opens the graph in
   its Shader Graph window (or focuses the window already showing it) and
   returns a structured summary: node count, each node's
   `{ id, name, type, position, slots }` (slots are `{ id, name, direction }`),
   edge count, each edge's `{ outputNodeId, outputSlot, inputNodeId, inputSlot }`,
   and `window` `{ action: opened | focused, open, title }`. The master stack's
   block nodes are listed too (`type: "BlockNode"`, names like
   `SurfaceDescription.BaseColor`) — connect into their input slot to feed the
   output. Writes no asset, no `paths_hint`. **Always call this first** to learn
   the existing node/slot ids; pass `window: "none"` to read without opening a
   window (also the only form that works in batch mode — `"open"` refuses
   `editor_window_unavailable` there).

3. **Add nodes** — `unity_open_mcp_shader_graph_node_add` per node. `node_type`
   is a friendly name (`UV`, `Multiply`, `Sample Texture 2D`, `Add`, `Time`,
   `Color`, `Vector3`, …) or a full `UnityEditor.ShaderGraph` class name.
   `position` is `x,y` in graph space. `properties_json` optionally seeds
   public node properties of a simple type (number, bool, string, enum);
   keys that match none come back in `ignoredProperties`. Returns the new
   node's `nodeId` + its `slots` (`{ id, name, direction }`).

4. **Connect nodes** — `unity_open_mcp_shader_graph_node_connect` wires an
   output slot to an input slot. Pass `source_node_id` + `source_slot`
   (output) and `destination_node_id` + `destination_slot` (input). Idempotent
   (re-connecting an existing edge is a no-op success). An unknown id answers
   `node_not_found` / `slot_not_found`; two outputs, two inputs or a cycle
   answer `connection_rejected`.

5. **Verify** — call `shader_graph_open` again to confirm the graph structure;
   `unity_open_mcp_shader_get_data` on the asset path inspects the compiled
   shader properties (the runtime-facing surface — complementary, not
   overlapping with the graph tools).

## The graph window

- `node_add` / `node_connect` on a graph whose window is open reload the window
  from the file, so it shows the edit and never asks "Graph has changed on
  disk". When the window holds unsaved edits (a human is editing), the tools
  refuse `graph_has_unsaved_changes` and write nothing — ask the human to save
  or discard them.
- The window stays open after `open`. Close it with
  `shader_graph_open {"asset_path": …, "window": "close"}` (`action: closed`,
  or `not_open`) **before deleting or moving the graph** — Shader Graph asks
  what to do in a modal dialog when an open graph's file disappears, and that
  dialog blocks the Editor until a human answers. `close` refuses
  `graph_has_unsaved_changes` instead of raising the save prompt.

## Common recipes

### Texture-tinted color

1. `shader_graph_create` → `Assets/Shaders/Tinted.shadergraph` (`shader_type:
   "Unlit"`).
2. `shader_graph_open` to read the `SurfaceDescription.BaseColor` block
   node's id (its input slot is `0`).
3. `shader_graph_node_add` `node_type: "Sample Texture 2D"` → note node id +
   its output slot.
4. `shader_graph_node_add` `node_type: "Color"` → note node id + output slot.
5. `shader_graph_node_add` `node_type: "Multiply"` → note id + its two input
   slots.
6. `shader_graph_node_connect`: SampleTexture2D.out → Multiply.in0,
   Color.out → Multiply.in1, Multiply.out → BaseColor block.in.

### Sub-graph reuse

`shader_graph_open` lists every node, so to wire a Sub Graph asset you already
authored, add its node by the Sub Graph's type/friendly name, then connect its
output slot into the parent graph. Read the Sub Graph's output slot id from its
own `open` summary first.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "game") visually confirms the shader on a
  material after assignment.
- `unity_open_mcp_shader_get_data` / `unity_open_mcp_shader_list_all` inspect
  the **compiled** shader (properties, passes, compile errors) — the runtime
  surface, complementary to this authoring surface.
- `unity_open_mcp_execute_csharp` is the fallback for advanced operations the
  reflection layer can't reach (e.g. custom function nodes, sub-graph
  creation, keyword setup).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `shader_graph_create` | yes | editor_settle | New `.shadergraph` from a template. |
| `shader_graph_open` | no (writes no asset) | none | Opens / focuses (`window: open`), closes (`close`) or skips (`none`) the graph's window + structured node/edge summary. Gate = Off. |
| `shader_graph_node_add` | yes | editor_settle | Add a node by friendly name or class name. |
| `shader_graph_node_connect` | yes | editor_settle | Connect output slot → input slot. Idempotent. |

Address every graph by `asset_path` (`Assets/.../*.shadergraph`). Every
mutating tool requires a non-empty `paths_hint` scoped to the graph asset path
— the gate has no whole-project fallback. When a mutating tool returns
`shadergraph_api_unavailable`, fall back to manual editing in the graph window
and re-open to read the result.
