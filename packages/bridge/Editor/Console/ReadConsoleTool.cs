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
    // Non-mutating unless clear: true (which clears the console).
    [BridgeToolType]
    public class Tool_ReadConsole
    {
        [BridgeTool("unity_agent_read_console", Title = "Read Console",
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
            bool include_unity_frames = false)
        {
            try
            {
                var entries = LogEntriesReader.GetEntries();
                var filtered = FilterByType(entries, (type ?? "all").ToLowerInvariant());
                var capped = CapEntries(filtered, max_entries, max_stack_frames, include_unity_frames);

                bool cleared = false;
                if (clear)
                    cleared = LogEntriesReader.Clear();

                return BuildJson(capped, entries.Count, cleared, type ?? "all",
                    max_entries, max_stack_frames, include_unity_frames);
            }
            catch (System.Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ---- entry filtering ----

        static List<LogEntryInfo> FilterByType(List<LogEntryInfo> entries, string type)
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

        static string Classify(int mode)
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

        static List<LogEntryInfo> CapEntries(List<LogEntryInfo> entries, int maxEntries, int maxFrames, bool includeUnity)
        {
            // Take the most recent entries (console is chronological; tail matters).
            if (entries.Count > maxEntries)
                entries = entries.GetRange(entries.Count - maxEntries, maxEntries);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                e.Stack = FormatStack(e.StackRaw, maxFrames, includeUnity);
                entries[i] = e;
            }

            return entries;
        }

        static string FormatStack(string stack, int maxFrames, bool includeUnity)
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

        static bool IsUnityInternalFrame(string frame)
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

        static string BuildJson(List<LogEntryInfo> entries, int totalBeforeFilter, bool cleared,
            string type, int maxEntries, int maxFrames, bool includeUnity)
        {
            int errorCount = 0, warningCount = 0, logCount = 0;
            foreach (var e in entries)
            {
                var c = Classify(e.Mode);
                if (c == "error") errorCount++;
                else if (c == "warning") warningCount++;
                else logCount++;
            }

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"totalBeforeFilter\":").Append(totalBeforeFilter).Append(',');
            sb.Append("\"returnedCount\":").Append(entries.Count).Append(',');
            sb.Append("\"counts\":{");
            sb.Append("\"error\":").Append(errorCount).Append(',');
            sb.Append("\"warning\":").Append(warningCount).Append(',');
            sb.Append("\"log\":").Append(logCount);
            sb.Append("},");
            sb.Append("\"cleared\":").Append(cleared ? "true" : "false").Append(',');
            sb.Append("\"filter\":").Append(Esc(type)).Append(',');
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

        static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        static string Esc(string s)
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

    struct LogEntryInfo
    {
        public int Mode;
        public string Message;
        public string StackRaw;
        public string Stack;  // formatted
    }

    static class LogEntriesReader
    {
        static Type _logEntriesType;
        static Type _logEntryType;
        static MethodInfo _getEntriesMethod;
        static MethodInfo _clearMethod;
        static FieldInfo _messageField;
        static FieldInfo _stackField;
        static FieldInfo _modeField;
        static bool _initialized;

        static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            var unityEditorAssembly = typeof(EditorWindow).Assembly;

            _logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries");
            _logEntryType = unityEditorAssembly.GetType("UnityEditor.LogEntry");

            if (_logEntriesType != null)
            {
                // GetEntries(List<LogEntry>) — returns count
                _getEntriesMethod = _logEntriesType.GetMethod("GetEntries",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                // Clear() — clears console
                _clearMethod = _logEntriesType.GetMethod("Clear",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
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
                return _logEntriesType != null && _getEntriesMethod != null && _logEntryType != null;
            }
        }

        public static List<LogEntryInfo> GetEntries()
        {
            Init();

            if (_getEntriesMethod == null || _logEntryType == null)
                throw new InvalidOperationException(
                    "UnityEditor.LogEntries internal API not available in this Unity version.");

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

        public static bool Clear()
        {
            Init();
            if (_clearMethod == null) return false;
            _clearMethod.Invoke(null, null);
            return true;
        }
    }
}
