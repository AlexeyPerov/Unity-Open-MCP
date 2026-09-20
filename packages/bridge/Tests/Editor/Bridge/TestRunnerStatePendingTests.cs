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
        private string markerDirectory;
        [SetUp] public void IsolateMarkers()
        {
            markerDirectory = Path.Combine(Path.GetTempPath(), "bridge-test-markers-" + System.Guid.NewGuid().ToString("N"));
            TestRunnerService.StatusDirOverride = markerDirectory;
        }
        [TearDown] public void RestoreMarkers()
        {
            TestRunnerService.StatusDirOverride = null;
            if (Directory.Exists(markerDirectory)) Directory.Delete(markerDirectory, true);
        }

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

        // -------------------------------------------------------------------
        // specs/feedback.md 2026-08-24 — a marker whose owning Editor process is
        // gone can never resume (the TestRunnerApi, its callbacks and the
        // collected results all died with the process), yet one survived a kill
        // + relaunch in the field and sat there for the full 1h TTL. The marker
        // now records the writing pid and the load-time sweep finalizes any
        // marker with a foreign pid.
        // -------------------------------------------------------------------

        [Test]
        public void MarkPending_RecordsTheWritingProcessId()
        {
            var runId = RunId("pid");
            try
            {
                TestRunnerState.MarkPending(runId, "A", null, null, null,
                    playMode: false, includePasses: true);
                var json = File.ReadAllText(TestRunnerService.PendingFilePath(runId));
                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                StringAssert.Contains("\"pid\":" + pid, json,
                    $"the marker must record the owning Editor process: {json}");
            }
            finally
            {
                Clear(runId);
            }
        }

        [Test]
        public void SweepDeadProcessMarkers_FinalizesDeadPidMarkers()
        {
            var runId = RunId("deadpid");
            try
            {
                // A marker that looks like it came from a previous Editor
                // process: fresh (well inside the TTL, so the TTL rule would
                // NOT have caught it) but owned by a pid that is not ours.
                var path = TestRunnerService.PendingFilePath(runId);
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                File.WriteAllText(path,
                    "{\"runId\":\"" + runId + "\",\"assemblyName\":\"GameTests\"," +
                    "\"testNamespace\":\"\",\"testClass\":\"\",\"testMethod\":\"\"," +
                    "\"playMode\":false,\"includePasses\":true,\"pid\":2147483647," +
                    "\"createdAt\":" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}");

                TestRunnerState.SweepDeadProcessMarkers();

                Assert.IsFalse(File.Exists(path),
                    "a marker from a dead Editor process must not survive the sweep");
                var resultsPath = TestRunnerService.ResultsFilePath(runId);
                Assert.IsTrue(File.Exists(resultsPath),
                    "the sweep must leave a TERMINAL file so a waiting poller stops");
                var json = File.ReadAllText(resultsPath);
                StringAssert.Contains("\"status\":\"aborted\"", json);
                StringAssert.Contains("\"reason\":\"editor_process_gone\"", json);
                StringAssert.Contains("\"mode\":\"EditMode\"", json,
                    "the swept run's mode comes from the marker, not a guess");
            }
            finally
            {
                ClearResults(runId);
                Clear(runId);
            }
        }

        [Test]
        public void EnteringPlayModePreservesPlayRunAndAbortsOnlyEditRun()
        {
            // Isolate the callback registry so this test cannot drain its own
            // native runner while exercising the real transition handler.
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var registry = (System.Collections.IList)typeof(TestRunnerState).GetField("ActiveRegistry", flags).GetValue(null);
            var saved = new object[registry.Count];
            registry.CopyTo(saved, 0);
            registry.Clear();
            var apis = new System.Collections.Generic.List<UnityEngine.ScriptableObject>();
            var entryType = typeof(TestRunnerState).GetNestedType("ActiveCallbacks", System.Reflection.BindingFlags.NonPublic);
            try
            {
                foreach (var mode in new[] { "EditMode", "PlayMode" })
                {
                    var entry = System.Activator.CreateInstance(entryType);
                    var api = UnityEngine.ScriptableObject.CreateInstance(entryType.GetField("Api").FieldType);
                    apis.Add(api);
                    entryType.GetField("Api").SetValue(entry, api);
                    entryType.GetField("RunId").SetValue(entry, mode);
                    entryType.GetField("Mode").SetValue(entry, mode);
                    registry.Add(entry);
                    TestRunnerState.MarkPending(mode, null, null, null, null, mode == "PlayMode");
                }
                typeof(TestRunnerState).GetMethod("OnPlayModeStateChanged", flags)
                    .Invoke(null, new object[] { UnityEditor.PlayModeStateChange.ExitingEditMode });
                Assert.AreEqual(1, registry.Count);
                Assert.AreEqual("PlayMode", entryType.GetField("Mode").GetValue(registry[0]));
                Assert.IsFalse(File.Exists(TestRunnerService.PendingFilePath("EditMode")));
                Assert.IsTrue(File.Exists(TestRunnerService.PendingFilePath("PlayMode")));
                StringAssert.Contains("playmode_entered", File.ReadAllText(TestRunnerService.ResultsFilePath("EditMode")));
                Assert.IsFalse(File.Exists(TestRunnerService.ResultsFilePath("PlayMode")));
            }
            finally
            {
                registry.Clear();
                foreach (var entry in saved) registry.Add(entry);
                foreach (var api in apis) if (api != null) UnityEngine.Object.DestroyImmediate(api);
            }
        }

        [Test]
        public void ForeignLiveProcessMarkerIsNeitherSweptNorSuperseded()
        {
            int foreignPid = 0;
            var processes = System.Diagnostics.Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                    if (process.Id > 0 && process.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                    { foreignPid = process.Id; break; }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            if (foreignPid == 0) Assert.Ignore("No foreign process available.");
            var runId = RunId("foreign-live");
            try
            {
                var json = "{\"runId\":\"" + runId + "\",\"playMode\":false,\"pid\":" + foreignPid + "}";
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                File.WriteAllText(TestRunnerService.PendingFilePath(runId), json);
                Assert.IsFalse(TestRunnerState.OwnsMarker(json));
                TestRunnerState.SweepDeadProcessMarkers();
                TestRunnerState.AbortOtherPendingRuns("another-run");
                Assert.IsTrue(File.Exists(TestRunnerService.PendingFilePath(runId)));
                Assert.IsFalse(File.Exists(TestRunnerService.ResultsFilePath(runId)));
            }
            finally { ClearResults(runId); Clear(runId); }
        }

        [Test]
        public void SweepDeadProcessMarkers_LeavesThisProcessMarkerAlone()
        {
            var runId = RunId("ourpid");
            try
            {
                // MarkPending stamps OUR pid — this may be a live run, so the
                // sweep must not touch it.
                TestRunnerState.MarkPending(runId, "A", null, null, null,
                    playMode: false, includePasses: true);

                TestRunnerState.SweepDeadProcessMarkers();

                Assert.IsTrue(File.Exists(TestRunnerService.PendingFilePath(runId)),
                    "a marker owned by the running Editor must survive the sweep");
                Assert.IsFalse(File.Exists(TestRunnerService.ResultsFilePath(runId)),
                    "a live run must not be given a terminal aborted file");
            }
            finally
            {
                ClearResults(runId);
                Clear(runId);
            }
        }

        [Test]
        public void SweepDeadProcessMarkers_LeavesPidlessMarkerToTheTtlRule()
        {
            var runId = RunId("nopid");
            try
            {
                // A marker written before the pid field existed. Absence is not
                // proof the owner is gone, so the sweep must defer to the TTL.
                var path = TestRunnerService.PendingFilePath(runId);
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                File.WriteAllText(path,
                    "{\"runId\":\"" + runId + "\",\"playMode\":false,\"includePasses\":true," +
                    "\"createdAt\":" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}");

                TestRunnerState.SweepDeadProcessMarkers();

                Assert.IsTrue(File.Exists(path),
                    "a pre-field marker must be left to the existing TTL rule");
            }
            finally
            {
                ClearResults(runId);
                Clear(runId);
            }
        }

        // -------------------------------------------------------------------
        // specs/feedback.md 2026-08-24 — `run_id` starts a run, it does not poll
        // one. IsRunInFlight is what lets RunTests refuse a re-call with an
        // in-flight id instead of silently aborting that run and starting a new
        // one under the same id (which rewrote the marker with the second call's
        // empty filters — the "params were dropped" symptom).
        // -------------------------------------------------------------------

        [Test]
        public void IsRunInFlight_TrueForAFreshMarker()
        {
            var runId = RunId("inflight");
            try
            {
                TestRunnerState.MarkPending(runId, "A", null, null, null,
                    playMode: false, includePasses: true);
                Assert.IsTrue(TestRunnerState.IsRunInFlight(runId));
            }
            finally
            {
                Clear(runId);
            }
        }

        [Test]
        public void IsRunInFlight_FalseWithNoMarkerAndForAStaleOne()
        {
            var missing = RunId("missing");
            Assert.IsFalse(TestRunnerState.IsRunInFlight(missing),
                "no marker means no run in flight");
            Assert.IsFalse(TestRunnerState.IsRunInFlight(null));
            Assert.IsFalse(TestRunnerState.IsRunInFlight(""));

            var stale = RunId("staleinflight");
            try
            {
                // Older than the TTL → a crashed run, so the id is reusable.
                var createdAt = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    - TestRunnerState.PendingTtlMs - 1000;
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                File.WriteAllText(TestRunnerService.PendingFilePath(stale),
                    "{\"runId\":\"" + stale + "\",\"playMode\":false,\"includePasses\":true," +
                    "\"createdAt\":" + createdAt + "}");
                Assert.IsFalse(TestRunnerState.IsRunInFlight(stale),
                    "a marker past the TTL is not an in-flight run");
            }
            finally
            {
                Clear(stale);
            }
        }
    }
}
