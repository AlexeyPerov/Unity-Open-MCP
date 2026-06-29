// M20 Plan 7 / T20.7.3 — reflection surface over the Memory Profiler capture
// API.
//
// The Memory Profiler capture surface has moved across namespaces:
//   - new (Unity 2023.1+/6000.0): Unity.Profiling.Memory.MemoryProfiler (engine
//     core, ships WITHOUT the package — but the .snap loader UI needs the
//     package).
//   - legacy: UnityEditor.MemoryProfiler / Profiling.Memory.Experimental.
//     MemoryProfiler (com.unity.memoryprofiler package).
//
// Both expose `TakeSnapshot(string path, Action<string,bool> callback)` plus
// optional screenshot-callback overloads. The capture is callback-based
// (async) — the method returns before the snapshot file is written. This helper
// resolves whichever surface is present at call time, invokes TakeSnapshot with
// a delegate, and blocks (bounded by a timeout) until the callback fires, so
// the tool returns a definitive path/result rather than deferring.
//
// When reflection cannot reach a needed member the helper returns false with an
// error code; the tool surfaces a `memoryprofiler_api_unavailable` envelope so
// the agent can fall back. The helper never throws out of the tool path —
// exceptions are caught and converted.
//
// Unity-version dependency: tested against com.unity.memoryprofiler as shipped
// with Unity 6. The callback shape `Action<string, bool>` (path, success) is
// stable across both the new and legacy namespaces.
#if UNITY_OPEN_MCP_EXT_MEMORYPROFILER
#pragma warning disable CS0618
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.MemoryProfiler
{
    // Reflection wrapper over the Memory Profiler capture API. All members are
    // static and best-effort; failures surface as (false, errorCode) tuples.
    internal static class MemoryProfilerApi
    {
        // The Memory Profiler capture type lives in different namespaces
        // depending on Unity version. Resolve both candidates up front; the
        // first one found wins.
        private static readonly Type MemoryProfilerType = ResolveCaptureType();

        private static Type ResolveCaptureType()
        {
            // New (engine core): Unity.Profiling.Memory.MemoryProfiler. This
            // type ships with the engine itself (assembly "Unity.Profiling" —
            // no package needed to capture), but the .snap loader UI needs the
            // com.unity.memoryprofiler package — which is why the whole
            // sub-asmdef is compile-gated on the package. Type.GetType only
            // searches the calling assembly + core lib by default, so we pass
            // the assembly-qualified name; the ResolveFromAnyAssembly fallback
            // covers versions where the assembly simple name differs.
            var core = Type.GetType(
                "Unity.Profiling.Memory.MemoryProfiler, Unity.Profiling");
            if (core != null) return core;
            core = ResolveFromAnyAssembly("Unity.Profiling.Memory.MemoryProfiler");
            if (core != null) return core;

            // Legacy (package): UnityEditor.MemoryProfiler.MemoryProfiler and
            // Profiling.Memory.Experimental.MemoryProfiler.
            var legacyEditor = ResolveFromAnyAssembly(
                "UnityEditor.MemoryProfiler.MemoryProfiler");
            if (legacyEditor != null) return legacyEditor;
            var legacyExperimental = ResolveFromAnyAssembly(
                "Profiling.Memory.Experimental.MemoryProfiler");
            if (legacyExperimental != null) return legacyExperimental;

            return null;
        }

        private static Type ResolveFromAnyAssembly(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // =====================================================================
        // capture
        // =====================================================================

        // Capture a Memory Profiler snapshot to a .snap file. The capture is
        // callback-based: TakeSnapshot returns immediately and invokes the
        // callback when the file is written. This helper blocks (bounded by
        // timeoutMs) until the callback fires so the caller gets a definitive
        // path/result. Returns true on a successful capture, false (with
        // error) when the API is unreachable, the capture times out, or the
        // callback reports failure.
        //
        // outputPath may be null/empty — in that case the helper derives a temp
        // path (Unity's own temp cache). The returned outputPath is always the
        // concrete path used.
        public static bool TryCapture(
            string outputPath,
            int timeoutMs,
            out string finalPath,
            out string error)
        {
            finalPath = ResolveOutputPath(outputPath);
            error = null;

            if (MemoryProfilerType == null)
            {
                error = "memoryprofiler_api_unavailable";
                return false;
            }

            // Find TakeSnapshot. The 2-arg (path, Action<string,bool>) overload
            // is stable across both namespaces. Some versions add an optional
            // screenshot callback — prefer the 2-arg form.
            var takeSnapshot = FindTakeSnapshot2Arg(MemoryProfilerType);
            if (takeSnapshot == null)
            {
                error = "memoryprofiler_take_snapshot_not_found";
                return false;
            }

            // The capture is async (callback fires on the main thread during a
            // later editor update). Block on a reset event, bounded by the
            // timeout, so the tool returns a definitive result. Editor updates
            // do NOT pump while a bridge tool handler is synchronously running,
            // so we pump EditorApplication.update manually between waits.
            bool done = false;
            bool success = false;
            string reportedPath = null;
            var reset = new ManualResetEvent(initialState: false);

            Action<string, bool> callback = (path, result) =>
            {
                reportedPath = path;
                success = result;
                done = true;
                reset.Set();
            };

            try
            {
                takeSnapshot.Invoke(null, new object[] { finalPath, callback });
            }
            catch (TargetInvocationException tie)
            {
                error = "memoryprofiler_capture_failed";
                Debug.LogWarning(
                    $"[unity-open-mcp] MemoryProfiler capture threw: {tie.InnerException?.Message ?? tie.Message}");
                return false;
            }
            catch (Exception e)
            {
                error = "memoryprofiler_capture_failed";
                Debug.LogWarning($"[unity-open-mcp] MemoryProfiler capture threw: {e.Message}");
                return false;
            }

            // Pump editor updates while waiting — TakeSnapshot's callback fires
            // during EditorApplication.update, which does not run while a
            // synchronous tool handler blocks the main thread.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    error = "memoryprofiler_capture_timeout";
                    return false;
                }
                // Pump pending editor updates so the snapshot callback can fire.
                // EditorApplication.update delegates run on the main thread
                // (we ARE the main thread), so this is safe.
                try
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch
                {
                    // Best-effort pump; ignore pump failures.
                }
                // Brief wait to avoid a tight spin; the callback fires on this
                // thread during a subsequent update.
                reset.WaitOne(50);
            }

            if (!success)
            {
                // The callback reported failure. Some versions report success
                // via the file existing even when the bool is misleading; trust
                // the file as the source of truth when it exists.
                if (File.Exists(finalPath))
                {
                    error = null;
                    return true;
                }
                error = "memoryprofiler_capture_failed";
                return false;
            }

            // Prefer the callback-reported path; fall back to the requested one.
            finalPath = !string.IsNullOrEmpty(reportedPath) ? reportedPath : finalPath;
            return true;
        }

        // Resolve the TakeSnapshot(string, Action<string,bool>) overload.
        // Reflection over the MethodInfo parameter types is more robust than
        // GetMethod by name (the package ships overloads with a screenshot
        // callback).
        private static MethodInfo FindTakeSnapshot2Arg(Type type)
        {
            foreach (var m in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "TakeSnapshot") continue;
                var p = m.GetParameters();
                if (p.Length != 2) continue;
                if (p[0].ParameterType != typeof(string)) continue;
                // The callback is Action<string, bool>. Match by the delegate
                // signature rather than exact type equality so both the engine
                // and package surface bind.
                if (!IsActionStringBool(p[1].ParameterType)) continue;
                return m;
            }
            return null;
        }

        private static bool IsActionStringBool(Type delegateType)
        {
            if (delegateType == typeof(Action<string, bool>)) return true;
            // Some versions use a named delegate (FinishedCaptureCallback) with
            // the same signature. Match by Invoke(string, bool).
            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null) return false;
            var parms = invoke.GetParameters();
            if (parms.Length != 2) return false;
            return parms[0].ParameterType == typeof(string) &&
                   parms[1].ParameterType == typeof(bool);
        }

        // Derive a default .snap output path under the project temp cache when
        // the caller did not supply one. Ensure the parent directory exists.
        private static string ResolveOutputPath(string requested)
        {
            if (!string.IsNullOrEmpty(requested))
            {
                // Accept either an Assets/... path or an absolute path. Ensure
                // the .snap extension (the Memory Profiler window only opens
                // .snap files) and that the parent dir exists.
                var path = requested;
                if (!path.EndsWith(".snap", StringComparison.OrdinalIgnoreCase))
                    path = path + ".snap";
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch
                {
                    // Parent-dir creation is best-effort; TakeSnapshot will
                    // surface a real error if the path is invalid.
                }
                return path;
            }

            // Default: a timestamped .snap under the OS temp dir. Snapshots can
            // be large (hundreds of MB+), so do NOT write them into Assets/ by
            // default — the agent passes an explicit Assets/ path only when it
            // wants to persist the file in the project.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var temp = System.IO.Path.GetTempPath().Replace('\\', '/');
            return temp + $"memory-snapshot-{stamp}.snap";
        }
    }
}
#endif
