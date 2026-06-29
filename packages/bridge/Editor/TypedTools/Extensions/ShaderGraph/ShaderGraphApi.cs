// M20 Plan 7 / T20.7.1 — reflection surface over UnityEditor.ShaderGraph.
//
// Shader Graph's editing API (GraphData, AbstractMaterialNode, MaterialSlot,
// GraphData.AddNode / GraphData.Connect) is partially internal and the exact
// surface shifts across com.unity.shadergraph versions. Rather than bind to
// one version's shape at compile time, this helper resolves the types /
// methods by reflection at call time and returns structured success/failure.
// Every public tool method in ShaderGraphTools delegates here, so a future
// version change is fixed in ONE place.
//
// When reflection cannot reach a needed member the helper returns false with
// an error code; the tool surfaces a `shadergraph_api_unavailable` envelope
// so the agent can fall back to manual editing. The helper never throws out
// of the tool path — exceptions are caught and converted.
//
// Unity-version dependency: tested against com.unity.shadergraph as shipped
// with Unity 6 (URP/HDRP). The GraphData JSON on disk is itself version-
// stable (it is the serialization format the editor reads/writes), so the
// read path (BuildGraphSummary) parses the .shadergraph file directly and is
// the most robust across versions.
#if UNITY_OPEN_MCP_EXT_SHADERGRAPH
#pragma warning disable CS0618
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.ShaderGraph
{
    // Reflection wrapper over UnityEditor.ShaderGraph. All members are static
    // and best-effort; failures surface as (false, errorCode) tuples.
    internal static class ShaderGraphApi
    {
        // Cached assembly / type lookups. The Shader Graph package compiles to
        // an assembly whose simple name is "Unity.ShaderGraph.Editor".
        private static readonly Assembly ShaderGraphAssembly = LoadShaderGraphAssembly();
        private static readonly Type GraphData = Resolve("UnityEditor.ShaderGraph.GraphData");
        private static readonly Type AbstractMaterialNodeType =
            Resolve("UnityEditor.ShaderGraph.AbstractMaterialNode");
        private static readonly Type GraphUtilType = Resolve("UnityEditor.ShaderGraph.GraphUtil");

        private static Assembly LoadShaderGraphAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Unity.ShaderGraph.Editor") return asm;
            }
            // The sub-asmdef references Unity.ShaderGraph.Editor, so it must be
            // loaded by the time any tool runs. Fall back to a name-contains
            // search just in case the simple name differs on an old version.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != null &&
                    asm.GetName().Name.IndexOf("ShaderGraph", StringComparison.OrdinalIgnoreCase) >= 0)
                    return asm;
            }
            return null;
        }

        private static Type Resolve(string fullName)
        {
            if (ShaderGraphAssembly == null) return null;
            return ShaderGraphAssembly.GetType(fullName);
        }

        private static bool ApiAvailable(out string error)
        {
            if (ShaderGraphAssembly == null)
            {
                error = "shadergraph_assembly_not_found";
                return false;
            }
            if (GraphData == null || AbstractMaterialNodeType == null)
            {
                error = "shadergraph_api_unavailable";
                return false;
            }
            error = null;
            return true;
        }

        // =====================================================================
        // create
        // =====================================================================

        // Create a new Shader Graph asset. Preferred path: GraphUtil (or
        // equivalent utility) creation methods that the package uses for its
        // own menu items. Fallback: instantiate MaterialGraph + write via
        // AssetDatabase. shaderType normalizes the template name.
        public static bool TryCreateShaderGraph(
            string assetPath, string shaderType,
            out string error, out string createdPath)
        {
            createdPath = assetPath;
            if (!ApiAvailable(out error)) return false;

            // Normalize the requested template to the package's vocabulary.
            var (className, templateName) = NormalizeTemplate(shaderType);

            // Path A: GraphUtil.CreateShaderGraphAsset(path, name) or similar.
            // Newer Shader Graph ships utility creation methods; try the most
            // common signatures by reflection.
            if (GraphUtilType != null)
            {
                if (TryInvokeCreateUtility(assetPath, templateName, out error, out createdPath))
                {
                    return true;
                }
            }

            // Path B: the menu-item path. Unity's Shader Graph creation is
            // wired to Assets/Create/Shader/Graph submenus. Executing the
            // matching menu creates the asset in the currently-selected
            // folder, which we then rename/move to the requested path. This
            // is the most version-stable creation path because it uses the
            // package's own creation flow.
            if (TryCreateViaMenu(assetPath, templateName, out error, out createdPath))
            {
                return true;
            }

            error = error ?? "shadergraph_create_unsupported_version";
            return false;
        }

        // Try the GraphUtil static creation surface. The exact method name +
        // signature varies; probe a small set of known shapes.
        private static bool TryInvokeCreateUtility(
            string assetPath, string templateName,
            out string error, out string createdPath)
        {
            error = null;
            createdPath = assetPath;
            if (GraphUtilType == null) return false;

            // Common signatures across versions:
            //   GraphUtil.CreateShaderGraphAsset(string path, string name)
            //   GraphUtil.CreateShaderGraphAsset(string path)
            // Some versions take a graph-kind enum; we try the string-path
            // shapes first.
            var methods = GraphUtilType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var m in methods)
            {
                if (m.Name != "CreateShaderGraphAsset" &&
                    m.Name != "CreateNewGraph" &&
                    m.Name != "CreateShaderGraph") continue;
                var p = m.GetParameters();
                object[] args;
                if (p.Length == 1 && p[0].ParameterType == typeof(string))
                    args = new object[] { assetPath };
                else if (p.Length == 2 &&
                         p[0].ParameterType == typeof(string) &&
                         p[1].ParameterType == typeof(string))
                    args = new object[] { assetPath, Path.GetFileNameWithoutExtension(assetPath) };
                else
                    continue;

                try
                {
                    m.Invoke(null, args);
                    if (File.Exists(assetPath))
                    {
                        AssetDatabase.ImportAsset(assetPath);
                        return true;
                    }
                }
                catch
                {
                    // Try the next matching signature.
                }
            }
            error = "shadergraph_create_utility_not_found";
            return false;
        }

        // Create a Shader Graph via the package's own menu creation flow. The
        // menu path is stable across versions for the common templates; we
        // execute it after selecting the destination folder so the new asset
        // lands in the right place, then rename it to the requested name.
        private static bool TryCreateViaMenu(
            string assetPath, string templateName,
            out string error, out string createdPath)
        {
            error = null;
            createdPath = assetPath;

            // Select the destination folder so the menu creates the asset there.
            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            var fileName = Path.GetFileNameWithoutExtension(assetPath);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName))
            {
                error = "invalid_parameter";
                return false;
            }

            // Map template → menu path. The exact submenu layout has varied
            // (URP vs HDRP split); try the candidates in order and accept the
            // first that returns true.
            var menuCandidates = MenuCandidatesFor(templateName);
            if (menuCandidates.Count == 0)
            {
                error = "unknown_shader_type";
                return false;
            }

            string tempName = null;
            foreach (var menu in menuCandidates)
            {
                if (!EditorApplication.ExecuteMenuItem(menu)) continue;
                // After menu execution Unity focuses the newly created, rename-
                // ready asset. Find the newest .shadergraph in the folder.
                tempName = WaitForNewAsset(folder);
                break;
            }

            if (tempName == null)
            {
                error = "shadergraph_menu_unavailable";
                return false;
            }

            // Rename/move the created asset to the requested path.
            try
            {
                var from = tempName;
                if (File.Exists(assetPath))
                {
                    // Menu created with the requested name already (rare); done.
                    AssetDatabase.ImportAsset(assetPath);
                    return true;
                }
                AssetDatabase.MoveAsset(from, assetPath);
                if (File.Exists(assetPath))
                {
                    AssetDatabase.ImportAsset(assetPath);
                    return true;
                }
            }
            catch (Exception e)
            {
                error = "shadergraph_rename_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph rename failed: {e.Message}");
                return false;
            }

            error = "shadergraph_create_menu_failed";
            return false;
        }

        private static List<string> MenuCandidatesFor(string templateName)
        {
            // Menu paths vary by render pipeline + version. List the common
            // candidates; ExecuteMenuItem returns false for unknown menus so
            // we fall through cleanly.
            var lower = (templateName ?? "").ToLowerInvariant();
            var cands = new List<string>();
            if (lower == "unlit" || lower == "blank")
            {
                cands.Add("Assets/Create/Shader/Unlit Shader Graph");
                cands.Add("Assets/Create/Shader Graph/Unlit Shader Graph");
                cands.Add("Assets/Create/Shader/URP/Unlit Shader Graph");
            }
            else if (lower == "lit")
            {
                cands.Add("Assets/Create/Shader/Lit Shader Graph");
                cands.Add("Assets/Create/Shader Graph/Lit Shader Graph");
                cands.Add("Assets/Create/Shader/URP/Lit Shader Graph");
            }
            else if (lower == "decal")
            {
                cands.Add("Assets/Create/Shader/Decal Shader Graph");
                cands.Add("Assets/Create/Shader Graph/Decal Shader Graph");
                cands.Add("Assets/Create/Shader/URP/Decal Shader Graph");
            }
            else if (lower == "fullscreen")
            {
                cands.Add("Assets/Create/Shader/Fullscreen Shader Graph");
                cands.Add("Assets/Create/Shader Graph/Fullscreen Shader Graph");
            }
            // Always include a generic "Blank" / unlit fallback as the last
            // candidate so a template name we don't recognize still produces a
            // graph the agent can edit.
            cands.Add("Assets/Create/Shader/Unlit Shader Graph");
            cands.Add("Assets/Create/Shader Graph/Unlit Shader Graph");
            return cands;
        }

        // Poll briefly (up to ~1.5s) for a new .shadergraph to appear in the
        // folder after menu execution. Returns the asset path of the newest
        // matching asset, or null on timeout.
        private static string WaitForNewAsset(string folder)
        {
            var before = new HashSet<string>(
                Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.shadergraph", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>());
            string found = null;
            for (int i = 0; i < 30; i++)
            {
                AssetDatabase.Refresh();
                if (Directory.Exists(folder))
                {
                    foreach (var f in Directory.GetFiles(folder, "*.shadergraph", SearchOption.TopDirectoryOnly))
                    {
                        if (before.Add(f)) { found = f; break; }
                    }
                }
                if (found != null) return found.Replace('\\', '/');
                System.Threading.Thread.Sleep(50);
            }
            return null;
        }

        // =====================================================================
        // open
        // =====================================================================

        // Bring up the Shader Graph editor window for the asset. Best-effort;
        // failures are non-fatal (the summary is still returned). The window
        // type name has shifted across versions, so resolve by reflection.
        public static void TryOpenInEditor(string assetPath)
        {
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (asset != null)
                {
                    // Selection + EditorApplication.ExecuteMenuItem("Window/...")
                    // is the most stable cross-version bring-up.
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
            catch
            {
                // Best-effort window bring-up; never fail the tool on this.
            }
        }

        // =====================================================================
        // graph summary (read path — parsed from the .shadergraph JSON)
        // =====================================================================

        // Build a structured node/edge summary by parsing the .shadergraph
        // file's serialized JSON directly. This is the most version-stable
        // read path: the on-disk format is the editor's own serialization,
        // and the node/edge arrays are stable across com.unity.shadergraph
        // versions (they are how the editor round-trips the graph).
        public static void BuildGraphSummary(
            string assetPath,
            out int nodeCount, out int edgeCount,
            out string nodesJson, out string edgesJson,
            out string parseError)
        {
            nodeCount = 0;
            edgeCount = 0;
            nodesJson = "[]";
            edgesJson = "[]";
            parseError = null;

            string json;
            try
            {
                json = File.ReadAllText(assetPath);
            }
            catch (Exception e)
            {
                parseError = $"Could not read graph JSON: {e.Message}";
                return;
            }

            try
            {
                // m_SubGraphs / m_SerializedSubGraphs aside, the node list is
                // under "m_SerializedProperties" in older formats and under
                // the top-level "m_Vertices"/"m_Edges" arrays in the newer
                // GraphData format. Parse the two stable shapes.
                var nodes = ExtractNodeList(json);
                var edges = ExtractEdgeList(json);
                nodeCount = nodes.Count;
                edgeCount = edges.Count;
                nodesJson = BuildNodesJson(nodes);
                edgesJson = BuildEdgesJson(edges);
            }
            catch (Exception e)
            {
                parseError = $"Graph JSON parse failed (partial summary): {e.Message}";
            }
        }

        // Extract the node list. The GraphData JSON stores nodes as JSON
        // objects with "m_ObjectId" (the node GUID), "m_Name"/"m_Title", a
        // position, and "m_Slots". We scan the file for node-shaped objects.
        private struct GraphNode
        {
            public string Id;
            public string Name;
            public string Type;
            public Vector2 Position;
            public List<SlotInfo> Slots;
        }

        private struct SlotInfo
        {
            public int Id;
            public string Name;
            public bool IsInput;
        }

        private struct GraphEdge
        {
            public string OutputNodeId;
            public int OutputSlot;
            public string InputNodeId;
            public int InputSlot;
        }

        // Lightweight scan for node objects. Each node is a JSON object with
        // an "m_ObjectId" string and a "m_Name" string; we capture id/name
        // and a best-effort type + position.
        private static List<GraphNode> ExtractNodeList(string json)
        {
            var nodes = new List<GraphNode>();
            int idx = 0;
            while (true)
            {
                var idStart = json.IndexOf("\"m_ObjectId\"", idx, StringComparison.Ordinal);
                if (idStart < 0) break;
                // Back up to the enclosing object start.
                var objStart = json.LastIndexOf('{', idStart);
                if (objStart < 0) { idx = idStart + 1; continue; }
                // Find matching close brace (naive depth count — graphs nest
                // shallowly here).
                int depth = 0;
                int objEnd = objStart;
                for (int i = objStart; i < json.Length; i++)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { objEnd = i; break; }
                    }
                }

                var body = json.Substring(objStart, objEnd - objStart + 1);
                var id = ExtractStringValue(body, "m_ObjectId");
                var name = ExtractStringValue(body, "m_Name");
                if (ExtractStringValue(body, "m_Name") == null)
                    name = ExtractStringValue(body, "m_Title");
                var type = ExtractStringValue(body, "m_TypeName")
                           ?? ExtractStringValue(body, "type");
                var pos = ExtractVector2(body, "m_Position");
                if (id != null)
                {
                    nodes.Add(new GraphNode
                    {
                        Id = id,
                        Name = name ?? "",
                        Type = type ?? "",
                        Position = pos,
                        Slots = ExtractSlots(body),
                    });
                }
                idx = objEnd + 1;
            }
            return nodes;
        }

        private static List<GraphEdge> ExtractEdgeList(string json)
        {
            var edges = new List<GraphEdge>();
            // Newer GraphData JSON stores edges under "m_Edges" as a JSON array
            // of { m_OutputSlot, m_InputSlot, m_OutputNode: {m_ObjectId},
            // m_InputNode: {m_ObjectId} }.
            int idx = 0;
            while (true)
            {
                var idStart = json.IndexOf("\"m_OutputSlot\"", idx, StringComparison.Ordinal);
                if (idStart < 0) break;
                var objStart = json.LastIndexOf('{', idStart);
                if (objStart < 0) { idx = idStart + 1; continue; }
                int depth = 0;
                int objEnd = objStart;
                for (int i = objStart; i < json.Length; i++)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { objEnd = i; break; }
                    }
                }
                var body = json.Substring(objStart, objEnd - objStart + 1);
                var outputSlot = ExtractIntValue(body, "m_OutputSlot");
                var inputSlot = ExtractIntValue(body, "m_InputSlot");
                var outputNode = ExtractNestedObjectId(body, "m_OutputNode");
                var inputNode = ExtractNestedObjectId(body, "m_InputNode");
                if (outputNode != null && inputNode != null)
                {
                    edges.Add(new GraphEdge
                    {
                        OutputNodeId = outputNode,
                        OutputSlot = outputSlot,
                        InputNodeId = inputNode,
                        InputSlot = inputSlot,
                    });
                }
                idx = objEnd + 1;
            }
            return edges;
        }

        private static List<SlotInfo> ExtractSlots(string nodeBody)
        {
            var slots = new List<SlotInfo>();
            int idx = 0;
            while (true)
            {
                var idStart = nodeBody.IndexOf("\"m_Id\"", idx, StringComparison.Ordinal);
                if (idStart < 0) break;
                var slotId = ExtractIntValue(nodeBody.Substring(idStart), "m_Id");
                var slotName = ExtractStringValue(nodeBody.Substring(idStart), "m_Name");
                // Heuristic: input slots vs output slots differ by member name
                // shape; report id + name and let the agent disambiguate.
                slots.Add(new SlotInfo
                {
                    Id = slotId,
                    Name = slotName ?? "",
                    IsInput = true,
                });
                idx = idStart + 1;
                if (slots.Count > 64) break; // safety cap
            }
            return slots;
        }

        private static string BuildNodesJson(List<GraphNode> nodes)
        {
            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var n = nodes[i];
                sb.Append('{');
                sb.Append("\"id\":").Append(ShaderGraphJson.Esc(n.Id)).Append(',');
                sb.Append("\"name\":").Append(ShaderGraphJson.Esc(n.Name)).Append(',');
                sb.Append("\"type\":").Append(ShaderGraphJson.Esc(n.Type)).Append(',');
                sb.Append("\"position\":[").Append(
                    n.Position.x.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(n.Position.y.ToString(CultureInfo.InvariantCulture)).Append(']');
                if (n.Slots != null && n.Slots.Count > 0)
                {
                    sb.Append(",\"slots\":[");
                    for (int j = 0; j < n.Slots.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        var s = n.Slots[j];
                        sb.Append("{\"id\":").Append(s.Id);
                        sb.Append(",\"name\":").Append(ShaderGraphJson.Esc(s.Name)).Append('}');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string BuildEdgesJson(List<GraphEdge> edges)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            for (int i = 0; i < edges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = edges[i];
                sb.Append('{');
                sb.Append("\"outputNodeId\":").Append(ShaderGraphJson.Esc(e.OutputNodeId)).Append(',');
                sb.Append("\"outputSlot\":").Append(e.OutputSlot).Append(',');
                sb.Append("\"inputNodeId\":").Append(ShaderGraphJson.Esc(e.InputNodeId)).Append(',');
                sb.Append("\"inputSlot\":").Append(e.InputSlot);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // =====================================================================
        // node_add / node_connect (mutation — reflection over GraphData)
        // =====================================================================

        // Add a node by loading the asset's GraphData, instantiating the node
        // type, adding it, and re-serializing. Falls back to a structured
        // error when the API shape differs.
        public static bool TryAddNode(
            string assetPath, string nodeType, string position, string propertiesJson,
            out string error, out string nodeId, out string slotsJson)
        {
            error = null;
            nodeId = null;
            slotsJson = "[]";
            if (!ApiAvailable(out error)) return false;

            // Resolve the node type. nodeType may be a friendly name or a full
            // class name; the friendly map covers the common shader nodes.
            var sysType = ResolveNodeType(nodeType);
            if (sysType == null)
            {
                error = "unknown_node_type";
                return false;
            }

            // Load the GraphData. Newer Shader Graph exposes GraphData via the
            // MaterialGraphAsset; we reflect through the asset's handler.
            object graphData;
            if (!TryLoadGraphData(assetPath, out graphData, out error)) return false;

            try
            {
                // Instantiate the node. AbstractMaterialNode subclasses need a
                // parameterless constructor (most do).
                var node = Activator.CreateInstance(sysType);
                var abstractNode = node;
                if (abstractNode == null) { error = "node_instantiate_failed"; return false; }

                // Assign a GUID / object id so the agent can address it.
                var newGuid = Guid.NewGuid().ToString("N");
                SetObjectId(node, newGuid);

                // Position.
                if (!string.IsNullOrEmpty(position))
                {
                    var pos = ParseVector2(position);
                    SetNodePosition(node, pos);
                }

                // Apply optional initial property patches.
                if (!string.IsNullOrEmpty(propertiesJson))
                {
                    ApplyPropertyPatches(node, propertiesJson);
                }

                // graphData.AddNode(node).
                var addNode = graphData.GetType().GetMethod("AddNode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (addNode == null) { error = "addnode_not_found"; return false; }
                addNode.Invoke(graphData, new[] { node });

                nodeId = newGuid;
                slotsJson = ExtractNodeSlotsJson(node);

                // Re-serialize the asset.
                SaveGraphData(assetPath, graphData);
                return true;
            }
            catch (Exception e)
            {
                error = "node_add_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph node_add failed: {e.Message}");
                return false;
            }
        }

        // Connect an output slot to an input slot. GraphData.Connect takes the
        // source node + slot and the destination node + slot.
        public static bool TryConnectNodes(
            string assetPath,
            string sourceNodeId, int sourceSlot,
            string destinationNodeId, int destinationSlot,
            out string error)
        {
            error = null;
            if (!ApiAvailable(out error)) return false;

            object graphData;
            if (!TryLoadGraphData(assetPath, out graphData, out error)) return false;

            try
            {
                var sourceNode = FindNodeById(graphData, sourceNodeId);
                var destNode = FindNodeById(graphData, destinationNodeId);
                if (sourceNode == null || destNode == null)
                {
                    error = "node_not_found";
                    return false;
                }

                // GraphData.Connect(outputSlot, outputNode, inputSlot, inputNode)
                // — signature varies; probe by parameter count.
                var connect = graphData.GetType().GetMethod("Connect",
                    BindingFlags.Public | BindingFlags.Instance);
                if (connect == null) { error = "connect_not_found"; return false; }

                var p = connect.GetParameters();
                if (p.Length == 4)
                {
                    connect.Invoke(graphData, new object[]
                    {
                        sourceSlot, sourceNode, destinationSlot, destNode,
                    });
                }
                else if (p.Length == 2)
                {
                    // Some versions take (outputSlotRef, inputSlotRef). Build
                    // the slot refs via the node's GetOutputSlot / GetInputSlot.
                    var outSlot = GetSlotRef(sourceNode, sourceSlot, isInput: false);
                    var inSlot = GetSlotRef(destNode, destinationSlot, isInput: true);
                    if (outSlot == null || inSlot == null) { error = "slot_not_found"; return false; }
                    connect.Invoke(graphData, new[] { outSlot, inSlot });
                }
                else
                {
                    error = "connect_signature_unsupported";
                    return false;
                }

                SaveGraphData(assetPath, graphData);
                return true;
            }
            catch (Exception e)
            {
                error = "node_connect_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph node_connect failed: {e.Message}");
                return false;
            }
        }

        // =====================================================================
        // Helpers — graph load/save, node lookup, slot refs
        // =====================================================================

        // Map a friendly node-type name to a UnityEditor.ShaderGraph type.
        private static Type ResolveNodeType(string nodeType)
        {
            if (string.IsNullOrEmpty(nodeType)) return null;
            // Full class name first.
            var full = ShaderGraphAssembly.GetType(nodeType);
            if (full != null) return full;

            // Friendly-name map. The package names node classes "<Name>Node"
            // (e.g. UVMNode → UVNode, MultiplyNode, SampleTexture2DNode).
            var friendly = nodeType.Replace(" ", "");
            var candidates = new[]
            {
                friendly + "Node",
                friendly,
            };
            foreach (var asmType in ShaderGraphAssembly.GetTypes())
            {
                if (asmType.IsAbstract) continue;
                if (!AbstractMaterialNodeType.IsAssignableFrom(asmType)) continue;
                foreach (var c in candidates)
                {
                    if (string.Equals(asmType.Name, c, StringComparison.OrdinalIgnoreCase))
                        return asmType;
                }
            }
            return null;
        }

        private static bool TryLoadGraphData(string assetPath, out object graphData, out string error)
        {
            graphData = null;
            error = null;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null) { error = "asset_not_found"; return false; }

            // The asset type name has shifted (MaterialGraphAsset → GraphData
            // hosted on a handler). Reflect over the most common shapes.
            var assetType = asset.GetType();

            // Direct property / field: graphData.
            var prop = assetType.GetProperty("graphData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                graphData = prop.GetValue(asset);
                if (graphData != null) return true;
            }
            var field = assetType.GetField("m_GraphData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                graphData = field.GetValue(asset);
                if (graphData != null) return true;
            }
            // Some versions expose a GetGraph() / GetGraphData() method.
            foreach (var m in assetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if ((m.Name == "GetGraph" || m.Name == "GetGraphData") &&
                    m.GetParameters().Length == 0)
                {
                    graphData = m.Invoke(asset, null);
                    if (graphData != null) return true;
                }
            }
            error = "graphdata_not_reachable";
            return false;
        }

        private static void SaveGraphData(string assetPath, object graphData)
        {
            // GraphUtil.WriteToFile(graphData, assetPath) is the standard
            // re-serialization entry point. Fall back to marking the asset
            // dirty + SaveAssets.
            if (GraphUtilType != null)
            {
                var write = GraphUtilType.GetMethod("WriteToFile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (write != null)
                {
                    var p = write.GetParameters();
                    if (p.Length == 2)
                    {
                        try
                        {
                            write.Invoke(null, new[] { graphData, assetPath });
                            AssetDatabase.ImportAsset(assetPath);
                            return;
                        }
                        catch
                        {
                            // Fall through to SaveAssets.
                        }
                    }
                }
            }
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        private static object FindNodeById(object graphData, string id)
        {
            // graphData.GetNodes() returns IEnumerable<AbstractMaterialNode>.
            var getNodes = graphData.GetType().GetMethod("GetNodes",
                BindingFlags.Public | BindingFlags.Instance);
            if (getNodes == null) return null;
            var enumerable = getNodes.Invoke(graphData, null) as IEnumerable;
            if (enumerable == null) return null;
            foreach (var node in enumerable)
            {
                if (string.Equals(GetObjectId(node), id, StringComparison.Ordinal))
                    return node;
            }
            return null;
        }

        private static string GetObjectId(object node)
        {
            var t = node.GetType();
            var prop = t.GetProperty("objectId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(node) as string;
            // Unity's JsonFromObject uses "m_ObjectId" via the object's tempId.
            var tempGuid = t.GetProperty("tempId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tempGuid != null) return tempGuid.GetValue(node)?.ToString();
            // Fallback: read via a JSON round-trip helper if available.
            return null;
        }

        private static void SetObjectId(object node, string id)
        {
            // Many versions do not expose a setter; GraphData.AddNode assigns
            // its own GUID. We try the setter; when absent AddNode owns the id
            // and the agent must read it back via shader_graph_open. The tool
            // still succeeds — nodeId may be empty in that case.
            var t = node.GetType();
            var prop = t.GetProperty("objectId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(node, id); } catch { /* best-effort */ }
            }
        }

        private static void SetNodePosition(object node, Vector2 pos)
        {
            var t = node.GetType();
            var prop = t.GetProperty("position",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(node, pos); return; } catch { }
            }
            var field = t.GetField("m_Position",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(Vector2))
            {
                field.SetValue(node, pos);
            }
        }

        private static void ApplyPropertyPatches(object node, string propertiesJson)
        {
            // propertiesJson is a { "field": value } object; apply each via the
            // node's serialized object so any [SerializeField] field works.
            // Shader Graph nodes are UnityEngine.Object subclasses (ScriptableObject-
            // derived); bail gracefully if reflection produced something else.
            var uo = node as UnityEngine.Object;
            if (uo == null) return;
            var so = new SerializedObject(uo);
            var entries = ParseJsonObject(propertiesJson);
            foreach (var kv in entries)
            {
                var sp = so.FindProperty(kv.Key);
                if (sp == null) continue;
                ApplyToProperty(sp, kv.Value);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string ExtractNodeSlotsJson(object node)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            // AbstractMaterialNode.GetSlots() returns IEnumerable<MaterialSlot>.
            var getSlots = node.GetType().GetMethod("GetSlots",
                BindingFlags.Public | BindingFlags.Instance);
            if (getSlots != null)
            {
                var slots = getSlots.Invoke(node, null) as IEnumerable;
                bool first = true;
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        var slotType = slot.GetType();
                        var idProp = slotType.GetProperty("id", BindingFlags.Public | BindingFlags.Instance);
                        var nameProp = slotType.GetProperty("displayName", BindingFlags.Public | BindingFlags.Instance);
                        sb.Append('{');
                        sb.Append("\"id\":").Append(idProp != null ? idProp.GetValue(slot) : 0).Append(',');
                        sb.Append("\"name\":").Append(ShaderGraphJson.Esc(
                            nameProp != null ? (string)nameProp.GetValue(slot) : ""));
                        sb.Append('}');
                        if (sb.Length > 4000) break; // safety cap
                    }
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static object GetSlotRef(object node, int slotId, bool isInput)
        {
            var method = node.GetType().GetMethod(
                isInput ? "GetInputSlot" : "GetOutputSlot",
                BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return null;
            return method.Invoke(node, new object[] { slotId });
        }

        // =====================================================================
        // small JSON helpers (hand-rolled — mirrors Timeline/Splines modify)
        // =====================================================================

        private static (string className, string templateName) NormalizeTemplate(string s)
        {
            var lower = (s ?? "").Trim().ToLowerInvariant();
            switch (lower)
            {
                case "lit": return ("", "Lit");
                case "unlit":
                case "": return ("", "Unlit");
                case "decal": return ("", "Decal");
                case "fullscreen": return ("", "Fullscreen");
                case "blank": return ("", "Blank");
                default: return ("", s);
            }
        }

        private static Vector2 ParseVector2(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector2.zero;
            var parts = s.Split(',');
            if (parts.Length != 2) return Vector2.zero;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return Vector2.zero;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return Vector2.zero;
            return new Vector2(x, y);
        }

        private static string ExtractStringValue(string body, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = body.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '"') return null;
            var end = start + 1;
            while (end < body.Length)
            {
                if (body[end] == '\\' && end + 1 < body.Length) { end += 2; continue; }
                if (body[end] == '"') break;
                end++;
            }
            if (end >= body.Length) return null;
            return body.Substring(start + 1, end - start - 1);
        }

        private static Vector2 ExtractVector2(string body, string key)
        {
            // Position is stored as { "x": n, "y": n } under m_Position.
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return Vector2.zero;
            var objStart = body.IndexOf('{', idx);
            if (objStart < 0) return Vector2.zero;
            var objEnd = body.IndexOf('}', objStart);
            if (objEnd < 0) return Vector2.zero;
            var obj = body.Substring(objStart, objEnd - objStart + 1);
            var x = ExtractFloat(obj, "x");
            var y = ExtractFloat(obj, "y");
            return new Vector2(x, y);
        }

        private static float ExtractFloat(string body, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return 0f;
            var colon = body.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return 0f;
            var start = colon + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            var end = start;
            while (end < body.Length &&
                   (char.IsDigit(body[end]) || body[end] == '-' || body[end] == '.' ||
                    body[end] == 'e' || body[end] == 'E' || body[end] == '+'))
                end++;
            if (float.TryParse(body.Substring(start, end - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v)) return v;
            return 0f;
        }

        private static int ExtractIntValue(string body, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return 0;
            var colon = body.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return 0;
            var start = colon + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            var end = start;
            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-'))
                end++;
            if (int.TryParse(body.Substring(start, end - start), out var v)) return v;
            return 0;
        }

        // Extract m_ObjectId from a nested object like { "m_ObjectId": "guid" }.
        private static string ExtractNestedObjectId(string body, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var objStart = body.IndexOf('{', idx);
            if (objStart < 0) return null;
            var objEnd = body.IndexOf('}', objStart);
            if (objEnd < 0) return null;
            var obj = body.Substring(objStart, objEnd - objStart + 1);
            return ExtractStringValue(obj, "m_ObjectId");
        }

        // Parse a flat JSON object of { "key": primitive } entries. Values are
        // kept as raw strings; the caller interprets per property type.
        private static List<KeyValuePair<string, string>> ParseJsonObject(string json)
        {
            var entries = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(json)) return entries;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return entries;
            var body = trimmed.Substring(1, trimmed.Length - 2);
            int i = 0;
            while (i < body.Length)
            {
                // Find the next key.
                var kStart = body.IndexOf('"', i);
                if (kStart < 0) break;
                var kEnd = body.IndexOf('"', kStart + 1);
                if (kEnd < 0) break;
                var key = body.Substring(kStart + 1, kEnd - kStart - 1);
                var colon = body.IndexOf(':', kEnd);
                if (colon < 0) break;
                var vStart = colon + 1;
                while (vStart < body.Length && char.IsWhiteSpace(body[vStart])) vStart++;
                if (vStart >= body.Length) break;
                int vEnd;
                string value;
                if (body[vStart] == '"')
                {
                    vEnd = body.IndexOf('"', vStart + 1);
                    if (vEnd < 0) break;
                    value = body.Substring(vStart + 1, vEnd - vStart - 1);
                    vEnd++;
                }
                else
                {
                    vEnd = body.IndexOf(',', vStart);
                    if (vEnd < 0) vEnd = body.Length;
                    value = body.Substring(vStart, vEnd - vStart).Trim();
                }
                entries.Add(new KeyValuePair<string, string>(key, value));
                i = vEnd;
            }
            return entries;
        }

        private static void ApplyToProperty(SerializedProperty prop, string raw)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(raw, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(raw, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = raw == "true" || raw == "True";
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = raw;
                    break;
                case SerializedPropertyType.Enum:
                    if (int.TryParse(raw, out var intVal))
                        prop.enumValueIndex = intVal;
                    break;
                default:
                    // Vector / color patches are possible but niche; skip.
                    break;
            }
        }
    }
}
#endif
