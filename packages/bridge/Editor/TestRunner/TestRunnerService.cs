using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

// Exposes TestRunnerService internals (BuildResultsJson, Cap, TestResultInfo)
// to the test assembly for the filtering/truncation unit tests.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(
    "com.alexeyperov.unity-open-mcp-bridge.Editor.Tests")]

namespace UnityOpenMcpBridge.TestRunner
{
    struct TestResultInfo
    {
        public string Name;
        public string Status;
        public double Duration;
        public string Message;
        public string StackTrace;
    }

    static class TestRunnerService
    {
        // Per-field caps keep the results payload within typical MCP client
        // context windows. A 390-test run with many failures can otherwise emit
        // hundreds of KB of stack traces, which clients truncate mid-JSON.
        internal const int MaxFieldLength = 2000;
        internal const int MaxStackTraceLength = 4000;

        internal static readonly string StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unity-open-mcp");

        internal static string ResultsFilePath(string runId) =>
            Path.Combine(StatusDir, $"test-results-{runId}.json");

        internal static string PendingFilePath(string runId) =>
            Path.Combine(StatusDir, $"test-pending-{runId}.json");

        internal static Filter BuildFilter(
            bool playMode,
            string assemblyName,
            string testNamespace,
            string testClass,
            string testMethod)
        {
            var filter = new Filter
            {
                testMode = playMode ? TestMode.PlayMode : TestMode.EditMode
            };

            if (!string.IsNullOrEmpty(assemblyName))
                filter.assemblyNames = new[] { assemblyName };

            var groups = new List<string>();
            if (!string.IsNullOrEmpty(testNamespace))
                groups.Add(testNamespace);
            if (!string.IsNullOrEmpty(testClass))
                groups.Add(testClass);
            if (groups.Count > 0)
                filter.groupNames = groups.ToArray();

            if (!string.IsNullOrEmpty(testMethod))
                filter.testNames = new[] { testMethod };

            return filter;
        }

        internal static void CollectResult(ITestResultAdaptor result, List<TestResultInfo> results)
        {
            if (result.Test.IsSuite) return;

            string status;
            switch (result.TestStatus)
            {
                case TestStatus.Passed: status = "passed"; break;
                case TestStatus.Failed: status = "failed"; break;
                case TestStatus.Skipped: status = "skipped"; break;
                default: status = "inconclusive"; break;
            }

            results.Add(new TestResultInfo
            {
                Name = result.Test.FullName ?? result.Name,
                Status = status,
                Duration = result.Duration,
                Message = result.Message ?? "",
                StackTrace = result.StackTrace ?? ""
            });
        }

        internal static void WriteResultsFile(string runId, string mode, List<TestResultInfo> results)
            => WriteResultsFile(runId, mode, results, includePasses: true);

        internal static void WriteResultsFile(string runId, string mode, List<TestResultInfo> results, bool includePasses)
        {
            try
            {
                Directory.CreateDirectory(StatusDir);
                File.WriteAllText(ResultsFilePath(runId), BuildResultsJson(runId, mode, results, includePasses));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestRunner] Failed to write results file: {ex.Message}");
            }
        }

        internal static void WriteErrorFile(string runId, string mode, string errorCode, string errorMessage)
        {
            try
            {
                Directory.CreateDirectory(StatusDir);
                var sb = new StringBuilder(256);
                sb.Append('{');
                sb.Append("\"status\":\"error\",");
                sb.Append("\"runId\":").Append(EscapeString(runId)).Append(',');
                sb.Append("\"mode\":").Append(EscapeString(mode)).Append(',');
                sb.Append("\"error\":{\"code\":").Append(EscapeString(errorCode)).Append(',');
                sb.Append("\"message\":").Append(EscapeString(errorMessage)).Append('}');
                sb.Append('}');
                File.WriteAllText(ResultsFilePath(runId), sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestRunner] Failed to write error file: {ex.Message}");
            }
        }

        internal static string BuildStartedJson(string runId, string mode)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"status\":\"started\",");
            sb.Append("\"runId\":").Append(EscapeString(runId)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(mode));
            sb.Append('}');
            return sb.ToString();
        }

        internal static string BuildResultsJson(string runId, string mode, List<TestResultInfo> results, bool includePasses)
        {
            int passed = 0, failed = 0, skipped = 0, inconclusive = 0;
            for (int i = 0; i < results.Count; i++)
            {
                switch (results[i].Status)
                {
                    case "passed": passed++; break;
                    case "failed": failed++; break;
                    case "skipped": skipped++; break;
                    default: inconclusive++; break;
                }
            }

            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"status\":\"completed\",");
            sb.Append("\"runId\":").Append(EscapeString(runId)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(mode)).Append(',');
            sb.Append("\"includePasses\":").Append(includePasses ? "true" : "false").Append(',');
            sb.Append("\"summary\":{");
            sb.Append("\"total\":").Append(results.Count).Append(',');
            sb.Append("\"passed\":").Append(passed).Append(',');
            sb.Append("\"failed\":").Append(failed).Append(',');
            sb.Append("\"skipped\":").Append(skipped).Append(',');
            sb.Append("\"inconclusive\":").Append(inconclusive);
            sb.Append("},");

            // When includePasses is false, emit only non-passed results so a
            // large suite doesn't overrun the client's context window. The
            // summary above always carries the full counts either way.
            sb.Append("\"results\":[");
            bool first = true;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (!includePasses && r.Status == "passed") continue;

                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(EscapeString(r.Name)).Append(',');
                sb.Append("\"status\":").Append(EscapeString(r.Status)).Append(',');
                sb.Append("\"duration\":").Append(r.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"message\":").Append(EscapeString(Cap(r.Message, MaxFieldLength)));
                sb.Append(",\"stackTrace\":").Append(EscapeString(Cap(r.StackTrace, MaxStackTraceLength)));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static string Cap(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? "";
            return value.Substring(0, max) + $"…[{value.Length - max} more chars]";
        }

        internal static string EscapeString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    internal class TestCallbacks : ICallbacks
    {
        private readonly Action<ITestResultAdaptor> _onResult;
        private readonly Action<ITestResultAdaptor> _onFinished;

        public TestCallbacks(Action<ITestResultAdaptor> onResult, Action<ITestResultAdaptor> onFinished)
        {
            _onResult = onResult;
            _onFinished = onFinished;
        }

        public void RunStarted(ITestAdaptor testsToRun) { }
        public void RunFinished(ITestResultAdaptor result) => _onFinished(result);
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) => _onResult(result);
    }
}
