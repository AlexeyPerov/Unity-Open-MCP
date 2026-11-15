using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Embedded-domain round trips dispatched by tool name from the core test
    // assembly, so they run wherever the domain is compiled into the bridge
    // (each test is ignored when its tool is not registered). They pin bugs a
    // single-call test missed: a response that was not valid JSON, arguments the
    // registry could not bind (double, int[]), and Shader Graph editing that
    // never reached the graph.
    public class ExtensionDomainRoundTripTests
    {
        private static void RequireTool(string name)
        {
            if (!BridgeToolRegistry.Contains(name))
                Assert.Ignore($"{name} is not compiled into this bridge.");
        }

        private static ToolDispatchResult Dispatch(string tool, string body)
        {
            var result = BridgeToolRegistry.TryDispatch(tool, body);
            Assert.IsNotNull(result, tool);
            return result;
        }

        private static string Field(string json, string key)
            => Regex.Match(json, "\"" + key + "\":\"([^\"]*)\"").Groups[1].Value;

        [Test]
        public void ProBuilder_MeshInfoIsValidJson_AndFaceIndicesBind()
        {
            RequireTool("unity_open_mcp_probuilder_get_mesh_info");
            const string name = "ExtRoundTripPbCube";
            // The shape must not dirty a project scene: the test runner's own
            // untitled scene is fine, a saved active scene gets a temporary
            // additive scene next to it.
            var previous = SceneManager.GetActiveScene();
            var temp = default(Scene);
            if (!string.IsNullOrEmpty(previous.path))
            {
                temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(temp);
            }
            try
            {
                var create = Dispatch("unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"name\":\"" + name + "\",\"paths_hint\":[\"Assets\"]}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);

                var extrude = Dispatch("unity_open_mcp_probuilder_extrude",
                    "{\"name\":\"" + name + "\",\"face_indices\":[0],\"distance\":0.25,\"paths_hint\":[\"Assets\"]}");
                Assert.IsTrue(extrude.Success, extrude.ErrorMessage ?? extrude.Output);
                StringAssert.Contains("\"selectionMethod\":\"by index\"", extrude.Output);

                var info = Dispatch("unity_open_mcp_probuilder_get_mesh_info", "{\"name\":\"" + name + "\"}");
                Assert.IsTrue(info.Success, info.ErrorMessage ?? info.Output);
                Assert.IsTrue(BridgeJson.IsValidJsonObject(info.Output), info.Output);
                StringAssert.Contains("\"faceCount\":10", info.Output);
            }
            finally
            {
                var go = GameObject.Find(name);
                if (go != null) Object.DestroyImmediate(go);
                if (temp.IsValid())
                {
                    SceneManager.SetActiveScene(previous);
                    EditorSceneManager.CloseScene(temp, true);
                }
            }
        }

        [Test]
        public void ShaderGraph_CreateAddConnectOpen_EditsTheGraph()
        {
            RequireTool("unity_open_mcp_shader_graph_create");
            var path = $"Assets/ExtRoundTrip_{System.Guid.NewGuid():N}.shadergraph";
            var hint = ",\"paths_hint\":[\"" + path + "\"]";
            try
            {
                var create = Dispatch("unity_open_mcp_shader_graph_create",
                    "{\"asset_path\":\"" + path + "\",\"shader_type\":\"Unlit\"" + hint + "}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Shader>(path), "the graph imports as a Shader");

                var color = Dispatch("unity_open_mcp_shader_graph_node_add",
                    "{\"asset_path\":\"" + path + "\",\"node_type\":\"Color\"" + hint + "}");
                Assert.IsTrue(color.Success, color.ErrorMessage ?? color.Output);
                StringAssert.Contains("\"direction\":\"output\"", color.Output);
                var multiply = Dispatch("unity_open_mcp_shader_graph_node_add",
                    "{\"asset_path\":\"" + path + "\",\"node_type\":\"Multiply\"" + hint + "}");
                Assert.IsTrue(multiply.Success, multiply.ErrorMessage ?? multiply.Output);
                var colorId = Field(color.Output, "nodeId");
                var multiplyId = Field(multiply.Output, "nodeId");
                Assert.IsNotEmpty(colorId);
                Assert.IsNotEmpty(multiplyId);

                var connect = Dispatch("unity_open_mcp_shader_graph_node_connect",
                    "{\"asset_path\":\"" + path + "\",\"source_node_id\":\"" + colorId + "\",\"source_slot\":0," +
                    "\"destination_node_id\":\"" + multiplyId + "\",\"destination_slot\":0" + hint + "}");
                Assert.IsTrue(connect.Success, connect.ErrorMessage ?? connect.Output);

                // Multiply slot 2 is its output: output → output is refused.
                var refused = Dispatch("unity_open_mcp_shader_graph_node_connect",
                    "{\"asset_path\":\"" + path + "\",\"source_node_id\":\"" + colorId + "\",\"source_slot\":0," +
                    "\"destination_node_id\":\"" + multiplyId + "\",\"destination_slot\":2" + hint + "}");
                Assert.IsFalse(refused.Success);
                Assert.AreEqual("connection_rejected", refused.ErrorCode);

                var open = Dispatch("unity_open_mcp_shader_graph_open",
                    "{\"asset_path\":\"" + path + "\",\"window\":\"none\"}");
                Assert.IsTrue(open.Success, open.ErrorMessage ?? open.Output);
                StringAssert.Contains("\"edgeCount\":1", open.Output);
                StringAssert.Contains("\"outputNodeId\":\"" + colorId + "\"", open.Output);
                StringAssert.Contains("\"type\":\"MultiplyNode\"", open.Output);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void ShaderGraph_Create_UnknownShaderType_Refuses()
        {
            RequireTool("unity_open_mcp_shader_graph_create");
            var path = $"Assets/ExtRoundTrip_{System.Guid.NewGuid():N}.shadergraph";
            try
            {
                var create = Dispatch("unity_open_mcp_shader_graph_create",
                    "{\"asset_path\":\"" + path + "\",\"shader_type\":\"Bogus\",\"paths_hint\":[\"" + path + "\"]}");
                Assert.IsFalse(create.Success);
                Assert.AreEqual("unknown_shader_type", create.ErrorCode);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        // shader_graph_open shows the graph in its Shader Graph window (once,
        // however often it is called), an edit made while the window is open
        // reloads the window instead of leaving it to ask "Graph has changed
        // on disk", and window:"close" closes it. In batch mode there are no
        // windows, so the call refuses.
        [Test]
        public void ShaderGraph_Open_ShowsTheWindow_EditReloadsIt_CloseClosesIt()
        {
            RequireTool("unity_open_mcp_shader_graph_open");
            var path = $"Assets/ExtRoundTrip_{System.Guid.NewGuid():N}.shadergraph";
            var hint = ",\"paths_hint\":[\"" + path + "\"]";
            try
            {
                var create = Dispatch("unity_open_mcp_shader_graph_create",
                    "{\"asset_path\":\"" + path + "\",\"shader_type\":\"Unlit\"" + hint + "}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);

                var open = Dispatch("unity_open_mcp_shader_graph_open", "{\"asset_path\":\"" + path + "\"}");
                if (Application.isBatchMode)
                {
                    Assert.IsFalse(open.Success);
                    Assert.AreEqual("editor_window_unavailable", open.ErrorCode);
                    return;
                }
                Assert.IsTrue(open.Success, open.ErrorMessage ?? open.Output);
                StringAssert.Contains("\"window\":{\"action\":\"opened\",\"open\":true", open.Output);
                Assert.AreEqual(1, GraphWindows(path).Length, "one Shader Graph window shows the graph");

                var again = Dispatch("unity_open_mcp_shader_graph_open", "{\"asset_path\":\"" + path + "\"}");
                Assert.IsTrue(again.Success, again.ErrorMessage ?? again.Output);
                StringAssert.Contains("\"action\":\"focused\"", again.Output);
                Assert.AreEqual(1, GraphWindows(path).Length, "a second open focuses the same window");

                var add = Dispatch("unity_open_mcp_shader_graph_node_add",
                    "{\"asset_path\":\"" + path + "\",\"node_type\":\"Color\"" + hint + "}");
                Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);
                var window = GraphWindows(path)[0];
                var nodeId = Field(add.Output, "nodeId");
                StringAssert.Contains(nodeId, WindowField(window, "m_LastSerializedFileContents") as string,
                    "the window reloaded the edited file");
                Assert.AreEqual(false, WindowField(window, "m_AssetMaybeChangedOnDisk"),
                    "the window has no pending changed-on-disk check (it would ask in a dialog)");
                Assert.IsFalse(window.hasUnsavedChanges);

                var close = Dispatch("unity_open_mcp_shader_graph_open",
                    "{\"asset_path\":\"" + path + "\",\"window\":\"close\"}");
                Assert.IsTrue(close.Success, close.ErrorMessage ?? close.Output);
                StringAssert.Contains("\"window\":{\"action\":\"closed\",\"open\":false,\"closed\":1}", close.Output);
                Assert.AreEqual(0, GraphWindows(path).Length);
            }
            finally
            {
                // Deleting a graph whose window is open makes the window ask
                // in a dialog: close it first, and keep the asset if that fails.
                Dispatch("unity_open_mcp_shader_graph_open", "{\"asset_path\":\"" + path + "\",\"window\":\"close\"}");
                if (GraphWindows(path).Length == 0) AssetDatabase.DeleteAsset(path);
            }
        }

        private static EditorWindow[] GraphWindows(string assetPath)
        {
            var type = System.Type.GetType("UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow, Unity.ShaderGraph.Editor");
            if (type == null) return new EditorWindow[0];
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var selected = type.GetProperty("selectedGuid");
            var list = new System.Collections.Generic.List<EditorWindow>();
            foreach (var o in Resources.FindObjectsOfTypeAll(type))
                if (o is EditorWindow w && w != null && selected?.GetValue(w) as string == guid) list.Add(w);
            return list.ToArray();
        }

        private static object WindowField(EditorWindow window, string field)
            => window.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(window);

        [Test]
        public void Timeline_ClipAdd_BindsDoubleTimes()
        {
            RequireTool("unity_open_mcp_timeline_clip_add");
            var path = $"Assets/ExtRoundTrip_{System.Guid.NewGuid():N}.playable";
            var hint = ",\"paths_hint\":[\"" + path + "\"]";
            try
            {
                var create = Dispatch("unity_open_mcp_timeline_create", "{\"asset_path\":\"" + path + "\"" + hint + "}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                var track = Dispatch("unity_open_mcp_timeline_track_add",
                    "{\"asset_path\":\"" + path + "\",\"track_type\":\"Activation\"" + hint + "}");
                Assert.IsTrue(track.Success, track.ErrorMessage ?? track.Output);
                var clip = Dispatch("unity_open_mcp_timeline_clip_add",
                    "{\"asset_path\":\"" + path + "\",\"track_index\":0,\"start_time\":0.5,\"duration\":1.25" + hint + "}");
                Assert.IsTrue(clip.Success, clip.ErrorMessage ?? clip.Output);
                StringAssert.Contains("\"start\":0.5", clip.Output);
                StringAssert.Contains("\"duration\":1.25", clip.Output);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
