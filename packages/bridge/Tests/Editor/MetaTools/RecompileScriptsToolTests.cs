using System.IO;
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
    //   - the paths_hint guard,
    //   - RebuildAfterSettle leaving unparseable / not-requested payloads
    //     untouched, and
    //   - RebuildAfterSettle detecting a DLL mtime change from a synthetic
    //     ScriptAssemblies dir (the recompiled:true flip).
    public class RecompileScriptsToolTests
    {
        [Test]
        public void Execute_MissingPathsHint_ReturnsPathsHintRequired()
        {
            var result = RecompileScriptsTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void RebuildAfterSettle_NotRequested_ReturnsInputUnchanged()
        {
            // requested:false means nothing was scheduled — the payload keeps
            // its request-error guidance and no worker-side wait happens.
            var output = "{\"status\":\"ok\",\"requested\":false,\"wasCompiling\":false," +
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
                var output = "{\"status\":\"ok\",\"requested\":true,\"wasCompiling\":false," +
                    "\"recompiled\":false,\"isCompiling\":false,\"dllMtimeBefore\":1," +
                    "\"dllMtimeAfter\":1,\"scriptAssembliesDir\":" + BridgeJson.EscapeString(dir) + "}";
                var rebuilt = RecompileScriptsTool.RebuildAfterSettle(output, out _);
                Assert.IsTrue(BridgeJson.IsValidJsonObject(rebuilt),
                    "rebuilt payload must be valid JSON: " + rebuilt);
                StringAssert.Contains("\"recompiled\":true", rebuilt);
                StringAssert.Contains("\"dllMtimeBefore\":1", rebuilt);
                StringAssert.Contains("\"agentNextSteps\":", rebuilt);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
