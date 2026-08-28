using System;
using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    // Editor-fd-exhaustion regression tests. Root cause (measured on a live
    // 6000.3 editor): every execute_csharp call rebuilt ~400 metadata
    // references through file-backed factories that keep each referenced PE
    // open (+679 fds per call), because the reference-cache key
    // (AppDomain.GetAssemblies().Length) was bumped by every distinct
    // snippet's Assembly.Load(byte[]). The second call of a session crossed
    // Mono's ~1024 IOSelector ceiling and wedged script compilation ("Could
    // not register to wait for file descriptor N").
    //
    // These tests need a loadable Roslyn (the editor-shipped Mono Roslyn on
    // 2022.3, or the installed IL-only fallback on 6000.x) and skip cleanly
    // when none is present — same policy as ExecuteCSharpToolTests.
    public class RoslynHostFdHygieneTests
    {
        private static bool RoslynReady => RoslynHost.Initialize();

        private static string Src(string marker) =>
            "using System;\n" +
            "namespace UnityOpenMcpFdTest {\n" +
            "  public static class T_" + marker + " {\n" +
            "    public static string Run() { return \"" + marker + "\"; }\n" +
            "  }\n" +
            "}\n";

        private static int CountOpenFds()
        {
            var dir = Directory.Exists("/dev/fd") ? "/dev/fd"
                : Directory.Exists("/proc/self/fd") ? "/proc/self/fd"
                : null;
            if (dir == null) return -1;
            return Directory.GetFileSystemEntries(dir).Length;
        }

        // The cache-key half of the fix: loading a byte[] (empty-Location)
        // assembly — what every distinct snippet does — must NOT invalidate
        // the metadata-reference cache. Under the old Length-based key this
        // exact sequence rebuilt (and re-leaked) all references.
        [Test]
        public void Compile_ByteLoadedAssemblyBetweenCompiles_DoesNotRebuildReferences()
        {
            if (!RoslynReady)
                Assert.Ignore("No loadable Roslyn on this editor (Unity 6000.x needs the IL-only fallback installed).");

            var marker = Guid.NewGuid().ToString("N");

            // Warm up until the reference count STOPS moving. Roslyn itself
            // loads file-backed assemblies lazily during its first compiles
            // (codegen pulls in System.Reflection.Metadata &c. after
            // BuildMetadataReferences already sampled the domain), so the first
            // one or two compiles legitimately rebuild. Those are Roslyn's own
            // loads, not the thing under test — asserting from compile #1
            // measured that confound instead of the invariant and failed on
            // every editor.
            byte[] peA = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var builds = RoslynHost.ReferenceBuildCount;
                string warmupError;
                (peA, warmupError) = RoslynHost.Compile(Src("Warm" + attempt + marker));
                Assert.IsNotNull(peA, "warm-up compile must succeed: " + warmupError);
                if (RoslynHost.ReferenceBuildCount == builds) break;
            }

            var buildsBefore = RoslynHost.ReferenceBuildCount;
            Assert.IsNotNull(peA);

            System.Reflection.Assembly.Load(peA);

            var (peB, errB) = RoslynHost.Compile(Src("B" + marker));
            Assert.IsNotNull(peB, "second compile must succeed: " + errB);

            Assert.AreEqual(buildsBefore, RoslynHost.ReferenceBuildCount,
                "a byte-loaded (empty-Location) assembly is not referencable and must not invalidate the metadata-reference cache");
        }

        // The no-fd-factory half of the fix: a full reference rebuild must not
        // hold file descriptors open (PrefetchMetadata copies the metadata
        // section and closes the stream). Pre-fix this leaked ~2 fds per
        // referenced assembly; the slack absorbs unrelated editor activity.
        [Test]
        public void Compile_FullReferenceRebuild_HoldsNoFdsOpen()
        {
            if (!RoslynReady)
                Assert.Ignore("No loadable Roslyn on this editor (Unity 6000.x needs the IL-only fallback installed).");

            var before = CountOpenFds();
            if (before < 0)
                Assert.Ignore("No /dev/fd or /proc/self/fd on this platform.");

            RoslynHost.ResetReferenceCacheForTests();
            var (pe, err) = RoslynHost.Compile(Src("Fd" + Guid.NewGuid().ToString("N")));
            Assert.IsNotNull(pe, "compile must succeed: " + err);

            var after = CountOpenFds();
            Assert.LessOrEqual(after - before, 60,
                $"a full metadata-reference rebuild leaked file descriptors: {before} -> {after}");

            // The stronger, non-statistical half of the same claim. A reference
            // built by CreateFromAssembly / CreateFromFile pins its PE for the
            // life of the reference and Roslyn exposes NO way to release it —
            // DisposeCachedReferences cannot close those, so every later
            // rebuild re-leaks whatever they hold. Both fd-free factories
            // (AssemblyMetadata.CreateFromStream, MetadataReference.
            // CreateFromImage) exist on every Roslyn this host loads, so the
            // file-backed pair must never be reached.
            Assert.AreEqual(0, RoslynHost.FileBackedReferenceCount,
                "no reference may come from a file-backed factory — those hold a descriptor " +
                "that nothing can close short of a domain reload");
        }

        // The snippet-cache half of the fix: an identical snippet re-run must
        // reuse its loaded assembly (source-keyed cache) instead of loading a
        // fresh copy into the AppDomain on every call.
        [Test]
        public void Execute_SameSnippetTwice_LoadsOneAssembly()
        {
            if (!RoslynReady)
                Assert.Ignore("No loadable Roslyn on this editor (Unity 6000.x needs the IL-only fallback installed).");

            var code = "return \"" + Guid.NewGuid().ToString("N") + "\";";
            var body = "{\"code\":" + BridgeJson.EscapeString(code) + "}";

            var before = ExecuteCSharpTool.CachedSnippetAssemblyCount;

            var first = ExecuteCSharpTool.Execute(body);
            Assert.IsTrue(first.Success, "first run failed: " + first.ErrorMessage);
            Assert.AreEqual(before + 1, ExecuteCSharpTool.CachedSnippetAssemblyCount,
                "first run of a fresh snippet must cache exactly one assembly");

            var second = ExecuteCSharpTool.Execute(body);
            Assert.IsTrue(second.Success, "second run failed: " + second.ErrorMessage);
            Assert.AreEqual(before + 1, ExecuteCSharpTool.CachedSnippetAssemblyCount,
                "re-running an identical snippet must reuse its cached assembly");
        }
    }
}
