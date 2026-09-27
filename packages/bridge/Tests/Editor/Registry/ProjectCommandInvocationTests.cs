using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ProjectCommandInvocationTests
    {
        private static int calls;
        public enum Choice { First, Second }
        public static string Typed(string required, int[] counts, long id, Choice choice, double precision = 1, int? optional = null)
        { calls++; return "{\"calls\":" + calls + ",\"id\":\"" + id + "\",\"count\":" + counts.Length + "}"; }
        public static System.Threading.Tasks.Task<string> AsyncEmpty(ProjectCommandContext context) => System.Threading.Tasks.Task.FromResult("{}");
        private static TaskCompletionSource<string> held;
        private static Func<bool> testRunActive;
        public static Task<string> AsyncHeld(ProjectCommandContext context) => held.Task;
        public static string Empty() { calls++; return "{}"; }
        public static string BadOutput() { calls++; return "{broken"; }
        public static string Throws() { calls++; throw new InvalidOperationException("after write"); }
        public static string Single(float value) { calls++; return "{}"; }
        public static string Aliased([ProjectCommandParameter(Minimum = 1, Maximum = 5, DeprecatedAliases = new[] { "old_value", "legacy_value" })] int value,
            [ProjectCommandParameter(DeprecatedAliases = new[] { "old_label" })] string label = "none")
        { calls++; return "{\"value\":" + value + ",\"label\":\"" + label + "\"}"; }
        private static ProjectCommandCatalog.Entry Register(string method = "Typed", bool mutating = false,
            LifecyclePolicy lifecycle = LifecyclePolicy.None, string[] paths = null, bool async = false)
        {
            var entry = ProjectCommandCatalog.Create(typeof(ProjectCommandInvocationTests).GetMethod(method),
                new ProjectCommandAttribute("project.tests.invoke") { Title = "Test", Description = "Invocation test", Package = "tests",
                    IsMutating = mutating, Lifecycle = lifecycle, PathsHint = paths ?? Array.Empty<string>(), Async = async });
            ProjectCommandCatalog.Publish(new[] { entry });
            return entry;
        }
        private static string Body(string args = "{}", string extra = "") => "{\"action\":\"invoke\",\"command_id\":\"project.tests.invoke\",\"args\":" + args + extra + "}";
        private const string Valid = "{\"required\":null,\"counts\":[1,2],\"id\":\"9223372036854775807\",\"choice\":\"Second\"}";
        [Test] public void AsyncDeclarationRequiresTaskAndOneInjectedContext()
        {
            var invalid = Register("Empty", async: true);
            Assert.AreEqual("invalid_command_declaration", invalid.Code);
            var valid = Register("AsyncEmpty", async: true);
            Assert.IsNull(valid.Code);
            StringAssert.DoesNotContain("context", valid.Schema);
            var context = new ProjectCommandContext("test", System.Threading.CancellationToken.None, _ => {});
            Assert.IsNull(ProjectCommandInvocation.Preflight(Body(), out _, out var values, context));
            Assert.AreSame(context, values[0]);
            Assert.AreEqual("async_not_supported", ProjectCommandInvocation.Execute(Body()).ErrorCode);
        }
        [SetUp] public void Reset() { calls = 0; }
        [TearDown] public void Restore() => BridgeToolRegistry.Scan();

        [TestCase("{}")]
        [TestCase("{\"required\":null,\"counts\":[\"bad\"],\"id\":\"1\",\"choice\":\"First\"}")]
        [TestCase("{\"required\":null,\"counts\":[1],\"id\":1,\"choice\":\"First\"}")]
        [TestCase("{\"required\":null,\"counts\":[1],\"id\":\"9223372036854775808\",\"choice\":\"First\"}")]
        [TestCase("{\"required\":null,\"counts\":[1],\"id\":\"1\",\"choice\":\"first\"}")]
        [TestCase("{\"required\":null,\"counts\":[1.5],\"id\":\"1\",\"choice\":\"First\"}")]
        public void InvalidArgumentsNeverExecute(string args)
        {
            Register();
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body(args)).ErrorCode);
            Assert.AreEqual(0, calls);
        }
        [Test] public void DuplicateKeysAndActionSpecificFieldsAreRejected()
        {
            Register("Empty");
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body(extra: ",\"action\":\"invoke\"")).ErrorCode);
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body(extra: ",\"id\":\"project.tests.invoke\"")).ErrorCode);
            Register("Single");
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body("{\"value\":1,\"value\":2}")).ErrorCode);
            Assert.AreEqual(0, calls);
        }
        [Test] public void StrictBindingPreservesInt64AndDefaults()
        {
            Register();
            var result = ProjectCommandInvocation.Execute(Body(Valid));
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("9223372036854775807", result.Output);
            Assert.AreEqual(1, calls);
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body(Valid.Replace("\"counts\"", "\"unknown\""))).ErrorCode);
            Register("Single");
            Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body("{\"value\":1e100}")).ErrorCode);
        }
        [Test] public void DeprecatedParameterAliasesBindToTheirCanonicalParameter()
        {
            Register("Aliased");
            var aliased = ProjectCommandInvocation.Execute(Body("{\"old_value\":3,\"old_label\":\"x\"}"));
            Assert.IsTrue(aliased.Success, aliased.ErrorMessage);
            StringAssert.Contains("\"value\":3", aliased.Output);
            StringAssert.Contains("\"label\":\"x\"", aliased.Output);
            var canonical = ProjectCommandInvocation.Execute(Body("{\"value\":4}"));
            Assert.IsTrue(canonical.Success, canonical.ErrorMessage);
            StringAssert.Contains("\"value\":4", canonical.Output);
            StringAssert.Contains("\"label\":\"none\"", canonical.Output);
            Assert.AreEqual(2, calls);
            var mixed = ProjectCommandInvocation.Execute(Body("{\"value\":3,\"old_value\":4}"));
            Assert.AreEqual("invalid_arguments", mixed.ErrorCode);
            StringAssert.Contains("args.old_value conflicts", mixed.ErrorMessage);
            // Two aliases of one parameter, a missing required parameter and an
            // alias outside the canonical range all fail before execution.
            foreach (var args in new[] { "{\"old_value\":1,\"legacy_value\":2}", "{\"old_label\":\"x\"}", "{\"old_value\":9}" })
                Assert.AreEqual("invalid_arguments", ProjectCommandInvocation.Execute(Body(args)).ErrorCode, args);
            Assert.AreEqual(2, calls);
        }
        [Test] public void ReadOnlyUsesSharedGateEnvelopeWithoutScopeOrCheckpoint()
        {
            Register("Empty");
            var result = Dispatch(Body());
            Assert.IsTrue(result.Mutation.Success);
            Assert.IsTrue(result.EffectiveReadOnly);
            Assert.IsFalse(result.GateRan);
            Assert.AreEqual("read_only", result.SkippedReason);
        }
        [Test] public void ScopeIsRequiredEvenWithGateOffAndCannotNarrowDeclaration()
        {
            Register("Empty", true, LifecyclePolicy.EditorSettle);
            Assert.AreEqual("paths_hint_required", ProjectCommandInvocation.Execute(Body(extra: ",\"gate\":\"off\"")).ErrorCode);
            Register("Empty", true, LifecyclePolicy.EditorSettle, new[] { "Assets/Declared" });
            CollectionAssert.AreEquivalent(new[] { "Assets/Declared", "Assets/Other" }, ProjectCommandInvocation.ScopedPaths(Body(extra: ",\"paths_hint\":[\"Assets/Other\"]")));
            var result = Dispatch(Body(extra: ",\"gate\":\"off\""));
            Assert.IsTrue(result.Mutation.Success);
            Assert.IsFalse(result.EffectiveReadOnly);
            Assert.AreEqual("gate_off", result.SkippedReason);
            Assert.AreEqual("invalid_paths", ProjectCommandInvocation.Execute(Body(extra: ",\"paths_hint\":[\"Assets/../Secret\"]")).ErrorCode);
        }
        [Test] public void LifecycleAndSchemaCannotBeOverriddenByArguments()
        {
            var entry = Register("Empty", true, LifecyclePolicy.RestartThenSettle, new[] { "Assets/Test" });
            var body = Body("{\"ignore_scene_dirty\":true}");
            Assert.IsTrue(SceneDirtyGuard.AppliesTo(ProjectCommandInvocation.ToolName, body));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(ProjectCommandInvocation.ToolName, Body(extra: ",\"ignore_scene_dirty\":true")));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle, EffectiveToolContract.Lifecycle(ProjectCommandInvocation.ToolName, body));
            Assert.AreEqual("command_schema_changed", ProjectCommandInvocation.Execute(Body(extra: ",\"schema_version\":\"old\"")).ErrorCode);
            Assert.IsNull(ProjectCommandInvocation.Preflight(Body(extra: ",\"schema_version\":\"" + entry.SchemaVersion + "\""), out _, out _));
            Assert.AreEqual(0, calls);
        }
        [Test] public void SharedDirtyGuardIncludesUnsavedAdditiveScenes()
        {
            var dirty = DirtyScratchScene.Mark();
            try
            {
                var guard = SceneDirtyGuard.Check();
                Assert.IsFalse(guard.Allowed);
                CollectionAssert.Contains(guard.DirtyScenePaths, "(unsaved scene)");
            }
            finally { dirty.Restore(); }
        }
        // An async start runs the synchronous route's dirty-scene preflight: the refusal is
        // synchronous, leaves no job record, and ignore_scene_dirty lets the same id start.
        [UnityTest] public IEnumerator JobStartRefusesRestartThenSettleCommandOnDirtyScene()
        {
            PrepareHeldJob();
            var entry = Register("AsyncHeld", true, LifecyclePolicy.EditorSettle, new[] { "Assets/Test" }, async: true);
            Assert.IsNull(entry.Code, entry.Message);
            // Declarations refuse async RestartThenSettle (a reload cannot resume user code).
            // Force it on the published entry so the start guard is pinned on its own.
            entry.Attribute.Lifecycle = LifecyclePolicy.RestartThenSettle;
            const string gateOff = ",\"gate\":\"off\"";
            var dirty = DirtyScratchScene.Mark();
            try
            {
                var refused = Job("start", gateOff);
                Assert.IsTrue(BridgeJson.IsCompleteJson(refused), refused);
                StringAssert.Contains("\"code\":\"scene_dirty\"", refused);
                StringAssert.Contains("\"dirtyScenes\":[", refused);
                StringAssert.Contains("(unsaved scene)", refused);
                StringAssert.Contains("\"lifecycle\":\"restart_then_settle\"", refused);
                StringAssert.Contains("\"projectCommand\":{\"id\":\"project.tests.invoke\"", refused);
                StringAssert.Contains("Save or discard", refused);
                Assert.IsFalse(ProjectCommandJobs.Active);
                StringAssert.Contains("job_not_found", Job("status"));

                StringAssert.Contains("\"state\":\"running\"", Job("start", gateOff + ",\"ignore_scene_dirty\":true"));
                for (int frame = 0; frame < 600 && !Job("status").Contains("\"phase\":\"executing\""); frame++) yield return null;
                yield return FinishHeldJob();
            }
            finally { dirty.Restore(); }
        }
        // Marks a disposable scene dirty. The native runner starts with an untitled scene;
        // Unity refuses an additive scene beside it, so that scene is reused instead.
        private struct DirtyScratchScene
        {
            private UnityEngine.SceneManagement.Scene scene;
            private bool reuse, wasDirty;
            internal static DirtyScratchScene Mark()
            {
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var dirty = new DirtyScratchScene { reuse = string.IsNullOrEmpty(active.path), wasDirty = active.isDirty };
                dirty.scene = dirty.reuse ? active : UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(dirty.scene);
                return dirty;
            }
            internal void Restore()
            {
                if (!reuse) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                // Hand the reused session scene back in the state we found it.
                else if (!wasDirty)
                    typeof(UnityEditor.SceneManagement.EditorSceneManager)
                        .GetMethod("ClearSceneDirtiness", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { scene });
            }
        }
        [Test] public void MissingLifecycleAndAsyncExecutionFailClosed()
        {
            Assert.AreEqual("invalid_command_declaration", Register("Empty", true).Code);
            Register("AsyncEmpty", async: true);
            Assert.AreEqual("async_not_supported", ProjectCommandInvocation.Execute(Body()).ErrorCode);
            Assert.AreEqual(0, calls);
        }
        [Test] public void OutputFailuresAreAtomicAndMutationUncertaintyIsPreserved()
        {
            foreach (var method in new[] { "BadOutput", "Throws" })
            {
                Register(method, true, LifecyclePolicy.EditorSettle, new[] { "Assets/Test" });
                var result = ProjectCommandInvocation.Execute(Body());
                Assert.IsFalse(result.Success);
                Assert.IsTrue(result.PartialCommit);
            }
            Assert.IsTrue(BridgeJson.IsCompleteJson(OutputSerializer.SerializeJson("[1,true,null,\"x\"]")));
            StringAssert.Contains("truncated", OutputSerializer.SerializeJson("[" + string.Join(",", Enumerable.Repeat("1", 110)) + "]"));
            Assert.Throws<ArgumentException>(() => OutputSerializer.SerializeJson("{broken"));
            Assert.Throws<ArgumentException>(() => OutputSerializer.SerializeJson("\"" + new string('x', 1024 * 1024) + "\""));
        }
        [Test] public void IdentityAndEffectivePolicyAreSerializedInResponseAndAudit()
        {
            var entry = Register("Empty");
            var result = Dispatch(Body());
            ProjectCommandInvocation.Decorate(result, Body(), 42);
            var json = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.None);
            Assert.IsTrue(BridgeJson.IsCompleteJson(json));
            StringAssert.Contains(entry.SchemaVersion, json);
            StringAssert.Contains(typeof(ProjectCommandInvocationTests).FullName, json);
            var audit = new BridgeAuditRecord { ProjectCommandJson = result.ProjectCommandJson, DurationMs = 42 }.ToJsonLine();
            Assert.IsTrue(BridgeJson.IsCompleteJson(audit));
            StringAssert.Contains("\"durationMs\":42", audit);
            StringAssert.Contains("\"jobId\":null", audit);
        }
        [Test] public void AuditIdentityDoesNotIncludeArgumentOrResultPayloads()
        {
            Register();
            var body = Body(Valid.Replace("\"required\":null", "\"required\":\"secret-argument-sentinel\""));
            var result = Dispatch(body);
            ProjectCommandInvocation.Decorate(result, body, 1);
            var audit = new BridgeAuditRecord { ProjectCommandJson = result.ProjectCommandJson }.ToJsonLine();
            StringAssert.DoesNotContain("secret-argument-sentinel", audit);
            StringAssert.DoesNotContain("9223372036854775807", audit);
            StringAssert.Contains("project.tests.invoke", audit);
        }
        [Test] public void DenyRulesMatchExactCommandIdentityAndRequireExplicitBypass()
        {
            Assert.IsFalse(BridgeDenyList.EvaluateProjectCommand("project.tests.invoke", new[] { "^project\\.tests\\." }, false).Allowed);
            Assert.IsTrue(BridgeDenyList.EvaluateProjectCommand("project.tests.invoke", new[] { ".*" }, true).Allowed);
        }
        [UnityTest] public IEnumerator RunningJobRefusesEditorStateWritesOnEveryRouteButNotReads()
        {
            yield return StartHeldJob();
            const string pause = "{\"state\":\"pause\"}";
            const string pauseBatch = "{\"commands\":[{\"tool\":\"unity_open_mcp_editor_set_state\",\"params\":" + pause + "}]}";
            Assert.AreEqual("job_busy", Direct("unity_open_mcp_editor_set_state", pause).ErrorCode);
            Assert.AreEqual("job_busy", Gated("unity_open_mcp_editor_set_state", pause).Mutation.ErrorCode);
            Assert.AreEqual("job_busy", Gated("unity_open_mcp_batch_execute", pauseBatch).Mutation.ErrorCode);
            Assert.AreEqual("job_busy", Gated("unity_open_mcp_gameobject_create", "{\"name\":\"__MCPTest_JobBusy\"}").Mutation.ErrorCode);
            Assert.IsTrue(Direct("unity_open_mcp_selection_get", "{}").Success);
            Assert.IsTrue(Gated("unity_open_mcp_editor_status", "{}").Mutation.Success);
            Assert.IsTrue(Gated("unity_open_mcp_batch_execute", "{\"commands\":[{\"tool\":\"unity_open_mcp_selection_get\",\"params\":{}}]}").Mutation.Success);
            StringAssert.Contains("not_cancellable", Job("cancel"));

            yield return FinishHeldJob();
            Assert.IsTrue(Direct("unity_open_mcp_editor_set_state", pause).Success);
        }
        // The route refuses a test start before the tool body runs, so the caller gets the
        // structured job_busy code. The invalid run_id stops the tool before it schedules a run:
        // neither a regressed guard nor the call after the job can start a nested test run.
        [UnityTest] public IEnumerator RunningJobRefusesTestStartsWithJobBusyOnEveryRoute()
        {
            yield return StartHeldJob();
            const string start = "{\"run_id\":\"../escape\"}";
            var refused = Direct("unity_senses_run_tests", start);
            Assert.AreEqual("job_busy", refused.ErrorCode, refused.ErrorMessage);
            Assert.AreEqual("job_busy", Gated("unity_senses_run_tests", start).Mutation.ErrorCode);

            yield return FinishHeldJob();
            var reached = Direct("unity_senses_run_tests", start);
            Assert.AreEqual("execution_error", reached.ErrorCode);
            StringAssert.Contains("Invalid 'run_id'", reached.ErrorMessage);
        }
        // read_only waives scope and the gate, but the bridge cannot verify a snippet writes nothing,
        // so a running job refuses even a pure read directly and as a batch step.
        [UnityTest] public IEnumerator RunningJobRefusesSelfAssertedReadOnlySnippetsOnEveryRoute()
        {
            yield return StartHeldJob();
            const string snippet = "{\"read_only\":true,\"code\":\"return 42;\"}";
            const string snippetBatch = "{\"commands\":[{\"tool\":\"unity_open_mcp_execute_csharp\",\"params\":" + snippet + "}]}";
            var refused = Gated("unity_open_mcp_execute_csharp", snippet);
            Assert.AreEqual("job_busy", refused.Mutation.ErrorCode, refused.Mutation.ErrorMessage);
            Assert.IsNull(refused.CheckpointId);
            Assert.AreEqual("job_busy", Gated("unity_open_mcp_batch_execute", snippetBatch).Mutation.ErrorCode);

            yield return FinishHeldJob();
        }
        private static string heldJobId;
        // Starts AsyncHeld as a project job and waits until it owns the Editor scope.
        private static IEnumerator StartHeldJob()
        {
            PrepareHeldJob();
            Register("AsyncHeld", async: true);
            StringAssert.Contains("\"state\":\"running\"", Job("start"));
            for (int frame = 0; frame < 600 && !Job("status").Contains("\"phase\":\"executing\""); frame++) yield return null;
            StringAssert.Contains("\"phase\":\"executing\"", Job("status"));
        }
        private static void PrepareHeldJob()
        {
            if (BridgeToolRegistry.Count == 0) BridgeToolRegistry.Scan();
            // A run started through the bridge's own test tool would refuse the start as editor_busy.
            testRunActive = ProjectCommandJobs.TestRunActive;
            ProjectCommandJobs.TestRunActive = null;
            held = new TaskCompletionSource<string>();
            heldJobId = Guid.NewGuid().ToString();
        }
        private static IEnumerator FinishHeldJob()
        {
            held.SetResult("{}");
            for (int frame = 0; frame < 600 && ProjectCommandJobs.Active; frame++) yield return null;
            StringAssert.Contains("\"state\":\"succeeded\"", Job("status"));
        }
        private static string Job(string action, string invocationExtra = "") => ProjectCommandJobs.Handle("{\"action\":\"" + action + "\",\"job_id\":\"" + heldJobId + "\""
            + (action == "start" ? ",\"invocation\":" + Body(extra: invocationExtra) : "") + "}", "tests");
        [UnityTearDown] public IEnumerator ReleaseHeldJob()
        {
            held?.TrySetResult("{}");
            for (int frame = 0; frame < 600 && ProjectCommandJobs.Active; frame++) yield return null;
            if (held != null) ProjectCommandJobs.TestRunActive = testRunActive;
            held = null;
        }
        private static ToolDispatchResult Direct(string tool, string body)
            => BridgeHttpServer.DispatchDirectResponse(tool, body, EffectiveToolContract.Resolve(tool, body));
        private static GateDispatchResult Gated(string tool, string body) => (GateDispatchResult)typeof(BridgeHttpServer)
            .GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { tool, body, "off", null });
        private static GateDispatchResult Dispatch(string body) => (GateDispatchResult)typeof(BridgeHttpServer)
            .GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { ProjectCommandInvocation.ToolName, body, BridgeRequestBody.ExtractGateMode(body), ProjectCommandInvocation.ScopedPaths(body) });
    }
}
