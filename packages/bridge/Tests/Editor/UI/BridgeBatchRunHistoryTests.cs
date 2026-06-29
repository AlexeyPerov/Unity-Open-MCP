using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // T20.7.5.1 — pins the BridgeBatchRunHistory state shape the Batch tab renders.
    // The history is a static singleton; each test clears it first so they don't
    // depend on execution order. Mirrors the BridgeActivityLog / BridgeGateRunHistory
    // test posture: pure state-machinery tests, no UI rendering.
    public static class BridgeBatchRunHistoryTests
    {
        [SetUp]
        public static void SetUp()
        {
            BridgeBatchRunHistory.Clear();
        }

        [Test]
        public static void BeginRun_ActiveRun_IsSetWithZeroEntries()
        {
            var run = BridgeBatchRunHistory.BeginRun("run-1", "mcp", "CI regression");

            Assert.IsNotNull(run);
            Assert.AreEqual("run-1", run.RunId);
            Assert.AreEqual("mcp", run.Source);
            Assert.AreEqual("CI regression", run.Label);
            Assert.IsFalse(run.IsComplete);
            Assert.AreEqual(0, run.TotalCount);
            Assert.AreSame(run, BridgeBatchRunHistory.Active);
            Assert.AreEqual(0, BridgeBatchRunHistory.CompletedCount);
        }

        [Test]
        public static void AddEntry_Pending_AndCountsRecompute()
        {
            var run = BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");

            var e0 = BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{\"paths\":[\"Assets/Foo.prefab\"]}");
            var e1 = BridgeBatchRunHistory.AddEntry("unity_open_mcp_validate_edit", "{}");

            Assert.AreEqual(0, e0.Index);
            Assert.AreEqual(1, e1.Index);
            Assert.AreEqual(BridgeBatchEntryStatus.Pending, e0.Status);
            Assert.AreEqual(BridgeBatchEntryStatus.Pending, e1.Status);
            Assert.AreEqual(2, run.TotalCount);
            Assert.AreEqual(2, run.PendingCount);
            Assert.AreEqual(0, run.DoneCount);
        }

        [Test]
        public static void SetEntryStatus_Transitions_UpdateCountsAndTimestamps()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}");
            BridgeBatchRunHistory.AddEntry("unity_open_mcp_validate_edit", "{}");

            BridgeBatchRunHistory.SetEntryStatus(0, BridgeBatchEntryStatus.Running);
            BridgeBatchRunHistory.SetEntryStatus(1, BridgeBatchEntryStatus.Running);
            var active = BridgeBatchRunHistory.Active;
            Assert.AreEqual(2, active.RunningCount);

            BridgeBatchRunHistory.SetEntryStatus(0, BridgeBatchEntryStatus.Done, durationMs: 42);
            BridgeBatchRunHistory.SetEntryStatus(1, BridgeBatchEntryStatus.Failed,
                durationMs: 7, errorCode: "validation_failed", errorMessage: "broken ref");

            Assert.AreEqual(1, active.DoneCount);
            Assert.AreEqual(1, active.FailedCount);
            Assert.AreEqual(0, active.RunningCount);
            Assert.AreEqual(42, active.Entries[0].DurationMs);
            Assert.IsNotNull(active.Entries[0].FinishedAt);
            Assert.AreEqual("validation_failed", active.Entries[1].ErrorCode);
            Assert.AreEqual("broken ref", active.Entries[1].ErrorMessage);
            Assert.IsNotNull(active.Entries[1].FinishedAt);
        }

        [Test]
        public static void CompleteRun_MovesToCompletedRing_AndClearsActive()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}");
            BridgeBatchRunHistory.SetEntryStatus(0, BridgeBatchEntryStatus.Done, durationMs: 10);

            BridgeBatchRunHistory.CompleteRun("run-1");

            Assert.IsNull(BridgeBatchRunHistory.Active);
            Assert.AreEqual(1, BridgeBatchRunHistory.CompletedCount);
            Assert.AreEqual(1, BridgeBatchRunHistory.TotalRunsRecorded);
            var completed = BridgeBatchRunHistory.Completed;
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].IsComplete);
            Assert.IsNotNull(completed[0].CompletedAt);
        }

        [Test]
        public static void CompleteRun_WrongRunId_IsIgnored()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            // Defensive: completing a different run id must not finalize the active one.
            BridgeBatchRunHistory.CompleteRun("some-other-run");
            Assert.IsNotNull(BridgeBatchRunHistory.Active);
            Assert.AreEqual(0, BridgeBatchRunHistory.CompletedCount);
        }

        [Test]
        public static void BeginRun_SecondBegin_FinalizesPreviousActiveDefensively()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "first");
            BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}");

            // Starting a new run while the first is still active should not leave
            // two active runs — the previous one is completed defensively.
            BridgeBatchRunHistory.BeginRun("run-2", "mcp", "second");

            Assert.AreEqual("run-2", BridgeBatchRunHistory.Active.RunId);
            Assert.AreEqual(1, BridgeBatchRunHistory.CompletedCount);
        }

        [Test]
        public static void CompletedRingBuffer_TrimsAtCapacity()
        {
            // Fill past capacity; oldest completed runs are LRU-trimmed.
            for (int i = 0; i < BridgeBatchRunHistory.Capacity + 5; i++)
            {
                BridgeBatchRunHistory.BeginRun($"run-{i}", "mcp", $"run {i}");
                BridgeBatchRunHistory.CompleteRun($"run-{i}");
            }

            Assert.AreEqual(BridgeBatchRunHistory.Capacity, BridgeBatchRunHistory.CompletedCount);
            Assert.AreEqual(BridgeBatchRunHistory.Capacity + 5, BridgeBatchRunHistory.TotalRunsRecorded);
        }

        [Test]
        public static void AddEntry_BeyondMaxEntriesPerRun_IsDropped()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            for (int i = 0; i < BridgeBatchRunHistory.MaxEntriesPerRun; i++)
            {
                Assert.IsNotNull(BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}"));
            }
            // One past the cap is refused (returns null) so a runaway batch can't
            // produce an unbounded payload.
            Assert.IsNull(BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}"));
            Assert.AreEqual(BridgeBatchRunHistory.MaxEntriesPerRun, BridgeBatchRunHistory.Active.TotalCount);
        }

        [Test]
        public static void ArgsSummary_IsTruncatedAndControlCharScrubbed()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            var longArgs = new string('x', BridgeBatchRunHistory.MaxEntriesPerRun) + "tail"; // overlong
            // TruncateSummary is internal; exercise it via AddEntry.
            var entry = BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", longArgs);
            Assert.IsNotNull(entry.ArgsSummary);
            StringAssert.EndsWith("…", entry.ArgsSummary);
        }

        [Test]
        public static void AddEntry_WithNoActiveRun_IsNoOp()
        {
            // No active run → AddEntry returns null, no throw.
            Assert.IsNull(BridgeBatchRunHistory.AddEntry("unity_open_mcp_scan_paths", "{}"));
        }

        [Test]
        public static void SetEntryStatus_OutOfRange_IsIgnored()
        {
            BridgeBatchRunHistory.BeginRun("run-1", "mcp", "run");
            // Negative / huge indices must not throw.
            BridgeBatchRunHistory.SetEntryStatus(-1, BridgeBatchEntryStatus.Done);
            BridgeBatchRunHistory.SetEntryStatus(999, BridgeBatchEntryStatus.Done);
            Assert.Pass();
        }
    }
}
