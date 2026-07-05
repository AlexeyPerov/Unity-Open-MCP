// M20 Plan 7 / T20.7.3 — Memory Profiler snapshot capture tool (compile-gated
// + auto-activating).
//
// One typed tool: unity_senses_memory_snapshot_capture — capture a Memory
// Profiler snapshot to a .snap file.
//
// Pairs with the existing profiler family — agents can capture a memory
// snapshot, then the existing profiler_get_script_stats / profiler_capture_frame
// tools give CPU/frame context, yielding a fuller performance picture than a
// standalone memory tool.
//
// The capture is read-only re: game/project state but produces a file. Per the
// execution plan §T20.7.3: Gate = Off (it is a capture, not a mutation of
// project assets), ReadOnlyHint = true, Lifecycle = EditorSettle (capture can
// take seconds — the dispatcher waits for the editor to settle so the snapshot
// reflects a stable state).
//
// The capture surface (Unity.Profiling.Memory.MemoryProfiler /
// UnityEditor.MemoryProfiler) is callback-based (async) and moved namespaces
// across Unity versions. The tool delegates to the MemoryProfilerApi reflection
// helper, which resolves whichever surface is present, invokes TakeSnapshot with
// a delegate, and blocks (bounded by a timeout) until the callback fires — so
// the tool returns a definitive path/result. When the API cannot be reached
// (version mismatch / internal rename) the tool returns a structured
// `memoryprofiler_api_unavailable` error instead of throwing.
//
// Compile-gate-only: when com.unity.memoryprofiler is absent the tool is not
// compiled in and the capability surface reports the domain as
// `available: false (dependency missing: com.unity.memoryprofiler)`. When the
// package IS present, the `memoryprofiler` group auto-activates for the session
// (no manual manage_tools call) — see T20.7.0.
//
// Naming: `unity_senses_memory_snapshot_capture` (snake_case, sense-prefixed
// because it pairs with the senses profiler family).
#if UNITY_OPEN_MCP_EXT_MEMORYPROFILER
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.MemoryProfilerExt
{
    [BridgeToolType]
    public static class MemorySnapshotCaptureTool
    {
        // =====================================================================
        // capture
        // =====================================================================

        // Capture a Memory Profiler snapshot to a .snap file. output_path is
        // optional — when omitted the snapshot is written to a temp path
        // (snapshots can be hundreds of MB+, so the default avoids writing into
        // Assets/). Pass an Assets/.../*.snap path to persist it in the
        // project. Read-only re: game/project state but produces a file — Gate
        // = Off, ReadOnlyHint = true, Lifecycle = EditorSettle.
        [BridgeTool("unity_senses_memory_snapshot_capture",
            Title = "Memory Snapshot Capture",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "memoryprofiler")]
        [System.ComponentModel.Description(
            "Capture a Memory Profiler snapshot to a .snap file using the " +
            "com.unity.memoryprofiler package API. Pairs with the existing " +
            "profiler family (profiler_get_script_stats / profiler_capture_frame) " +
            "for a fuller performance picture. output_path is optional — when " +
            "omitted the snapshot is written to a temp path (snapshots can be " +
            "hundreds of MB+, so the default avoids writing into Assets/). Pass " +
            "an 'Assets/.../*.snap' path to persist it in the project. Read-only " +
            "re: game/project state but produces a file — Gate = Off, " +
            "ReadOnlyHint = true, Lifecycle = EditorSettle (capture can take " +
            "seconds). Requires the com.unity.memoryprofiler package installed.")]
        public static string Capture(string output_path = null, int timeout_ms = 60000)
        {
            if (timeout_ms <= 0) timeout_ms = 60000;
            // Hard cap the wait so a wedged capture cannot hang the bridge.
            if (timeout_ms > 300000) timeout_ms = 300000;

            if (!MemoryProfilerApi.TryCapture(output_path, timeout_ms,
                    out var finalPath, out var error))
            {
                return MemoryProfilerJson.Error(error ?? "memoryprofiler_api_unavailable",
                    "Could not capture a Memory Profiler snapshot. The " +
                    "com.unity.memoryprofiler package version may expose a " +
                    "different capture surface, or the capture timed out. The " +
                    "capture is callback-based and blocks until the snapshot " +
                    "file is written; if it timed out, increase timeout_ms or " +
                    "capture manually from the Memory Profiler window " +
                    "(Window > Analysis > Memory Profiler).");
            }

            // Report the file size so the agent knows the payload scale.
            long sizeBytes = 0;
            try
            {
                if (File.Exists(finalPath)) sizeBytes = new FileInfo(finalPath).Length;
            }
            catch
            {
                // Size read is best-effort.
            }

            // Refresh the asset database when the snapshot landed inside
            // Assets/ so it shows up in the Project window + capabilities.
            var inAssets = finalPath != null &&
                           finalPath.IndexOf("Assets/", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (inAssets)
            {
                try { AssetDatabase.ImportAsset(finalPath, ImportAssetOptions.ForceUpdate); }
                catch { /* best-effort import */ }
            }

            var sb = new StringBuilder(256);
            sb.Append("\"memorySnapshot\":{");
            sb.Append("\"captured\":true,");
            sb.Append("\"outputPath\":").Append(MemoryProfilerJson.Esc(finalPath ?? "")).Append(',');
            sb.Append("\"fileSizeBytes\":").Append(sizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"inAssets\":").Append(inAssets ? "true" : "false").Append(',');
            sb.Append("\"humanReadable\":").Append(MemoryProfilerJson.Esc(HumanBytes(sizeBytes)));
            sb.Append('}');
            return MemoryProfilerJson.Ok(sb.ToString());
        }

        private static string HumanBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB" };
            int ui = -1;
            for (int i = 0; i < units.Length; i++)
            {
                v /= 1024.0;
                ui = i;
                if (v < 1024.0) break;
            }
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[ui];
        }
    }
}
#endif
