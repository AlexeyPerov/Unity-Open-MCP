# ShaderGraph — embedded domain tools

Shader Graph typed tools (`unity_open_mcp_shader_graph_*`), embedded inside the
bridge. Four tools for in-editor shader-graph authoring: create a Shader Graph
asset, open it in its Shader Graph window (with a structured node/edge
summary; `window: none` reads without a window, `close` closes it), add a
node, and connect two node ports.

## Compile gate

Two-layer gate (see `docs/contributing/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_SHADERGRAPH` via `versionDefines` when
   `com.unity.shadergraph` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_SHADERGRAPH"]` and references
   `Unity.ShaderGraph.Editor`. Unity only compiles it when the define is set,
   so the optional package reference never breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_SHADERGRAPH` as a belt-and-suspenders guard.

## Auto-activation (M20 Plan 7 / T20.7.0)

This is the first domain that ships with **auto-activation**: the
`shadergraph` group activates automatically for the session when
`com.unity.shadergraph` is installed — no manual `manage_tools` call required.
Auto-activation is ephemeral (per session, resets on server restart) and is
additive to the manual-activation model. Deactivate via
`unity_open_mcp_manage_tools(action="deactivate", group="shadergraph")` to
hide the tools.

## Reflection over the editing API

Shader Graph's editing API (`UnityEditor.ShaderGraph`: `GraphData`,
`AbstractMaterialNode`, `MaterialSlot`, `MultiJson`, `FileUtilities`) is
internal to the package. Every tool goes through a single reflection helper,
`ShaderGraphApi`, so a version change is fixed in one place. The helper works
on the graph the way the package does: it loads a `.shadergraph` like
`ShaderGraphImporter` (`GraphData` + `MultiJson.Deserialize` + `OnEnable` +
`ValidateGraph`), edits it through `GraphData` (`AddNode`, `Connect`) and
writes it with `FileUtilities.WriteShaderGraphToDisk` before importing.
`create` builds the graph like the package's `NewGraphAction` (contexts, the
active pipeline's target and sub-target for the template, the target's active
blocks, the default category) instead of running the `Assets/Create` menu items,
which need an interactive rename and may raise a modal preference dialog.
`open` summarizes the loaded graph and opens the window the way double-clicking
the asset does (`AssetDatabase.OpenAsset`). The tools keep the window's own
modal dialogs away: an edit reloads an open window from the file (its "Reload"
answer to "Graph has changed on disk"), and nothing closes or writes under a
window that holds unsaved edits (`graph_has_unsaved_changes`). When reflection cannot reach a needed
member (version mismatch / internal rename), the tool returns a structured
error envelope (`shadergraph_api_unavailable`, `graph_load_failed`) rather than
throwing — the agent can fall back to manual editing in the graph window.
Verified on `com.unity.shadergraph` 17.3 (Unity 6000.3, URP).

## Tool group

All four tools belong to the `shadergraph` group (M20 Plan 7). Auto-activated
when `com.unity.shadergraph` is present; otherwise hidden from `ListTools`
until the session activates the group via `unity_open_mcp_manage_tools`.
Mutating members (`create` / `node_add` / `node_connect`) run the full gate
path with `paths_hint` scoped to the `.shadergraph` asset path; `open` writes
no asset (`Gate = Off`) and refuses `editor_window_unavailable` in batch mode
unless `window: none`.

## Relationship to the inspect surface

`unity_open_mcp_shader_get_data` / `unity_open_mcp_shader_list_all` read
**compiled shader properties** (the runtime-facing surface). This pack reads
and edits the **graph structure** (the authoring-facing surface). They are
complementary: use `shader_get_data` to inspect a shader's properties, and
`shader_graph_*` to author the graph that produces them.
