using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.TestRunner
{
    [InitializeOnLoad]
    public static class TestRunnerState
    {
        // B9 — pending markers from a failed/crashed run (Execute threw, the
        // editor was force-quit mid-run, the onFinished callback never fired)
        // would otherwise linger forever and make OnAfterAssemblyReload
        // reattach callbacks on every subsequent recompile — arming the B8 bug
        // (unwanted PlayMode run) for the rest of the session. A pending marker
        // older than this TTL is treated as stale: skipped AND deleted on
        // reattach. One hour is far longer than any real test run (PlayMode
        // including a recompile finishes in minutes); only a leaked marker
        // survives that long.
        internal const long PendingTtlMs = 60 * 60 * 1000; // 1 hour

        // A6 — registry of every (api, callbacks) pair currently subscribed to
        // the framework's domain-level callback holder, keyed by runId. The
        // framework's RegisterCallbacks stores the ICallbacks in a holder that
        // survives for the domain's lifetime; DestroyImmediate(api) does NOT
        // detach it. So if a run's onFinished never fires (the user pressed Stop
        // during PlayMode, or a reload aborted an EditMode run, or a reattached
        // run simply never resumed), that callbacks instance — closure and all —
        // stays subscribed. The NEXT run's TestFinished/onFinished then fires it
        // too, appending the new run's results to the leaked list and rewriting
        // test-results-<oldRunId>.json with the wrong counts.
        //
        // Guard: before registering fresh pairs, DrainActiveCallbacks()
        // unregisters and destroys every pair still in this registry whose
        // onFinished never ran. A pair whose onFinished DID fire has already
        // removed itself, so only genuine leaks are swept. The drain runs in
        // RunTestsTool.StartRunDeferred (a new run sweeps leftovers) and ONCE
        // in OnAfterAssemblyReload before the pending-marker loop — NOT inside
        // ReattachCallbacks, which may run several times per reload and would
        // otherwise destroy the pair the previous loop iteration registered
        // (B-R3). The registry is also drained on beforeAssemblyReload as a
        // belt-and-suspenders cleanup before the domain ends.
        private static readonly List<ActiveCallbacks> ActiveRegistry = new List<ActiveCallbacks>();

        private struct ActiveCallbacks
        {
            public TestRunnerApi Api;
            public TestCallbacks Callbacks;
            public string RunId;
            public string Mode;
        }

        static TestRunnerState()
        {
            // A destroyed runner can leave a callback registration after terminal output.
            // The pending marker, cleared before result publication, owns execution.
            ProjectCommandJobs.TestRunActive = () => Tool_TestRunner.RunScheduled
                || ActiveRegistry.Exists(a => File.Exists(PendingFilePath(a.RunId)));
            // specs/feedback.md 2026-08-24 — finalize markers from a DEAD Editor
            // process before anything else. On a fresh Editor launch
            // AssemblyReloadEvents.afterAssemblyReload does not fire, so the
            // marker loop below never ran and a marker from the previous
            // (killed) process survived for the full 1h TTL. Safe to run on
            // every domain reload too: a live run's marker carries THIS pid and
            // is skipped.
            SweepDeadProcessMarkers();

            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            // feedback #7 — entering play mode aborts any in-flight EditMode
            // run (the framework does not deliver its onFinished). Without a
            // terminal file the polling agent sees silence until the TTL sweep
            // (an hour later). Write an aborted file so the agent sees a final
            // state. PlayMode runs are not aborted by this transition.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Only the EnteringPlayMode edge aborts an EditMode run. EditMode
            // runs are synchronous on the main thread and cannot survive a
            // play-mode entry; their onFinished never fires.
            if (state != PlayModeStateChange.ExitingEditMode) return;
            DrainActiveCallbacks("playmode_entered");
        }

        /// <summary>Track a freshly registered (api, callbacks) pair so a leaked
        /// instance can be unregistered later. Called AFTER
        /// api.RegisterCallbacks(callbacks).</summary>
        internal static void RegisterActive(TestRunnerApi api, TestCallbacks callbacks, string runId)
            => RegisterActive(api, callbacks, runId, null);

        internal static void RegisterActive(TestRunnerApi api, TestCallbacks callbacks, string runId, string mode)
        {
            if (api == null || callbacks == null) return;
            ActiveRegistry.Add(new ActiveCallbacks { Api = api, Callbacks = callbacks, RunId = runId, Mode = mode });
        }

        /// <summary>Remove a pair from the registry (its onFinished fired). Does
        /// NOT unregister from the framework — the caller already did. Kept
        /// distinct from DrainActiveCallbacks so the happy path does no work
        /// beyond a list remove.</summary>
        internal static void UnregisterActive(TestRunnerApi api, TestCallbacks callbacks)
        {
            if (api == null && callbacks == null) return;
            ActiveRegistry.RemoveAll(a => ReferenceEquals(a.Api, api) && ReferenceEquals(a.Callbacks, callbacks));
        }

        /// <summary>Unregister + destroy every pair still in the registry. Only
        /// leaks reach here: a pair whose onFinished fired already removed
        /// itself. Safe to call when the registry is empty.</summary>
        /// <param name="reason">feedback #7 — when non-null, each swept run also
        /// gets a terminal `aborted` file so a polling agent sees a final state
        /// instead of silence. `null` suppresses the write (used by the
        /// beforeAssemblyReload drain, where the domain is ending and a pending
        /// PlayMode run is expected to resume).</param>
        internal static void DrainActiveCallbacks(string reason = null)
        {
            if (ActiveRegistry.Count == 0) return;
            // Iterate over a snapshot — UnregisterActive mutates the list.
            var snapshot = ActiveRegistry.ToArray();
            ActiveRegistry.Clear();
            foreach (var entry in snapshot)
            {
                if (entry.Api != null && entry.Callbacks != null)
                {
                    try { entry.Api.UnregisterCallbacks(entry.Callbacks); } catch { }
                }
                if (entry.Api != null)
                {
                    try { Object.DestroyImmediate(entry.Api); } catch { }
                }
                // feedback #7 — leave a terminal file so the poller stops
                // waiting. Only when a reason is supplied (superseded by a new
                // run, or play mode entered); the beforeAssemblyReload drain
                // passes null because a resumable PlayMode run should NOT be
                // marked aborted.
                if (reason != null && !string.IsNullOrEmpty(entry.RunId))
                {
                    TestRunnerService.WriteAbortedFile(entry.RunId, entry.Mode, reason);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            // A6 — a domain reload destroys every ScriptableObject and its
            // closures anyway, so the leaked-instance leak cannot survive the
            // reload. But the registry is a domain-lifetime structure: clear it
            // explicitly so it never holds references across the reload (which
            // would otherwise keep dead objects alive past their natural
            // lifetime and confuse a reader inspecting it post-reload). The
            // pending-marker side of cleanup is handled by the TTL in
            // OnAfterAssemblyReload; we deliberately do NOT clear pending
            // markers here, because a legitimate in-flight PlayMode run is
            // EXPECTED to resume after the reload and needs its reattach.
            DrainActiveCallbacks();
        }

        public static void MarkPending(
            string runId,
            string assemblyName,
            string testNamespace,
            string testClass,
            string testMethod,
            bool playMode,
            bool includePasses = true)
        {
            try
            {
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                var sb = new StringBuilder(256);
                sb.Append('{');
                sb.Append("\"runId\":").Append(TestRunnerService.EscapeString(runId)).Append(',');
                sb.Append("\"assemblyName\":").Append(TestRunnerService.EscapeString(assemblyName ?? "")).Append(',');
                sb.Append("\"testNamespace\":").Append(TestRunnerService.EscapeString(testNamespace ?? "")).Append(',');
                sb.Append("\"testClass\":").Append(TestRunnerService.EscapeString(testClass ?? "")).Append(',');
                sb.Append("\"testMethod\":").Append(TestRunnerService.EscapeString(testMethod ?? "")).Append(',');
                // B8 — persist the real test mode so OnAfterAssemblyReload can
                // reattach callbacks for the correct run without guessing
                // (the previous form always assumed PlayMode and started a
                // fresh PlayMode run on every recompile, even for EditMode).
                sb.Append("\"playMode\":").Append(playMode ? "true" : "false").Append(',');
                sb.Append("\"includePasses\":").Append(includePasses ? "true" : "false");
                // specs/feedback.md 2026-08-24 — record the Editor process that
                // owns this run. A marker whose pid is not the CURRENT process
                // was written by an Editor that is gone (killed, crashed,
                // relaunched), so its run can never resume: the TestRunnerApi
                // and its callbacks died with the process. SweepDeadProcessMarkers
                // finalizes those on load instead of leaving them to the 1h TTL —
                // the field report found one still sitting there after the Editor
                // was killed and relaunched, and any MCP poller still waiting on
                // that run saw silence until its own timeout.
                sb.Append(",\"pid\":").Append(
                    System.Diagnostics.Process.GetCurrentProcess().Id
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
                // B9 — record when the marker was written so OnAfterAssemblyReload
                // can discard a stale one (failed Execute, force-quit, lost
                // onFinished) instead of reattaching on every future recompile.
                sb.Append(",\"createdAt\":").Append(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('}');
                File.WriteAllText(PendingFilePath(runId), sb.ToString());
            }
            catch { }
        }

        public static void ClearPending(string runId)
        {
            try
            {
                var path = PendingFilePath(runId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        // feedback #7 — when a new run starts, any OTHER run's lingering
        // test-pending-*.json marker belongs to a run that will never produce
        // results (the single-run model supersedes it; or it crashed / was
        // force-quit and the TTL sweep would only clear it an hour later).
        // Write a terminal `aborted` file for each so a polling agent sees a
        // final state. The new run's own marker is left untouched.
        internal static void AbortOtherPendingRuns(string currentRunId)
        {
            try
            {
                if (!Directory.Exists(TestRunnerService.StatusDir)) return;
                foreach (var file in Directory.GetFiles(TestRunnerService.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var runId = JsonBody.GetString(json, "runId");
                    if (string.IsNullOrEmpty(runId) || runId == currentRunId) continue;
                    var playMode = JsonBody.GetBool(json, "playMode", true);
                    TestRunnerService.WriteAbortedFile(runId, playMode ? "PlayMode" : "EditMode", "superseded_by_run");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunnerState] AbortOtherPendingRuns error: {ex.Message}");
            }
        }

        // specs/feedback.md 2026-08-24 — is a run with this id still in flight?
        //
        // `run_id` is a START-time id, not a poll handle, but its schema
        // description read like one: the field report re-called run_tests with
        // the runId from the first response to "poll" it and got
        // {status:"started"} again — because the second call STARTED A SECOND
        // RUN under the same id, which superseded the first, aborted its
        // callbacks, and rewrote its pending marker with the second call's
        // (empty) filters. The visible symptom was "the batch route drops the
        // declared params"; the actual cause was this silent restart.
        //
        // A fresh, non-stale marker for the requested id means a run is still
        // in flight, so RunTests refuses instead of clobbering it. Markers past
        // the TTL (a crashed run) are NOT in flight and the id is reusable.
        internal static bool IsRunInFlight(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return false;
            try
            {
                var path = PendingFilePath(runId);
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                var createdAtMs = JsonBody.GetLong(json, "createdAt", 0);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return !IsPendingStale(createdAtMs, nowMs);
            }
            catch
            {
                // Unreadable marker — do not block the run on a filesystem
                // hiccup. The worst case is the pre-existing behavior.
                return false;
            }
        }

        // specs/feedback.md 2026-08-24 — finalize every pending marker written by
        // an Editor process that no longer exists.
        //
        // Such a run is unrecoverable by construction: the TestRunnerApi
        // instance, its callbacks and the collected results all lived in that
        // process's managed heap. Nothing will ever write its results file, so
        // the marker is pure litter AND an MCP poller still waiting on the run
        // gets silence until its own timeout. WriteAbortedFile gives the poller a
        // terminal answer and clears the marker.
        //
        // Deliberately narrower than the TTL rule below: this only touches
        // markers whose pid is present and foreign. A marker with no pid predates
        // this field, and one with THIS pid may be a live run — both are left to
        // the existing TTL/reattach logic.
        internal static void SweepDeadProcessMarkers()
        {
            try
            {
                if (!Directory.Exists(TestRunnerService.StatusDir)) return;
                var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var file in Directory.GetFiles(TestRunnerService.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var runId = JsonBody.GetString(json, "runId");
                    if (string.IsNullOrEmpty(runId)) continue;
                    var pid = JsonBody.GetLong(json, "pid", 0);
                    // 0 = absent (pre-field marker). Only a KNOWN foreign pid is
                    // proof the owning process is gone.
                    if (pid <= 0 || pid == currentPid) continue;
                    var playMode = JsonBody.GetBool(json, "playMode", true);
                    TestRunnerService.WriteAbortedFile(
                        runId, playMode ? "PlayMode" : "EditMode", "editor_process_gone");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunnerState] SweepDeadProcessMarkers error: {ex.Message}");
            }
        }

        private static void OnAfterAssemblyReload()
        {
            try
            {
                Directory.CreateDirectory(TestRunnerService.StatusDir);

                // A6/B-R3 — sweep leaked (api, callbacks) pairs ONCE, before
                // the marker loop. Draining inside ReattachCallbacks destroyed
                // the pair the previous loop iteration had just registered
                // whenever multiple pending markers exist (e.g. a stale-but-
                // within-TTL marker plus a genuinely in-flight PlayMode run),
                // leaving only the last-enumerated marker with live callbacks.
                DrainActiveCallbacks();

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var file in Directory.GetFiles(TestRunnerService.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var runId = JsonBody.GetString(json, "runId");
                    if (string.IsNullOrEmpty(runId)) continue;

                    // B9 — discard stale markers. A pending file older than the
                    // TTL is from a failed/crashed run (Execute threw and the
                    // catch cleared it, OR the editor was force-quit before
                    // onFinished fired, OR an even older pre-fix leak). Reattaching
                    // would arm the B8 bug on every future recompile. createdAt
                    // is absent on pending files written before this fix landed;
                    // those are assumed fresh (0 → not stale) so an in-flight run
                    // from a just-upgraded editor is not discarded.
                    var createdAtMs = JsonBody.GetLong(json, "createdAt", 0);
                    if (IsPendingStale(createdAtMs, nowMs))
                    {
                        try { File.Delete(file); } catch { }
                        continue;
                    }

                    // B8 — read the persisted mode. Pending files written before
                    // this fix lacked the field; default to PlayMode to preserve
                    // the prior behaviour for in-flight PlayMode runs (the only
                    // case where reattach is meaningful).
                    var playMode = JsonBody.GetBool(json, "playMode", true);
                    var includePasses = JsonBody.GetBool(json, "includePasses", true);

                    // The marker's filter fields (assemblyName / testNamespace /
                    // testClass / testMethod) are NOT read here: reattach only
                    // re-registers callbacks for the run the framework is already
                    // resuming, and re-deriving a Filter would mean calling
                    // Execute again — the B8 bug. They stay in the marker as
                    // human-readable provenance for whoever inspects the file.
                    ReattachCallbacks(runId, playMode, includePasses);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunnerState] OnAfterAssemblyReload error: {ex.Message}");
            }
        }

        // B9 — is a pending marker stale? Pure decision split out so the TTL
        // boundary is unit-testable without synthesizing clock skew. A marker
        // with createdAt == 0 (absent — pre-fix file, or MarkPending failed
        // mid-write) is treated as fresh so an in-flight run is not discarded.
        internal static bool IsPendingStale(long createdAtMs, long nowMs)
        {
            if (createdAtMs <= 0) return false;
            return (nowMs - createdAtMs) > PendingTtlMs;
        }

        // B8 — reattach MUST only re-register callbacks, never call Execute.
        // The previous form built a PlayMode filter unconditionally and called
        // api.Execute(...), which STARTS A NEW RUN rather than resuming the
        // in-flight one. Unity's TestRunnerApi tracks the running run by GUID;
        // after a domain reload the framework resumes automatically and
        // delivers events to callbacks re-registered via RegisterCallbacks
        // (the canonical [InitializeOnLoad] pattern from the Unity docs).
        // Re-calling Execute made the Editor enter PlayMode and run PlayMode
        // tests unasked on every recompile while a pending file existed —
        // including the recompile an EditMode run can trigger — and the
        // spurious run's onFinished then overwrote the real results file with
        // a 0-test summary, so an agent polling for EditMode results got a
        // bogus "all passed".
        //
        // If the original run already finished (EditMode runs are synchronous
        // and don't reload; or the PlayMode run completed before this hook),
        // the results file was already written by the original onFinished and
        // these re-registered callbacks simply never fire — harmless, PROVIDED
        // any leaked callbacks from a previous abandoned run were already
        // drained. A6 requires that drain; B-R3 moved it out of this method:
        // a reattached pair whose onFinished never fires (reload aborted the
        // run, the user pressed Stop) would otherwise stay subscribed for the
        // session and collect the NEXT run's results into the old runId's
        // results file.
        //
        // CALLER-DRAINS CONTRACT (B-R3): this method does NOT drain the active
        // registry. The caller must call DrainActiveCallbacks() exactly once
        // before its first ReattachCallbacks call — OnAfterAssemblyReload does
        // so before its marker loop. Draining here destroyed the pair a
        // previous loop iteration had just registered when multiple pending
        // markers exist, so only the last-enumerated marker kept live
        // callbacks and the other runs' results files were never written.
        private static void ReattachCallbacks(string runId, bool playMode, bool includePasses)
        {
            var mode = playMode ? "PlayMode" : "EditMode";
            var results = new List<TestResultInfo>();
            TestRunnerApi api = null;
            TestCallbacks callbacks = null;

            callbacks = new TestCallbacks(
                onResult: r => TestRunnerService.CollectResult(r, results),
                onFinished: _ =>
                {
                    // B26 — unregister before destroying, same as the
                    // RunTestsTool path. Reattach subscribes a fresh callbacks
                    // instance on every domain reload; without Unregister the
                    // previous instance's closure (capturing this `results`
                    // list and `runId`) stays subscribed and pollutes the next
                    // run with merged counts.
                    if (api != null && callbacks != null)
                    {
                        try { api.UnregisterCallbacks(callbacks); } catch { }
                        UnregisterActive(api, callbacks);
                        Object.DestroyImmediate(api);
                    }
                    ClearPending(runId);
                    TestRunnerService.WriteResultsFile(runId, mode, results, includePasses);
                });

            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);
            RegisterActive(api, callbacks, runId);
            // Deliberately NOT calling api.Execute(...) — see the B8 note above.
        }

        private static string PendingFilePath(string runId) =>
            TestRunnerService.PendingFilePath(runId);
    }
}
