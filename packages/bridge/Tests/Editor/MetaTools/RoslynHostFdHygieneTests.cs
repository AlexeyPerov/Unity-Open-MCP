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
            var (peA, errA) = RoslynHost.Compile(Src("A" + marker));
            Assert.IsNotNull(peA, "warm-up compile must succeed: " + errA);

            var buildsBefore = RoslynHost.ReferenceBuildCount;

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
