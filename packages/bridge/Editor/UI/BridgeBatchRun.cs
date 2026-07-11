using System;
using System.Collections.Generic;
using System.Text;

namespace UnityOpenMcpBridge
{
    // T20.7.5.1 — in-Editor batch-run state.
    //
    // This is the read-only state the Activity tab's Batch section observes. It
    // is the same surface an in-Editor batch machinery (the future M26 / T26.3
    // "batch_*" flow, or any operator-driven bulk run that wants progress
    // visible in-Editor) writes to. The bridge owns the state because the bridge
    // is the single Editor process; batch runs dispatched from the MCP `batch_*`
    // surface (M26) or from the Hub funnel their progress through here so the
    // Activity Batch section can render it without a manual refresh.
    //
    // Design notes:
    //  - Modelled on BridgeActivityLog / BridgeGateRunHistory: a static ring
    //    buffer of completed runs + a single Active run, a Changed event the UI
    //    subscribes to for repaint, and a domain-reload reset hook.
    //  - The UI is strictly read-only — nothing here starts or mutates a batch.
    //  - In v1 the state is in-memory only (cleared on domain reload), matching
    //    the activity log and gate-run history policies.

    public enum BridgeBatchEntryStatus
    {
        Pending,
        Running,
        Done,
        Failed,
        Skipped
    }

    public class BridgeBatchRunEntry
    {
        public int Index;                       // 0-based position within the run
        public string ToolName;                 // MCP tool id (unity_open_mcp_*)
        public string ArgsSummary;              // short, redacted summary (no full body)
        public BridgeBatchEntryStatus Status;
        public long DurationMs;                 // wall-clock for this entry (0 while pending/running)
        public string ErrorCode;                // mutation.error.code on failure
        public string ErrorMessage;             // short error message on failure
        public DateTime? StartedAt;
        public DateTime? FinishedAt;
    }

    public class BridgeBatchRun
    {
        public string RunId;                    // opaque id supplied by the batch source
        public string Source;                   // who started it ("mcp", "hub", "manual", ...)
        public string Label;                    // human-readable title for the run
        public DateTime StartedAt;
        public DateTime? CompletedAt;
        public List<BridgeBatchRunEntry> Entries = new List<BridgeBatchRunEntry>();

        // Convenience roll-ups the UI renders each frame. Kept on the record so
        // the repaint path is a pure read (no per-frame LINQ over the entry list).
        public int PendingCount;
        public int RunningCount;
        public int DoneCount;
        public int FailedCount;
        public int SkippedCount;

        public int TotalCount => Entries?.Count ?? 0;
        public bool IsComplete => CompletedAt.HasValue;

        // Recompute the roll-up counters from the entry list. Called by the state
        // holder whenever an entry transitions, so the UI reads cached counts.
        public void RecomputeCounts()
        {
            PendingCount = 0;
            RunningCount = 0;
            DoneCount = 0;
            FailedCount = 0;
            SkippedCount = 0;
            if (Entries == null) return;
            for (int i = 0; i < Entries.Count; i++)
            {
                switch (Entries[i].Status)
                {
                    case BridgeBatchEntryStatus.Pending: PendingCount++; break;
                    case BridgeBatchEntryStatus.Running: RunningCount++; break;
                    case BridgeBatchEntryStatus.Done: DoneCount++; break;
                    case BridgeBatchEntryStatus.Failed: FailedCount++; break;
                    case BridgeBatchEntryStatus.Skipped: SkippedCount++; break;
                }
            }
        }
    }

    public static class BridgeBatchRunHistory
    {
        // Capacity for completed runs retained for inspection. Matches the
        // activity-log ring buffer policy (in-memory, LRU-trimmed).
        public const int Capacity = 20;

        // Hard cap on per-run entry count surfaced to the UI, so a runaway batch
        // can't produce an unbounded payload. Beyond this, entries are still
        // counted in the roll-up but not retained individually.
        public const int MaxEntriesPerRun = 500;

        private static readonly LinkedList<BridgeBatchRun> _completed = new LinkedList<BridgeBatchRun>();
        private static BridgeBatchRun _active;
        private static int _totalRunsRecorded;

        public static event Action Changed;

        public static BridgeBatchRun Active => _active;
        public static int CompletedCount => _completed.Count;
        public static int TotalRunsRecorded => _totalRunsRecorded;

        public static IReadOnlyList<BridgeBatchRun> Completed
        {
            get
            {
                var list = new List<BridgeBatchRun>(_completed.Count);
                foreach (var r in _completed) list.Add(r);
                return list;
            }
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnLoad()
        {
            // Domain reload clears the in-memory state (same policy as the
            // activity log). There is no on-disk persistence in v1.
            _completed.Clear();
            _active = null;
            _totalRunsRecorded = 0;
        }

        // Begin a new run. Returns the run record so the caller can populate
        // entries via AddEntry / SetEntryStatus. Starting a new run while another
        // is active completes the previous one (defensive — there should only be
        // one active run at a time).
        public static BridgeBatchRun BeginRun(string runId, string source, string label)
        {
            // If a previous run is still marked active, finalize it defensively
            // so the UI doesn't show two active runs.
            if (_active != null && !_active.IsComplete)
            {
                CompleteRun(null);
            }

            var run = new BridgeBatchRun
            {
                RunId = runId ?? Guid.NewGuid().ToString("N"),
                Source = source ?? "unknown",
                Label = label ?? "Batch run",
                StartedAt = DateTime.Now,
            };
            _active = run;
            EmitChanged();
            return run;
        }

        // Append an entry to the active run. Returns the entry, or null if there
        // is no active run or the per-run cap was hit. Safe to call from any
        // thread — the Changed event is best-effort wrapped.
        public static BridgeBatchRunEntry AddEntry(string toolName, string argsSummary)
        {
            if (_active == null) return null;
            if (_active.Entries.Count >= MaxEntriesPerRun) return null;

            var entry = new BridgeBatchRunEntry
            {
                Index = _active.Entries.Count,
                ToolName = toolName,
                ArgsSummary = TruncateSummary(argsSummary),
                Status = BridgeBatchEntryStatus.Pending,
            };
            _active.Entries.Add(entry);
            _active.RecomputeCounts();
            EmitChanged();
            return entry;
        }

        // Transition an entry's status. Looked up by index on the active run.
        public static void SetEntryStatus(int index, BridgeBatchEntryStatus status,
            long durationMs = 0, string errorCode = null, string errorMessage = null)
        {
            if (_active == null) return;
            if (index < 0 || index >= _active.Entries.Count) return;
            var entry = _active.Entries[index];
            entry.Status = status;
            if (durationMs > 0) entry.DurationMs = durationMs;
            if (errorCode != null) entry.ErrorCode = errorCode;
            if (errorMessage != null) entry.ErrorMessage = TruncateMessage(errorMessage);
            if (status == BridgeBatchEntryStatus.Running && entry.StartedAt == null)
                entry.StartedAt = DateTime.Now;
            if ((status == BridgeBatchEntryStatus.Done ||
                 status == BridgeBatchEntryStatus.Failed ||
                 status == BridgeBatchEntryStatus.Skipped) && entry.FinishedAt == null)
                entry.FinishedAt = DateTime.Now;
            _active.RecomputeCounts();
            EmitChanged();
        }

        // Mark the active run complete and move it to the completed ring buffer.
        // Called with the run's own id by the batch source when it finishes; the
        // defensive finalize path (BeginRun while another is active) calls with
        // null/empty to finalize whatever is active.
        public static void CompleteRun(string runId)
        {
            if (_active == null) return;
            if (!string.IsNullOrEmpty(runId) && _active.RunId != runId) return;

            _active.CompletedAt = DateTime.Now;
            _active.RecomputeCounts();

            _completed.AddLast(_active);
            while (_completed.Count > Capacity)
            {
                _completed.RemoveFirst();
            }
            _totalRunsRecorded++;
            _active = null;
            EmitChanged();
        }

        public static void Clear()
        {
            if (_completed.Count == 0 && _active == null) return;
            _completed.Clear();
            _active = null;
            _totalRunsRecorded = 0;
            EmitChanged();
        }

        private static void EmitChanged()
        {
            try { Changed?.Invoke(); } catch { }
        }

        // Keep the args summary short and control-char-free so the UI label stays
        // readable. Mirrors BridgeActivityLog.TruncateSnippet's cleaning.
        private const int SummaryMaxChars = 160;

        internal static string TruncateSummary(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var cleaned = StripControlChars(raw);
            if (cleaned.Length <= SummaryMaxChars) return cleaned;
            return cleaned.Substring(0, SummaryMaxChars) + "…";
        }

        private const int MessageMaxChars = 200;

        internal static string TruncateMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length <= MessageMaxChars ? message : message.Substring(0, MessageMaxChars) + "…";
        }

        private static string StripControlChars(string s)
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
