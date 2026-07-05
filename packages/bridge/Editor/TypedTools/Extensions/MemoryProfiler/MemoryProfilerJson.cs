// M20 Plan 7 / T20.7.3 — Memory Profiler (com.unity.memoryprofiler) embedded
// domain tool.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_MEMORYPROFILER. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.MemoryProfiler.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_MEMORYPROFILER"]` and references
// Unity.MemoryProfiler.Editor; the bridge root asmdef sets the define via
// `versionDefines` when the package resolves.
//
// Memory Profiler is the third package-gated specialty (M20 Plan 7 fallback).
// It ships with compile-gate AND auto-activation (the `memoryprofiler` group
// auto-activates for the session when com.unity.memoryprofiler is installed —
// see tool-groups.ts). It pairs with the existing profiler family — agents can
// capture a memory snapshot, then the existing profiler_get_script_stats /
// profiler_capture_frame tools give CPU/frame context, yielding a fuller
// performance picture than a standalone memory tool.
//
// The capture surface (Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot /
// UnityEditor.MemoryProfiler / Profiling.Memory.Experimental.MemoryProfiler)
// is callback-based (async). The capture tool reflects over whichever surface
// the installed version exposes and blocks until the callback fires (bounded
// by a timeout), so the tool returns a definitive path/result rather than
// deferring. When the API cannot be reached (version mismatch / internal
// rename) the tool returns a structured `memoryprofiler_api_unavailable` error
// instead of throwing.
//
// Naming: tool id is `unity_senses_memory_snapshot_capture` (snake_case,
// sense-prefixed because it pairs with the senses profiler family).
#if UNITY_OPEN_MCP_EXT_MEMORYPROFILER
using System.Text;

namespace UnityOpenMcpBridge.Extensions.MemoryProfilerExt
{
    // Shared helpers for the Memory Profiler embedded domain tool.
    //
    // JSON envelope builders + a reflection surface over the Memory Profiler
    // capture API. Mirrors the MemoryProfilerJson / ShaderGraphJson helper
    // shape so the domain packs read consistently.
    internal static class MemoryProfilerJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
#endif
