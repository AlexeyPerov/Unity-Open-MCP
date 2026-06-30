using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class EditorConsoleSelectionToolsTests
    {
        private const string CleanupTag = "__MCPTest_Plan5_Tag";
        private const string CleanupLayer = "__MCPTest_Plan5_Layer";

        [TearDown]
        public void TearDown()
        {
            // Defensive cleanup: never leave a test-added tag/layer behind.
            RemoveTagIfPresent(CleanupTag);
            RemoveLayerIfPresent(CleanupLayer);
            Selection.objects = new Object[0];
        }

        // ----------------------- console_clear ---------------------------

        [Test]
        public void ConsoleClear_ReturnsOkEnvelope()
        {
            var result = EditorConsoleSelectionTools.ConsoleClear("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"cleared\":", result.Output);
        }

        // ----------------------- console_log -----------------------------

        [Test]
        public void ConsoleLog_MissingMessage_ReturnsMissingParameter()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void ConsoleLog_EmptyMessage_ReturnsMissingParameter()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog("{\"message\":\"\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void ConsoleLog_Log_EmitsOkWithLevel()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog(
                "{\"message\":\"plan5 test log\",\"level\":\"log\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"logged\":true", result.Output);
            StringAssert.Contains("\"level\":\"log\"", result.Output);
            StringAssert.Contains("plan5 test log", result.Output);
        }

        [Test]
        public void ConsoleLog_WarningLevel_EmitsWarning()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog(
                "{\"message\":\"plan5 test warn\",\"level\":\"warning\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"level\":\"warning\"", result.Output);
        }

        [Test]
        public void ConsoleLog_UnknownLevel_FallsBackToLog()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog(
                "{\"message\":\"plan5 fallback\",\"level\":\"bogus\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"level\":\"log\"", result.Output);
        }

        [Test]
        public void ConsoleLog_BadAssetContext_ReturnsContextNotFound()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog(
                "{\"message\":\"x\",\"context_asset_path\":\"Assets/__MCPTest_DoesNotExist.mat\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("context_not_found", result.ErrorCode);
        }

        [Test]
        public void ConsoleLog_BadInstanceIdContext_ReturnsContextNotFound()
        {
            var result = EditorConsoleSelectionTools.ConsoleLog(
                "{\"message\":\"x\",\"context_instance_id\":-999999}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("context_not_found", result.ErrorCode);
        }

        // ----------------------- editor_set_state ------------------------

        [Test]
        public void EditorSetState_MissingState_ReturnsInvalidParameter()
        {
            var result = EditorConsoleSelectionTools.EditorSetState("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
        }

        [Test]
        public void EditorSetState_UnknownState_ReturnsInvalidParameter()
        {
            var result = EditorConsoleSelectionTools.EditorSetState("{\"state\":\"bogus\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
            StringAssert.Contains("play, pause, stop", result.ErrorMessage);
        }

        [Test]
        public void EditorSetState_StopWhenNotPlaying_ReturnsNoop()
        {
            // EditMode is never in play mode — stop is a clean idempotent noop.
            var result = EditorConsoleSelectionTools.EditorSetState("{\"state\":\"stop\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"stop_noop\"", result.Output);
        }

        [Test]
        public void EditorSetState_PauseWhenNotPlaying_ReturnsNoop()
        {
            var result = EditorConsoleSelectionTools.EditorSetState("{\"state\":\"pause\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"pause_noop\"", result.Output);
        }

        // ----------------------- selection_get ---------------------------

        [Test]
        public void SelectionGet_EmptySelection_ReturnsOkWithNullActive()
        {
            Selection.objects = new Object[0];
            var result = EditorConsoleSelectionTools.SelectionGet("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"active\":null", result.Output);
            StringAssert.Contains("\"selection\":[]", result.Output);
            StringAssert.Contains("\"count\":0", result.Output);
        }

        [Test]
        public void SelectionGet_WithTempObject_ReportsItInArray()
        {
            var go = new GameObject("__MCPTest_SelectionGet");
            try
            {
                Selection.objects = new Object[] { go };
                var result = EditorConsoleSelectionTools.SelectionGet("{}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("__MCPTest_SelectionGet", result.Output);
                StringAssert.Contains("\"isAsset\":false", result.Output);
                StringAssert.Contains("\"path\":", result.Output);
                StringAssert.Contains("\"count\":1", result.Output);
                StringAssert.Contains("\"total\":1", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectionGet_RespectsMaxResultsAndReportsTruncation()
        {
            var created = new System.Collections.Generic.List<GameObject>();
            try
            {
                for (int i = 0; i < 3; i++)
                    created.Add(new GameObject("__MCPTest_SelectionCap_" + i));
                Selection.objects = created.ToArray();
                var result = EditorConsoleSelectionTools.SelectionGet("{\"max_results\":1}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"count\":1", result.Output);
                StringAssert.Contains("\"total\":3", result.Output);
                StringAssert.Contains("\"truncated\":2", result.Output);
            }
            finally
            {
                foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            }
        }

        // ----------------------- selection_set ---------------------------

        [Test]
        public void SelectionSet_ClearShortcut_ClearsSelection()
        {
            var go = new GameObject("__MCPTest_SelectionClear");
            try
            {
                Selection.objects = new Object[] { go };
                var result = EditorConsoleSelectionTools.SelectionSet("{\"clear\":true}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"action\":\"cleared\"", result.Output);
                Assert.AreEqual(0, Selection.objects.Length);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectionSet_NoResolverFields_ClearsSelection()
        {
            // No target fields at all = clear (empty array), not an error.
            var result = EditorConsoleSelectionTools.SelectionSet("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"cleared\"", result.Output);
        }

        [Test]
        public void SelectionSet_ByName_ResolvesAndSelects()
        {
            var go = new GameObject("__MCPTest_SelectionByName");
            try
            {
                var result = EditorConsoleSelectionTools.SelectionSet(
                    "{\"name\":\"__MCPTest_SelectionByName\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"action\":\"set\"", result.Output);
                Assert.AreEqual(1, Selection.objects.Length);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectionSet_BadName_ReturnsTargetNotFound()
        {
            var result = EditorConsoleSelectionTools.SelectionSet(
                "{\"name\":\"__MCPTest_DoesNotExist\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        [Test]
        public void SelectionSet_BadAssetPath_ReturnsTargetNotFound()
        {
            var result = EditorConsoleSelectionTools.SelectionSet(
                "{\"asset_path\":\"Assets/__MCPTest_DoesNotExist.prefab\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        [Test]
        public void SelectionSet_MultiTarget_PartialFailure_ReportsNotFound()
        {
            // One resolvable + one not → ResolveTargets filters nulls, but the
            // contract is: if NOTHING resolved, fail. With a good target, the
            // call succeeds and only the good one is selected.
            var go = new GameObject("__MCPTest_SelectionMulti");
            try
            {
                var body = "{\"targets\":[" +
                    "{\"name\":\"__MCPTest_SelectionMulti\"}," +
                    "{\"name\":\"__MCPTest_DoesNotExist\"}]}";
                var result = EditorConsoleSelectionTools.SelectionSet(body);
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(1, Selection.objects.Length);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SelectionSet_AllTargetsUnresolvable_ReturnsTargetNotFound()
        {
            var body = "{\"targets\":[" +
                "{\"name\":\"__MCPTest_NoA\"},{\"name\":\"__MCPTest_NoB\"}]}";
            var result = EditorConsoleSelectionTools.SelectionSet(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        // ----------------------- editor_undo / redo ----------------------

        [Test]
        public void EditorUndo_ReturnsOkWithSteps()
        {
            var result = EditorConsoleSelectionTools.EditorUndo("{\"steps\":1}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"undo\"", result.Output);
            StringAssert.Contains("\"steps\":1", result.Output);
            StringAssert.Contains("\"activeSelection\":", result.Output);
        }

        [Test]
        public void EditorRedo_ReturnsOkWithSteps()
        {
            var result = EditorConsoleSelectionTools.EditorRedo("{\"steps\":2}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"redo\"", result.Output);
            StringAssert.Contains("\"steps\":2", result.Output);
        }

        [Test]
        public void EditorUndo_ZeroSteps_CoercesToOne()
        {
            var result = EditorConsoleSelectionTools.EditorUndo("{\"steps\":0}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"steps\":1", result.Output);
        }

        [Test]
        public void EditorUndoHistory_ReturnsRecentEntriesAndTruncationFields()
        {
            // Create at least one undo record to make history non-empty.
            var go = new GameObject("__MCPTest_UndoHistory");
            Undo.RegisterCreatedObjectUndo(go, "Create __MCPTest_UndoHistory");
            try
            {
                var result = EditorConsoleSelectionTools.EditorUndoHistory("{\"max_entries\":1}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"status\":\"ok\"", result.Output);
                StringAssert.Contains("\"entries\":[", result.Output);
                StringAssert.Contains("\"requested\":1", result.Output);
                StringAssert.Contains("\"cap\":50", result.Output);
                StringAssert.Contains("\"truncated\":", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void EditorClearHistory_ClearsThenHistoryReportsEmpty()
        {
            // Seed one record so clear has something to remove.
            var go = new GameObject("__MCPTest_ClearHistory");
            Undo.RegisterCreatedObjectUndo(go, "Create __MCPTest_ClearHistory");
            Object.DestroyImmediate(go);

            var clear = EditorConsoleSelectionTools.EditorClearHistory("{}");
            Assert.IsTrue(clear.Success, clear.ErrorMessage);
            StringAssert.Contains("\"cleared\":true", clear.Output);

            var history = EditorConsoleSelectionTools.EditorUndoHistory("{\"max_entries\":5}");
            Assert.IsTrue(history.Success, history.ErrorMessage);
            StringAssert.Contains("\"count\":0", history.Output);
            StringAssert.Contains("\"total\":0", history.Output);
        }

        // ----------------------- editor_get_tags / layers ----------------

        [Test]
        public void EditorGetTags_ReturnsOkWithBuiltIns()
        {
            var result = EditorConsoleSelectionTools.EditorGetTags("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"tags\":[", result.Output);
            // Built-ins are always present.
            StringAssert.Contains("\"Untagged\"", result.Output);
            StringAssert.Contains("\"MainCamera\"", result.Output);
        }

        [Test]
        public void EditorGetLayers_ReturnsOkWithBuiltInsAndIndices()
        {
            var result = EditorConsoleSelectionTools.EditorGetLayers("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"layers\":[", result.Output);
            // Default (slot 0) + UI (slot 5) are always present.
            StringAssert.Contains("{\"index\":0,\"name\":\"Default\"}", result.Output);
            StringAssert.Contains("\"index\":5", result.Output);
            StringAssert.Contains("\"UI\"", result.Output);
        }

        // ----------------------- editor_add_tag --------------------------

        [Test]
        public void EditorAddTag_MissingTag_ReturnsMissingParameter()
        {
            var result = EditorConsoleSelectionTools.EditorAddTag("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void EditorAddTag_ReservedTag_ReturnsReservedTag()
        {
            var result = EditorConsoleSelectionTools.EditorAddTag(
                "{\"tag\":\"MainCamera\",\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("reserved_tag", result.ErrorCode);
        }

        [Test]
        public void EditorAddTag_NewTag_AddsThenIdempotent()
        {
            // Add (saved:true) → add again (saved:false noop) → cleanup.
            var first = EditorConsoleSelectionTools.EditorAddTag(
                "{\"tag\":\"" + CleanupTag + "\",\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
            Assert.IsTrue(first.Success, first.ErrorMessage);
            StringAssert.Contains("\"saved\":true", first.Output);
            StringAssert.Contains("\"" + CleanupTag + "\"", first.Output);

            try
            {
                var second = EditorConsoleSelectionTools.EditorAddTag(
                    "{\"tag\":\"" + CleanupTag + "\",\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
                Assert.IsTrue(second.Success, second.ErrorMessage);
                StringAssert.Contains("\"saved\":false", second.Output);
                StringAssert.Contains("already exists", second.Output);
            }
            finally
            {
                RemoveTagIfPresent(CleanupTag);
            }
        }

        // ----------------------- editor_add_layer ------------------------

        [Test]
        public void EditorAddLayer_MissingLayer_ReturnsMissingParameter()
        {
            var result = EditorConsoleSelectionTools.EditorAddLayer("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void EditorAddLayer_ReservedLayer_ReturnsReservedLayer()
        {
            var result = EditorConsoleSelectionTools.EditorAddLayer(
                "{\"layer\":\"Water\",\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("reserved_layer", result.ErrorCode);
        }

        [Test]
        public void EditorAddLayer_ExplicitReservedSlot_ReturnsInvalidParameter()
        {
            // Slot 5 is reserved for UI; we refuse slots < 8.
            var result = EditorConsoleSelectionTools.EditorAddLayer(
                "{\"layer\":\"" + CleanupLayer + "\",\"slot\":5," +
                "\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
        }

        [Test]
        public void EditorAddLayer_NewLayer_AddsToUserSlotThenIdempotent()
        {
            var first = EditorConsoleSelectionTools.EditorAddLayer(
                "{\"layer\":\"" + CleanupLayer + "\",\"slot\":30," +
                "\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
            Assert.IsTrue(first.Success, first.ErrorMessage);
            StringAssert.Contains("\"saved\":true", first.Output);
            StringAssert.Contains("\"slot\":30", first.Output);

            try
            {
                var second = EditorConsoleSelectionTools.EditorAddLayer(
                    "{\"layer\":\"" + CleanupLayer + "\",\"slot\":30," +
                    "\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
                Assert.IsTrue(second.Success, second.ErrorMessage);
                StringAssert.Contains("\"saved\":false", second.Output);
                StringAssert.Contains("already exists", second.Output);
                StringAssert.Contains("\"slot\":30", second.Output);
            }
            finally
            {
                RemoveLayerIfPresent(CleanupLayer);
            }
        }

        [Test]
        public void EditorAddLayer_OccupiedSlot_ReturnsSlotOccupied()
        {
            // Place a layer in slot 31, then try to place a different name there.
            try
            {
                var setup = EditorConsoleSelectionTools.EditorAddLayer(
                    "{\"layer\":\"" + CleanupLayer + "\",\"slot\":31," +
                    "\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
                Assert.IsTrue(setup.Success, setup.ErrorMessage);

                var result = EditorConsoleSelectionTools.EditorAddLayer(
                    "{\"layer\":\"__MCPTest_OtherLayer\",\"slot\":31," +
                    "\"paths_hint\":[\"ProjectSettings/TagManager.asset\"]}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("slot_occupied", result.ErrorCode);
                RemoveLayerIfPresent("__MCPTest_OtherLayer");
            }
            finally
            {
                RemoveLayerIfPresent(CleanupLayer);
            }
        }

        // ----------------------- Dispatch wiring -------------------------

        // The dispatch switch + KnownTools / DirectResponseTools /
        // MutatingTools / ToolLifecycle table live in BridgeHttpServer /
        // ToolLifecycle. We assert the static membership contracts so a future
        // edit that forgets to wire a Plan 5 tool fails loudly here, not
        // silently at runtime.

        [Test]
        public void Lifecycle_AddTagAddLayerAreEditorSettle_OthersNone()
        {
            // add_tag / add_layer write TagManager.asset — EditorSettle (asset
            // refresh, no domain-reload risk).
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_editor_add_tag"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_editor_add_layer"));
            // The gate-free direct-response Plan 5 tools default to None.
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_console_clear"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_console_log"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_set_state"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_selection_get"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_selection_set"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_undo"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_redo"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_undo_history"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_clear_history"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_get_tags"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editor_get_layers"));
        }

        [Test]
        public void DirtyGuard_NeverPreflightsPlan5Tools()
        {
            // None of the Plan 5 tools are RestartThenSettle, so the dirty
            // guard never preflights them. editor_set_state runs its OWN inline
            // guard inside the handler (entering play mode can trigger the
            // native save modal) — that path is not exercised here.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_editor_add_tag", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_editor_add_layer", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_console_clear", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_editor_set_state", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_selection_set", "{}"));
        }

        [Test]
        public void Classification_UndoHistoryTools_AreWiredAsExpected()
        {
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_editor_undo_history"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_editor_clear_history"));

            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_editor_undo_history"));
            Assert.IsFalse(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_editor_clear_history"));

            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_editor_clear_history"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_editor_undo_history"));
        }

        // ----------------------- helpers ---------------------------------

        private static void RemoveTagIfPresent(string tag)
        {
            var so = new SerializedObject(
                AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
            var tagsProp = so.FindProperty("tags");
            if (tagsProp == null) return;
            for (int i = tagsProp.arraySize - 1; i >= 0; i--)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                    tagsProp.DeleteArrayElementAtIndex(i);
            }
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(
                AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
        }

        private static void RemoveLayerIfPresent(string layer)
        {
            var so = new SerializedObject(
                AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
            var layersProp = so.FindProperty("layers");
            if (layersProp == null) return;
            for (int i = 0; i < layersProp.arraySize; i++)
            {
                if (layersProp.GetArrayElementAtIndex(i).stringValue == layer)
                    layersProp.GetArrayElementAtIndex(i).stringValue = "";
            }
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(
                AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
        }
    }
}
