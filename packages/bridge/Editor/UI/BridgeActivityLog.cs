// In-memory ring buffer of bridge HTTP activity for the M4.5 Activity tab.
//
// Captures the metadata of every bridge HTTP event by default: timestamp, tool name,
// gate mode, durations, and outcome. Request and response bodies are **excluded** by
// default per questions-9 Q12 — only counts, sizes, and short header-like hints are
// captured. When verbose mode is enabled (also via Q12) the buffer additionally stores
// a **truncated** JSON snippet of the request body and the mutation success/error code
// to help with debugging, but never the full body and never response bodies.
//
// Retention is in-memory only per Q13 — the buffer survives the Editor session but is
// cleared on domain reload / Editor restart. No on-disk persistence in v1.
//
// Capacity is intentionally larger than the gate run history (100 vs 20) because every
// tool call — including fast read-only ones — lands here, and a long MCP session can
// produce hundreds of calls. Allocation is amortized via a single `LinkedList` push
// pattern; trimming to capacity on every insert keeps memory bounded.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public enum BridgeActivityKind
    {
        Ping,
        ToolRequest,
        ToolDisabled,
        ToolError,
        ResourceRequest,
        ResourceError,
        UnknownPath
    }

    public enum BridgeActivityOutcome
    {
        Unknown,
        Success,
        Failed,
        Timeout,
        Skipped
    }

    public class BridgeActivityEvent
    {
        public DateTime Timestamp;
        public BridgeActivityKind Kind;
        public string ToolName;        // null for non-tool events
        public string GateMode;        // request-level effective gate (mutating tools only)
        public BridgeActivityOutcome Outcome;
        public long DurationMs;        // wall-clock duration for the request handler
        public int HttpStatus;         // 200 / 404 / 405 / 500 / 503
        public int RequestBodyLength;  // raw body byte/char count (no body content)
        public string ErrorCode;       // mutation.error.code on failure paths
        public string ErrorMessage;    // short error message (truncated, no full stack)

        // Verbose-only fields (populated only when ActivityLogVerboseMode is on):
        public string RequestSnippet;   // truncated JSON snippet of the request body
        public string ResponseSnippet;  // short response outcome snippet (no body)

        // M13 T4.4 — set by streaming endpoints (SSE). When true the listener's
        // finally clause skips the automatic Response.Close() so the long-lived
        // stream stays open; the endpoint owns its own lifecycle.
        public bool StreamingResponse;
    }

    public static class BridgeActivityLog
    {
        public const int Capacity = 100;
        public const int SnippetMaxChars = 240; // truncated payload length cap (Q12)

        static readonly LinkedList<BridgeActivityEvent> _events = new LinkedList<BridgeActivityEvent>();
        static int _totalRecorded;
        static int _totalDroppedTrim;

        public static event Action Changed;

        public static bool Verbose
        {
            get => BridgeProjectSettings.VerboseActivityLog;
            set
            {
                if (BridgeProjectSettings.VerboseActivityLog == value) return;
                BridgeProjectSettings.SetVerboseActivityLog(value);
                try { Changed?.Invoke(); } catch { }
            }
        }

        public static int Count => _events.Count;
        public static int TotalRecorded => _totalRecorded;
        public static int TotalDroppedTrim => _totalDroppedTrim;

        public static IReadOnlyList<BridgeActivityEvent> Events
        {
            get
            {
                var list = new List<BridgeActivityEvent>(_events.Count);
                foreach (var e in _events) list.Add(e);
                return list;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsOnLoad()
        {
            // Domain reload resets the buffer (Q13 — in-memory only). Verbose preference
            // is read on every property access from BridgeProjectSettings, so the user
            // toggle survives reload via the project settings file.
            _events.Clear();
            _totalRecorded = 0;
            _totalDroppedTrim = 0;
        }

        public static void Record(BridgeActivityEvent evt)
        {
            if (evt == null) return;

            // Strip verbose-only fields when the user has not opted in. This keeps the
            // default policy explicit at the record site and makes accidental body capture
            // (e.g. by callers who pass through RequestSnippet unconditionally) impossible.
            if (!Verbose)
            {
                evt.RequestSnippet = null;
                evt.ResponseSnippet = null;
            }

            _events.AddLast(evt);
            while (_events.Count > Capacity)
            {
                _events.RemoveFirst();
                _totalDroppedTrim++;
            }
            _totalRecorded++;
            try { Changed?.Invoke(); } catch { }
        }

        public static void Clear()
        {
            if (_events.Count == 0) return;
            _events.Clear();
            _totalRecorded = 0;
            _totalDroppedTrim = 0;
            try { Changed?.Invoke(); } catch { }
        }

        // Helper for callers that want a redacted JSON snippet.
        // Keeps the first <SnippetMaxChars> characters and appends an ellipsis when truncated.
        // Removes control characters to keep the UI label clean.
        public static string TruncateSnippet(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var cleaned = StripControlChars(raw);
            if (cleaned.Length <= SnippetMaxChars) return cleaned;
            return cleaned.Substring(0, SnippetMaxChars) + "…";
        }

        static string StripControlChars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '\n' || c == '\r' || c == '\t') { sb.Append(' '); continue; }
                if (c < 32) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
