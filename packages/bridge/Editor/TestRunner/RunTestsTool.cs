using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.TestRunner
{
    [BridgeToolType]
    public class Tool_TestRunner
    {
        internal static bool RunScheduled;
        [BridgeTool("unity_senses_run_tests", Title = "Run Tests",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.CustomConfirmation)]
        [System.ComponentModel.Description(
            "Run Unity EditMode or PlayMode tests. Returns a runId for result polling. " +
            "EditMode results are typically available within seconds; PlayMode requires " +
            "domain reload and may take longer.")]
        public string RunTests(
            bool play_mode = false,
            string assembly_name = null,
            string test_namespace = null,
            string test_class = null,
            string test_method = null,
            string run_id = null,
            bool include_passes = true)
        {
            if (ProjectCommandJobs.Active) throw new InvalidOperationException("job_busy: an asynchronous project command owns the Editor.");
            bool callerSuppliedRunId = !string.IsNullOrEmpty(run_id);
            if (!callerSuppliedRunId)
                run_id = System.Diagnostics.Process.GetCurrentProcess().Id + "-"
                        + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            // B41 — run_id is caller-supplied (via the bridge body, bypassing
            // the MCP schema under batch_execute) and reaches File.Delete /
            // File.WriteAllText inside ResultsFilePath / PendingFilePath /
            // MarkPending. Validate BEFORE any filesystem op: a value like
            // "../../.." would escape ~/.unity-open-mcp, and "a/b" would invent
            // subpaths. The auto-generated "<pid>-<unixMs>" form always passes.
            // Registry tools cannot emit a custom error code (TryDispatch maps a
            // throw to execution_error), so throw with a precise message.
            if (!TestRunnerService.IsValidRunId(run_id))
            {
                throw new ArgumentException(
                    "Invalid 'run_id': must be 1.." + TestRunnerService.MaxRunIdLength +
                    " characters of [A-Za-z0-9._-] only (no path separators, whitespace, or '..'). " +
                    "Omit run_id to let the tool generate a safe one.");
            }

            // specs/feedback.md 2026-08-24 — `run_id` starts a run; it does NOT
            // poll one. Re-calling with the id from a previous {status:"started"}
            // response used to start a SECOND run under the same id: the new run
            // superseded the first (DrainActiveCallbacks below), rewrote its
            // pending marker with this call's filters, and answered "started"
            // again — so an agent polling that way saw an endless "started" and a
            // marker whose filters looked like they had been dropped. Refuse
            // instead, and say what the id is actually for. Only a CALLER-supplied
            // id can collide; the generated form embeds a fresh timestamp.
            if (callerSuppliedRunId && TestRunnerState.IsRunInFlight(run_id))
            {
                throw new InvalidOperationException(
                    "A test run with run_id '" + run_id + "' is already in flight. 'run_id' names a " +
                    "run to START, not one to poll — re-calling with it would abort the running " +
                    "run and start a new one under the same id. Results are delivered by the MCP " +
                    "server polling test-results-<runId>.json, so the ORIGINAL run_tests call " +
                    "returns them when the run finishes (raise its timeout_ms for a long suite). " +
                    "To start a genuinely different run, omit run_id or pass a new one.");
            }

            var mode = play_mode ? "PlayMode" : "EditMode";
            var filter = TestRunnerService.BuildFilter(play_mode, assembly_name, test_namespace, test_class, test_method);

            try
            {
                var f = TestRunnerService.ResultsFilePath(run_id);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }

            // MarkPending writes the on-disk test-pending-<runId>.json signal for
            // ALL modes (not just PlayMode). For PlayMode it survives the domain
            // reload TestRunnerApi triggers, letting OnAfterAssemblyReload
            // reattach callbacks (TestRunnerState.cs). For EditMode (which does
            // not reload) it doubles as the MCP-server signal that suppresses the
            // dead-bridge classification during the brief post-run window —
            // without it, an EditMode run that nudges a recompile freezes the
            // heartbeat writer long enough for classifyInstance to flip to
            // dead_bridge and poison every subsequent tool call (feedback entry).
            // ClearPending in the onFinished callback below is unconditional for
            // the same reason.
            TestRunnerState.MarkPending(run_id, assembly_name, test_namespace, test_class, test_method, play_mode, include_passes);

            // specs/feedback.md 2026-07-03 — TestRunnerApi.Execute is the
            // blocking call that wedged the bridge: EditMode runs execute
            // synchronously on the main thread (the same thread this tool
            // dispatches on), so a 33-45s test run held the dispatch queue and
            // every subsequent tool timed out. Defer Execute to the next editor
            // tick (update) so this method returns the documented
            // {status:"started", runId} envelope IMMEDIATELY, before the test
            // runner starts. Results are polled via the results file the
            // callbacks write — the agent's next step is unchanged.
            //
            // Use a one-shot update callback: delayCall can remain pending
            // indefinitely when the Editor has no inspector repaint. The
            // callbacks (registered below) own the api lifetime and the results
            // file write, so no further coordination is needed here.
            RunScheduled = true;
            StartRunDeferred(filter, run_id, mode, play_mode, include_passes);

            return TestRunnerService.BuildStartedJson(run_id, mode);
        }

        // Register the TestRunnerApi callbacks and queue Execute on the next
        // editor tick. Split out so the deferred-call closure captures the same
        // locals the synchronous path did.
        private static void StartRunDeferred(Filter filter, string runId, string mode, bool playMode, bool includePasses)
        {
            var results = new List<TestResultInfo>();
            TestRunnerApi api = null;
            TestCallbacks callbacks = null;

            callbacks = new TestCallbacks(
                onResult: r => TestRunnerService.CollectResult(r, results),
                onFinished: _ =>
                {
                    // B26 — unregister the callbacks BEFORE destroying the api.
                    // RegisterCallbacks stores the ICallbacks in the framework's
                    // domain-level holder, so DestroyImmediate(api) does NOT
                    // detach it: the closure (which captures this run's `results`
                    // list and `runId`) stays subscribed for the rest of the
                    // session. On the next run, run 1's TestFinished fires again
                    // and appends run 2's results to run 1's list, then rewrites
                    // test-results-<runId1>.json with merged, wrong counts.
                    // UnregisterCallbacks is the documented counterpart and
                    // removes the instance from that holder.
                    if (api != null && callbacks != null)
                    {
                        try { api.UnregisterCallbacks(callbacks); } catch { }
                        // A6 — also drop the pair from the active registry so a
                        // later DrainActiveCallbacks sweep does not touch it.
                        TestRunnerState.UnregisterActive(api, callbacks);
                        Object.DestroyImmediate(api);
                    }
                    // Unconditional for all modes — see MarkPending note above.
                    TestRunnerState.ClearPending(runId);
                    TestRunnerService.WriteResultsFile(runId, mode, results, includePasses);
                });

            ScheduleOnUpdate(() =>
            {
                RunScheduled = false;
                // A6 — sweep any leaked (api, callbacks) pair from a previous
                // run whose onFinished never fired (Stop during PlayMode, a
                // reload that aborted an EditMode run) before subscribing a
                // fresh one. Without this the leaked instance stays in the
                // framework's domain-level holder, collects THIS run's results
                // into the old run's captured list, and its onFinished rewrites
                // test-results-<oldRunId>.json with the wrong counts.
                //
                // B-R3 note: if another run is genuinely still in flight, this
                // sweep unsubscribes it and its results file is never written —
                // that run's results are discarded BY DESIGN (single-run model;
                // the old alternative was cross-talk that corrupted both runs'
                // counts). Unlike TestRunnerState.ReattachCallbacks (which no
                // longer drains — its caller drains once before the marker
                // loop), starting a NEW run is exactly the point where sweeping
                // leftovers is legitimate.
                //
                // feedback #7 — pass "superseded_by_run" so each swept run also
                // gets a terminal `aborted` file: previously a superseded run
                // left only a lingering test-pending marker and a polling agent
                // saw silence forever. Also sweep OTHER pending markers from
                // prior runs (crashed/force-quit) the same way.
                TestRunnerState.DrainActiveCallbacks("superseded_by_run");
                TestRunnerState.AbortOtherPendingRuns(runId);

                // Re-resolve api inside the deferred call: TestRunnerApi is a
                // ScriptableObject and must be created on the main thread (which
                // update guarantees), and the callbacks close over it so the
                // onFinished callback can destroy it.
                api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(callbacks);
                TestRunnerState.RegisterActive(api, callbacks, runId, mode);
                try
                {
                    api.Execute(new ExecutionSettings(filter));
                }
                catch (Exception e)
                {
                    // If Execute itself throws (e.g. filter rejected), still
                    // surface a results file so the polling agent sees a
                    // terminal state instead of waiting on a never-written file.
                    TestRunnerService.WriteErrorFile(runId, mode, "test_run_failed", e.Message);
                    // B26 — same UnregisterCallbacks defense as onFinished: the
                    // callbacks were already registered above, so a rejected
                    // Execute still leaves them subscribed in the domain-level
                    // holder. Unregister before destroying the api.
                    try { api.UnregisterCallbacks(callbacks); } catch { }
                    TestRunnerState.UnregisterActive(api, callbacks);
                    Object.DestroyImmediate(api);
                    // B9 — clear the pending marker so it does not linger. The
                    // onFinished callback (the only other ClearPending site)
                    // never fires for a rejected Execute, so without this the
                    // marker would persist forever and OnAfterAssemblyReload
                    // would reattach on EVERY subsequent recompile — arming the
                    // B8 bug (unwanted PlayMode run) for the rest of the
                    // session. Same defense if a domain reload lands between
                    // MarkPending and this deferred closure firing.
                    TestRunnerState.ClearPending(runId);
                }
            });
        }

        internal static void ScheduleOnUpdate(Action action)
        {
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;
                action();
            };
            EditorApplication.update += callback;
        }
    }
}
