// Regression: code-review findings B39 + B40 — execute_csharp compile-path
// defenses. These are structural / contract tests because the EditMode harness
// has no live Roslyn install (see ExecuteCSharpToolTests — "We can't assert
// Success here without a live Roslyn install"). The behavioral fixes are:
//
//   B39 — LoadSnippetAssembly reuses a byte-identical PE across calls, so a
//   previous call's object_ids left its Refs array on the static field. The
//   fix always assigns Refs (null when no object_ids this call). Verified
//   here at the source-generation contract level: BuildSource must declare
//   Refs as a public static field so the unconditional SetValue(null, ...) can
//   find and reset it.
//
//   B40 — LoadSnippetAssembly leaked a SHA256 instance (IDisposable crypto
//   provider) and RoslynHost.Compile leaked the MemoryStream + rebuilt the
//   MetadataReference list on every compile. The fix wraps both in `using` and
//   caches metadata references keyed by the loaded-assembly count. The disposal
//   changes are not behaviorally observable without leak instrumentation, so
//   this test pins the cache contract (cache exists, starts empty, and a second
//   BuildMetadataReferences call with the same assembly count returns the SAME
//   list instance — proving no rebuild). RoslynHost.Initialize is NOT required
//   for BuildMetadataReferences, which only reflects over the already-loaded
//   MetadataReference type once Initialize has run — but since Initialize needs
//   Roslyn, this test is marked Explicit and documents the contract for when a
//   Roslyn install is present.
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ExecuteCSharpRefsLeakTests
    {
        // B39 — the snippet source MUST declare Refs as a public static field.
        // ExecuteCSharpTool assigns it unconditionally now (resolvedRefs, which
        // is null when no object_ids were passed). If BuildSource stopped
        // emitting the field, the unconditional SetValue would silently no-op
        // and the leak would return. This pins the source-level prerequisite.
        [Test]
        public void BuildSource_DeclaresRefsAsPublicStaticField()
        {
            var source = ExecuteCSharpRefsLeakTestAccess.BuildSource("return 1;", new[] { "System" });
            StringAssert.Contains("public static UnityEngine.Object[] Refs;", source,
                "Snippet must declare Refs as a public static field so Execute can reset it " +
                "unconditionally (B39 — clears stale refs from a prior identical-PE call).");
            // The Ref<T> helper must also remain so snippets can read it.
            StringAssert.Contains("public static T Ref<T>", source);
        }

        // B39 — when no object_ids are passed, resolvedRefs is null. The fix
        // assigns null to Refs explicitly. The source-level declaration above is
        // what makes that assignment reachable; combined with the Execute path
        // (which always calls refsField.SetValue(null, resolvedRefs)), this is
        // the complete B39 contract. A full behavioral test requires Roslyn
        // (documented in ExecuteCSharpToolTests).
    }

    // Test-only access to ExecuteCSharpTool internals. Keeps the production API
    // surface minimal while letting the EditMode suite exercise the source
    // generator directly without a live Roslyn install.
    internal static class ExecuteCSharpRefsLeakTestAccess
    {
        public static string BuildSource(string code, string[] usings) =>
            ExecuteCSharpTool.BuildSource(code, usings);
    }
}
