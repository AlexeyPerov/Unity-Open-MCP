using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.TestRunner
{
    [BridgeToolType]
    public class Tool_TestRunner
    {
        [BridgeTool("unity_agent_run_tests", Title = "Run Tests",
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

            StartRun(filter, run_id, mode, play_mode, include_passes);

            return TestRunnerService.BuildStartedJson(run_id, mode);
        }

        static void StartRun(Filter filter, string runId, string mode, bool playMode, bool includePasses)
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

            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);
            api.Execute(new ExecutionSettings(filter));
        }
    }
}
