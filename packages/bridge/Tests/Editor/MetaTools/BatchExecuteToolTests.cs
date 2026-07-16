using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    // M27 Plan 4 — BatchExecuteTool unit tests.
    //
    // Covers the Done-When items from execution-plan-4 T27.4.2:
    //   - happy path (3× gameobject_create)
    //   - fail_fast abort (skipped tail)
    //   - over-limit rejection
    //   - deny-listed nested tool
    //   - fail_fast:false collects all per-step errors
    //
    // These call BatchExecuteTool.Execute(body) directly (no HTTP / queue), the
    // same pattern as GameObjectsToolsTests. The batch-level gate cycle
    // (BatchExecuteGateRunner) is exercised by the integration tests; here we
    // assert the per-step envelope shape + dispatch reuse.
    [TestFixture]
    public class BatchExecuteToolTests
    {
        private const string CleanupPrefix = "__MCPTest_Batch_";

        [TearDown]
        public void TearDown()
        {
            // Destroy any GameObjects the batch steps created so the test
            // scene stays clean between runs.
            DestroyAllPrefixed();
        }

        // -------------------------------------------------------------------
        // Validation: missing / empty commands array
        // -------------------------------------------------------------------

        [Test]
        public void Execute_MissingCommands_ReturnsMissingParameter()
        {
            var result = BatchExecuteTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'commands'", result.ErrorMessage);
        }

        [Test]
        public void Execute_EmptyCommands_ReturnsMissingParameter()
        {
            var result = BatchExecuteTool.Execute("{\"commands\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // -------------------------------------------------------------------
        // Over-limit rejection
        // -------------------------------------------------------------------

        [Test]
        public void Execute_OverLimit_ReturnsBatchTooManyCommands()
        {
            // Build a commands array that exceeds the HARD MAX (100) so the test
            // is deterministic regardless of the project's batchExecuteMaxCommands
            // setting (which could be raised up to 100 in settings.json).
            var cmds = new List<string>();
            for (int i = 0; i < 101; i++)
            {
                cmds.Add("{\"tool\":\"unity_open_mcp_gameobject_find\",\"params\":{}}");
            }
            var body = "{\"commands\":[" + string.Join(",", cmds) + "]}";

            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_too_many_commands", result.ErrorCode);
            StringAssert.Contains("limit", result.ErrorMessage);
        }

        // -------------------------------------------------------------------
        // Deny-listed nested tool
        // -------------------------------------------------------------------

        [Test]
        public void Execute_DenyListedNestedTool_ReturnsBatchToolNotInvokable()
        {
            // batch_execute cannot be nested inside itself.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_batch_execute\",\"params\":{}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_tool_not_invokable", result.ErrorCode);
        }

        [Test]
        public void Execute_DenyListedPowerTool_ReturnsBatchToolNotInvokable()
        {
            // compile_check is headless-only and blocked in v1.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_compile_check\",\"params\":{}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_tool_not_invokable", result.ErrorCode);
        }

        // -------------------------------------------------------------------
        // T5.2 — RestartThenSettle lifecycle tools are denied as nested steps
        // (domain reload / scene switch would silently abort remaining steps).
        // -------------------------------------------------------------------

        [Test]
        public void Execute_PackageAdd_RefusedAsNestedReloadUnsafe()
        {
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_package_add\",\"params\":{\"package_id\":\"com.unity.test\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("package_add", result.ErrorMessage);
            StringAssert.Contains("restart_then_settle", result.ErrorMessage);
        }

        [Test]
        public void Execute_SceneOpen_RefusedAsNestedReloadUnsafe()
        {
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_scene_open\",\"params\":{\"path\":\"Assets/Foo.unity\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("scene_open", result.ErrorMessage);
        }

        [Test]
        public void Execute_AsmdefModify_RefusedAsNestedReloadUnsafe()
        {
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_asmdef_modify\",\"params\":{\"asset_path\":\"Assets/Foo.asmdef\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("asmdef_modify", result.ErrorMessage);
        }

        [Test]
        public void Execute_ExecuteCsharp_RefusedAsNestedReloadUnsafe()
        {
            // execute_csharp is RestartThenSettle — now denied via the lifecycle
            // path (batch_nested_reload_unsafe), not the old explicit deny-list.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_execute_csharp\",\"params\":{\"code\":\"return 1;\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("execute_csharp", result.ErrorMessage);
        }

        [Test]
        public void Execute_ReloadUnsafe_NamesTheOffendingStepIndex()
        {
            // A batch with a safe step before the unsafe one — the error must
            // name commands[1] (the offending step).
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_find\",\"params\":{}}," +
                       "{\"tool\":\"unity_open_mcp_package_remove\",\"params\":{\"package_id\":\"x\"}}" +
                       "]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("commands[1]", result.ErrorMessage);
        }

        // -------------------------------------------------------------------
        // Review follow-up — scene_create is EditorSettle (no domain reload),
        // so IsNestedReloadUnsafe does NOT catch it. But its default Single
        // mode replaces the active scene stack. The param-aware guard refuses
        // Single/default scene_create; additive is allowed.
        // -------------------------------------------------------------------

        [Test]
        public void Execute_SceneCreate_DefaultMode_RefusedAsNestedReloadUnsafe()
        {
            // No mode → defaults to Single → replaces the scene stack.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_scene_create\",\"params\":{\"path\":\"Assets/__MCPTest_Scene.unity\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("scene_create", result.ErrorMessage);
            StringAssert.Contains("Single mode", result.ErrorMessage);
        }

        [Test]
        public void Execute_SceneCreate_SingleMode_RefusedAsNestedReloadUnsafe()
        {
            // Explicit Single mode is refused for the same reason.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_scene_create\",\"params\":{\"path\":\"Assets/__MCPTest_Scene.unity\",\"mode\":\"single\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_nested_reload_unsafe", result.ErrorCode);
            StringAssert.Contains("scene_create", result.ErrorMessage);
        }

        [Test]
        public void Execute_SceneCreate_AdditiveMode_NotRefusedBySceneStackGuard()
        {
            // Additive mode preserves the scene stack and must pass the
            // scene-stack guard. It may still fail later for unrelated
            // asset-path reasons, but it must NOT fail with the scene-stack
            // batch_nested_reload_unsafe error.
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_scene_create\",\"params\":{\"path\":\"Assets/__MCPTest_Scene_Additive.unity\",\"mode\":\"additive\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            // The scene-stack guard specifically must not have fired.
            Assert.AreNotEqual("batch_nested_reload_unsafe", result.ErrorCode,
                "Additive scene_create must not be refused by the scene-stack guard. " +
                "Got: " + result.ErrorCode + " — " + result.ErrorMessage);
        }

        // -------------------------------------------------------------------
        // Happy path: 3× gameobject_create
        // -------------------------------------------------------------------

        [Test]
        public void Execute_ThreeGameObjects_AllSucceed()
        {
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "A\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "B\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "C\"}}" +
                       "]}";

            var result = BatchExecuteTool.Execute(body);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Batch envelope shape.
            StringAssert.Contains("\"batch\":{", result.Output);
            StringAssert.Contains("\"callSuccessCount\":3", result.Output);
            StringAssert.Contains("\"callFailureCount\":0", result.Output);
            // All three statuses are success.
            Assert.AreEqual(3, Regex.Matches(result.Output, "\"status\":\"success\"").Count);
            // The GameObjects were actually created.
            Assert.IsNotNull(GameObject.Find(CleanupPrefix + "A"));
            Assert.IsNotNull(GameObject.Find(CleanupPrefix + "B"));
            Assert.IsNotNull(GameObject.Find(CleanupPrefix + "C"));
        }

        // -------------------------------------------------------------------
        // fail_fast: true (default) — stops on first failure, rest skipped
        // -------------------------------------------------------------------

        [Test]
        public void Execute_FailFastTrue_StopsOnFirstFailure()
        {
            // Step 0 succeeds; step 1 fails (empty name → missing_parameter);
            // step 2 must be skipped (not executed).
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "OK\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "Skipped\"}}" +
                       "],\"fail_fast\":true}";

            var result = BatchExecuteTool.Execute(body);
            // Batch-level success is false because one step failed.
            Assert.IsFalse(result.Success);
            StringAssert.Contains("\"callSuccessCount\":1", result.Output);
            StringAssert.Contains("\"callFailureCount\":1", result.Output);
            // Step 1 failed.
            StringAssert.Contains("\"status\":\"failed\"", result.Output);
            StringAssert.Contains("\"missing_parameter\"", result.Output);
            // Step 2 was skipped (not created).
            StringAssert.Contains("\"status\":\"skipped\"", result.Output);
            Assert.IsNull(GameObject.Find(CleanupPrefix + "Skipped"));
        }

        // -------------------------------------------------------------------
        // fail_fast: false — runs every step, collects per-step errors
        // -------------------------------------------------------------------

        [Test]
        public void Execute_FailFastFalse_RunsAllAndCollectsErrors()
        {
            // Step 0 fails; step 1 succeeds; step 2 fails. With fail_fast:false
            // all three run and we see 2 failures + 1 success.
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "OK2\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"\"}}" +
                       "],\"fail_fast\":false}";

            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("\"callSuccessCount\":1", result.Output);
            StringAssert.Contains("\"callFailureCount\":2", result.Output);
            // No skipped entries — every step ran.
            Assert.AreEqual(0, Regex.Matches(result.Output, "\"status\":\"skipped\"").Count);
            Assert.AreEqual(2, Regex.Matches(result.Output, "\"status\":\"failed\"").Count);
            // The middle step's GameObject was created despite surrounding failures.
            Assert.IsNotNull(GameObject.Find(CleanupPrefix + "OK2"));
        }

        // -------------------------------------------------------------------
        // Per-step output is present on success
        // -------------------------------------------------------------------

        [Test]
        public void Execute_SuccessStep_IncludesOutput()
        {
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "Out\"}}}" +
                       "]}";

            var result = BatchExecuteTool.Execute(body);
            Assert.IsTrue(result.Success);
            // The success step carries the nested tool's output (instanceId etc.).
            StringAssert.Contains("\"output\":{", result.Output);
            StringAssert.Contains("\"instanceId\":", result.Output);
        }

        // -------------------------------------------------------------------
        // T1.1 — BatchExecuteGateRunner collapses the whole batch into ONE undo
        // step. Pre-fix the collapse passed groupAfter (the latest group), which
        // collapses nothing; post-fix it passes groupBefore so a single Ctrl+Z
        // reverts the entire batch.
        // -------------------------------------------------------------------

        [Test]
        public void GateRunner_TwoSteps_CollapseToSingleUndoEntry()
        {
            // Drive the runner with gate:"off" so the gate cycle short-circuits
            // and we isolate the undo-group behavior. The runner's finally block
            // (the collapse) runs regardless of the gate decision.
            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "UndoA\"}}," +
                       "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + CleanupPrefix + "UndoB\"}}" +
                       "]}";

            GameObject objA = null, objB = null;
            try
            {
                var result = BatchExecuteGateRunner.Execute(body, "off",
                    new[] { "Assets/__BatchUndoTest.unity" });
                Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);

                objA = GameObject.Find(CleanupPrefix + "UndoA");
                objB = GameObject.Find(CleanupPrefix + "UndoB");
                Assert.IsNotNull(objA, "step 1 must create UndoA");
                Assert.IsNotNull(objB, "step 2 must create UndoB");

                // A single undo must revert the WHOLE batch (both creates),
                // proving the per-step undos were collapsed into one entry.
                Undo.PerformUndo();

                Assert.IsNull(GameObject.Find(CleanupPrefix + "UndoA"),
                    "one undo must revert UndoA (collapsed batch)");
                Assert.IsNull(GameObject.Find(CleanupPrefix + "UndoB"),
                    "one undo must revert UndoB (collapsed batch)");

                // Mark null so TearDown / finally does not double-destroy.
                objA = null;
                objB = null;
            }
            finally
            {
                if (objA != null) Object.DestroyImmediate(objA);
                if (objB != null) Object.DestroyImmediate(objB);
            }
        }

        [Test]
        public void GateRunner_NoOpBatch_DoesNotCollapse()
        {
            // A batch that produces NO undo entries (read-only find) must not
            // call CollapseUndoOperations — groupAfter == groupBefore, and the
            // guard skips the collapse. We assert the guard holds by checking
            // the undo group count does not decrease after the runner returns.
            int groupBefore = Undo.GetCurrentGroup();

            var body = "{\"commands\":[" +
                       "{\"tool\":\"unity_open_mcp_gameobject_find\",\"params\":{\"name\":\"" + CleanupPrefix + "NoopFind\"}}" +
                       "]}";

            var result = BatchExecuteGateRunner.Execute(body, "off",
                new[] { "Assets/__BatchUndoTest.unity" });
            // Find on a non-existent name returns success with notFound.
            Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);

            int groupAfter = Undo.GetCurrentGroup();
            // A no-op batch (no Undo.RegisterCreatedObjectUndo / RecordObject)
            // leaves the group index unchanged → no collapse.
            Assert.AreEqual(groupBefore, groupAfter,
                "a read-only batch must not advance or collapse the undo group");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private void DestroyAllPrefixed()
        {
            var all = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude);
            foreach (var go in all)
            {
                if (go != null && go.name != null && go.name.StartsWith(CleanupPrefix))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }
    }
}
