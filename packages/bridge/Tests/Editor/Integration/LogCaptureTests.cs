using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Console;

namespace UnityOpenMcpBridge.Tests
{
    // M22 T22.1.3 — per-call `logs` capture. These exercise the LogEntriesReader
    // capture-session API (StartCapture/StopCapture) that the dispatch path uses
    // to surface Unity warnings/errors emitted during a tool call inline.
    public class LogCaptureTests
    {
        [Test]
        public static void LogEntriesReader_CaptureAvailableInEditor()
        {
            Assert.IsTrue(LogEntriesReader.IsCaptureAvailable,
                "GetEntryCount/GetEntryInternal should be available in the Editor. " +
                "If this fails, the bridge degrades to an empty `logs` array on that Unity version.");
        }

        // Core T22.1.3 acceptance: a warning emitted between Start/StopCapture is
        // returned in the delta. The envelope path (DispatchWithGate) wraps every
        // dispatch with this pair; this is the unit-level proof of the capture.
        [Test]
        public static void StartCapture_ToStopCapture_ReturnsWarningDelta()
        {
            if (!LogEntriesReader.IsCaptureAvailable)
                Assert.Ignore("LogEntries capture API unavailable on this Unity version.");

            // Clear so the baseline count is stable and we don't pick up noise
            // from earlier tests. best-effort; ignore failures.
            LogEntriesReader.Clear();
            try
            {
                const string marker = "M22_LOGCAPTURE_WARNING_MARKER";
                int start = LogEntriesReader.StartCapture();
                Debug.LogWarning(marker);
                var captured = LogEntriesReader.StopCapture(start);

                Assert.IsNotNull(captured, "StopCapture must never return null (empty list when nothing new).");
                var match = captured.FirstOrDefault(e => (e.Message ?? "").Contains(marker));
                Assert.IsNotNull(match, $"captured delta should include the warning '{marker}'. " +
                    $"Got {captured.Count} entries: " +
                    string.Join(" | ", captured.Select(e => e.Message)));
                Assert.AreEqual("warning", LogEntriesReader.Classify(match.Mode),
                    "Warning marker must classify as 'warning'.");
            }
            finally
            {
                LogEntriesReader.Clear();
            }
        }

        // StartCapture returns -1 when capture is unavailable; StopCapture(-1)
        // returns an empty list (never null) so the envelope always emits `logs`.
        [Test]
        public static void StopCapture_InvalidStart_ReturnsEmptyNotNull()
        {
            var captured = LogEntriesReader.StopCapture(-1);
            Assert.IsNotNull(captured, "StopCapture(-1) must return empty list, not null.");
            Assert.AreEqual(0, captured.Count);
        }

        // A run that emits nothing produces an empty (not null) delta — the
        // envelope contract is "logs present, [] when nothing new".
        [Test]
        public static void StartCapture_ToStopCapture_NoEmit_ReturnsEmpty()
        {
            if (!LogEntriesReader.IsCaptureAvailable)
                Assert.Ignore("LogEntries capture API unavailable on this Unity version.");

            LogEntriesReader.Clear();
            try
            {
                int start = LogEntriesReader.StartCapture();
                // Emit nothing between start and stop.
                var captured = LogEntriesReader.StopCapture(start);
                Assert.IsNotNull(captured);
                Assert.AreEqual(0, captured.Count, "No emits → empty delta.");
            }
            finally
            {
                LogEntriesReader.Clear();
            }
        }

        // Regression: specs/feedback.md 2026-07-03 — read_console returned
        // execution_error "UnityEditor.LogEntries internal API not available"
        // on Unity 6 because GetEntries(List<LogEntry>) was removed from the
        // bindings. IsAvailable must be true as long as EITHER the legacy
        // GetEntries OR the GetCount()+GetEntryInternal fallback resolves; on
        // 6000.x only the fallback is present.
        [Test]
        public static void LogEntriesReader_AvailableInEditor_EitherPath()
        {
            Assert.IsTrue(LogEntriesReader.IsAvailable,
                "read_console requires either GetEntries(List<LogEntry>) or the " +
                "GetCount()+GetEntryInternal fallback. On Unity 6 (6000.x) only the " +
                "fallback resolves — IsAvailable must be true regardless.");
        }

        // Regression: same entry. GetEntries() must not throw on Unity 6; it
        // should drain the console via the per-row fallback and include any entry
        // emitted before the call.
        [Test]
        public static void GetEntries_ReturnsEmittedEntry_OnUnity6Fallback()
        {
            if (!LogEntriesReader.IsAvailable)
                Assert.Ignore("LogEntries reader unavailable on this Unity version.");

            LogEntriesReader.Clear();
            try
            {
                const string marker = "M22_READCONSOLE_FALLBACK_MARKER";
                Debug.Log(marker);

                var entries = LogEntriesReader.GetEntries();
                Assert.IsNotNull(entries, "GetEntries must never return null.");
                var match = entries.FirstOrDefault(e => (e.Message ?? "").Contains(marker));
                Assert.IsNotNull(match,
                    $"GetEntries should include the emitted log '{marker}'. " +
                    $"Got {entries.Count} entries: " +
                    string.Join(" | ", entries.Select(e => e.Message)));
            }
            finally
            {
                LogEntriesReader.Clear();
            }
        }
    }
}
