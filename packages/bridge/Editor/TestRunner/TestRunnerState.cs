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

        private static void OnAfterAssemblyReload()
        {
            try
            {
                Directory.CreateDirectory(TestRunnerService.StatusDir);
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

                    var assemblyName = JsonBody.GetString(json, "assemblyName");
                    var testNamespace = JsonBody.GetString(json, "testNamespace");
                    var testClass = JsonBody.GetString(json, "testClass");
                    var testMethod = JsonBody.GetString(json, "testMethod");
                    // B8 — read the persisted mode. Pending files written before
                    // this fix lacked the field; default to PlayMode to preserve
                    // the prior behaviour for in-flight PlayMode runs (the only
                    // case where reattach is meaningful).
                    var playMode = JsonBody.GetBool(json, "playMode", true);
                    var includePasses = JsonBody.GetBool(json, "includePasses", true);

                    if (assemblyName == "") assemblyName = null;
                    if (testNamespace == "") testNamespace = null;
                    if (testClass == "") testClass = null;
                    if (testMethod == "") testMethod = null;

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
        // these re-registered callbacks simply never fire — harmless.
        private static void ReattachCallbacks(string runId, bool playMode, bool includePasses)
        {
            var mode = playMode ? "PlayMode" : "EditMode";
            var results = new List<TestResultInfo>();
            TestRunnerApi api = null;

            var callbacks = new TestCallbacks(
                onResult: r => TestRunnerService.CollectResult(r, results),
                onFinished: _ =>
                {
                    if (api != null) Object.DestroyImmediate(api);
                    ClearPending(runId);
                    TestRunnerService.WriteResultsFile(runId, mode, results, includePasses);
                });

            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);
            // Deliberately NOT calling api.Execute(...) — see the B8 note above.
        }

        private static string PendingFilePath(string runId) =>
            TestRunnerService.PendingFilePath(runId);
    }
}
