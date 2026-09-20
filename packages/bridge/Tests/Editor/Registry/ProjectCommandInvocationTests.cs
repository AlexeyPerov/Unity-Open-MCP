using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ProjectCommandInvocationTests
    {
        private static int calls;
        public enum Choice { First, Second }
        public static string Typed(string required, int[] counts, long id, Choice choice, double precision = 1, int? optional = null)
        { calls++; return "{\"calls\":" + calls + ",\"id\":\"" + id + "\",\"count\":" + counts.Length + "}"; }
        public static string Empty() { calls++; return "{}"; }
        public static string BadOutput() { calls++; return "{broken"; }
        public static string Throws() { calls++; throw new InvalidOperationException("after write"); }
        public static string Single(float value) { calls++; return "{}"; }
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
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
            try
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                var guard = SceneDirtyGuard.Check();
                Assert.IsFalse(guard.Allowed);
                CollectionAssert.Contains(guard.DirtyScenePaths, "(unsaved scene)");
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true); }
        }
        [Test] public void MissingLifecycleAndAsyncExecutionFailClosed()
        {
            Assert.AreEqual("invalid_command_declaration", Register("Empty", true).Code);
            Register("Empty", async: true);
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
        [Test] public void DenyRulesMatchExactCommandIdentityAndRequireExplicitBypass()
        {
            Assert.IsFalse(BridgeDenyList.EvaluateProjectCommand("project.tests.invoke", new[] { "^project\\.tests\\." }, false).Allowed);
            Assert.IsTrue(BridgeDenyList.EvaluateProjectCommand("project.tests.invoke", new[] { ".*" }, true).Allowed);
        }
        private static GateDispatchResult Dispatch(string body) => (GateDispatchResult)typeof(BridgeHttpServer)
            .GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { ProjectCommandInvocation.ToolName, body, BridgeRequestBody.ExtractGateMode(body), ProjectCommandInvocation.ScopedPaths(body) });
    }
}
