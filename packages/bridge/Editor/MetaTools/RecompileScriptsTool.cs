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
    // (or EditorUtility.RequestScriptCompilation on older versions) and blocks
    // until the compile settles, reporting the newest Library/ScriptAssemblies
    // DLL mtime before/after so a no-op recompile is detectable — mirroring
    // reimport_package's contract but project-wide (no package scoping).
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
            // already in flight — the settle wait below covers either case.
            bool requested = TryRequestScriptCompilation(out var requestError);

            // Block briefly for the compile to settle on this (main) thread so
            // the AFTER mtime reflects the recompiled DLL. The dispatcher's own
            // RestartThenSettle wait (set via ToolLifecycle) covers the long
            // tail; this short inline poll lets us report a meaningful
            // dllMtimeAfter without relying solely on the envelope's settleMs.
            SpinWaitForCompileSettle();

            long mtimeAfter = NewestAssemblyMtime(dllDir);
            bool recompiled = mtimeAfter > mtimeBefore;
            bool isCompiling = EditorApplication.isCompiling;

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
            else if (wasCompiling)
            {
                steps.Append("- A compile was already in flight when recompile_scripts was called. ");
                steps.Append("Poll unity_open_mcp_editor_status.isCompiling until false, then read_compile_errors. ");
            }
            else
            {
                steps.Append("- No DLL mtime change detected (recompiled:false). Unity's incremental compiler ");
                steps.Append("may have judged the sources unchanged. If you edited C#, verify the file on disk ");
                steps.Append("and consider unity_open_mcp_reimport_package (local package) or editing then ");
                steps.Append("unity_open_mcp_assets_refresh. Execute_csharp + RequestScriptCompilation is also ");
                steps.Append("available as a stronger nudge. ");
            }
            sb.Append(",\"agentNextSteps\":").Append(BridgeJson.EscapeString(steps.ToString()));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
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

        // Block briefly for the compile to settle on this (main) thread so the
        // AFTER mtime reflects the recompiled DLL. Mirrors PackagesTools' wait.
        private static void SpinWaitForCompileSettle()
        {
            const int capMs = 4000;
            const int tickMs = 100;
            int elapsed = 0;
            System.Threading.Thread.Sleep(tickMs);
            elapsed += tickMs;
            while (elapsed < capMs)
            {
                if (!EditorApplication.isCompiling) break;
                System.Threading.Thread.Sleep(tickMs);
                elapsed += tickMs;
            }
        }
    }
}
