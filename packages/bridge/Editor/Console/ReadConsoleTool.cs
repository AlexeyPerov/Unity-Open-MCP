using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Console
{
    // M10 Plan 2 T3.2 — Console log reader via reflection on internal
    // UnityEditor.LogEntries / LogEntry (unity-cli approach).
    //
    // LogEntries and LogEntry are internal Unity types whose exact shape varies
    // across versions. We use reflection to access them and fall back
    // gracefully if the API surface changes.
    //
    // The `mode` bitmask on each LogEntry determines the log type:
    //   bit 0 (1)   → Error
    //   bit 1 (2)   → Assert
    //   bit 2 (4)   → Warning
    //   bit 3 (8)   → Log
    //   bit 4 (16)  → Fatal/Error (used by exceptions in some versions)
    //   bit 5 (32)  → Exception (some versions)
    //
    // We classify conservatively: Exception/Fatal/Assert bits → "error",
    // Warning bit → "warning", everything else → "log". This avoids
    // mis-classifying exceptions as logs.
    //
    // M13 T4.6 — standardized token-bounded output: `detail` controls stack
    // inclusion (summary = message only; normal = capped stack with Unity
    // frames stripped; verbose = full stack with Unity frames), and `truncated`
    // always reports how many entries were dropped by `max_entries`.
    //
    // Non-mutating unless clear: true (which clears the console).
    [BridgeToolType]
    public class Tool_ReadConsole
    {
        [BridgeTool("unity_senses_read_console", Title = "Read Console",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Read the Unity console log entries. Filter by type (error/warning/log/all), " +
            "optionally clear the console, and get structured entries with stack traces " +
            "(Unity-internal frames stripped by default).")]
        public string ReadConsole(
            string type = "all",
            bool clear = false,
            int max_entries = 100,
            int max_stack_frames = 20,
            bool include_unity_frames = false,
            string detail = "normal")
        {
            try
            {
                var detailLevel = ParseDetail(detail);

                var entries = LogEntriesReader.GetEntries();
                var filtered = FilterByType(entries, (type ?? "all").ToLowerInvariant());

                // M13 T4.6 — truncation accounting. The console is chronological;
                // we keep the most recent `max_entries` after type-filtering and
                // report how many were silently dropped so the caller can widen
                // the cap if needed.
                int truncated;
                var capped = CapEntries(filtered, max_entries, out truncated);

                // Stack formatting is the dominant token cost; `detail` controls it.
                // `summary` skips stacks entirely; `verbose` includes Unity frames
                // and ignores the per-entry frame cap.
                bool wantStack = detailLevel != DetailLevel.Summary;
                bool verboseFrames = detailLevel == DetailLevel.Verbose;
                int effectiveMaxFrames = verboseFrames ? int.MaxValue : max_stack_frames;
                bool effectiveIncludeUnity = verboseFrames || include_unity_frames;

                for (int i = 0; i < capped.Count; i++)
                {
                    var e = capped[i];
                    e.Stack = wantStack
                        ? FormatStack(e.StackRaw, effectiveMaxFrames, effectiveIncludeUnity)
                        : "";
                    capped[i] = e;
                }

                bool cleared = false;
                if (clear)
                    cleared = LogEntriesReader.Clear();

                return BuildJson(capped, entries.Count, filtered.Count, truncated, cleared,
                    type ?? "all", max_entries, max_stack_frames, include_unity_frames, detailLevel);
            }
            catch (System.Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        enum DetailLevel { Summary, Normal, Verbose }

        private static DetailLevel ParseDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return DetailLevel.Normal;
            switch (detail.ToLowerInvariant())
            {
                case "summary": return DetailLevel.Summary;
                case "verbose": return DetailLevel.Verbose;
                case "normal":
                default: return DetailLevel.Normal;
            }
        }

        // ---- entry filtering ----

        private static List<LogEntryInfo> FilterByType(List<LogEntryInfo> entries, string type)
        {
            if (type == "all") return entries;

            var result = new List<LogEntryInfo>();
            foreach (var e in entries)
            {
                var classified = Classify(e.Mode);
                if (type == "error" && (classified == "error"))
                    result.Add(e);
                else if (type == "warning" && classified == "warning")
                    result.Add(e);
                else if (type == "log" && classified == "log")
                    result.Add(e);
            }
            return result;
        }

        private static string Classify(int mode) => LogEntriesReader.Classify(mode);

        // M13 T4.6 — keep the most recent entries (console is chronological; the
        // tail matters most) and report how many were dropped so the caller can
        // widen the cap. Returns the trimmed list and sets `truncated` to the
        // number of dropped entries (never silent elision).
        private static List<LogEntryInfo> CapEntries(List<LogEntryInfo> entries, int maxEntries, out int truncated)
        {
            if (maxEntries <= 0) maxEntries = 1;
            if (entries.Count > maxEntries)
            {
                truncated = entries.Count - maxEntries;
                return entries.GetRange(entries.Count - maxEntries, maxEntries);
            }
            truncated = 0;
            return entries;
        }

        private static string FormatStack(string stack, int maxFrames, bool includeUnity)
        {
            if (string.IsNullOrEmpty(stack)) return "";

            var lines = stack.Split('\n');
            var result = new List<string>();
            int count = 0;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r', ' ');
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (!includeUnity && IsUnityInternalFrame(line))
                    continue;

                result.Add(line);
                count++;
                if (count >= maxFrames) break;
            }

            if (count >= maxFrames && lines.Length > count)
                result.Add($"... ({lines.Length - count} more frames omitted)");

            return string.Join("\n", result);
        }

        private static bool IsUnityInternalFrame(string frame)
        {
            if (string.IsNullOrEmpty(frame)) return true;
            var lower = frame.ToLowerInvariant();
            return lower.Contains("unityengine.")
                || lower.Contains("unityeditor.")
                || lower.Contains("system.")
                || lower.Contains("packages/com.unity")
                || lower.Contains("library/packagecache");
        }

        // ---- JSON building ----

        private static string BuildJson(List<LogEntryInfo> entries, int totalBeforeFilter, int totalAfterFilter,
            int truncated, bool cleared, string type, int maxEntries, int maxFrames, bool includeUnity,
            DetailLevel detailLevel)
        {
            int errorCount = 0, warningCount = 0, logCount = 0;
            foreach (var e in entries)
            {
                var c = Classify(e.Mode);
                if (c == "error") errorCount++;
                else if (c == "warning") warningCount++;
                else logCount++;
            }

            var detailWire = detailLevel == DetailLevel.Summary ? "summary"
                : detailLevel == DetailLevel.Verbose ? "verbose"
                : "normal";

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"totalBeforeFilter\":").Append(totalBeforeFilter).Append(',');
            sb.Append("\"totalAfterFilter\":").Append(totalAfterFilter).Append(',');
            sb.Append("\"returnedCount\":").Append(entries.Count).Append(',');
            // M13 T4.6 — never silently elide. Even at 0 we emit the field so
            // agents can trust it as "no truncation" rather than "unknown".
            sb.Append("\"truncated\":").Append(truncated).Append(',');
            sb.Append("\"counts\":{");
            sb.Append("\"error\":").Append(errorCount).Append(',');
            sb.Append("\"warning\":").Append(warningCount).Append(',');
            sb.Append("\"log\":").Append(logCount);
            sb.Append("},");
            sb.Append("\"cleared\":").Append(cleared ? "true" : "false").Append(',');
            sb.Append("\"filter\":").Append(Esc(type)).Append(',');
            sb.Append("\"detail\":").Append(Esc(detailWire)).Append(',');
            sb.Append("\"settings\":{");
            sb.Append("\"maxEntries\":").Append(maxEntries).Append(',');
            sb.Append("\"maxStackFrames\":").Append(maxFrames).Append(',');
            sb.Append("\"includeUnityFrames\":").Append(includeUnity ? "true" : "false");
            sb.Append("},");
            sb.Append("\"entries\":[");

            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                sb.Append('{');
                sb.Append("\"type\":").Append(Esc(Classify(e.Mode))).Append(',');
                sb.Append("\"message\":").Append(Esc(e.Message));
                if (!string.IsNullOrEmpty(e.Stack))
                    sb.Append(",\"stack\":").Append(Esc(e.Stack));
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Esc(string s)
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

    // ---- reflection reader ----

    // M22 T22.1.3 — made public so it can surface as the type of
    // GateDispatchResult.Logs (a public field on a public class).
    public struct LogEntryInfo
    {
        public int Mode;
        public string Message;
        public string StackRaw;
        public string Stack;  // formatted
    }

    static class LogEntriesReader
    {
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _getEntriesMethod;
        private static MethodInfo _clearMethod;
        // The total-row-count getter. Renamed across Unity versions: legacy code
        // looked for `GetEntryCount()` (a 2019-2021 internal name), but modern
        // Unity (2022 LTS, 6000.x) exposes it as `GetCount()`. Resolve both so
        // the reader degrades gracefully regardless of editor version.
        private static MethodInfo _getEntryCountMethod;
        private static MethodInfo _getEntryInternalMethod;
        // Unity's LogEntries contract requires GetEntryInternal calls to be
        // bracketed by StartGettingEntries()/EndGettingEntries() (see
        // LogEntries.bindings.cs: "All functions marked internal may not be
        // called unless you call StartGettingEntries and EndGettingEntries").
        // These are no-ops on versions that don't enforce it, but calling them
        // when present avoids corrupted reads on 6000.x.
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod;
        private static FieldInfo _messageField;
        private static FieldInfo _stackField;
        private static FieldInfo _modeField;
        private static bool _initialized;

        private static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            var unityEditorAssembly = typeof(EditorWindow).Assembly;

            _logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries");
            _logEntryType = unityEditorAssembly.GetType("UnityEditor.LogEntry");

            if (_logEntriesType != null)
            {
                const BindingFlags StaticFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                // GetEntries(List<LogEntry>) — legacy bulk-drain API. Removed in
                // Unity 6 (the bindings no longer declare it); read_console falls
                // back to GetCount() + GetEntryInternal iteration when this is null.
                _getEntriesMethod = _logEntriesType.GetMethod("GetEntries", StaticFlags);

                // Clear() — clears console
                _clearMethod = _logEntriesType.GetMethod("Clear", StaticFlags);

                // Total row count. Modern Unity (6000.x) names this `GetCount()`;
                // older internals exposed it as `GetEntryCount()`. Resolve either.
                _getEntryCountMethod =
                    _logEntriesType.GetMethod("GetCount", StaticFlags)
                    ?? _logEntriesType.GetMethod("GetEntryCount", StaticFlags);

                _getEntryInternalMethod = _logEntriesType.GetMethod("GetEntryInternal", StaticFlags);

                // Bracketing pair required by the LogEntries contract on modern
                // Unity (see comment above). Absent on older versions — the
                // iteration path simply skips the bracket when null.
                _startGettingEntriesMethod = _logEntriesType.GetMethod("StartGettingEntries", StaticFlags);
                _endGettingEntriesMethod = _logEntriesType.GetMethod("EndGettingEntries", StaticFlags);
            }

            if (_logEntryType != null)
            {
                _messageField = _logEntryType.GetField("message",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _stackField = _logEntryType.GetField("stackTrace",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _modeField = _logEntryType.GetField("mode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        public static bool IsAvailable
        {
            get
            {
                Init();
                // Either path is enough: the legacy GetEntries(List<LogEntry>)
                // bulk drain, OR the per-entry GetCount()+GetEntryInternal walk
                // (the only surface Unity 6 exposes). Requiring GetEntries left
                // read_console throwing "internal API not available" on 6000.x.
                return _logEntriesType != null && _logEntryType != null
                    && (_getEntriesMethod != null
                        || (_getEntryCountMethod != null && _getEntryInternalMethod != null));
            }
        }

        // M22 T22.1.3 — delta-capture availability. StartCapture/StopCapture
        // need the per-entry count + GetEntryInternal pair. Kept distinct from
        // IsAvailable (the full-drain path read_console uses) so a Unity version
        // that exposes only one surface still leaves the other working.
        public static bool IsCaptureAvailable
        {
            get
            {
                Init();
                return _logEntriesType != null && _getEntryCountMethod != null
                    && _getEntryInternalMethod != null && _logEntryType != null;
            }
        }

        // Classify a LogEntry mode bitmask into the wire severity used by both
        // read_console and the per-call `logs` envelope field. Factored out of
        // Tool_ReadConsole so the two surfaces agree on the error/warning/log
        // vocabulary.
        public static string Classify(int mode)
        {
            // Exception/Fatal/Error/Assert bits
            if ((mode & 1) != 0) return "error";       // Error
            if ((mode & 2) != 0) return "error";       // Assert (treat as error)
            if ((mode & 4) != 0) return "warning";      // Warning
            if ((mode & 8) != 0) return "log";          // Log
            if ((mode & 16) != 0) return "error";       // Fatal
            if ((mode & 32) != 0) return "error";       // Exception
            if ((mode & 64) != 0) return "error";       // scripting error variant
            return "log";
        }

        public static List<LogEntryInfo> GetEntries()
        {
            Init();

            if (_logEntryType == null)
                throw new InvalidOperationException(
                    "UnityEditor.LogEntry internal type not available in this Unity version.");

            // Prefer the legacy bulk-drain API when present (older Unity). It
            // allocates one managed LogEntry per row in C++ and hands back the
            // list — cheaper than the per-row reflection walk below.
            if (_getEntriesMethod != null)
                return DrainViaGetEntries();

            // Unity 6 fallback: GetEntries(List<LogEntry>) was removed from the
            // bindings. Walk GetCount() + GetEntryInternal(int, LogEntry) inside
            // the StartGettingEntries/EndGettingEntries bracket the LogEntries
            // contract requires. Without this fallback read_console threw
            // "internal API not available" on 6000.x (specs/feedback.md 2026-07-03).
            if (_getEntryCountMethod != null && _getEntryInternalMethod != null)
                return ReadAllViaGetEntryInternal(0);

            throw new InvalidOperationException(
                "UnityEditor.LogEntries internal API not available in this Unity version. " +
                "Neither GetEntries(List<LogEntry>) nor GetCount()+GetEntryInternal(int,LogEntry) resolved.");
        }

        // Legacy path: GetEntries(List<LogEntry>) populates a managed list in
        // one C++ call. Used on Unity versions that still expose it.
        private static List<LogEntryInfo> DrainViaGetEntries()
        {
            var listType = typeof(List<>).MakeGenericType(_logEntryType);
            var entriesList = Activator.CreateInstance(listType);

            _getEntriesMethod.Invoke(null, new[] { entriesList });

            var result = new List<LogEntryInfo>();
            var countProp = listType.GetProperty("Count");
            var itemProp = listType.GetProperty("Item");
            int count = (int)countProp.GetValue(entriesList);

            for (int i = 0; i < count; i++)
            {
                var entry = itemProp.GetValue(entriesList, new object[] { i });

                result.Add(new LogEntryInfo
                {
                    Mode = _modeField != null ? Convert.ToInt32(_modeField.GetValue(entry)) : 0,
                    Message = _messageField != null ? (string)_messageField.GetValue(entry) : "",
                    StackRaw = _stackField != null ? (string)_stackField.GetValue(entry) : "",
                });
            }

            return result;
        }

        // Per-row walk used by both the Unity-6 read_console fallback (from 0)
        // and the delta-capture StopCapture path (from startIndex). Brackets the
        // iteration with StartGettingEntries/EndGettingEntries when present —
        // the LogEntries contract requires it on modern Unity, and it's a no-op
        // on versions where those methods don't exist.
        private static List<LogEntryInfo> ReadAllViaGetEntryInternal(int startIndex)
        {
            int endIndex;
            try { endIndex = (int)_getEntryCountMethod.Invoke(null, null); }
            catch { return new List<LogEntryInfo>(0); }
            if (endIndex <= startIndex) return new List<LogEntryInfo>(0);

            var result = new List<LogEntryInfo>(endIndex - startIndex);
            // GetEntryInternal(int index, LogEntry entry) fills a single reused
            // LogEntry instance by reference. Allocate once and reuse.
            object entryInstance;
            try { entryInstance = Activator.CreateInstance(_logEntryType); }
            catch { return result; }

            // Bracket the iteration. StartGettingEntries returns the total line
            // count (ignored here — we use GetCount for the row count); we only
            // need its side effect of making GetEntryInternal safe to call.
            bool started = false;
            if (_startGettingEntriesMethod != null)
            {
                try { _startGettingEntriesMethod.Invoke(null, null); started = true; }
                catch { /* fall through to unbracketed read */ }
            }

            try
            {
                for (int i = startIndex; i < endIndex; i++)
                {
                    try
                    {
                        _getEntryInternalMethod.Invoke(null, new[] { (object)i, entryInstance });
                    }
                    catch
                    {
                        // Skip entries that fail to read rather than aborting.
                        continue;
                    }

                    result.Add(new LogEntryInfo
                    {
                        Mode = _modeField != null ? Convert.ToInt32(_modeField.GetValue(entryInstance)) : 0,
                        Message = _messageField != null ? (string)_messageField.GetValue(entryInstance) : "",
                        StackRaw = _stackField != null ? (string)_stackField.GetValue(entryInstance) : "",
                    });
                }
            }
            finally
            {
                if (started && _endGettingEntriesMethod != null)
                {
                    try { _endGettingEntriesMethod.Invoke(null, null); }
                    catch { /* best-effort release */ }
                }
            }
            return result;
        }

        public static bool Clear()
        {
            Init();
            if (_clearMethod == null) return false;
            _clearMethod.Invoke(null, null);
            return true;
        }

        // M22 T22.1.3 — per-call `logs` capture. StartCapture records the
        // current console entry count as a baseline; StopCapture returns the
        // LogEntryInfo list appended since the baseline. The caller wraps a
        // tool dispatch with this pair so warnings/errors emitted *during this
        // call* surface inline in the response envelope (no need to poll
        // read_console afterwards). MUST run on the main thread (LogEntries is
        // main-thread-only). Returns -1 when capture is unavailable; callers
        // treat that as "no capture" and emit an empty logs array.
        public static int StartCapture()
        {
            Init();
            if (_getEntryCountMethod == null) return -1;
            try { return (int)_getEntryCountMethod.Invoke(null, null); }
            catch { return -1; }
        }

        public static List<LogEntryInfo> StopCapture(int startIndex)
        {
            if (startIndex < 0) return new List<LogEntryInfo>(0);
            Init();
            if (_getEntryCountMethod == null || _getEntryInternalMethod == null || _logEntryType == null)
                return new List<LogEntryInfo>(0);

            return ReadAllViaGetEntryInternal(startIndex);
        }
    }
}
