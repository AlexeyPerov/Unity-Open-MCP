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
`AbstractMaterialNode`, slots) is partially internal and shifts across
`com.unity.shadergraph` versions. The mutating tools (`create`, `node_add`,
`node_connect`) wrap it behind a reflection helper:

- `shader_graph_open` parses the `.shadergraph` JSON directly — **always
  works**, stable across versions.
- `create` / `node_add` / `node_connect` use reflection; when the installed
  version exposes a different surface, they return a structured
  `shadergraph_api_unavailable` error rather than throwing. In that case, do
  the edit manually in the graph window, then call `shader_graph_open` to read
  the result.

## Canonical workflow: build a graph

1. **Create the asset** — `unity_open_mcp_shader_graph_create` writes a new
   `.shadergraph` at `asset_path` (an `Assets/.../*.shadergraph` path; parent
   folder must exist). `shader_type` selects the template: `Unlit` (default),
   `Lit`, `Decal`, `Fullscreen`, `Blank`. Returns the created asset path.

2. **Open + inspect** — `unity_open_mcp_shader_graph_open` brings up the
   Shader Graph editor window and returns a structured summary: node count,
   each node's `{ id, name, type, position, slots }`, edge count, each edge's
   `{ outputNodeId, outputSlot, inputNodeId, inputSlot }`. Read-only, no
   `paths_hint`. **Always call this first** to learn the existing node/slot
   ids and the output node's input slots.

3. **Add nodes** — `unity_open_mcp_shader_graph_node_add` per node. `node_type`
   is a friendly name (`UV`, `Multiply`, `Sample Texture 2D`, `Add`, `Time`,
   `Color`, `Vector3`, …) or a full `UnityEditor.ShaderGraph` class name.
   `position` is `x,y` in graph space. `properties_json` optionally seeds
   initial field values. Returns the new node's id + its slot ids.

4. **Connect nodes** — `unity_open_mcp_shader_graph_node_connect` wires an
   output slot to an input slot. Pass `source_node_id` + `source_slot`
   (output) and `destination_node_id` + `destination_slot` (input). Idempotent
   (re-connecting an existing edge is a no-op success).

5. **Verify** — call `shader_graph_open` again to confirm the graph structure;
   `unity_open_mcp_shader_get_data` on the asset path inspects the compiled
   shader properties (the runtime-facing surface — complementary, not
   overlapping with the graph tools).

## Common recipes

### Texture-tinted color

1. `shader_graph_create` → `Assets/Shaders/Tinted.shadergraph` (`shader_type:
   "Unlit"`).
2. `shader_graph_open` to read the output node's id + the Fragment input slot
   id.
3. `shader_graph_node_add` `node_type: "Sample Texture 2D"` → note node id +
   its output slot.
4. `shader_graph_node_add` `node_type: "Color"` → note node id + output slot.
5. `shader_graph_node_add` `node_type: "Multiply"` → note id + its two input
   slots.
6. `shader_graph_node_connect`: SampleTexture2D.out → Multiply.in0,
   Color.out → Multiply.in1, Multiply.out → Fragment.input.

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
| `shader_graph_open` | no (read-only) | none | Window bring-up + structured node/edge summary. Gate = Off. |
| `shader_graph_node_add` | yes | editor_settle | Add a node by friendly name or class name. |
| `shader_graph_node_connect` | yes | editor_settle | Connect output slot → input slot. Idempotent. |

Address every graph by `asset_path` (`Assets/.../*.shadergraph`). Every
mutating tool requires a non-empty `paths_hint` scoped to the graph asset path
— the gate has no whole-project fallback. When a mutating tool returns
`shadergraph_api_unavailable`, fall back to manual editing in the graph window
and re-open to read the result.
