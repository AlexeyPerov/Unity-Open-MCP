// M20 Plan 7 / T20.7.1 — reflection surface over UnityEditor.ShaderGraph.
//
// Shader Graph's editing API (GraphData, AbstractMaterialNode, MaterialSlot,
// MultiJson, FileUtilities) is internal to the package, so this helper
// resolves the types / members by reflection at call time and returns
// structured success/failure. Every public tool method in ShaderGraphTools
// delegates here, so a future version change is fixed in ONE place.
//
// The helper works on the graph the way the package itself does: it loads a
// .shadergraph like ShaderGraphImporter (GraphData + MultiJson.Deserialize +
// OnEnable + ValidateGraph), edits it through GraphData (AddNode, Connect),
// and writes it with FileUtilities.WriteShaderGraphToDisk before importing.
// Creation builds the graph like the package's NewGraphAction instead of
// running the Assets/Create menu items, which need an interactive rename and
// may raise a modal preference dialog.
//
// When reflection cannot reach a needed member the helper returns false with
// an error code; the tool surfaces a structured error envelope so the agent
// can fall back to manual editing. The helper never throws out of the tool
// path — exceptions are caught and converted.
//
// Unity-version dependency: written against com.unity.shadergraph 17.x as
// shipped with Unity 6 (verified live on 6000.3 with URP 17.3).
#if UNITY_OPEN_MCP_EXT_SHADERGRAPH
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

namespace UnityOpenMcpBridge.Extensions.ShaderGraphExt
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
        private static readonly Type MultiJsonType =
            Resolve("UnityEditor.ShaderGraph.Serialization.MultiJson");
        private static readonly Type FileUtilitiesType = Resolve("UnityEditor.ShaderGraph.FileUtilities");
        private static readonly Type CategoryDataType = Resolve("UnityEditor.ShaderGraph.CategoryData");
        private static readonly Type MessageManagerType = Resolve("UnityEditor.Graphing.Util.MessageManager");
        private static readonly Type SlotReferenceType = Resolve("UnityEditor.Graphing.SlotReference");
        private static readonly Type MaterialSlotType = Resolve("UnityEditor.ShaderGraph.MaterialSlot");
        private static readonly Type TargetType = Resolve("UnityEditor.ShaderGraph.Target");
        private static readonly Type SubTargetType = Resolve("UnityEditor.ShaderGraph.SubTarget");

        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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
            if (GraphData == null || AbstractMaterialNodeType == null || MultiJsonType == null ||
                FileUtilitiesType == null || CategoryDataType == null || SlotReferenceType == null ||
                MaterialSlotType == null)
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

        // Create a new Shader Graph asset without the editor UI. The package's
        // own Assets/Create menu items start an in-place rename in the Project
        // window (the file is only written when the rename ends) and can raise a
        // modal "open graphs on creation?" preference dialog, so a tool cannot
        // drive them. Instead this builds the graph the way the package's
        // NewGraphAction does — GraphData + contexts + the template's target +
        // the target's active blocks + the default category — and writes it
        // with the package's own serializer (FileUtilities.WriteShaderGraphToDisk),
        // then imports it.
        public static bool TryCreateShaderGraph(
            string assetPath, string shaderType,
            out string error, out string createdPath, out string targetName)
        {
            createdPath = assetPath;
            targetName = null;
            if (!ApiAvailable(out error)) return false;

            var templateName = NormalizeTemplate(shaderType);
            if (templateName == null)
            {
                error = "unknown_shader_type";
                return false;
            }

            try
            {
                Array targets = null;
                if (templateName != "Blank" &&
                    !TryBuildTargets(templateName, out targets, out targetName, out error))
                    return false;

                var graph = Activator.CreateInstance(GraphData, true);
                Call(graph, "AddContexts");
                var blockDescriptorArray = Array.CreateInstance(
                    GraphData.GetMethod("InitializeOutputs", AnyInstance).GetParameters()[1].ParameterType
                        .GetElementType(), 0);
                Call(graph, "InitializeOutputs", targets, blockDescriptorArray);
                if (targets != null)
                {
                    // The menu templates list their blocks explicitly; the
                    // target knows which blocks it needs, so ask it instead.
                    var active = Call(graph, "GetActiveBlocksForAllActiveTargets");
                    Call(graph, "AddRemoveBlocksFromActiveList", active);
                }
                var category = CategoryDataType.GetMethod("DefaultCategory", AnyStatic)
                    ?.Invoke(null, new object[] { null });
                if (category != null) Call(graph, "AddCategory", category);
                GraphData.GetProperty("path", AnyInstance)?.SetValue(graph, "Shader Graphs");

                if (!WriteGraph(assetPath, graph))
                {
                    error = "shadergraph_write_failed";
                    return false;
                }
                if (AssetDatabase.LoadAssetAtPath<Shader>(assetPath) == null)
                {
                    error = "shadergraph_import_failed";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "shadergraph_api_unavailable";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph create failed: {Unwrap(e).Message}");
                return false;
            }
        }

        // Pick the Shader Graph target for the active render pipeline and set
        // the sub-target the template names (Unlit / Lit / Decal / Fullscreen),
        // e.g. URP: UniversalTarget + UniversalUnlitSubTarget.
        private static bool TryBuildTargets(
            string templateName, out Array targets, out string targetName, out string error)
        {
            targets = null;
            targetName = null;
            error = null;
            if (TargetType == null || SubTargetType == null)
            {
                error = "shadergraph_api_unavailable";
                return false;
            }

            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var subTargetPattern = new System.Text.RegularExpressions.Regex(
                "^(Universal|HD|BuiltIn)?" + templateName + "SubTarget$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var subTargetTypes = new List<Type>();
            foreach (var t in TypeCache.GetTypesDerivedFrom(SubTargetType))
                if (!t.IsAbstract && !t.IsGenericTypeDefinition && subTargetPattern.IsMatch(t.Name))
                    subTargetTypes.Add(t);

            foreach (var t in TypeCache.GetTypesDerivedFrom(TargetType))
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition ||
                    t.GetConstructor(AnyInstance, null, Type.EmptyTypes, null) == null) continue;
                object target;
                try { target = Activator.CreateInstance(t, true); }
                catch { continue; }
                if (TargetType.GetProperty("isHidden", AnyInstance)?.GetValue(target) is bool hidden && hidden)
                    continue;
                var works = TargetType.GetMethod("WorksWithSRP", AnyInstance);
                if (works == null || !(bool)works.Invoke(target, new object[] { pipeline })) continue;

                var setSub = t.GetMethod("TrySetActiveSubTarget", AnyInstance, null,
                    new[] { typeof(Type) }, null);
                if (setSub == null) continue;
                foreach (var st in subTargetTypes)
                {
                    if (!(bool)setSub.Invoke(target, new object[] { st })) continue;
                    targets = Array.CreateInstance(TargetType, 1);
                    targets.SetValue(target, 0);
                    targetName = t.Name + "/" + st.Name;
                    return true;
                }
            }

            error = "shader_type_unsupported";
            return false;
        }

        private static object Call(object instance, string method, params object[] args)
        {
            var type = instance.GetType();
            foreach (var m in type.GetMethods(AnyInstance))
            {
                if (m.Name != method || m.IsGenericMethodDefinition) continue;
                var p = m.GetParameters();
                if (p.Length < args.Length) continue;
                bool fits = true;
                for (int i = args.Length; i < p.Length; i++)
                    if (!p[i].HasDefaultValue) { fits = false; break; }
                if (!fits) continue;
                var full = new object[p.Length];
                for (int i = 0; i < p.Length; i++)
                    full[i] = i < args.Length ? args[i] : p[i].DefaultValue;
                return m.Invoke(instance, full);
            }
            throw new MissingMethodException(type.Name, method);
        }

        private static Exception Unwrap(Exception e)
            => e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;

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
        // graph summary (read path — the graph as the importer loads it)
        // =====================================================================

        // Build a node/edge summary from the loaded GraphData: every material
        // node (block nodes of the master stack included) with its id, name,
        // type, position and slots, and every edge as output node/slot →
        // input node/slot. The ids are the ones node_add returns and
        // node_connect takes.
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

            if (!ApiAvailable(out var apiError))
            {
                parseError = $"Shader Graph API unavailable ({apiError}); no summary.";
                return;
            }
            if (!TryLoadGraphData(assetPath, out var graph, out var loadError))
            {
                parseError = $"Could not load the graph ({loadError}).";
                return;
            }

            try
            {
                var nodes = new StringBuilder(512);
                nodes.Append('[');
                MethodInfo getNodes = null;
                foreach (var m in GraphData.GetMethods(AnyInstance))
                    if (m.Name == "GetNodes" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    { getNodes = m; break; }
                var all = getNodes?.MakeGenericMethod(AbstractMaterialNodeType).Invoke(graph, null) as IEnumerable;
                if (all != null)
                {
                    foreach (var node in all)
                    {
                        if (nodeCount > 0) nodes.Append(',');
                        nodeCount++;
                        var t = node.GetType();
                        var name = t.GetProperty("name", AnyInstance)?.GetValue(node) as string;
                        var pos = Vector2.zero;
                        var state = t.GetProperty("drawState", AnyInstance)?.GetValue(node);
                        if (state?.GetType().GetProperty("position", AnyInstance)?.GetValue(state) is Rect r)
                            pos = r.position;
                        nodes.Append('{');
                        nodes.Append("\"id\":").Append(ShaderGraphJson.Esc(GetObjectId(node) ?? "")).Append(',');
                        nodes.Append("\"name\":").Append(ShaderGraphJson.Esc(name ?? "")).Append(',');
                        nodes.Append("\"type\":").Append(ShaderGraphJson.Esc(t.Name)).Append(',');
                        nodes.Append("\"position\":[")
                            .Append(pos.x.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append(pos.y.ToString(CultureInfo.InvariantCulture)).Append("],");
                        nodes.Append("\"slots\":").Append(ExtractNodeSlotsJson(node));
                        nodes.Append('}');
                    }
                }
                nodes.Append(']');
                nodesJson = nodes.ToString();

                var edges = new StringBuilder(256);
                edges.Append('[');
                if (GraphData.GetProperty("edges", AnyInstance)?.GetValue(graph) is IEnumerable edgeList)
                {
                    foreach (var edge in edgeList)
                    {
                        var et = edge.GetType();
                        var output = et.GetProperty("outputSlot", AnyInstance)?.GetValue(edge);
                        var input = et.GetProperty("inputSlot", AnyInstance)?.GetValue(edge);
                        if (output == null || input == null) continue;
                        if (edgeCount > 0) edges.Append(',');
                        edgeCount++;
                        edges.Append('{');
                        edges.Append("\"outputNodeId\":").Append(ShaderGraphJson.Esc(SlotNodeId(output))).Append(',');
                        edges.Append("\"outputSlot\":").Append(SlotId(output)).Append(',');
                        edges.Append("\"inputNodeId\":").Append(ShaderGraphJson.Esc(SlotNodeId(input))).Append(',');
                        edges.Append("\"inputSlot\":").Append(SlotId(input));
                        edges.Append('}');
                    }
                }
                edges.Append(']');
                edgesJson = edges.ToString();
            }
            catch (Exception e)
            {
                parseError = $"Graph summary failed (partial summary): {Unwrap(e).Message}";
            }
        }

        private static string SlotNodeId(object slotReference)
        {
            var node = slotReference.GetType().GetProperty("node", AnyInstance)?.GetValue(slotReference);
            return node == null ? "" : GetObjectId(node) ?? "";
        }

        private static int SlotId(object slotReference)
            => slotReference.GetType().GetProperty("slotId", AnyInstance)?.GetValue(slotReference) is int id ? id : -1;

        // =====================================================================
        // node_add / node_connect (mutation — reflection over GraphData)
        // =====================================================================

        // Add a node: load the graph the way the importer does, instantiate the
        // node type, add it, and write the graph back with the package's own
        // serializer. The node id is the JsonObject id GraphData stores it under.
        public static bool TryAddNode(
            string assetPath, string nodeType, string position, string propertiesJson,
            out string error, out string nodeId, out string slotsJson, out List<string> ignoredProperties)
        {
            error = null;
            nodeId = null;
            slotsJson = "[]";
            ignoredProperties = new List<string>();
            if (!ApiAvailable(out error)) return false;

            // Resolve the node type. nodeType may be a friendly name or a full
            // class name; the friendly map covers the common shader nodes.
            var sysType = ResolveNodeType(nodeType);
            if (sysType == null)
            {
                error = "unknown_node_type";
                return false;
            }

            if (!TryLoadGraphData(assetPath, out var graphData, out error)) return false;

            try
            {
                var node = Activator.CreateInstance(sysType, true);
                if (!string.IsNullOrEmpty(position))
                    SetNodePosition(node, ParseVector2(position));
                if (!string.IsNullOrEmpty(propertiesJson))
                    ApplyPropertyPatches(node, propertiesJson, ignoredProperties);

                Call(graphData, "AddNode", node);
                nodeId = GetObjectId(node);
                slotsJson = ExtractNodeSlotsJson(node);

                if (!WriteGraph(assetPath, graphData))
                {
                    error = "shadergraph_write_failed";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "node_add_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph node_add failed: {Unwrap(e).Message}");
                return false;
            }
        }

        // Connect an output slot to an input slot with GraphData.Connect, which
        // returns null for a connection the graph refuses (unknown slot, two
        // inputs or two outputs, a cycle).
        public static bool TryConnectNodes(
            string assetPath,
            string sourceNodeId, int sourceSlot,
            string destinationNodeId, int destinationSlot,
            out string error)
        {
            error = null;
            if (!ApiAvailable(out error)) return false;

            if (!TryLoadGraphData(assetPath, out var graphData, out error)) return false;

            try
            {
                var sourceNode = FindNodeById(graphData, sourceNodeId);
                var destNode = FindNodeById(graphData, destinationNodeId);
                if (sourceNode == null || destNode == null)
                {
                    error = "node_not_found";
                    return false;
                }
                if (FindSlot(sourceNode, sourceSlot) == null || FindSlot(destNode, destinationSlot) == null)
                {
                    error = "slot_not_found";
                    return false;
                }

                var from = Activator.CreateInstance(SlotReferenceType, sourceNode, sourceSlot);
                var to = Activator.CreateInstance(SlotReferenceType, destNode, destinationSlot);
                var edge = Call(graphData, "Connect", from, to);
                if (edge == null)
                {
                    error = "connection_rejected";
                    return false;
                }

                if (!WriteGraph(assetPath, graphData))
                {
                    error = "shadergraph_write_failed";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "node_connect_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph node_connect failed: {Unwrap(e).Message}");
                return false;
            }
        }

        // =====================================================================
        // Helpers — graph load/save, node lookup, slots
        // =====================================================================

        // Map a friendly node-type name to a UnityEditor.ShaderGraph type.
        private static Type ResolveNodeType(string nodeType)
        {
            if (string.IsNullOrEmpty(nodeType)) return null;
            // Full class name first.
            var full = ShaderGraphAssembly.GetType(nodeType);
            if (full != null && AbstractMaterialNodeType.IsAssignableFrom(full) && !full.IsAbstract)
                return full;

            // Friendly-name map. The package names node classes "<Name>Node"
            // (e.g. UVNode, MultiplyNode, SampleTexture2DNode).
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

        // Load the graph exactly as ShaderGraphImporter does: a GraphData with a
        // message manager and the asset GUID, filled by MultiJson.Deserialize,
        // then OnEnable + ValidateGraph.
        private static bool TryLoadGraphData(string assetPath, out object graphData, out string error)
        {
            graphData = null;
            error = null;
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath) ||
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) == null)
            {
                error = "asset_not_found";
                return false;
            }

            try
            {
                var graph = Activator.CreateInstance(GraphData, true);
                if (MessageManagerType != null)
                    GraphData.GetProperty("messageManager", AnyInstance)
                        ?.SetValue(graph, Activator.CreateInstance(MessageManagerType, true));
                GraphData.GetProperty("assetGuid", AnyInstance)
                    ?.SetValue(graph, AssetDatabase.AssetPathToGUID(assetPath));

                MethodInfo deserialize = null;
                foreach (var m in MultiJsonType.GetMethods(AnyStatic))
                    if (m.Name == "Deserialize" && m.IsGenericMethodDefinition) { deserialize = m; break; }
                if (deserialize == null)
                {
                    error = "shadergraph_api_unavailable";
                    return false;
                }
                var args = new object[deserialize.GetParameters().Length];
                args[0] = graph;
                args[1] = File.ReadAllText(assetPath, Encoding.UTF8);
                for (int i = 2; i < args.Length; i++)
                    args[i] = deserialize.GetParameters()[i].DefaultValue;
                deserialize.MakeGenericMethod(GraphData).Invoke(null, args);
                Call(graph, "OnEnable");
                Call(graph, "ValidateGraph");
                graphData = graph;
                return true;
            }
            catch (Exception e)
            {
                error = "graph_load_failed";
                Debug.LogWarning($"[unity-open-mcp] ShaderGraph load failed: {Unwrap(e).Message}");
                return false;
            }
        }

        // Write the graph with the package's serializer and import it, so the
        // compiled Shader matches the file.
        private static bool WriteGraph(string assetPath, object graphData)
        {
            var write = FileUtilitiesType.GetMethod("WriteShaderGraphToDisk", AnyStatic);
            if (write == null) return false;
            if (write.Invoke(null, new[] { assetPath, graphData }) == null) return false;
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return true;
        }

        private static object FindNodeById(object graphData, string id)
        {
            // GetNodeFromId(string) has a generic GetNodeFromId<T>(string)
            // sibling, so pick the non-generic one explicitly.
            foreach (var m in GraphData.GetMethods(AnyInstance))
                if (m.Name == "GetNodeFromId" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    return m.Invoke(graphData, new object[] { id });
            return null;
        }

        private static string GetObjectId(object node)
            => node.GetType().GetProperty("objectId", AnyInstance)?.GetValue(node) as string;

        private static void SetNodePosition(object node, Vector2 pos)
        {
            // AbstractMaterialNode.drawState is a struct; set its position on a
            // boxed copy and assign it back.
            var prop = node.GetType().GetProperty("drawState", AnyInstance);
            if (prop == null || !prop.CanWrite) return;
            var state = prop.GetValue(node);
            var position = state.GetType().GetProperty("position", AnyInstance);
            if (position == null) return;
            var rect = (Rect)position.GetValue(state);
            position.SetValue(state, new Rect(pos, rect.size));
            prop.SetValue(node, state);
        }

        // Patch public writable node properties of simple types (number, bool,
        // string, enum). Keys that name no such property are reported back.
        private static void ApplyPropertyPatches(object node, string propertiesJson, List<string> ignored)
        {
            foreach (var kv in ParseJsonObject(propertiesJson))
            {
                var prop = node.GetType().GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                object value = null;
                if (prop != null && prop.CanWrite && TryConvert(kv.Value, prop.PropertyType, out value))
                {
                    try { prop.SetValue(node, value); continue; }
                    catch { /* reported below */ }
                }
                ignored.Add(kv.Key);
            }
        }

        private static bool TryConvert(string raw, Type type, out object value)
        {
            value = null;
            var ci = CultureInfo.InvariantCulture;
            if (type == typeof(string)) { value = raw; return true; }
            if (type == typeof(bool)) { value = raw == "true" || raw == "True"; return raw == "true" || raw == "True" || raw == "false" || raw == "False"; }
            if (type == typeof(int) && int.TryParse(raw, NumberStyles.Integer, ci, out var i)) { value = i; return true; }
            if (type == typeof(float) && float.TryParse(raw, NumberStyles.Float, ci, out var f)) { value = f; return true; }
            if (type.IsEnum)
            {
                try { value = Enum.Parse(type, raw, true); return true; }
                catch { return false; }
            }
            return false;
        }

        private static object FindSlot(object node, int slotId)
        {
            foreach (var m in node.GetType().GetMethods(AnyInstance))
            {
                if (m.Name != "FindSlot" || !m.IsGenericMethodDefinition || m.GetParameters().Length != 1) continue;
                return m.MakeGenericMethod(MaterialSlotType).Invoke(node, new object[] { slotId });
            }
            return null;
        }

        private static string ExtractNodeSlotsJson(object node)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            // AbstractMaterialNode.GetSlots<T>(List<T>) fills a list of MaterialSlot.
            MethodInfo getSlots = null;
            foreach (var m in node.GetType().GetMethods(AnyInstance))
                if (m.Name == "GetSlots" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                { getSlots = m; break; }
            if (getSlots != null)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(MaterialSlotType));
                getSlots.MakeGenericMethod(MaterialSlotType).Invoke(node, new object[] { list });
                bool first = true;
                foreach (var slot in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    var slotType = slot.GetType();
                    var id = slotType.GetProperty("id", AnyInstance)?.GetValue(slot) ?? 0;
                    var name = slotType.GetProperty("displayName", AnyInstance)?.GetValue(slot) as string;
                    var isInput = slotType.GetProperty("isInputSlot", AnyInstance)?.GetValue(slot) is bool b && b;
                    sb.Append("{\"id\":").Append(Convert.ToInt32(id, CultureInfo.InvariantCulture));
                    sb.Append(",\"name\":").Append(ShaderGraphJson.Esc(name ?? ""));
                    sb.Append(",\"direction\":").Append(isInput ? "\"input\"" : "\"output\"");
                    sb.Append('}');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        // =====================================================================
        // small JSON helpers (hand-rolled — mirrors Timeline/Splines modify)
        // =====================================================================

        // Template vocabulary of shader_type; null for a name the tool does not know.
        private static string NormalizeTemplate(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "unlit": return "Unlit";
                case "lit": return "Lit";
                case "decal": return "Decal";
                case "fullscreen": return "Fullscreen";
                case "blank": return "Blank";
                default: return null;
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
    }
}
#endif
