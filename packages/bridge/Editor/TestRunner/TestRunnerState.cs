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
        static TestRunnerState()
        {
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static void MarkPending(
            string runId,
            string assemblyName,
            string testNamespace,
            string testClass,
            string testMethod,
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
                sb.Append("\"includePasses\":").Append(includePasses ? "true" : "false");
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

        static void OnAfterAssemblyReload()
        {
            try
            {
                Directory.CreateDirectory(TestRunnerService.StatusDir);
                foreach (var file in Directory.GetFiles(TestRunnerService.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var runId = JsonBody.GetString(json, "runId");
                    if (string.IsNullOrEmpty(runId)) continue;

                    var assemblyName = JsonBody.GetString(json, "assemblyName");
                    var testNamespace = JsonBody.GetString(json, "testNamespace");
                    var testClass = JsonBody.GetString(json, "testClass");
                    var testMethod = JsonBody.GetString(json, "testMethod");
                    var includePasses = JsonBody.GetBool(json, "includePasses", true);

                    if (assemblyName == "") assemblyName = null;
                    if (testNamespace == "") testNamespace = null;
                    if (testClass == "") testClass = null;
                    if (testMethod == "") testMethod = null;

                    ReattachCallbacks(runId, assemblyName, testNamespace, testClass, testMethod, includePasses);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunnerState] OnAfterAssemblyReload error: {ex.Message}");
            }
        }

        static void ReattachCallbacks(
            string runId,
            string assemblyName,
            string testNamespace,
            string testClass,
            string testMethod,
            bool includePasses)
        {
            var filter = TestRunnerService.BuildFilter(true, assemblyName, testNamespace, testClass, testMethod);
            var results = new List<TestResultInfo>();
            TestRunnerApi api = null;

            var callbacks = new TestCallbacks(
                onResult: r => TestRunnerService.CollectResult(r, results),
                onFinished: _ =>
                {
                    if (api != null) Object.DestroyImmediate(api);
                    ClearPending(runId);
                    TestRunnerService.WriteResultsFile(runId, "PlayMode", results, includePasses);
                });

            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);
            api.Execute(new ExecutionSettings(filter));
        }

        static string PendingFilePath(string runId) =>
            TestRunnerService.PendingFilePath(runId);
    }
}
