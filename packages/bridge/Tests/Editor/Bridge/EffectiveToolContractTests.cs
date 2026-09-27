using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class EffectiveToolContractTests
    {
        private const string Read = "{\"commands\":[{\"tool\":\"unity_open_mcp_editor_status\",\"params\":{}}]}";
        private const string Scope = "Assets/__MCPTest_EffectiveContract.mat";

        [MenuItem("Tools/Open MCP Test/Inspect"), BridgeReadOnlyMenu]
        public static void InspectMenu() { }

        // BatchExecuteTool.Preflight accepts a step only when the typed-tool
        // registry knows it. Production fills the registry at bridge startup,
        // which never runs in an isolated test session, so the batch tests
        // below passed only when an earlier class happened to scan first.
        // Scan here (leaving a registry another fixture already populated
        // untouched) so the class is order-independent.
        [OneTimeSetUp]
        public void EnsureTypedToolRegistry()
        {
            if (BridgeToolRegistry.Count == 0) BridgeToolRegistry.Scan();
        }

        [Test]
        public void VerifierMenu_ExactDeclaration_SkipsScopeAndLifecycle()
        {
            Assert.IsTrue(ExecuteMenuTool.IsReadOnlyMenu("Tools/Open MCP Test/Inspect"));
            Assert.IsTrue(ExecuteMenuTool.IsNonDisruptiveReadOnlyMenu("Tools/Open MCP Test/Inspect"));
            Assert.IsFalse(ExecuteMenuTool.IsReadOnlyMenu("Tools/Open MCP Test/Inspect/Fix"));
            Assert.IsFalse(ExecuteMenuTool.IsNonDisruptiveReadOnlyMenu("Assets/Refresh"));
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_menu", "{\"menu_path\":\"Edit/Play\"}"));
        }

        [Test]
        public void ReadOnlySnippet_DirtyScratchScene_DoesNotChangeSceneOrUndo()
        {
            var scene = SceneManager.GetActiveScene();
            bool wasDirty = scene.isDirty;
            int rootCount = scene.rootCount;
            try
            {
                EditorSceneManager.MarkSceneDirty(scene);
                var before = Undo.GetCurrentGroup();
                var body = "{\"read_only\":true,\"code\":\"return 42;\"}";
                Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp", body));
                Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp", "{\"code\":\"return 42;\"}"));
                var result = Dispatch("unity_open_mcp_execute_csharp", body);
                Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);
                Assert.IsTrue(result.EffectiveReadOnly);
                Assert.AreEqual("read_only", result.SkippedReason);
                Assert.IsNull(result.CheckpointId);
                Assert.IsTrue(scene.isDirty);
                Assert.AreEqual(rootCount, scene.rootCount);
                Assert.AreEqual(before, Undo.GetCurrentGroup());
            }
            finally
            {
                if (!wasDirty)
                    typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { scene });
            }
        }

        // read_only keeps its scope/gate/settle waiver, but the assertion is
        // unverifiable, so every snippet conflicts with a running project job.
        [Test]
        public void ReadOnlySnippet_WaivesTheGateButStillConflictsWithAProjectJob()
        {
            const string tool = "unity_open_mcp_execute_csharp";
            const string snippet = "{\"read_only\":true,\"code\":\"UnityEditor.EditorPrefs.SetInt(\\\"k\\\", 1); return 42;\"}";
            var read = EffectiveToolContract.Resolve(tool, snippet);
            Assert.IsFalse(read.IsMutating);
            Assert.AreEqual(LifecyclePolicy.None, read.Lifecycle);
            Assert.IsTrue(read.ConflictsWithJob);

            var batch = EffectiveToolContract.Resolve("unity_open_mcp_batch_execute",
                "{\"commands\":[{\"tool\":\"" + tool + "\",\"params\":" + snippet + "}]}");
            Assert.IsNull(batch.Refusal, batch.Refusal?.ErrorMessage);
            Assert.IsFalse(batch.IsMutating);
            Assert.IsTrue(batch.ConflictsWithJob);
        }

        [Test]
        public void UpgradePreview_IsARead_OnlyApplyKeepsTheReloadContract()
        {
            const string tool = "unity_open_mcp_upgrade";
            foreach (var body in new[] { "{}", "{\"dry_run\":true}" })
            {
                var preview = EffectiveToolContract.Resolve(tool, body);
                Assert.IsFalse(preview.IsMutating, body);
                Assert.IsFalse(preview.ConflictsWithJob, body);
                Assert.AreEqual(LifecyclePolicy.None, preview.Lifecycle, body);
                Assert.IsFalse(SceneDirtyGuard.AppliesTo(tool, body), body);
            }
            const string apply = "{\"dry_run\":false}";
            var applied = EffectiveToolContract.Resolve(tool, apply);
            Assert.IsTrue(applied.IsMutating);
            Assert.IsTrue(applied.ConflictsWithJob);
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle, applied.Lifecycle);
            Assert.IsTrue(SceneDirtyGuard.AppliesTo(tool, apply));

            // Nested, a preview is a read step; the apply still needs its top-level reload wait.
            Assert.IsNull(BatchExecuteTool.Preflight(UpgradeBatch("{\"target_version\":\"9.9.9\"}"), out var mutating));
            Assert.IsFalse(mutating);
            Assert.AreEqual("batch_nested_reload_unsafe",
                BatchExecuteTool.Preflight(UpgradeBatch("{\"target_version\":\"9.9.9\",\"dry_run\":false}"), out _).ErrorCode);
        }

        [Test]
        public void UpgradePreview_DirtyScene_ReturnsTheReportInsteadOfSceneDirty()
        {
            // Same scratch-scene choice as SceneDirtyGuardTests: an unsaved dirty
            // scene, which the demo's auto-save cannot save away.
            var active = SceneManager.GetActiveScene();
            bool reuse = string.IsNullOrEmpty(active.path);
            bool wasDirty = active.isDirty;
            var scratch = reuse ? active : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                EditorSceneManager.MarkSceneDirty(scratch);
                var result = Dispatch("unity_open_mcp_upgrade",
                    "{\"target_version\":\"9.9.9\",\"update_home_configs\":false}");
                Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorCode + ": " + result.Mutation.ErrorMessage);
                StringAssert.Contains("\"phase\":\"preview\"", result.Mutation.Output);
                Assert.IsTrue(result.EffectiveReadOnly);
                Assert.AreEqual("read_only", result.SkippedReason);
                Assert.IsTrue(scratch.isDirty);
            }
            finally
            {
                if (!reuse) EditorSceneManager.CloseScene(scratch, true);
                else if (!wasDirty)
                    typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { scratch });
            }
        }

        [Test]
        public void ReadOnlyAssertion_KnownDisruptionKeepsProtection()
        {
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp",
                "{\"read_only\":true,\"code\":\"return EditorApplication.isPlaying;\"}"));
            foreach (var code in new[] { "CompilationPipeline.RequestScriptCompilation();", "EditorSceneManager.OpenScene(\"x\");", "EditorApplication.isPlaying = true;", "AssetDatabase.Refresh();" })
            {
                var body = "{\"read_only\":true,\"code\":\"" + BridgeJson.EscapeStringContent(code) + "\"}";
                Assert.IsTrue(EffectiveToolContract.IsMutating("unity_open_mcp_execute_csharp", body));
                Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp", body));
            }
        }

        [Test]
        public void ReadBatch_NoScopeCheckpointOrUndo_EvenWithScopeSupplied()
        {
            int before = Undo.GetCurrentGroup();
            foreach (var hint in new[] { null, new[] { Scope } })
            {
                var result = BatchExecuteGateRunner.Execute(Read, "enforce", hint);
                Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);
                Assert.IsTrue(result.EffectiveReadOnly);
                Assert.IsFalse(result.GateRan);
                Assert.IsNull(result.CheckpointId);
                Assert.AreEqual(before, Undo.GetCurrentGroup());
            }
        }

        [Test]
        public void ScreenshotBatch_IsReadOnly()
        {
            Assert.IsNull(BatchExecuteTool.Preflight("{\"commands\":[{\"tool\":\"unity_senses_screenshot\",\"params\":{}}]}", out var mutating));
            Assert.IsFalse(mutating);
        }

        [Test]
        public void MixedBatch_NoScope_RejectsBeforeFirstMutation()
        {
            const string name = "__MCPTest_NoDispatch";
            string body = "{\"commands\":[{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + name + "\"}},{\"tool\":\"unity_open_mcp_editor_status\",\"params\":{}}]}";
            var before = Undo.GetCurrentGroup();
            var result = BatchExecuteGateRunner.Execute(body, "off", null);
            Assert.AreEqual("paths_hint_required", result.Mutation.ErrorCode);
            Assert.AreEqual("request_rejected", result.SkippedReason);
            Assert.IsNull(UnityEngine.GameObject.Find(name));
            Assert.AreEqual(before, Undo.GetCurrentGroup());
        }

        [Test]
        public void Preflight_ReportsEverySchemaAndLifecycleViolation()
        {
            string body = "{\"commands\":[{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":12,\"typo\":true}},{\"tool\":\"unity_senses_run_tests\",\"params\":{}},{\"tool\":\"unity_open_mcp_scene_open\",\"params\":{}},{\"tool\":\"missing_tool\",\"params\":{}}]}";
            var result = BatchExecuteTool.Preflight(body, out _);
            Assert.IsNotNull(result);
            foreach (var expected in new[] { "commands[0].params.name", "typo", "commands[1]", "commands[2]", "commands[3]" })
                StringAssert.Contains(expected, result.ErrorMessage);
        }

        [Test]
        public void NestedKeys_CannotSupplyOuterScopeOrReadOnlyAssertion()
        {
            Assert.IsNull(JsonBody.GetTopLevelRawValue("{\"commands\":[{\"params\":{\"paths_hint\":[\"Assets\"]}}]}", "paths_hint"));
            Assert.IsTrue(EffectiveToolContract.IsMutating("unity_open_mcp_execute_csharp", "{\"payload\":{\"read_only\":true},\"code\":\"return 42;\"}"));
        }

        [Test]
        public void MutationFailure_SkipsValidation_AndPreservesEnvelope()
        {
            int validations = 0;
            GatePolicy.ValidatePathsOverride = (_, __, ___) => { validations++; throw new Exception("must not run"); };
            try
            {
                foreach (var mode in new[] { GateMode.Off, GateMode.Enforce })
                {
                    var result = GatePolicy.Execute(mode, new[] { Scope }, () => ToolDispatchResult.Fail("compile_error", "bad snippet"));
                    Assert.IsFalse(result.Mutation.Success);
                    Assert.IsFalse(result.GateRan);
                    Assert.IsFalse(result.GateFailed);
                    Assert.AreEqual(GateOutcome.Skipped, result.Outcome);
                    Assert.AreEqual("mutation_failed", result.SkippedReason);
                    var json = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.None);
                    StringAssert.Contains("\"delta\":null", json);
                    StringAssert.Contains("agentNextSteps", json);
                }
                Assert.AreEqual(0, validations);
            }
            finally { GatePolicy.ValidatePathsOverride = null; }
        }

        [Test]
        public void PartialMutation_ValidationOutcomeIsIndependent()
        {
            var result = GatePolicy.Execute(GateMode.Enforce, new[] { Scope },
                () => ToolDispatchResult.PartialFailure("batch_partial_failure", "one committed", "{}"));
            Assert.IsFalse(result.Mutation.Success);
            Assert.IsTrue(result.GateRan);
            Assert.IsFalse(result.GateFailed);
            Assert.AreEqual(GateOutcome.Passed, result.Outcome);
            Assert.IsNotNull(result.Delta);
        }

        [Test]
        public void MalformedBatch_RejectsBeforeDispatch()
        {
            var result = BatchExecuteTool.Preflight("{\"commands\":[{\"tool\":\"unity_open_mcp_editor_status\",\"params\":{}}}]}", out _);
            Assert.AreEqual("batch_invalid_step", result.ErrorCode);
        }

        [Test]
        public void Preflight_UnionTypesAndExclusiveBoundsAreEnforced()
        {
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_gameobject_find\",\"params\":{\"instance_id\":true}},{\"tool\":\"unity_senses_screenshot_camera\",\"params\":{\"fov\":180}}]}";
            var result = BatchExecuteTool.Preflight(body, out _);
            StringAssert.Contains("instance_id", result.ErrorMessage);
            StringAssert.Contains("exclusiveMaximum", result.ErrorMessage);
        }

        [Test]
        public void ReadSuccessThenMutationFailure_IsNotPartialCommit()
        {
            var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_editor_status\",\"params\":{}},{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"\"}}]}";
            var result = BatchExecuteTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.IsFalse(result.PartialCommit);
        }

        [Test]
        public void FailedMutation_PlayModeAndNoScope_DoNotCountAsGateFailures()
        {
            bool wasPlaying = BridgeSession.IsPlaying;
            try
            {
                foreach (bool playing in new[] { false, true })
                {
                    BridgeSession.SetPlayingForTest(playing);
                    foreach (var paths in new[] { null, new[] { Scope } })
                    {
                        var result = GatePolicy.Execute(GateMode.Enforce, paths,
                            () => ToolDispatchResult.Fail("compile_error", "bad snippet"));
                        Assert.AreEqual("mutation_failed", result.SkippedReason);
                        Assert.IsFalse(result.GateFailed);
                        Assert.IsFalse(result.GateRan);
                        var activity = new BridgeActivityEvent();
                        BridgeActivityRecorder.ApplyToolResultToActivity(activity, result, 1);
                        Assert.AreEqual(BridgeActivityOutcome.Failed, activity.Outcome);
                        BridgeAuditRecorder.RecordGateRun("unity_open_mcp_execute_csharp", "enforce", result, paths);
                        Assert.AreEqual(GateOutcome.Skipped, BridgeGateRunHistory.Latest.Outcome);
                        Assert.IsFalse(BridgeGateRunHistory.Latest.GateFailed);
                    }
                }
            }
            finally { BridgeSession.SetPlayingForTest(wasPlaying); }
        }

        [Test]
        public void ThrownMutation_SkipsValidationAndPreservesError()
        {
            foreach (var mode in new[] { GateMode.Off, GateMode.Enforce })
            {
                var result = GatePolicy.Execute(mode, new[] { Scope },
                    () => throw new InvalidOperationException("test mutation throw"));
                Assert.AreEqual("execution_error", result.Mutation.ErrorCode);
                Assert.AreEqual("test mutation throw", result.Mutation.ErrorMessage);
                Assert.AreEqual("mutation_failed", result.SkippedReason);
                Assert.IsFalse(result.GateRan);
                Assert.IsFalse(result.GateFailed);
            }
            var json = BridgeJson.BuildFaultEnvelope(new Exception("unexpected"), "enforce");
            StringAssert.Contains("\"outcome\":\"skipped\"", json);
            StringAssert.Contains("\"skippedReason\":\"mutation_failed\"", json);
        }

        private static string UpgradeBatch(string args) =>
            "{\"commands\":[{\"tool\":\"unity_open_mcp_upgrade\",\"params\":" + args + "}]}";

        private static GateDispatchResult Dispatch(string tool, string body) => (GateDispatchResult)
            typeof(BridgeHttpServer).GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { tool, body, "enforce", null });
    }
}
