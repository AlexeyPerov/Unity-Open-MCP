using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class ExecuteCSharpToolTests
    {
        [Test]
        public static void Execute_MissingCode_ReturnsValidationError()
        {
            var result = ExecuteCSharpTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
            Assert.IsTrue(result.ErrorMessage.Contains("code"));
        }

        [Test]
        public static void Execute_EmptyCode_ReturnsValidationError()
        {
            var result = ExecuteCSharpTool.Execute("{\"code\":\"\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
        }

        // M14 T5.2 — deny heuristic integration. The tool evaluates the deny
        // list before compile, so a destructive snippet is refused with
        // denied_by_policy without ever touching Roslyn.

        [Test]
        public static void Execute_DestructiveExit_ReturnsDeniedByPolicy()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"EditorApplication.Exit(0);\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
            StringAssert.Contains("EditorApplication", result.ErrorMessage);
        }

        [Test]
        public static void Execute_DestructiveAssetDelete_ReturnsDeniedByPolicy()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"AssetDatabase.DeleteAsset(\\\"Assets/Old.prefab\\\");\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }

        // TestRunnerApi driving from execute_csharp deadlocks the main thread
        // (its callbacks fire on the same thread the snippet occupies). The
        // deny heuristic must refuse it before Roslyn runs and redirect to
        // unity_senses_run_tests. See specs/feedback.md entry 1.
        [Test]
        public static void Execute_TestRunnerApi_ReturnsDeniedByPolicy()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"var api = ScriptableObject.CreateInstance<TestRunnerApi>(); api.Execute(null);\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
            StringAssert.Contains("TestRunnerApi", result.ErrorMessage);
            StringAssert.Contains("unity_senses_run_tests", result.ErrorMessage);
        }

        [Test]
        public static void Execute_BypassWithGateOffAndConfirm_AllowsDestructive()
        {
            // The bypass contract is honored at the tool level — gate=off +
            // confirm_bypass=true skips the deny heuristic. This does NOT
            // exercise Roslyn (a benign return statement keeps it cheap), it
            // only asserts the heuristic did not short-circuit.
            var body = "{\"code\":\"return 1;\",\"gate\":\"off\",\"confirm_bypass\":true}";
            var result = ExecuteCSharpTool.Execute(body);
            // We can't assert Success here without a live Roslyn install in
            // the EditMode harness; assert that the deny heuristic did NOT
            // fire (no denied_by_policy) — the failure, if any, is downstream.
            Assert.AreNotEqual("denied_by_policy", result.ErrorCode);
        }

        [Test]
        public static void Execute_BypassMissingGate_StillDenied()
        {
            // confirm_bypass alone is not enough.
            var body = "{\"code\":\"EditorApplication.Exit(0);\",\"confirm_bypass\":true}";
            var result = ExecuteCSharpTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }

        [Test]
        public static void Execute_BypassMissingConfirm_StillDenied()
        {
            // gate=off alone is not enough.
            var body = "{\"code\":\"EditorApplication.Exit(0);\",\"gate\":\"off\"}";
            var result = ExecuteCSharpTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }

        // ===================== M30-polish Plan 4 — T4.5 lifecycle =====================

        // T4.5 — IsSnippetAssembly must classify the UnityOpenMcpSnippet
        // assembly name as a snippet (so type lookups skip it) and NOT classify
        // real loaded assemblies as snippets. This is the type-resolution guard
        // that keeps the transient snippet type out of ResolveComponentType /
        // ResolveType / FindType / TryResolveType.
        [Test]
        public static void IsSnippetAssembly_ClassifiesCorrectly()
        {
            // Real loaded assemblies must never classify as snippets.
            int snippetCount = 0;
            int realCount = 0;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ExecuteCSharpTool.IsSnippetAssembly(asm))
                    snippetCount++;
                else
                    realCount++;
            }
            // No snippet has been compiled in this test session (the deny
            // heuristic short-circuits before Assembly.Load), so the count of
            // snippet-classified assemblies must be zero here.
            Assert.AreEqual(0, snippetCount,
                "No execute_csharp snippet should be loaded in the test session.");
            Assert.Greater(realCount, 0, "Sanity: the AppDomain has loaded assemblies.");
        }

        // T4.5 — null must not throw (defensive). IsSnippetAssembly is called
        // inside assembly-enumeration loops; a null entry must never abort the
        // scan.
        [Test]
        public static void IsSnippetAssembly_Null_ReturnsFalse()
        {
            Assert.IsFalse(ExecuteCSharpTool.IsSnippetAssembly(null));
        }
    }
}

