# ShaderGraph — embedded domain tools

Shader Graph typed tools (`unity_open_mcp_shader_graph_*`), embedded inside the
bridge. Four tools for in-editor shader-graph authoring: create a Shader Graph
asset, open it in the graph editor (with a structured node/edge summary), add
a node, and connect two node ports.

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

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
`AbstractMaterialNode`, `MaterialSlot`) is partially internal and the exact
surface shifts across `com.unity.shadergraph` versions. The mutating tools
(`create` / `node_add` / `node_connect`) wrap the public-ish surface behind a
single reflection helper, `ShaderGraphApi`, so a version change is fixed in one
place. When reflection cannot reach a needed member (version mismatch /
internal rename), the tool returns a structured `shadergraph_api_unavailable`
error envelope rather than throwing — the agent can fall back to manual editing
in the graph window. The read path (`open`) parses the serialized `.shadergraph`
JSON directly and is the most version-stable surface.

## Tool group

All four tools belong to the `shadergraph` group (M20 Plan 7). Auto-activated
when `com.unity.shadergraph` is present; otherwise hidden from `ListTools`
until the session activates the group via `unity_open_mcp_manage_tools`.
Mutating members (`create` / `node_add` / `node_connect`) run the full gate
path with `paths_hint` scoped to the `.shadergraph` asset path; `open` is a
non-mutating window bring-up (`Gate = Off`).

## Relationship to the inspect surface

`unity_open_mcp_shader_get_data` / `unity_open_mcp_shader_list_all` read
**compiled shader properties** (the runtime-facing surface). This pack reads
and edits the **graph structure** (the authoring-facing surface). They are
complementary: use `shader_get_data` to inspect a shader's properties, and
`shader_graph_*` to author the graph that produces them.
