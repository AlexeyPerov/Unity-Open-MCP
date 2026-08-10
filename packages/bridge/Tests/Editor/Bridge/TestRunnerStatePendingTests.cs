using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.TestRunner;

namespace UnityOpenMcpBridge.Tests
{
    // Covers the on-disk pending-file contract that TestRunnerState uses to
    // survive domain reloads. MarkPending writes the signal for ALL modes
    // (PlayMode + EditMode); OnAfterAssemblyReload reads it back to reattach
    // callbacks. The pending file MUST persist the real test mode so reattach
    // does not guess — see code-review finding B8.
    public class TestRunnerStatePendingTests
    {
        // Each test uses a unique runId and clears its pending file in cleanup
        // so the suite never leaves a marker that OnAfterAssemblyReload would
        // pick up on the next domain reload.
        private static string RunId(string suffix) =>
            "b8test-" + suffix + "-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

        private static void Clear(string runId)
        {
            try
            {
                var path = TestRunnerService.PendingFilePath(runId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        // -------------------------------------------------------------------
        // Regression: code-review finding B8 — TestRunnerState.MarkPending did
        // not persist the test mode, so OnAfterAssemblyReload had to guess and
        // always reattached as PlayMode. That made the Editor enter PlayMode
        // and run PlayMode tests unasked on every recompile while a pending
        // file existed — including the recompile an EditMode run can trigger.
        // The fix adds a playMode field to the pending file so reattach can
        // restore the correct mode (and, per the ReattachCallbacks rewrite,
        // only re-register callbacks rather than starting a fresh run).
        // -------------------------------------------------------------------

        [Test]
        public void MarkPending_PlayMode_PersistsPlayModeTrue()
        {
            var runId = RunId("play");
            try
            {
                TestRunnerState.MarkPending(runId, "MyAsm", null, null, null,
                    playMode: true, includePasses: true);
                var json = File.ReadAllText(TestRunnerService.PendingFilePath(runId));
                // The playMode field must be present and true — the pre-fix
                // pending file had no such field, forcing reattach to assume
                // PlayMode unconditionally.
                StringAssert.Contains("\"playMode\":true", json);
                StringAssert.Contains("\"includePasses\":true", json);
            }
            finally
            {
                Clear(runId);
            }
        }

        [Test]
        public void MarkPending_EditMode_PersistsPlayModeFalse()
        {
            var runId = RunId("edit");
            try
            {
                TestRunnerState.MarkPending(runId, "MyAsm", null, null, null,
                    playMode: false, includePasses: false);
                var json = File.ReadAllText(TestRunnerService.PendingFilePath(runId));
                // playMode must be false for an EditMode run — the pre-fix bug
                // meant an EditMode run's pending file was indistinguishable
                // from a PlayMode run's, so reattach started a PlayMode run.
                StringAssert.Contains("\"playMode\":false", json);
                StringAssert.Contains("\"includePasses\":false", json);
            }
            finally
            {
                Clear(runId);
            }
        }

        [Test]
        public void MarkPending_RoundTripsAllFields()
        {
            // The pending file is read back by OnAfterAssemblyReload to
            // reattach callbacks; every field MarkPending writes must
            // round-trip through JsonBody readers.
            var runId = RunId("rt");
            try
            {
                TestRunnerState.MarkPending(runId, "My.Assembly", "NS", "Cls", "Method",
                    playMode: true, includePasses: true);
                var json = File.ReadAllText(TestRunnerService.PendingFilePath(runId));

                Assert.AreEqual(runId, JsonBody.GetString(json, "runId"));
                Assert.AreEqual("My.Assembly", JsonBody.GetString(json, "assemblyName"));
                Assert.AreEqual("NS", JsonBody.GetString(json, "testNamespace"));
                Assert.AreEqual("Cls", JsonBody.GetString(json, "testClass"));
                Assert.AreEqual("Method", JsonBody.GetString(json, "testMethod"));
                Assert.IsTrue(JsonBody.GetBool(json, "playMode", false),
                    "playMode must round-trip as true: " + json);
                Assert.IsTrue(JsonBody.GetBool(json, "includePasses", false),
                    "includePasses must round-trip as true: " + json);
            }
            finally
            {
                Clear(runId);
            }
        }

        // -------------------------------------------------------------------
        // Regression: code-review finding B9 — a failed TestRunnerApi.Execute
        // (or a domain reload landing between MarkPending and the delayCall
        // closure firing) left the test-pending-*.json marker on disk forever,
        // because ClearPending ran only in onFinished, which never fired. That
        // permanently poisoned the editor: every subsequent script recompile
        // triggered OnAfterAssemblyReload → ReattachCallbacks → (pre-fix B8) a
        // fresh PlayMode run. The fix clears the marker in the Execute catch
        // AND stamps createdAt on every pending file so OnAfterAssemblyReload
        // can discard a stale marker (failed run, force-quit) instead of
        // reattaching forever.
        // -------------------------------------------------------------------

        [Test]
        public void MarkPending_WritesCreatedAtTimestamp()
        {
            // createdAt is what lets OnAfterAssemblyReload tell a fresh marker
            // (in-flight run) from a stale one (leaked from a failed run). It
            // must be present and a positive unix-ms value.
            var runId = RunId("ts");
            try
            {
                var before = ((System.DateTimeOffset)System.DateTime.UtcNow).ToUnixTimeMilliseconds();
                TestRunnerState.MarkPending(runId, null, null, null, null,
                    playMode: false, includePasses: true);
                var after = ((System.DateTimeOffset)System.DateTime.UtcNow).ToUnixTimeMilliseconds();
                var json = File.ReadAllText(TestRunnerService.PendingFilePath(runId));

                var createdAt = JsonBody.GetLong(json, "createdAt", 0);
                Assert.Greater(createdAt, 0, "createdAt must be present and positive: " + json);
                Assert.GreaterOrEqual(createdAt, before, "createdAt must be >= pre-call time");
                Assert.LessOrEqual(createdAt, after, "createdAt must be <= post-call time");
            }
            finally
            {
                Clear(runId);
            }
        }

        [Test]
        public void IsPendingStale_OlderThanTtl_IsStale()
        {
            // A marker older than the TTL is from a leaked run — must be
            // treated as stale so OnAfterAssemblyReload discards it.
            var now = 1_000_000_000L; // arbitrary fixed "now"
            var stale = now - TestRunnerState.PendingTtlMs - 1;
            Assert.IsTrue(TestRunnerState.IsPendingStale(stale, now),
                $"createdAt {stale} (>{TestRunnerState.PendingTtlMs}ms before {now}) must be stale");
        }

        [Test]
        public void IsPendingStale_WithinTtl_IsNotStale()
        {
            // A fresh marker is an in-flight run — must NOT be discarded.
            var now = 1_000_000_000L;
            var fresh = now - TestRunnerState.PendingTtlMs + 1;
            Assert.IsFalse(TestRunnerState.IsPendingStale(fresh, now),
                $"createdAt {fresh} (<={TestRunnerState.PendingTtlMs}ms before {now}) must not be stale");
        }

        [Test]
        public void IsPendingStale_AbsentCreatedAt_IsNotStale()
        {
            // A pending file written before this fix landed has no createdAt
            // field (parsed as 0). Treat it as fresh so an in-flight run on a
            // just-upgraded editor is not silently discarded.
            Assert.IsFalse(TestRunnerState.IsPendingStale(0, 1_000_000_000L),
                "absent createdAt (0) must be treated as fresh, not stale");
        }

        // -------------------------------------------------------------------
        // feedback #7 (2026-08-07) — an aborted run must leave a TERMINAL file
        // so a polling agent sees a final state instead of silence. A new run
        // starting (or play mode entered mid-EditMode-run) sweeps other runs'
        // pending markers and writes an `aborted` results file for each. The
        // poller in live-client.ts consumes test-results-*.json regardless of
        // status, so `aborted` surfaces as a parseable terminal body.
        // -------------------------------------------------------------------

        private static void ClearResults(string runId)
        {
            try
            {
                var path = TestRunnerService.ResultsFilePath(runId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        [Test]
        public void WriteAbortedFile_WritesTerminalAbortedStatusAndClearsPending()
        {
            var runId = RunId("abort");
            try
            {
                // Seed a pending marker (the swept run had one) + no results yet.
                TestRunnerState.MarkPending(runId, "A", null, null, null,
                    playMode: false, includePasses: true);
                Assert.IsTrue(File.Exists(TestRunnerService.PendingFilePath(runId)),
                    "precondition: pending marker exists");

                TestRunnerService.WriteAbortedFile(runId, "EditMode", "superseded_by_run");

                var resultsPath = TestRunnerService.ResultsFilePath(runId);
                Assert.IsTrue(File.Exists(resultsPath),
                    "an aborted run must write a terminal results file");
                var json = File.ReadAllText(resultsPath);
                StringAssert.Contains("\"status\":\"aborted\"", json);
                StringAssert.Contains("\"reason\":\"superseded_by_run\"", json);
                StringAssert.Contains("\"mode\":\"EditMode\"", json);
                // The pending marker must be cleared so a later reload does not
                // reattach a finished run.
                Assert.IsFalse(File.Exists(TestRunnerService.PendingFilePath(runId)),
                    "aborted file must clear the pending marker");
            }
            finally
            {
                ClearResults(runId);
                Clear(runId);
            }
        }

        [Test]
        public void AbortOtherPendingRuns_AbortsOtherRuns_LeavesCurrentAlone()
        {
            var current = RunId("cur");
            var other = RunId("oth");
            try
            {
                // Two pending markers: the new (current) run + a stale one from
                // a prior crashed run.
                TestRunnerState.MarkPending(current, "A", null, null, null,
                    playMode: false, includePasses: true);
                TestRunnerState.MarkPending(other, "A", null, null, null,
                    playMode: true, includePasses: true);

                TestRunnerState.AbortOtherPendingRuns(current);

                // The other run gets a terminal aborted file + its marker gone.
                Assert.IsTrue(File.Exists(TestRunnerService.ResultsFilePath(other)),
                    "the swept run must get a terminal aborted file");
                var otherJson = File.ReadAllText(TestRunnerService.ResultsFilePath(other));
                StringAssert.Contains("\"status\":\"aborted\"", otherJson);
                Assert.IsFalse(File.Exists(TestRunnerService.PendingFilePath(other)),
                    "the swept run's pending marker must be cleared");

                // The current run's marker is untouched (it's about to start).
                Assert.IsTrue(File.Exists(TestRunnerService.PendingFilePath(current)),
                    "the current run's pending marker must survive");
                Assert.IsFalse(File.Exists(TestRunnerService.ResultsFilePath(current)),
                    "the current run must not get an aborted file");
            }
            finally
            {
                ClearResults(current);
                Clear(current);
                ClearResults(other);
                Clear(other);
            }
        }
    }
}
