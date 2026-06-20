using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeAuditLog
    {
        // 5 MiB per active file. Large enough for a typical session, small
        // enough that rotation is frequent enough to matter for retention.
        public const long MaxFileBytes = 5L * 1024 * 1024;
        public const int MaxRetainedFiles = 5;

        // Test-only override for the audit directory. Production callers leave
        // it null and the log lands under ~/.unity-open-mcp/audit/.
        public static string AuditDirOverride;

        private static readonly object _writeLock = new object();
        private static bool _availabilityLogged;

        public static string AuditDir
        {
            get
            {
                if (!string.IsNullOrEmpty(AuditDirOverride)) return AuditDirOverride;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".unity-open-mcp",
                    "audit");
            }
        }

        public static bool Enabled => BridgeProjectSettings.AuditLogEnabled;

        // Append one record. Safe to call on the listener worker thread. No-op
        // when the operator has not opted in. Best-effort: an I/O failure is
        // logged once and the record is dropped (audit logging must never break
        // the dispatch path, mirroring BridgeActivityLog.Record).
        public static void Record(BridgeAuditRecord record)
        {
            if (!Enabled) return;
            if (record == null) return;
            try
            {
                WriteRecord(record);
            }
            catch (Exception e)
            {
                LogOnce($"[BridgeAuditLog] write failed: {e.Message}");
            }
        }

        private static void WriteRecord(BridgeAuditRecord record)
        {
            var dir = AuditDir;
            var path = ActiveFilePath(dir, record.ProjectHash);
            var line = record.ToJsonLine() + "\n";

            lock (_writeLock)
            {
                EnsureDir(dir);
                MaybeRotate(dir, record.ProjectHash, line.Length);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }

        private static string ActiveFilePath(string dir, string projectHash) =>
            Path.Combine(dir, $"audit-{projectHash}.jsonl");

        private static string RotatedFilePath(string dir, string projectHash, int seq) =>
            Path.Combine(dir, $"audit-{projectHash}.{seq}.jsonl");

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // Rotate when the active file + the new line would exceed the cap.
        // Renames active → .1, .1 → .2, …, and deletes anything past the
        // retention count. Rotation is best-effort: a rename failure leaves
        // the active file oversized rather than dropping the audit record.
        private static void MaybeRotate(string dir, string projectHash, int incomingBytes)
        {
            var active = ActiveFilePath(dir, projectHash);
            long currentSize = 0;
            try
            {
                var fi = new FileInfo(active);
                if (fi.Exists) currentSize = fi.Length;
            }
            catch { }

            if (currentSize + incomingBytes <= MaxFileBytes) return;

            // Drop the oldest retained file first so the rename chain has room.
            for (int seq = MaxRetainedFiles; seq >= 1; seq--)
            {
                var target = RotatedFilePath(dir, projectHash, seq);
                var next = seq == MaxRetainedFiles ? null : RotatedFilePath(dir, projectHash, seq + 1);
                try
                {
                    if (next == null)
                    {
                        // Oldest slot: delete in place.
                        if (File.Exists(target)) File.Delete(target);
                    }
                    else if (File.Exists(target))
                    {
                        File.Move(target, next);
                    }
                }
                catch { /* best-effort rotation */ }
            }

            try
            {
                if (File.Exists(active)) File.Move(active, RotatedFilePath(dir, projectHash, 1));
            }
            catch { }
        }

        private static void LogOnce(string message)
        {
            if (_availabilityLogged) return;
            _availabilityLogged = true;
            Debug.LogWarning(message);
        }

        // Reset the one-shot warning flag. Test-only — lets a fresh test see
        // the warning again if a write fails.
        internal static void ResetWarningFlagForTests() => _availabilityLogged = false;
    }

    // One audit record. Serialized as a single JSON line (no pretty-print) so
    // the file is greppable and streamable. Field names match the gate envelope
    // so a downstream SIEM / grep can join on tool/mode/checkpointId.
    public sealed class BridgeAuditRecord
    {
        public DateTime Timestamp;
        public string ProjectHash;
        public string Tool;
        public string GateMode;
        public string[] PathsHint;
        public string Outcome;          // passed | warned | failed | skipped
        public bool GateRan;
        public int NewErrors;
        public int NewWarnings;
        public int ResolvedErrors;
        public int ResolvedWarnings;
        public string CheckpointId;
        public long TotalGateDurationMs;
        public string MutationErrorCode;
        // Bypass marker: true when the deny heuristic was skipped via the
        // gate=off + confirm_bypass escape hatch. Lets an auditor grep for
        // every time an agent talked its way past a deny pattern.
        public bool BypassedDenyList;
        // The matched deny pattern when the request was refused by the deny
        // heuristic (Outcome == "denied"). Null otherwise.
        public string DeniedPattern;

        public string ToJsonLine()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"ts\":\"").Append(Escape(Timestamp.ToString("o", CultureInfo.InvariantCulture))).Append("\",");
            sb.Append("\"projectHash\":\"").Append(Escape(ProjectHash ?? "")).Append("\",");
            sb.Append("\"tool\":").Append(JsonString(Tool)).Append(',');
            sb.Append("\"gate\":").Append(JsonString(GateMode)).Append(',');
            sb.Append("\"pathsHint\":").Append(JsonStringArray(PathsHint)).Append(',');
            sb.Append("\"outcome\":").Append(JsonString(Outcome)).Append(',');
            sb.Append("\"gateRan\":").Append(GateRan ? "true" : "false").Append(',');
            sb.Append("\"newErrors\":").Append(NewErrors).Append(',');
            sb.Append("\"newWarnings\":").Append(NewWarnings).Append(',');
            sb.Append("\"resolvedErrors\":").Append(ResolvedErrors).Append(',');
            sb.Append("\"resolvedWarnings\":").Append(ResolvedWarnings).Append(',');
            sb.Append("\"checkpointId\":").Append(JsonString(CheckpointId)).Append(',');
            sb.Append("\"totalGateMs\":").Append(TotalGateDurationMs).Append(',');
            sb.Append("\"mutationError\":").Append(JsonString(MutationErrorCode)).Append(',');
            sb.Append("\"bypassedDenyList\":").Append(BypassedDenyList ? "true" : "false").Append(',');
            sb.Append("\"deniedPattern\":").Append(JsonString(DeniedPattern));
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonString(string s) => s == null ? "null" : "\"" + Escape(s) + "\"";

        private static string JsonStringArray(string[] arr)
        {
            if (arr == null) return "null";
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(arr[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
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
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
