// M20 Plan 7 / T20.7.1 — ShaderGraph embedded domain tools (compile-gated +
// auto-activating).
//
// Four typed tools for in-editor Shader Graph authoring:
//   - create: create a new Shader Graph asset from a template
//   - open: open a Shader Graph in its graph window (returns node/edge summary)
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
// asset path). open opens (or closes) the graph's Editor window and writes
// no asset (Gate = Off). node_add / node_connect mutate the graph asset (paths_hint is
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
        // template selects the graph kind (Unlit when omitted / Lit / Decal /
        // Fullscreen / Blank). Implemented via reflection over
        // UnityEditor.ShaderGraph (see ShaderGraphApi) without the editor's
        // interactive Assets/Create flow.
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
            "/ Fullscreen / Blank, using the active render pipeline's target. " +
            "Mutating: runs the gate path; paths_hint " +
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
                    out var createError, out var createdPath, out var targetName))
            {
                if (createError == "unknown_shader_type")
                    return ShaderGraphJson.Error(createError,
                        $"Unknown shader_type '{shader_type}'. Use Unlit, Lit, Decal, Fullscreen or Blank.");
                if (createError == "shader_type_unsupported")
                    return ShaderGraphJson.Error(createError,
                        $"No Shader Graph target in the active render pipeline supports '{shaderType}'. " +
                        "Use shader_type Blank (no target) or a template the pipeline provides.");
                return ShaderGraphJson.Error(createError ?? "shadergraph_api_unavailable",
                    "Could not create the Shader Graph via the installed " +
                    "com.unity.shadergraph API. Create it manually via " +
                    "Assets/Create > Shader Graph, then use shader_graph_open " +
                    "to inspect it.");
            }

            var sb = new StringBuilder(160);
            sb.Append("\"shaderGraph\":{");
            sb.Append("\"assetPath\":").Append(ShaderGraphJson.Esc(createdPath ?? asset_path)).Append(',');
            sb.Append("\"shaderType\":").Append(ShaderGraphJson.Esc(shaderType)).Append(',');
            sb.Append("\"target\":").Append(targetName == null ? "null" : ShaderGraphJson.Esc(targetName)).Append(',');
            sb.Append("\"created\":true");
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }

        // =====================================================================
        // open (graph window + structured summary)
        // =====================================================================

        // Open a Shader Graph in its graph editor window (or focus the window
        // already showing it) and return a structured node/edge summary.
        // window: "none" returns the summary only; "close" closes the graph's
        // window. Writes no asset — Gate = Off.
        [BridgeTool("unity_open_mcp_shader_graph_open",
            Title = "ShaderGraph: Open",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "shadergraph")]
        [System.ComponentModel.Description(
            "Open a Shader Graph asset in its Shader Graph editor window (or " +
            "focus the window already showing it) and return a structured " +
            "summary (node count, node ids/types/positions/slots, edge count, " +
            "edges) plus window {action, open, title}. window: open (default) | " +
            "none (summary only, no window) | close (close the graph's window). " +
            "Writes no asset — Gate is Off. Refuses editor_window_unavailable " +
            "in batch mode (use window none) and graph_has_unsaved_changes when " +
            "closing a window with unsaved edits. Address the graph by " +
            "asset_path ('Assets/.../*.shadergraph'). Requires " +
            "com.unity.shadergraph.")]
        public static string Open(string asset_path = null, string window = "open")
        {
            if (string.IsNullOrEmpty(asset_path))
                return ShaderGraphJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.shadergraph').");

            var mode = string.IsNullOrEmpty(window) ? "open" : window.Trim().ToLowerInvariant();
            if (mode != "open" && mode != "none" && mode != "close")
                return ShaderGraphJson.Error("invalid_parameter",
                    $"'window' must be open, none or close — got '{window}'.");

            // A graph or a sub graph: OpenAsset on any other asset would open
            // whatever the project opens it with (a code editor for a .shader).
            var asset = AssetDatabase.LoadAssetAtPath<Object>(asset_path);
            if (asset == null ||
                !(asset_path.EndsWith(".shadergraph", System.StringComparison.OrdinalIgnoreCase) ||
                  asset_path.EndsWith(".shadersubgraph", System.StringComparison.OrdinalIgnoreCase)))
                return ShaderGraphJson.Error("asset_not_found",
                    $"No Shader Graph asset found at '{asset_path}'.");

            string windowJson;
            if (mode == "open")
            {
                if (!ShaderGraphApi.TryOpenWindow(asset_path, out var reused, out var opened,
                        out var openError, out var openMessage))
                {
                    var hint = openError == "editor_window_unavailable"
                        ? " Pass window: \"none\" for the summary without a window."
                        : "";
                    return ShaderGraphJson.Error(openError,
                        $"Could not open '{asset_path}' in a Shader Graph window: {openMessage}{hint}");
                }
                windowJson = "{\"action\":" + ShaderGraphJson.Esc(reused ? "focused" : "opened") +
                             ",\"open\":true,\"title\":" + ShaderGraphJson.Esc(opened.titleContent?.text ?? "") + "}";
            }
            else if (mode == "close")
            {
                if (!ShaderGraphApi.TryCloseWindows(asset_path, out var closed, out var closeError))
                    return ShaderGraphJson.Error(closeError,
                        $"The Shader Graph window of '{asset_path}' has unsaved changes; closing it would " +
                        "ask to save them. Save or discard them in the window first.");
                windowJson = "{\"action\":" + ShaderGraphJson.Esc(closed > 0 ? "closed" : "not_open") +
                             ",\"open\":false,\"closed\":" + closed + "}";
            }
            else
            {
                windowJson = "{\"action\":\"none\",\"open\":" +
                             (ShaderGraphApi.OpenWindows(asset_path).Count > 0 ? "true" : "false") + "}";
            }

            // Structured summary of the graph as the importer loads it.
            ShaderGraphApi.BuildGraphSummary(asset_path,
                out int nodeCount, out int edgeCount,
                out string nodesJson, out string edgesJson,
                out string parseError);

            var sb = new StringBuilder(512);
            sb.Append("\"shaderGraph\":{");
            sb.Append("\"assetPath\":").Append(ShaderGraphJson.Esc(asset_path)).Append(',');
            sb.Append("\"window\":").Append(windowJson).Append(',');
            sb.Append("\"nodeCount\":").Append(nodeCount).Append(',');
            sb.Append("\"edgeCount\":").Append(edgeCount).Append(',');
            if (parseError != null)
                sb.Append("\"parseWarning\":").Append(ShaderGraphJson.Esc(parseError)).Append(',');
            sb.Append("\"nodes\":").Append(nodesJson ?? "[]").Append(',');
            sb.Append("\"edges\":").Append(edgesJson ?? "[]");
            sb.Append('}');
            return ShaderGraphJson.Ok(sb.ToString());
        }

        // An edit would write the file under a graph window holding unsaved
        // edits: they would be lost (or the window would ask about them).
        private static string UnsavedWindowRefusal(string assetPath)
            => ShaderGraphJson.Error("graph_has_unsaved_changes",
                $"'{assetPath}' is open in a Shader Graph window with unsaved changes. Save or " +
                "discard them in the window, then retry; an open window without unsaved changes " +
                "reloads after the edit.");

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
            "{ property: value } patches (public node properties of a simple " +
            "type) applied after creation; unmatched keys come back in " +
            "ignoredProperties. Returns the new node's id + its slots. Mutating: runs the gate " +
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
                    out var addError, out var nodeId, out var slotsJson, out var ignoredProperties))
            {
                if (addError == "asset_not_found")
                    return ShaderGraphJson.Error(addError,
                        $"No Shader Graph asset found at '{asset_path}'.");
                if (addError == "graph_has_unsaved_changes")
                    return UnsavedWindowRefusal(asset_path);
                if (addError == "unknown_node_type")
                    return ShaderGraphJson.Error(addError,
                        $"No Shader Graph node type matches '{node_type}'. Use a node " +
                        "name as shown in the graph (UV, Multiply, Color, Sample " +
                        "Texture 2D, ...) or a full UnityEditor.ShaderGraph class name.");
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
            if (ignoredProperties != null && ignoredProperties.Count > 0)
            {
                sb.Append(",\"ignoredProperties\":[");
                for (int i = 0; i < ignoredProperties.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ShaderGraphJson.Esc(ignoredProperties[i]));
                }
                sb.Append(']');
            }
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
                if (connectError == "asset_not_found")
                    return ShaderGraphJson.Error(connectError,
                        $"No Shader Graph asset found at '{asset_path}'.");
                if (connectError == "graph_has_unsaved_changes")
                    return UnsavedWindowRefusal(asset_path);
                if (connectError == "node_not_found" || connectError == "slot_not_found")
                    return ShaderGraphJson.Error(connectError,
                        "A node id or slot id does not exist in the graph. Read the " +
                        "ids from shader_graph_node_add (nodeId, slots[].id) or " +
                        "shader_graph_open.");
                if (connectError == "connection_rejected")
                    return ShaderGraphJson.Error(connectError,
                        "The graph refused the connection: connect an output slot " +
                        "to an input slot, and do not create a cycle.");
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
