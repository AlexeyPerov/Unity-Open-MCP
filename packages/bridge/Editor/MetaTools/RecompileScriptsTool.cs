using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    // feedback-01-08-glm §5 — a first-class "force a real recompile and wait
    // for it" primitive. assets_refresh / AssetDatabase.ImportAsset frequently
    // no-op on edited C# (Unity's incremental compiler sees no import change),
    // leaving the running assembly stale while read_compile_errors reports the
    // old healthy state. This tool calls CompilationPipeline.RequestScriptCompilation
    // (or EditorUtility.RequestScriptCompilation on older versions), reporting
    // the newest Library/ScriptAssemblies DLL mtime before/after so a no-op
    // recompile is detectable — mirroring reimport_package's contract but
    // project-wide (no package scoping). The compile itself is observed on the
    // dispatcher's WORKER thread after the RestartThenSettle settle wait (see
    // RebuildAfterSettle) — Execute runs on the main thread, where the
    // scheduled compile can never start until this call returns.
    public static class RecompileScriptsTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var pathsHint = JsonBody.GetStringArray(body, "paths_hint");
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail("paths_hint_required",
                    "recompile_scripts is mutating; pass a non-empty paths_hint. The natural scope is " +
                    "the set of script assets you edited (e.g. [\"Assets/Scripts/MyClass.cs\"]) — the " +
                    "gate validates the post-recompile state of those paths.");

            var dllDir = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "Library", "ScriptAssemblies");
            long mtimeBefore = NewestAssemblyMtime(dllDir);

            bool wasCompiling = EditorApplication.isCompiling;

            // Request a script compilation. CompilationPipeline.RequestScriptCompilation
            // exists on 2019.3+; the internal EditorUtility.RequestScriptCompilation
            // is the older fallback. Wrapped because it throws if a compile is
            // already in flight — the worker-side settle path covers either case.
            bool requested = TryRequestScriptCompilation(out var requestError);

            // Deliberately NO inline wait here. Execute runs on the MAIN
            // thread (via MainThreadDispatcher), and RequestScriptCompilation
            // only schedules a compile for a future editor tick — a tick that
            // can never run while this thread sleeps. The old Thread.Sleep
            // spin wait therefore always expired with isCompiling still false
            // and reported recompiled:false with misleading guidance. Return
            // promptly instead; BridgeHttpServer calls RebuildAfterSettle on
            // the worker thread after the settle wait to patch recompiled /
            // dllMtimeAfter / isCompiling with the post-settle values.
            long mtimeAfter = NewestAssemblyMtime(dllDir);
            bool isCompiling = EditorApplication.isCompiling;

            return ToolDispatchResult.Ok(BuildResult(
                requested, requestError, wasCompiling,
                mtimeBefore, mtimeAfter, isCompiling, dllDir, afterSettle: false));
        }

        // Worker-side post-settle rebuild — called by BridgeHttpServer on the
        // dispatcher's worker thread AFTER the RestartThenSettle settle wait.
        // With the main thread free again, give the scheduled compile a short
        // grace window to START (isCompiling flips true, or the DLLs change),
        // ride the normal settle wait if it does, then rebuild the payload
        // from the post-settle DLL mtimes. `extraWaitMs` is the additional
        // worker-side wait, for the envelope's settleMs. Never throws; returns
        // the input unchanged when it cannot be parsed or nothing was
        // requested. Polls BridgeSession.IsCompiling (the volatile flag cached
        // on the main-thread update tick) because EditorApplication.isCompiling
        // is a main-thread-only API.
        public static string RebuildAfterSettle(string output, out long extraWaitMs)
        {
            extraWaitMs = 0;
            if (string.IsNullOrEmpty(output)) return output;
            if (!JsonBody.GetBool(output, "requested", false))
                return output; // nothing scheduled — keep the error guidance intact.
            var dllDir = JsonBody.GetString(output, "scriptAssembliesDir");
            if (string.IsNullOrEmpty(dllDir)) return output;
            long mtimeBefore = JsonBody.GetLongFlexible(output, "dllMtimeBefore", long.MinValue);
            if (mtimeBefore == long.MinValue) return output;
            bool wasCompiling = JsonBody.GetBool(output, "wasCompiling", false);

            const int graceCapMs = 2000;
            const int tickMs = 100;
            long waited = 0;
            while (waited < graceCapMs
                && !BridgeSession.IsCompiling
                && NewestAssemblyMtime(dllDir) <= mtimeBefore)
            {
                System.Threading.Thread.Sleep(tickMs);
                waited += tickMs;
            }
            // The compile started — ride the normal settle wait until it
            // finishes. Safe here: sleeping the WORKER thread does not stall
            // the main thread doing the compiling.
            if (BridgeSession.IsCompiling)
                waited += EditorSettleWait.Wait(LifecyclePolicy.RestartThenSettle);
            extraWaitMs = waited;

            long mtimeAfter = NewestAssemblyMtime(dllDir);
            return BuildResult(true, null, wasCompiling,
                mtimeBefore, mtimeAfter, BridgeSession.IsCompiling, dllDir, afterSettle: true);
        }

        // Shared payload builder for the pre-settle (Execute, main thread) and
        // post-settle (RebuildAfterSettle, worker thread) shapes. The field
        // set is identical in both — only the values and the agentNextSteps
        // guidance differ.
        private static string BuildResult(bool requested, string requestError,
            bool wasCompiling, long mtimeBefore, long mtimeAfter, bool isCompiling,
            string dllDir, bool afterSettle)
        {
            bool recompiled = mtimeAfter > mtimeBefore;

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\"");
            sb.Append(",\"requested\":").Append(requested ? "true" : "false");
            sb.Append(",\"wasCompiling\":").Append(wasCompiling ? "true" : "false");
            sb.Append(",\"recompiled\":").Append(recompiled ? "true" : "false");
            sb.Append(",\"isCompiling\":").Append(isCompiling ? "true" : "false");
            sb.Append(",\"dllMtimeBefore\":").Append(mtimeBefore);
            sb.Append(",\"dllMtimeAfter\":").Append(mtimeAfter);
            sb.Append(",\"scriptAssembliesDir\":").Append(BridgeJson.EscapeString(dllDir));

            // Actionable next steps so an agent can branch on the outcome.
            var steps = new StringBuilder(128);
            if (!requested && requestError != null)
            {
                steps.Append("- RequestScriptCompilation was unavailable (").Append(requestError).Append("). ");
                steps.Append("Fall back to unity_open_mcp_assets_refresh or reimport the edited .cs files. ");
            }
            else if (recompiled)
            {
                steps.Append("- Assembly rebuilt (dllMtimeAfter > dllMtimeBefore). ");
                steps.Append("Run unity_open_mcp_read_compile_errors to confirm the new code is healthy. ");
            }
            else if (isCompiling)
            {
                steps.Append("- The requested compile is still in progress. ");
                steps.Append("Poll unity_open_mcp_editor_status.isCompiling until false, then read_compile_errors. ");
            }
            else if (wasCompiling)
            {
                steps.Append("- A compile was already in flight when recompile_scripts was called. ");
                steps.Append("Poll unity_open_mcp_editor_status.isCompiling until false, then read_compile_errors. ");
            }
            else if (!afterSettle)
            {
                steps.Append("- The compile was scheduled but runs on a later editor tick, AFTER this payload ");
                steps.Append("was built — recompiled:false here does NOT mean the recompile no-opped. The ");
                steps.Append("dispatcher re-checks the DLL mtimes after its settle wait; if this text still ");
                steps.Append("reaches you, poll unity_open_mcp_editor_status.isCompiling until false, then ");
                steps.Append("unity_open_mcp_read_compile_errors. ");
            }
            else
            {
                steps.Append("- No DLL mtime change detected after the settle wait (recompiled:false). Unity's ");
                steps.Append("incremental compiler may have judged the sources unchanged. If you edited C#, verify ");
                steps.Append("the file on disk and consider unity_open_mcp_reimport_package (local package) or ");
                steps.Append("editing then unity_open_mcp_assets_refresh. Execute_csharp + RequestScriptCompilation ");
                steps.Append("is also available as a stronger nudge. ");
            }
            sb.Append(",\"agentNextSteps\":").Append(BridgeJson.EscapeString(steps.ToString()));
            sb.Append('}');
            return sb.ToString();
        }

        // Request a script compilation via CompilationPipeline where available,
        // falling back to the internal EditorUtility.RequestScriptCompilation on
        // older Unity versions. Returns false (with an error string) only when
        // neither API is reachable by reflection — a no-op compile still leaves
        // the settle wait as the backstop.
        private static bool TryRequestScriptCompilation(out string error)
        {
            error = null;
            try
            {
                // 2019.3+: UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation().
                var cpType = System.Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                if (cpType != null)
                {
                    var mi = cpType.GetMethod("RequestScriptCompilation", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (mi != null) { mi.Invoke(null, null); return true; }
                }
            }
            catch (System.Exception e)
            {
                // CompilationPipeline.RequestScriptCompilation throws if a
                // compile is already in flight — the settle wait covers it; we
                // still report the request as made.
                if (IsAlreadyCompilingException(e)) return true;
                error = "CompilationPipeline.RequestScriptCompilation threw: " + e.Message;
            }
            try
            {
                // Older Unity: internal EditorUtility.RequestScriptCompilation().
                var euType = typeof(EditorUtility);
                var mi = euType.GetMethod("RequestScriptCompilation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (mi != null) { mi.Invoke(null, null); return true; }
                error = "No RequestScriptCompilation API found on this Unity version.";
                return false;
            }
            catch (System.Exception e)
            {
                if (IsAlreadyCompilingException(e)) return true;
                error = "EditorUtility.RequestScriptCompilation threw: " + e.Message;
                return false;
            }
        }

        private static bool IsAlreadyCompilingException(System.Exception e)
        {
            var msg = (e.InnerException?.Message ?? e.Message) ?? "";
            // "already in progress" / "compilation is already" — the exact text
            // varies across versions; match the stable substring.
            return msg.IndexOf("already", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Newest mtime (UTC ticks) across every Library/ScriptAssemblies/*.dll.
        // When the dir is missing, returns DateTime.MinValue.Ticks so the before
        // snapshot is never spuriously newer than the after snapshot.
        private static long NewestAssemblyMtime(string scriptAssembliesDir)
        {
            if (string.IsNullOrEmpty(scriptAssembliesDir) || !Directory.Exists(scriptAssembliesDir))
                return System.DateTime.MinValue.Ticks;
            string[] candidates;
            try { candidates = Directory.GetFiles(scriptAssembliesDir, "*.dll"); }
            catch { return System.DateTime.MinValue.Ticks; }
            long newest = System.DateTime.MinValue.Ticks;
            foreach (var dll in candidates)
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(dll).Ticks;
                    if (t > newest) newest = t;
                }
                catch { /* file vanished mid-scan */ }
            }
            return newest;
        }
    }
}
