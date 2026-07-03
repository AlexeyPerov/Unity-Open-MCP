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
            if (string.IsNullOrEmpty(run_id))
                run_id = System.Diagnostics.Process.GetCurrentProcess().Id + "-"
                        + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            var mode = play_mode ? "PlayMode" : "EditMode";
            var filter = TestRunnerService.BuildFilter(play_mode, assembly_name, test_namespace, test_class, test_method);

            try
            {
                var f = TestRunnerService.ResultsFilePath(run_id);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }

            if (play_mode)
            {
                TestRunnerState.MarkPending(run_id, assembly_name, test_namespace, test_class, test_method, include_passes);
            }

            // specs/feedback.md 2026-07-03 — TestRunnerApi.Execute is the
            // blocking call that wedged the bridge: EditMode runs execute
            // synchronously on the main thread (the same thread this tool
            // dispatches on), so a 33-45s test run held the dispatch queue and
            // every subsequent tool timed out. Defer Execute to the next editor
            // tick (delayCall) so this method returns the documented
            // {status:"started", runId} envelope IMMEDIATELY, before the test
            // runner starts. Results are polled via the results file the
            // callbacks write — the agent's next step is unchanged.
            //
            // delayCall fires once on the next main-thread update, which is the
            // earliest point TestRunnerApi will accept the run anyway. The
            // callbacks (registered below) own the api lifetime and the results
            // file write, so no further coordination is needed here.
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

            var callbacks = new TestCallbacks(
                onResult: r => TestRunnerService.CollectResult(r, results),
                onFinished: _ =>
                {
                    if (api != null) Object.DestroyImmediate(api);
                    if (playMode)
                        TestRunnerState.ClearPending(runId);
                    TestRunnerService.WriteResultsFile(runId, mode, results, includePasses);
                });

            EditorApplication.delayCall += () =>
            {
                // Re-resolve api inside the deferred call: TestRunnerApi is a
                // ScriptableObject and must be created on the main thread (which
                // delayCall guarantees), and the callbacks close over it so the
                // onFinished callback can destroy it.
                api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(callbacks);
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
                    Object.DestroyImmediate(api);
                }
            };
        }
    }
}
