using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ScenesToolsTests
    {
        // ----------------------- Create ----------------------------------

        [Test]
        public void Create_MissingPath_ReturnsMissingParameter()
        {
            var result = ScenesTools.Create("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'path'", result.ErrorMessage);
        }

        [Test]
        public void Create_BadExtension_ReturnsInvalidParameter()
        {
            var result = ScenesTools.Create("{\"path\":\"Assets/Scenes/Foo.unity2\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
            StringAssert.Contains("'.unity'", result.ErrorMessage);
        }

        [Test]
        public void Create_EmptyPath_ReturnsMissingParameter()
        {
            var result = ScenesTools.Create("{\"path\":\"  \"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // ----------------------- Open ------------------------------------

        [Test]
        public void Open_MissingPath_ReturnsMissingParameter()
        {
            var result = ScenesTools.Open("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Open_BadExtension_ReturnsInvalidParameter()
        {
            var result = ScenesTools.Open("{\"path\":\"Assets/Foo.txt\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
        }

        [Test]
        public void Open_NonExistentFile_ReturnsSceneNotFound()
        {
            // File does not exist on disk — fail fast before reaching OpenScene.
            var result = ScenesTools.Open("{\"path\":\"Assets/__MCPTest_DoesNotExist.unity\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        // ----------------------- Save ------------------------------------

        [Test]
        public void Save_BadDestinationExtension_ReturnsInvalidParameter()
        {
            // Active scene read needs the active scene to be valid; in EditMode
            // it is. We supply a bad-extension destination so the validator
            // trips before any save attempt.
            var result = ScenesTools.Save("{\"path\":\"Assets/Foo.txt\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
        }

        [Test]
        public void Save_UnknownSceneName_ReturnsSceneNotFound()
        {
            var result = ScenesTools.Save("{\"name\":\"__MCPTest_NoSuchScene\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        // ----------------------- Unload ----------------------------------

        [Test]
        public void Unload_MissingName_ReturnsMissingParameter()
        {
            var result = ScenesTools.Unload("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Unload_UnknownName_ReturnsSceneNotFound()
        {
            var result = ScenesTools.Unload("{\"name\":\"__MCPTest_NoSuchScene\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        // ----------------------- SetActive -------------------------------

        [Test]
        public void SetActive_MissingName_ReturnsMissingParameter()
        {
            var result = ScenesTools.SetActive("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SetActive_UnknownName_ReturnsSceneNotFound()
        {
            var result = ScenesTools.SetActive("{\"name\":\"__MCPTest_NoSuchScene\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        // ----------------------- ListOpened (read) -----------------------

        [Test]
        public void ListOpened_ReturnsOkEnvelopeWithScenesArray()
        {
            var result = ScenesTools.ListOpened("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Always emits the action + scenes array + active scene pointer.
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"action\":\"list_opened\"", result.Output);
            StringAssert.Contains("\"scenes\":[", result.Output);
            StringAssert.Contains("\"activeScene\":", result.Output);
            StringAssert.Contains("\"openedSceneCount\":", result.Output);
        }

        // ----------------------- GetDirtySummary (read) ------------------

        [Test]
        public void GetDirtySummary_ReturnsOkEnvelopeWithScenesArray()
        {
            var result = ScenesTools.GetDirtySummary("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"scenes\":[", result.Output);
            StringAssert.Contains("\"dirtySceneCount\":", result.Output);
            StringAssert.Contains("\"openedSceneCount\":", result.Output);
        }

        // ----------------------- GetData (read) --------------------------

        [Test]
        public void GetData_UnknownSceneName_ReturnsSceneNotFound()
        {
            var result = ScenesTools.GetData("{\"name\":\"__MCPTest_NoSuchScene\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        [Test]
        public void GetData_ActiveScene_SummaryMode_ReturnsOverview()
        {
            var result = ScenesTools.GetData("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Summary mode emits the scene overview + roots roster + caps.
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"scene\":{", result.Output);
            StringAssert.Contains("\"detail\":\"summary\"", result.Output);
            StringAssert.Contains("\"roots\":[", result.Output);
            StringAssert.Contains("\"moreHidden\":[", result.Output);
            StringAssert.Contains("\"truncated\":", result.Output);
        }

        [Test]
        public void GetData_WithTempRoot_EmitsRootInSummary()
        {
            // Spin up a GameObject in the active scene so the summary has a
            // root to report. EditMode's active scene accepts new instances.
            var go = new GameObject("__MCPTest_SceneDataRoot");
            try
            {
                var result = ScenesTools.GetData("{\"detail\":\"summary\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("__MCPTest_SceneDataRoot", result.Output);
                // Summary emits childCount + components but NOT children[].
                StringAssert.Contains("\"childCount\":0", result.Output);
                StringAssert.Contains("\"components\":[", result.Output);
                // Summary must NOT recurse into children array.
                Assert.IsFalse(result.Output.Contains("\"children\":["),
                    "summary detail should not emit nested children: " + result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetData_NormalMode_EmitsNestedChildrenAndActiveFlag()
        {
            var parent = new GameObject("__MCPTest_SceneDataParent");
            var child = new GameObject("__MCPTest_SceneDataChild");
            child.transform.SetParent(parent.transform, false);
            try
            {
                var result = ScenesTools.GetData("{\"detail\":\"normal\",\"depth\":3}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"detail\":\"normal\"", result.Output);
                StringAssert.Contains("__MCPTest_SceneDataChild", result.Output);
                // Normal mode emits active/tag/layer per node.
                StringAssert.Contains("\"active\":", result.Output);
                StringAssert.Contains("\"tag\":", result.Output);
                StringAssert.Contains("\"layer\":", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void GetData_VerboseMode_EmitsInstanceIdAndTransform()
        {
            var go = new GameObject("__MCPTest_SceneDataVerbose");
            try
            {
                var result = ScenesTools.GetData("{\"detail\":\"verbose\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"detail\":\"verbose\"", result.Output);
                StringAssert.Contains("\"instanceId\":", result.Output);
                StringAssert.Contains("\"transform\":", result.Output);
                StringAssert.Contains("\"position\":", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetData_DepthZero_HidesChildrenAndReportsMoreHidden()
        {
            var parent = new GameObject("__MCPTest_SceneDataDepthParent");
            var child = new GameObject("__MCPTest_SceneDataDepthChild");
            child.transform.SetParent(parent.transform, false);
            try
            {
                var result = ScenesTools.GetData("{\"detail\":\"normal\",\"depth\":0}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                // depth:0 means roots only — the child must not appear in the
                // emitted tree, but the parent's hidden children are reported.
                Assert.IsFalse(result.Output.Contains("\"children\":["),
                    "depth:0 should not emit children arrays: " + result.Output);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void GetData_MaxNodesCap_TruncatesAndReportsCount()
        {
            // Create more roots than the cap so the truncation path fires.
            var created = new System.Collections.Generic.List<GameObject>();
            try
            {
                for (int i = 0; i < 5; i++)
                    created.Add(new GameObject("__MCPTest_SceneDataCap_" + i));
                var result = ScenesTools.GetData("{\"detail\":\"summary\",\"max_nodes\":2}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                // The active scene may carry pre-existing roots (Camera, etc.),
                // so we assert the cap dropped *at least* the 5 created roots
                // beyond the cap, not an exact count.
                StringAssert.Contains("\"truncated\":", result.Output);
                Assert.IsTrue(result.Output.Contains("\"rootCount\""),
                    "output must report rootCount");
                // At least 3 roots beyond a cap of 2 must be dropped.
                var truncated = ExtractInt(result.Output, "\"truncated\":");
                Assert.GreaterOrEqual(truncated, 3,
                    $"truncated count ({truncated}) must be >= 3 with 5 extra roots and a cap of 2");
            }
            finally
            {
                foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            }
        }

        // ----------------------- Focus -----------------------------------

        [Test]
        public void Focus_NoTarget_ReturnsGameObjectNotFound()
        {
            var result = ScenesTools.Focus("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("gameobject_not_found", result.ErrorCode);
        }

        [Test]
        public void Focus_TargetNotFound_ReturnsGameObjectNotFound()
        {
            var result = ScenesTools.Focus("{\"name\":\"__MCPTest_NoSuchGO\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("gameobject_not_found", result.ErrorCode);
        }

        // ----------------------- SceneView camera ------------------------

        [Test]
        public void SceneViewGetCamera_ReturnsPoseEnvelope()
        {
            var result = ScenesTools.SceneViewGetCamera("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"camera\":{", result.Output);
            StringAssert.Contains("\"position\":", result.Output);
            StringAssert.Contains("\"rotationEuler\":", result.Output);
            StringAssert.Contains("\"pivot\":", result.Output);
            StringAssert.Contains("\"orthographic\":", result.Output);
            StringAssert.Contains("\"size\":", result.Output);
            StringAssert.Contains("\"fieldOfView\":", result.Output);
            StringAssert.Contains("\"windowMoved\":false", result.Output);
        }

        [Test]
        public void SceneViewSetCamera_SetThenGet_RoundTripsPoseAndDoesNotDirtyScene()
        {
            var sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            Assert.IsNotNull(sceneView);

            var active = EditorSceneManager.GetActiveScene();
            var wasDirty = active.isDirty;

            var setResult = ScenesTools.SceneViewSetCamera(
                "{\"position\":{\"x\":3.25,\"y\":4.5,\"z\":-6.75}," +
                "\"rotation\":{\"x\":15,\"y\":30,\"z\":0}," +
                "\"orthographic\":true,\"size\":9.5}");
            Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
            StringAssert.Contains("\"windowMoved\":true", setResult.Output);
            StringAssert.Contains("\"orthographic\":true", setResult.Output);
            StringAssert.Contains("\"size\":9.5", setResult.Output);

            var getResult = ScenesTools.SceneViewGetCamera("{}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"orthographic\":true", getResult.Output);
            StringAssert.Contains("\"camera\":{", getResult.Output);

            Assert.AreEqual(wasDirty, EditorSceneManager.GetActiveScene().isDirty,
                "sceneview_set_camera should not dirty the active scene");
        }

        // ----------------------- Dispatch wiring -------------------------
        //
        // The dispatch switch lives in BridgeHttpServer; the KnownTools /
        // DirectResponseTools / MutatingTools classification sets live in
        // BridgeToolCatalog (aliased into BridgeHttpServer). We assert the
        // static membership contracts so a future edit that forgets to wire a
        // new scene tool fails loudly here, not silently at runtime.

        [Test]
        public void DirtyGuard_PreflightsSceneOpen_NotOtherSceneMutators()
        {
            // scene_open is RestartThenSettle: the dirty guard preflights it
            // (Single-mode open can lose unsaved changes). The other scene
            // mutators are EditorSettle — no dirty-scene refusal.
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_open", "{}"),
                "scene_open must be guarded (RestartThenSettle lifecycle)");
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_create", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_save", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_unload", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_set_active", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_focus", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_sceneview_set_camera", "{}"));
            // Read-only scene tools are never guarded.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_list_opened", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_get_data", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_get_dirty_summary", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_sceneview_get_camera", "{}"));
        }

        [Test]
        public void DirtyGuard_SceneOpen_IgnoreSceneDirtyOptOut()
        {
            // The ignore_scene_dirty flag is the explicit opt-out for scene_open.
            Assert.IsFalse(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_open",
                    "{\"ignore_scene_dirty\":true}"));
        }

        [Test]
        public void ToolLifecycle_SceneOpenIsRestartThenSettle_OthersEditorSettle()
        {
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_open"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_create"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_save"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_unload"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_set_active"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scene_focus"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_sceneview_set_camera"));
            // Read-only tools resolve to None (safe default).
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_scene_list_opened"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_scene_get_data"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_scene_get_dirty_summary"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_sceneview_get_camera"));
        }

        [Test]
        public void Classification_SceneViewCameraTools_AreWiredAsExpected()
        {
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_sceneview_get_camera"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_sceneview_set_camera"));

            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_sceneview_get_camera"));
            Assert.IsFalse(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_sceneview_set_camera"));

            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_sceneview_set_camera"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_sceneview_get_camera"));
        }

        // Pull the integer following a JSON key token from a JSON string body.
        private static int ExtractInt(string json, string key)
        {
            var idx = json.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return -1;
            var start = idx + key.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return int.TryParse(json.Substring(start, end - start), out var v) ? v : -1;
        }
    }
}
