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
    // old healthy state. This tool calls CompilationPipeline.RequestScriptCompilation,
    // reporting the newest Library/ScriptAssemblies DLL mtime before/after so a no-op
    // recompile is detectable — mirroring reimport_package's contract but
    // project-wide (no package scoping). The compile itself is observed on the
    // dispatcher's WORKER thread after the RestartThenSettle settle wait (see
    // RebuildAfterSettle) — Execute runs on the main thread, where the
    // scheduled compile can never start until this call returns. Its RESULT is
    // not known here at all (a successful compile ends in a domain reload that
    // closes the response): the MCP server waits for the compile and reports
    // its status as the call's answer.
    //
    // paths_hint is optional — a recompile has no natural asset scope; the
    // dispatcher scopes the gate to "Assets" when it is omitted.
    public static class RecompileScriptsTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var dllDir = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "Library", "ScriptAssemblies");
            long mtimeBefore = NewestAssemblyMtime(dllDir);

            bool wasCompiling = EditorApplication.isCompiling;
            int generationAtStart = BridgeCompileState.Generation;

            // New or deleted script files written outside Unity: import them
            // first, or the compile cannot see them (ScriptImportSync).
            var scriptImport = ScriptImportSync.Run();
            if (scriptImport.Action == "refused") return scriptImport.Refresh.Refusal();

            // Request a script compilation. Wrapped because
            // CompilationPipeline.RequestScriptCompilation throws if a compile
            // is already in flight — the worker-side settle path covers that.
            bool requested = TryRequestScriptCompilation(out var requestError);
            if (!requested)
                return ToolDispatchResult.Fail("compile_request_failed",
                    requestError + " Nothing was compiled. Import the edited scripts with " +
                    "unity_open_mcp_assets_refresh, which starts the compile they need.");
            // The compile starts on a later tick: publish that one is due, so
            // a wait cannot read the gap before it starts as idle.
            BridgeCompileState.NoteRequested();

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
                wasCompiling, mtimeBefore, mtimeAfter, isCompiling, dllDir, generationAtStart, scriptImport.ToJson()));
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
            int generationAtStart = JsonBody.GetInt(output, "compileGenerationAtStart", 0);
            var scriptImport = JsonBody.GetTopLevelRawValue(output, "scriptImport");

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
            return BuildResult(wasCompiling, mtimeBefore, mtimeAfter, BridgeSession.IsCompiling, dllDir,
                generationAtStart, scriptImport);
        }

        // Shared payload builder for the pre-settle (Execute, main thread) and
        // post-settle (RebuildAfterSettle, worker thread) shapes. The field
        // set is identical in both — only the values differ. It describes the
        // REQUEST (and the DLL evidence), never the compile's outcome, which
        // only a wait past this call can see: the gate envelope names that
        // wait (the compile is due), and the MCP server runs it and reports
        // the compile status as the call's answer.
        private static string BuildResult(bool wasCompiling, long mtimeBefore, long mtimeAfter, bool isCompiling,
            string dllDir, int generationAtStart, string scriptImportJson)
        {
            bool recompiled = mtimeAfter > mtimeBefore;
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"compile_requested\"");
            sb.Append(",\"requested\":true");
            sb.Append(",\"wasCompiling\":").Append(wasCompiling ? "true" : "false");
            sb.Append(",\"recompiled\":").Append(recompiled ? "true" : "false");
            sb.Append(",\"isCompiling\":").Append(isCompiling ? "true" : "false");
            sb.Append(",\"dllMtimeBefore\":").Append(mtimeBefore);
            sb.Append(",\"dllMtimeAfter\":").Append(mtimeAfter);
            sb.Append(",\"scriptAssembliesDir\":").Append(BridgeJson.EscapeString(dllDir));
            sb.Append(",\"compileGenerationAtStart\":").Append(generationAtStart);
            if (!string.IsNullOrEmpty(scriptImportJson)) sb.Append(",\"scriptImport\":").Append(scriptImportJson);
            sb.Append('}');
            return sb.ToString();
        }

        // Request a script compilation via the compile-time
        // CompilationPipeline.RequestScriptCompilation() call — public since
        // 2019.3, so always present on the bridge's 2022.3 floor. The previous
        // reflection lookup (GetMethod("RequestScriptCompilation", Public |
        // Static)) threw AmbiguousMatchException on every supported Unity
        // (2021.2+ adds the RequestScriptCompilationOptions overload), then
        // fell through to the long-removed internal
        // EditorUtility.RequestScriptCompilation and reported "No
        // RequestScriptCompilation API found on this Unity version" — the tool
        // never actually requested a compile. Returns false with an error
        // string only when the call throws for a reason other than a compile
        // already being in flight (which counts as requested — the settle wait
        // covers it).
        private static bool TryRequestScriptCompilation(out string error)
        {
            error = null;
            try
            {
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                return true;
            }
            catch (System.Exception e)
            {
                if (IsAlreadyCompilingException(e)) return true;
                error = "CompilationPipeline.RequestScriptCompilation threw: " + e.Message;
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
