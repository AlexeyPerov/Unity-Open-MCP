using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests
{
    // Every id a create / add tool returns is one the next call accepts: the
    // `instanceId` of a tool that adds a component is its host GameObject's
    // (what every typed tool takes as instance_id / parent_instance_id), and
    // the component's own id is `componentInstanceId` (component_get /
    // component_modify take it as component_instance_id). Each case creates
    // or adds, then feeds the returned ids back. Domain tools are dispatched
    // by name, so a case is ignored where its domain is not compiled in.
    public class ReturnedIdRoundTripTests
    {
        private const string Hint = ",\"paths_hint\":[\"Assets\"]";
        private Scene _previous;
        private Scene _temp;
        private HashSet<GameObject> _rootsBefore;
        private int _undoGroup;

        [SetUp]
        public void SetUp()
        {
            // New roots must not dirty a project scene: the test runner's own
            // untitled scene is fine, a saved active scene gets a temporary
            // additive scene next to it.
            _previous = SceneManager.GetActiveScene();
            _temp = default;
            if (!string.IsNullOrEmpty(_previous.path))
            {
                _temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(_temp);
            }
            _rootsBefore = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());
            Undo.IncrementCurrentGroup();
            _undoGroup = Undo.GetCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            // Take the tools' undo records back off the stack before the hosts
            // go: the Test Framework reverts the run's undo groups when it ends,
            // and a record of an object destroyed outside Undo would bring the
            // object back into the restored scene.
            Undo.RevertAllDownToGroup(_undoGroup);
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_rootsBefore.Contains(root)) Object.DestroyImmediate(root);
            if (_temp.IsValid())
            {
                SceneManager.SetActiveScene(_previous);
                EditorSceneManager.CloseScene(_temp, true);
            }
        }

        private static void RequireTool(string name)
        {
            if (!BridgeToolRegistry.Contains(name) && !BridgeToolClassification.KnownTools.Contains(name))
                Assert.Ignore($"{name} is not compiled into this bridge.");
        }

        private static ToolDispatchResult Dispatch(string tool, string body)
        {
            var result = BridgeHttpServer.DispatchTool(tool, body);
            Assert.IsNotNull(result, tool);
            return result;
        }

        // The quoted id `field` of the result object `objectKey`.
        private static string IdIn(string json, string objectKey, string field)
        {
            var start = json.IndexOf("\"" + objectKey + "\":{", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, $"no \"{objectKey}\" object in {json}");
            var m = new Regex("\"" + field + "\":\"(-?\\d+)\"").Match(json, start);
            Assert.IsTrue(m.Success, $"no quoted \"{field}\" in \"{objectKey}\": {json}");
            return m.Groups[1].Value;
        }

        // The ids `objectKey` carries: instanceId names the host GameObject,
        // componentInstanceId a component of the type named, on that host;
        // component_get accepts the pair.
        private static string AssertHostAndComponentIds(string json, string objectKey, string hostName, string componentTypeName)
        {
            var id = IdIn(json, objectKey, "instanceId");
            var host = InstanceId.ToObject(long.Parse(id)) as GameObject;
            Assert.IsNotNull(host, $"{objectKey}.instanceId {id} is not a GameObject: {json}");
            Assert.AreEqual(hostName, host.name);
            var componentId = IdIn(json, objectKey, "componentInstanceId");
            var component = InstanceId.ToObject(long.Parse(componentId)) as Component;
            Assert.IsNotNull(component, $"{objectKey}.componentInstanceId {componentId} is not a Component");
            Assert.AreEqual(componentTypeName, component.GetType().Name);
            Assert.AreSame(host, component.gameObject);

            var get = Dispatch("unity_open_mcp_component_get",
                "{\"instance_id\":\"" + id + "\",\"component_instance_id\":\"" + componentId + "\"}");
            Assert.IsTrue(get.Success, get.ErrorMessage ?? get.Output);
            StringAssert.Contains("\"name\":\"" + componentTypeName + "\"", get.Output);
            return id;
        }

        private static GameObject Host(string name, params System.Type[] components)
        {
            var go = new GameObject(name, components);
            return go;
        }

        // Add a component with `addTool`, then feed the returned host id to
        // `followTool` (its body gets "instance_id":"<id>" prepended).
        private static void AddThenFollow(string addTool, string hostName, string addArgs, string objectKey,
            string componentTypeName, string followTool, string followArgs)
        {
            RequireTool(addTool);
            Host(hostName);
            var add = Dispatch(addTool, "{\"path\":\"" + hostName + "\"" + addArgs + Hint + "}");
            Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);
            var id = AssertHostAndComponentIds(add.Output, objectKey, hostName, componentTypeName);

            var follow = Dispatch(followTool, "{\"instance_id\":\"" + id + "\"" + followArgs + "}");
            Assert.IsTrue(follow.Success, $"{followTool} refused the id {addTool} returned: {follow.ErrorCode} {follow.ErrorMessage}");
        }

        [Test]
        public void UiCanvasAdd_ReturnsTheHostId_ElementAndLayoutToolsAcceptIt()
        {
            RequireTool("unity_open_mcp_ui_canvas_add");
            var canvas = Dispatch("unity_open_mcp_ui_canvas_add", "{\"new_root_name\":\"IdRt_Canvas\"" + Hint + "}");
            Assert.IsTrue(canvas.Success, canvas.ErrorMessage ?? canvas.Output);
            var id = AssertHostAndComponentIds(canvas.Output, "canvas", "IdRt_Canvas", "Canvas");

            var element = Dispatch("unity_open_mcp_ui_element_add",
                "{\"parent_instance_id\":\"" + id + "\",\"element_type\":\"Image\"" + Hint + "}");
            Assert.IsTrue(element.Success, element.ErrorMessage ?? element.Output);
            StringAssert.Contains("\"parentPath\":\"IdRt_Canvas\"", element.Output);

            var layout = Dispatch("unity_open_mcp_ui_layout_group_add",
                "{\"instance_id\":\"" + id + "\",\"layout_type\":\"VerticalLayoutGroup\"" + Hint + "}");
            Assert.IsTrue(layout.Success, layout.ErrorMessage ?? layout.Output);
            var layoutHost = AssertHostAndComponentIds(layout.Output, "layoutGroup", "IdRt_Canvas", "VerticalLayoutGroup");
            Assert.AreEqual(id, layoutHost);

            var modify = Dispatch("unity_open_mcp_ui_element_modify",
                "{\"instance_id\":\"" + layoutHost + "\",\"component_type\":\"VerticalLayoutGroup\"," +
                "\"fields_json\":\"[{\\\"field\\\":\\\"spacing\\\",\\\"value\\\":4}]\"" + Hint + "}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
            StringAssert.Contains("\"applied\":true", modify.Output);
        }

        [Test]
        public void ConstraintAdd_ReturnsTheHostId()
            => AddThenFollow("unity_open_mcp_constraint_add", "IdRt_Constraint",
                ",\"constraint_type\":\"PositionConstraint\"", "constraint", "PositionConstraint",
                "unity_open_mcp_constraint_add", ",\"constraint_type\":\"PositionConstraint\"" + Hint);

        [Test]
        public void LodGroupConfigure_ReturnsTheHostId_LodAddLevelAcceptsIt()
            => AddThenFollow("unity_open_mcp_lod_group_configure", "IdRt_Lod", ",\"lod_count\":2", "lodGroup", "LODGroup",
                "unity_open_mcp_lod_add_level", ",\"index\":1,\"screen_relative_transition_height\":0.3" + Hint);

        [Test]
        public void LightAdd_ReturnsTheHostId_LightSetAcceptsIt()
            => AddThenFollow("unity_open_mcp_light_add", "IdRt_Light", ",\"light_type\":\"Point\"", "light", "Light",
                "unity_open_mcp_light_set", ",\"intensity\":2" + Hint);

        [Test]
        public void AudioSourceAdd_ReturnsTheHostId_AudioSourceModifyAcceptsIt()
            => AddThenFollow("unity_open_mcp_audio_source_add", "IdRt_Audio", "", "source", "AudioSource",
                "unity_open_mcp_audio_source_modify", ",\"volume\":0.5" + Hint);

        [TestCase("unity_open_mcp_navigation_surface_add", "surface", "NavMeshSurface")]
        [TestCase("unity_open_mcp_navigation_modifier_add", "modifier", "NavMeshModifier")]
        [TestCase("unity_open_mcp_navigation_modifier_volume_add", "volume", "NavMeshModifierVolume")]
        [TestCase("unity_open_mcp_navigation_link_add", "link", "NavMeshLink")]
        [TestCase("unity_open_mcp_navigation_agent_add", "agent", "NavMeshAgent")]
        public void NavigationAdd_ReturnsTheHostId_NavigationGetAcceptsIt(string tool, string objectKey, string componentType)
            => AddThenFollow(tool, "IdRt_" + objectKey, "", objectKey, componentType, "unity_open_mcp_navigation_get", "");

        [Test]
        public void TimelineDirectorBind_ReturnsTheHostId()
        {
            RequireTool("unity_open_mcp_timeline_director_bind");
            var asset = $"Assets/IdRt_{System.Guid.NewGuid():N}.playable";
            try
            {
                var create = Dispatch("unity_open_mcp_timeline_create",
                    "{\"asset_path\":\"" + asset + "\",\"paths_hint\":[\"" + asset + "\"]}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                AddThenFollow("unity_open_mcp_timeline_director_bind", "IdRt_Director",
                    ",\"asset_path\":\"" + asset + "\"", "director", "PlayableDirector",
                    "unity_open_mcp_timeline_director_bind", ",\"asset_path\":\"" + asset + "\"" + Hint);
            }
            finally
            {
                AssetDatabase.DeleteAsset(asset);
            }
        }

        [Test]
        public void CinemachineBrainEnsure_ReturnsTheHostId()
        {
            RequireTool("unity_open_mcp_cinemachine_brain_ensure");
            Host("IdRt_Camera", typeof(Camera));
            var ensure = Dispatch("unity_open_mcp_cinemachine_brain_ensure", "{\"path\":\"IdRt_Camera\"" + Hint + "}");
            if (ensure.ErrorCode == "cinemachine_package_required")
                Assert.Ignore("Cinemachine is not installed.");
            Assert.IsTrue(ensure.Success, ensure.ErrorMessage ?? ensure.Output);
            var id = AssertHostAndComponentIds(ensure.Output, "brain", "IdRt_Camera", "CinemachineBrain");

            var again = Dispatch("unity_open_mcp_cinemachine_brain_ensure", "{\"instance_id\":\"" + id + "\"" + Hint + "}");
            Assert.IsTrue(again.Success, again.ErrorMessage ?? again.Output);
            StringAssert.Contains("\"alreadyPresent\":true", again.Output);
        }

        // The atlas file holds the authoring SpriteAtlasAsset and the runtime
        // SpriteAtlas exists only once packed, so create returns no id (it
        // answered "instanceId":0) and the atlas tools take its path.
        [Test]
        public void SpriteAtlasCreate_ReturnsNoInstanceId_AndThePathWorks()
        {
            RequireTool("unity_open_mcp_spriteatlas_create");
            var atlas = $"Assets/IdRt_{System.Guid.NewGuid():N}.spriteatlas";
            try
            {
                var create = Dispatch("unity_open_mcp_spriteatlas_create",
                    "{\"asset_path\":\"" + atlas + "\",\"paths_hint\":[\"" + atlas + "\"]}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                StringAssert.DoesNotContain("instanceId", create.Output);
                StringAssert.Contains("\"path\":\"" + atlas + "\"", create.Output);

                var get = Dispatch("unity_open_mcp_spriteatlas_get", "{\"asset_path\":\"" + atlas + "\"}");
                Assert.IsTrue(get.Success, get.ErrorMessage ?? get.Output);
            }
            finally
            {
                AssetDatabase.DeleteAsset(atlas);
            }
        }
    }
}
