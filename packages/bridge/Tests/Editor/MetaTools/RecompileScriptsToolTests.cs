using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    // recompile_scripts worker-side settle rebuild. Execute() cannot observe
    // the compile it schedules — RequestScriptCompilation only queues work for
    // a future editor tick, which cannot run while Execute still holds the
    // main thread — so the dispatcher patches the payload AFTER the settle
    // wait via RebuildAfterSettle (on the worker thread). These tests pin that
    // contract without triggering a real recompile (which would domain-reload
    // the test run):
    //   - RebuildAfterSettle leaving unparseable / not-requested payloads
    //     untouched,
    //   - RebuildAfterSettle detecting a DLL mtime change from a synthetic
    //     ScriptAssemblies dir (the recompiled:true flip) and carrying the
    //     scriptImport block through, and
    //   - the script-import planning that brings new / deleted scripts in
    //     before the compile is requested (ScriptImportSync, pure parts).
    public class RecompileScriptsToolTests
    {
        [Test]
        public void RebuildAfterSettle_NotRequested_ReturnsInputUnchanged()
        {
            // requested:false means nothing was scheduled — no worker-side wait.
            var output = "{\"status\":\"compile_requested\",\"requested\":false,\"wasCompiling\":false," +
                "\"recompiled\":false,\"isCompiling\":false,\"dllMtimeBefore\":1," +
                "\"dllMtimeAfter\":1,\"scriptAssembliesDir\":\"X\"}";
            var rebuilt = RecompileScriptsTool.RebuildAfterSettle(output, out var extraWaitMs);
            Assert.AreSame(output, rebuilt);
            Assert.AreEqual(0, extraWaitMs);
        }

        [Test]
        public void RebuildAfterSettle_UnparseableOutput_ReturnsInputUnchanged()
        {
            var rebuilt = RecompileScriptsTool.RebuildAfterSettle(null, out var extraWaitMs);
            Assert.IsNull(rebuilt);
            Assert.AreEqual(0, extraWaitMs);
        }

        [Test]
        public void RebuildAfterSettle_DllNewerThanSnapshot_ReportsRecompiled()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "unity-open-mcp-recompile-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "Fake.dll"), "not a real dll");
                // dllMtimeBefore:1 is older than any real mtime, so the grace
                // loop exits immediately (mtime changed) and the rebuilt
                // payload must flip recompiled:true with the contract fields
                // intact.
                var output = "{\"status\":\"compile_requested\",\"requested\":true,\"wasCompiling\":false," +
                    "\"recompiled\":false,\"isCompiling\":false,\"dllMtimeBefore\":1," +
                    "\"dllMtimeAfter\":1,\"scriptAssembliesDir\":" + BridgeJson.EscapeString(dir) +
                    ",\"compileGenerationAtStart\":7," +
                    "\"scriptImport\":{\"action\":\"imported\",\"newScripts\":[\"Assets/New.cs\"],\"newCount\":1,\"deletedScripts\":[],\"deletedCount\":0}}";
                var rebuilt = RecompileScriptsTool.RebuildAfterSettle(output, out _);
                Assert.IsTrue(BridgeJson.IsValidJsonObject(rebuilt),
                    "rebuilt payload must be valid JSON: " + rebuilt);
                StringAssert.Contains("\"recompiled\":true", rebuilt);
                StringAssert.Contains("\"dllMtimeBefore\":1", rebuilt);
                StringAssert.Contains("\"compileGenerationAtStart\":7", rebuilt);
                StringAssert.Contains("\"status\":\"compile_requested\"", rebuilt);
                StringAssert.Contains("\"scriptImport\":{\"action\":\"imported\"", rebuilt);
                StringAssert.DoesNotContain("\"status\":\"ok\"", rebuilt,
                    "the request payload never claims the compile succeeded");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void ScriptImportSync_Detect_FindsUnknownScriptsAndMissingCompiledInputs()
        {
            var known = new HashSet<string> { "Assets/Old.cs", "Assets/Game.asmdef" };
            var onDisk = new[] { "Assets/Old.cs", "Assets/Game.asmdef", "Assets/New/NewType.cs", "Assets/Uses.asmref" };
            var compiled = new[] { "Assets/Old.cs", "Assets/Gone.cs", "Assets/Game.asmdef", "Library/PackageCache/x/Y.cs" };
            var exists = new HashSet<string> { "Assets/Old.cs", "Assets/Game.asmdef" };

            var plan = ScriptImportSync.Detect(onDisk, known.Contains, compiled,
                path => path.StartsWith("Assets/"), exists.Contains);

            CollectionAssert.AreEqual(new[] { "Assets/New/NewType.cs", "Assets/Uses.asmref" }, plan.NewPaths);
            CollectionAssert.AreEqual(new[] { "Assets/Gone.cs" }, plan.DeletedPaths,
                "an immutable package's source is never reported as deleted");
        }

        [Test]
        public void ScriptImportSync_Detect_InSyncProjectPlansNothing()
        {
            var paths = new[] { "Assets/A.cs", "Assets/B.cs" };
            var plan = ScriptImportSync.Detect(paths, _ => true, paths, _ => true, _ => true, _ => false);
            Assert.IsEmpty(plan.NewPaths);
            Assert.IsEmpty(plan.ModifiedPaths);
            Assert.IsEmpty(plan.DeletedPaths);
        }

        [Test]
        public void ScriptImportSync_Detect_ReimportsOnlyEditedAssemblyDefinitions()
        {
            var onDisk = new[] { "Assets/Game.asmdef", "Assets/Tests/Tests.asmdef", "Assets/Uses.asmref", "Assets/Edited.cs", "Assets/New.asmdef" };
            var known = new HashSet<string> { "Assets/Game.asmdef", "Assets/Tests/Tests.asmdef", "Assets/Uses.asmref", "Assets/Edited.cs" };
            // Every file reads as changed: only known assembly definitions are
            // re-imported (an edited .cs compiles from disk; a new file is new).
            var plan = ScriptImportSync.Detect(onDisk, known.Contains, onDisk, _ => true, _ => true,
                path => path != "Assets/Game.asmdef");

            CollectionAssert.AreEqual(new[] { "Assets/Tests/Tests.asmdef", "Assets/Uses.asmref" }, plan.ModifiedPaths);
            CollectionAssert.AreEqual(new[] { "Assets/New.asmdef" }, plan.NewPaths);
        }

        [Test]
        public void ScriptImportSync_ImportTargets_ImportsTheTopmostUnknownFolderOnce()
        {
            var knownFolders = new HashSet<string> { "Assets", "Assets/Scripts" };
            var targets = ScriptImportSync.ImportTargets(new[]
            {
                "Assets/Scripts/Direct.cs",          // in a known folder: the file itself
                "Assets/Scratch/NewType.cs",         // new folder: the folder, recursively
                "Assets/Scratch/Deep/UsesIt.cs",     // inside that folder: covered already
                "Assets/Scripts/Fresh/Other.cs",     // new sub-folder of a known one
            }, knownFolders.Contains);

            CollectionAssert.AreEquivalent(new[]
            {
                ("Assets/Scripts/Direct.cs", false),
                ("Assets/Scratch", true),
                ("Assets/Scripts/Fresh", true),
            }, targets.Select(t => (t.Path, t.Recursive)).ToArray());
        }

        [Test]
        public void ScriptImportSync_OutcomeJson_ListsWhatItImported()
        {
            var outcome = new ScriptImportSync.Outcome { Action = "imported" };
            outcome.Plan.NewPaths.Add("Assets/New.cs");
            var json = outcome.ToJson();
            Assert.IsTrue(BridgeJson.IsValidJsonObject(json), json);
            StringAssert.Contains("\"action\":\"imported\"", json);
            StringAssert.Contains("\"newScripts\":[\"Assets/New.cs\"]", json);
            StringAssert.Contains("\"newCount\":1", json);
            StringAssert.Contains("\"modifiedCount\":0", json);
            StringAssert.Contains("\"deletedCount\":0", json);
        }
    }
}
