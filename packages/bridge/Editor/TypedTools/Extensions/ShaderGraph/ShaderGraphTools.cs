// M20 Plan 7 / T20.7.1 — ShaderGraph embedded domain tools (compile-gated +
// auto-activating).
//
// Four typed tools for in-editor Shader Graph authoring:
//   - create: create a new Shader Graph asset from a template
//   - open: open a Shader Graph in the graph editor (returns node/edge summary)
//   - node_add: add a node to a graph
//   - node_connect: connect two node ports
//
// Shader Graph's editing API (UnityEditor.ShaderGraph: GraphData,
// AbstractMaterialNode, slots) is partially internal and varies across
// versions. The mutating tools wrap the public-ish surface behind the
// ShaderGraphApi reflection helper. When the API cannot be reached (version
// mismatch / internal rename) the tool returns a structured
// `shadergraph_api_unavailable` error rather than throwing — the agent can
// fall back to manual editing in the graph window.
//
// create is mutating (produces a .shadergraph asset — paths_hint includes the
// asset path). open opens an Editor window (non-mutating state change,
// Gate = Off). node_add / node_connect mutate the graph asset (paths_hint is
// the asset path, EditorSettle lifecycle). The gate + paths_hint contract on
// every mutating member is the documented advantage over the competitor's
// ungated graph edits.
//
// Compile-gate-only: when com.unity.shadergraph is absent the tools are not
// compiled in and the capability surface reports the domain as
// `available: false (dependency missing: com.unity.shadergraph)`. When the
// package IS present, the `shadergraph` group auto-activates for the session
// (no manual manage_tools call) — see T20.7.0.
//
// Naming: `unity_open_mcp_shader_graph_<action>` (snake_case domain prefix).
#if UNITY_OPEN_MCP_EXT_SHADERGRAPH
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.ShaderGraphExt
{
    [BridgeToolType]
    public static class ShaderGraphTools
    {
        // =====================================================================
        // create
        // =====================================================================

        // Create a new Shader Graph asset at the given asset path. The
        // template selects the graph kind (Unlit / Lit / Decal / Fullscreen;
        // blank when omitted). Implemented via reflection over
        // UnityEditor.ShaderGraph so it tracks the installed package version
        // without a hard compile-time dependency on a specific API shape.
        [BridgeTool("unity_open_mcp_shader_graph_create",
            Title = "ShaderGraph: Create",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "shadergraph")]
        [System.ComponentModel.Description(
            "Create a new Shader Graph asset at the given asset_path " +
            "('Assets/.../*.shadergraph'; the parent folder must exist). " +
            "shader_type selects the template: Unlit (default) / Lit / Decal " +
            "/ Fullscreen / Blank. Mutating: runs the gate path; paths_hint " +
            "includes the new asset path. Requires the com.unity.shadergraph " +
            "package installed in the project.")]
        public static string Create(
            string asset_path = null,
            string shader_type = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ShaderGraphJson.Error("paths_hint_required",
                    "shader_graph_create is mutating; pass a non-empty " +
                    "paths_hint that includes the new asset path.");

            if (string.IsNullOrEmpty(asset_path))
                return ShaderGraphJson.Error("missing_parameter",
                    "'asset_path' is required (an 'Assets/.../*.shadergraph' path).");

            if (!asset_path.EndsWith(".shadergraph"))
                return ShaderGraphJson.Error("invalid_parameter",
                    "'asset_path' must end with '.shadergraph'.");

            if (AssetDatabase.LoadAssetAtPath<Object>(asset_path) != null)
                return ShaderGraphJson.Error("already_exists",
                    $"An asset already exists at '{asset_path}'. Use a " +
                    "different path or open the existing graph.");

            var shaderType = string.IsNullOrEmpty(shader_type) ? "Unlit" : shader_type;
            if (!ShaderGraphApi.TryCreateShaderGraph(asset_path, shaderType,
                    out var createError, out var createdPath))
            {
                return ShaderGraphJson.Error(createError ?? "shadergraph_api_unavailable",
                    "Could not create the Shader Graph via the installed " +
                    "com.unity.shadergraph API. The package version may expose " +
                    "a different creation surface; open the graph manually via " +
                    "Assets/Create > Shader Graph, then use shader_graph_open " +
                    "to inspect it.");
            }

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(
                AssetDatabase.LoadAssetAtPath<Object>(createdPath ?? asset_path));

            var sb = new StringBuilder(160);
            sb.Append("\"shaderGraph\":{");
            sb.Append("\"assetPath\":").Append(ShaderGraphJson.Esc(createdPath ?? asset_path)).Append(',');
            sb.Append("\"shaderType\":").Append(ShaderGraphJson.Esc(shaderType)).Append(',');
            sb.Append("\"created\":true");
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }

        // =====================================================================
        // open (read-only window bring-up + structured summary)
        // =====================================================================

        // Open a Shader Graph in the graph editor window and return a
        // structured node/edge summary. Non-mutating state change (opens a
        // window) — Gate = Off. The summary lets an agent inspect the graph
        // structure without a separate tool.
        [BridgeTool("unity_open_mcp_shader_graph_open",
            Title = "ShaderGraph: Open",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "shadergraph")]
        [System.ComponentModel.Description(
            "Open a Shader Graph asset in the graph editor window and return a " +
            "structured summary (node count, node ids/types/positions, edge " +
            "count, edges). Read-only window bring-up — Gate is Off. Address " +
            "the graph by asset_path ('Assets/.../*.shadergraph'). The summary " +
            "is parsed from the asset; use it to learn node ids / slot ids " +
            "before node_add / node_connect. Requires com.unity.shadergraph.")]
        public static string Open(string asset_path = null)
        {
            if (string.IsNullOrEmpty(asset_path))
                return ShaderGraphJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.shadergraph').");

            var asset = AssetDatabase.LoadAssetAtPath<Object>(asset_path);
            if (asset == null)
                return ShaderGraphJson.Error("asset_not_found",
                    $"No Shader Graph asset found at '{asset_path}'.");

            // Bring up the Shader Graph editor window for the asset. Reflection
            // over UnityEditor.ShaderGraph (the window type name has shifted
            // across versions). Failure to open the window is non-fatal — the
            // summary is still returned.
            ShaderGraphApi.TryOpenInEditor(asset_path);

            // Structured summary parsed from the serialized graph JSON.
            ShaderGraphApi.BuildGraphSummary(asset_path,
                out int nodeCount, out int edgeCount,
                out string nodesJson, out string edgesJson,
                out string parseError);

            var sb = new StringBuilder(512);
            sb.Append("\"shaderGraph\":{");
            sb.Append("\"assetPath\":").Append(ShaderGraphJson.Esc(asset_path)).Append(',');
            sb.Append("\"nodeCount\":").Append(nodeCount).Append(',');
            sb.Append("\"edgeCount\":").Append(edgeCount).Append(',');
            if (parseError != null)
                sb.Append("\"parseWarning\":").Append(ShaderGraphJson.Esc(parseError)).Append(',');
            sb.Append("\"nodes\":").Append(nodesJson ?? "[]").Append(',');
            sb.Append("\"edges\":").Append(edgesJson ?? "[]");
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }

        // =====================================================================
        // node_add
        // =====================================================================

        // Add a node to a Shader Graph. node_type is a friendly name
        // (UV / Multiply / Sample Texture 2D / ...) or a full class name in
        // UnityEditor.ShaderGraph. position is "x,y". properties_json is an
        // optional JSON object of initial { field: value } patches applied to
        // the node after creation. Mutating: paths_hint is the asset path.
        [BridgeTool("unity_open_mcp_shader_graph_node_add",
            Title = "ShaderGraph: Add Node",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "shadergraph")]
        [System.ComponentModel.Description(
            "Add a node to a Shader Graph. node_type is a friendly name (UV, " +
            "Multiply, Sample Texture 2D, Add, Time, Color, Vector3, ...) or a " +
            "full class name in UnityEditor.ShaderGraph. position is 'x,y'. " +
            "properties_json is an optional JSON object of initial " +
            "{ field: value } patches applied after creation. Returns the new " +
            "node's id + its input/output slot ids. Mutating: runs the gate " +
            "path; paths_hint is the graph asset path. Requires " +
            "com.unity.shadergraph.")]
        public static string NodeAdd(
            string asset_path = null,
            string node_type = null,
            string position = null,
            string properties_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ShaderGraphJson.Error("paths_hint_required",
                    "shader_graph_node_add is mutating; pass a non-empty " +
                    "paths_hint (the graph asset path).");

            if (string.IsNullOrEmpty(asset_path))
                return ShaderGraphJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.shadergraph').");

            if (string.IsNullOrEmpty(node_type))
                return ShaderGraphJson.Error("missing_parameter",
                    "'node_type' is required (e.g. 'UV', 'Multiply', " +
                    "'Sample Texture 2D', or a full UnityEditor.ShaderGraph " +
                    "class name).");

            if (!ShaderGraphApi.TryAddNode(asset_path, node_type,
                    position, properties_json,
                    out var addError, out var nodeId, out var slotsJson))
            {
                return ShaderGraphJson.Error(addError ?? "shadergraph_api_unavailable",
                    $"Could not add node '{node_type}' to '{asset_path}'. The " +
                    "installed com.unity.shadergraph API may differ; open the " +
                    "graph in the editor and add the node manually, then use " +
                    "shader_graph_open to read the result.");
            }

            var sb = new StringBuilder(200);
            sb.Append("\"node\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"nodeId\":").Append(ShaderGraphJson.Esc(nodeId ?? "")).Append(',');
            sb.Append("\"nodeType\":").Append(ShaderGraphJson.Esc(node_type)).Append(',');
            sb.Append("\"slots\":").Append(slotsJson ?? "[]");
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }

        // =====================================================================
        // node_connect
        // =====================================================================

        // Connect an output slot of one node to an input slot of another.
        // source_node_id / destination_node_id come from shader_graph_open or
        // a prior node_add. Slot ids are integers. Mutating: paths_hint is the
        // asset path.
        [BridgeTool("unity_open_mcp_shader_graph_node_connect",
            Title = "ShaderGraph: Connect Nodes",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "shadergraph")]
        [System.ComponentModel.Description(
            "Connect an output slot of one node to an input slot of another in " +
            "a Shader Graph. source_node_id + source_slot identify the output; " +
            "destination_node_id + destination_slot identify the input. Node ids " +
            "come from shader_graph_open or a prior node_add; slot ids are " +
            "integers. Mutating: runs the gate path; paths_hint is the graph " +
            "asset path. Requires com.unity.shadergraph.")]
        public static string NodeConnect(
            string asset_path = null,
            string source_node_id = null,
            int source_slot = 0,
            string destination_node_id = null,
            int destination_slot = 0,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ShaderGraphJson.Error("paths_hint_required",
                    "shader_graph_node_connect is mutating; pass a non-empty " +
                    "paths_hint (the graph asset path).");

            if (string.IsNullOrEmpty(asset_path))
                return ShaderGraphJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.shadergraph').");

            if (string.IsNullOrEmpty(source_node_id) || string.IsNullOrEmpty(destination_node_id))
                return ShaderGraphJson.Error("missing_parameter",
                    "'source_node_id' and 'destination_node_id' are required.");

            if (!ShaderGraphApi.TryConnectNodes(asset_path,
                    source_node_id, source_slot,
                    destination_node_id, destination_slot,
                    out var connectError))
            {
                return ShaderGraphJson.Error(connectError ?? "shadergraph_api_unavailable",
                    "Could not connect the nodes. Verify the node ids + slot " +
                    "ids with shader_graph_open; the installed " +
                    "com.unity.shadergraph API may differ.");
            }

            var sb = new StringBuilder(180);
            sb.Append("\"edge\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"sourceNodeId\":").Append(ShaderGraphJson.Esc(source_node_id)).Append(',');
            sb.Append("\"sourceSlot\":").Append(source_slot).Append(',');
            sb.Append("\"destinationNodeId\":").Append(ShaderGraphJson.Esc(destination_node_id)).Append(',');
            sb.Append("\"destinationSlot\":").Append(destination_slot);
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }
    }
}
#endif
