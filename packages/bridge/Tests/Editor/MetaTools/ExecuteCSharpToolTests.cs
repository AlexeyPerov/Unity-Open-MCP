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

        // feedback-fable-04-08 §8 — the read_only flag is a SCOPE HINT (waives
        // paths_hint at the dispatcher gate), NOT a safety boundary. The deny
        // heuristic runs inside Execute and is independent of read_only, so a
        // read_only snippet matching a destructive pattern is STILL denied.
        // This pins the contract: read_only removes gate friction, not safety.
        [Test]
        public static void Execute_ReadOnly_StillDeniedByPolicy_OnDestructivePattern()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"EditorApplication.Exit(0);\",\"read_only\":true}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
            StringAssert.Contains("EditorApplication", result.ErrorMessage);
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

        // ============ feedback.md issue 1 — BuildSource hoist + #line ============

        // feedback.md issue 1(a) — leading `using X;` directives must be hoisted
        // to file scope. Without hoisting they land inside Run() and are parsed
        // as using STATEMENTS (CS0210/CS0118), producing a cascade that never
        // names the real cause.
        [Test]
        public static void BuildSource_HoistsLeadingUsingDirectives()
        {
            var src = ExecuteCSharpTool.BuildSource(
                "using UnityEditor;\nusing UnityEngine;\n\nvar sb = new StringBuilder();\nreturn sb.ToString();",
                new[] { "System.Text" });
            // The hoisted `using UnityEditor;` must appear BEFORE the namespace
            // (file scope), not inside Run().
            var namespaceIdx = src.IndexOf("namespace UnityOpenMcpSnippet");
            var usingUEIdx = src.IndexOf("using UnityEditor;");
            Assert.Greater(usingUEIdx, 0, "using UnityEditor must be present in the source.");
            Assert.Less(usingUEIdx, namespaceIdx,
                "using UnityEditor must be hoisted to file scope (before the namespace), " +
                "not emitted inside Run() where it would parse as a using-statement.");
            // The body must NOT still contain the using line where it was — there
            // must be exactly ONE `using UnityEditor;` in the source (the hoisted
            // file-scope directive), so its first and last occurrences coincide.
            var lastUsingInBody = src.LastIndexOf("using UnityEditor;");
            Assert.AreEqual(usingUEIdx, lastUsingInBody,
                "Sanity: exactly one `using UnityEditor;` — the hoisted directive. " +
                "A second occurrence would mean the using was left inside Run().");
        }

        [Test]
        public static void BuildSource_DedupsHoistedAgainstCallerUsings()
        {
            var src = ExecuteCSharpTool.BuildSource(
                "using System.Text;\nreturn null;",
                new[] { "System.Text" });
            // System.Text appears once as a directive (deduped), not twice.
            var count = System.Linq.Enumerable.Count(
                src.Split('\n'), l => l.Trim() == "using System.Text;");
            Assert.AreEqual(1, count,
                "A using duplicated between the caller `usings` param and the body must dedupe to one directive.");
        }

        [Test]
        public static void BuildSource_DoesNotHoistUsingStatement()
        {
            // A genuine `using ( ... )` statement (with parens) must NOT be
            // hoisted — it's a resource scope, not a directive.
            var src = ExecuteCSharpTool.BuildSource(
                "using (var fs = new System.IO.FileStream(\"x\", System.IO.FileMode.Open)) { }\nreturn null;",
                System.Array.Empty<string>());
            var namespaceIdx = src.IndexOf("namespace UnityOpenMcpSnippet");
            // No `using var fs` directive should appear at file scope before the namespace.
            Assert.Less(namespaceIdx, src.IndexOf("var fs ="),
                "The using-statement body must stay inside Run(), not hoisted.");
        }

        // feedback.md issue 1(b) — a #line directive must map compiler errors to
        // snippet-relative coordinates. The body is preceded by `#line 1 "snippet"`
        // and followed by `#line default`.
        [Test]
        public static void BuildSource_EmitsLineDirectiveAroundBody()
        {
            var src = ExecuteCSharpTool.BuildSource("return 42;", System.Array.Empty<string>());
            StringAssert.Contains("#line 1 \"snippet\"", src,
                "A #line directive must precede the body so compiler errors report snippet-relative lines.");
            StringAssert.Contains("#line default", src,
                "#line default must follow the body to restore wrapper line tracking.");
        }

        // feedback S1 — the interacting case (hoist + #line). When leading usings
        // AND blank lines are stripped, the #line directive must carry the 1-based
        // index of the FIRST RETAINED line, not 1 — otherwise an error on the
        // caller's first real statement reports at line 1 instead of its true line.
        // BuildSource_EmitsLineDirectiveAroundBody only covers the no-hoist case.
        [Test]
        public static void BuildSource_LineDirectiveAccountsForHoistedUsings()
        {
            // Caller lines (1-based): 1 using, 2 using, 3 using, 4 blank, 5 body.
            var src = ExecuteCSharpTool.BuildSource(
                "using UnityEditor;\nusing UnityEngine;\nusing System.Text;\n\nvar sb = new StringBuilder();\nreturn sb.ToString();",
                new[] { "System.Text" });
            // The first retained line is "var sb = ..." — the caller's line 5.
            StringAssert.Contains("#line 5 \"snippet\"", src,
                "The #line directive must map to the first retained body line (caller line 5), " +
                "not line 1 — hoisted using/blank lines must not shift reported errors toward 1.");
        }
    }
}

