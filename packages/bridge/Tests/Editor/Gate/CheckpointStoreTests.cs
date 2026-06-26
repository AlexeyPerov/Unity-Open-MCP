using System;
using NUnit.Framework;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.Tests
{
    [TestFixture]
    public class CheckpointStoreTests
    {
        // -------------------------------------------------------------------
        // Item A — duplicate CheckpointId must overwrite, not silently no-op.
        // The previous behaviour returned without updating, so the FIRST entry
        // won and the freshly re-captured fingerprint was discarded with no
        // error surfaced. The fix treats a re-submission as "latest data wins".
        // -------------------------------------------------------------------

        [SetUp]
        public void SetUp() => CheckpointStore.Clear();
        [TearDown]
        public void TearDown() => CheckpointStore.Clear();

        [Test]
        public void Store_DuplicateId_OverwritesExisting()
        {
            var first = Entry("cp_x", "label-first", ts(0));
            CheckpointStore.Store(first);

            var second = Entry("cp_x", "label-second", ts(1));
            CheckpointStore.Store(second);

            Assert.AreEqual(1, CheckpointStore.Count, "duplicate id must replace, not append");
            var stored = CheckpointStore.Get("cp_x");
            Assert.AreEqual("label-second", stored.Label, "the later entry must win");
        }

        [Test]
        public void Store_DuplicateId_RefreshesRecency()
        {
            // Overwriting a duplicate must refresh recency: re-storing cp_0 AFTER
            // cp_1 means cp_1 (older) is the LRU victim when pressure arrives.
            CheckpointStore.Store(Entry("cp_0", "first",  ts(0)));
            CheckpointStore.Store(Entry("cp_1", "filler", ts(1)));
            // Re-submit cp_0 with a NEWER timestamp than cp_1.
            CheckpointStore.Store(Entry("cp_0", "second", ts(9)));

            Assert.AreEqual(2, CheckpointStore.Count);
            Assert.AreEqual("second", CheckpointStore.Get("cp_0").Label);
            Assert.AreEqual("filler", CheckpointStore.Get("cp_1").Label);
        }

        // -------------------------------------------------------------------
        // Item B — LRU eviction. A checkpoint an agent reads via Get() must not
        // be evicted purely because 20 newer inserts arrived; the least-
        // recently-ACCESSED entry (not the least-recently-INSERTED one) goes.
        // -------------------------------------------------------------------

        [Test]
        public void Get_BumpsAccessClock()
        {
            CheckpointStore.Store(Entry("cp_a", "a", ts(0)));
            var before = CheckpointStore.Get("cp_a").LastAccessedUtc;
            Assert.IsFalse(string.IsNullOrEmpty(before),
                "Get must populate LastAccessedUtc so the entry survives LRU pressure");
        }

        [Test]
        public void Store_OverCapacity_EvictsLeastRecentlyAccessed()
        {
            // Insert capacity (20) entries with monotonically increasing
            // timestamps, oldest first. All pinned to 2026-01-01 so that any
            // real UtcNow produced by Get() (>= 2026-06-26) is strictly newer —
            // guaranteeing the touched entry survives regardless of wall clock.
            for (int i = 0; i < 20; i++)
                CheckpointStore.Store(Entry("cp_" + i, "e" + i, ts(i)));

            // Touch the OLDEST (cp_0) so its access clock is newer than cp_1,
            // which was never read after insertion.
            CheckpointStore.Get("cp_0");

            // Insert one more → must evict the least-recently-accessed, which is
            // cp_1 (cp_0 was just accessed and must survive). cp_0 keeps its
            // bumped access clock even though cp_new is fresher on insert.
            CheckpointStore.Store(Entry("cp_new", "new", ts(20)));

            Assert.IsNotNull(CheckpointStore.Get("cp_0"),
                "recently-accessed checkpoint must survive LRU eviction");
            Assert.IsNotNull(CheckpointStore.Get("cp_new"),
                "newly-inserted checkpoint must be present");
            Assert.IsNull(CheckpointStore.Get("cp_1"),
                "least-recently-accessed checkpoint must be evicted under LRU");
        }

        [Test]
        public void Store_OverCapacity_FallbackEvictsWhenAllNeverAccessed()
        {
            // Nobody calls Get(); all entries keep their insert-time LastAccessed.
            // Insert 21 distinct ids; exactly one must be evicted and Count == 20.
            for (int i = 0; i < 21; i++)
                CheckpointStore.Store(Entry("cp_" + i, "e" + i, ts(i)));

            Assert.AreEqual(20, CheckpointStore.Count, "capacity must be enforced");
            Assert.IsNull(CheckpointStore.Get("cp_0"),
                "oldest-inserted (and never accessed) entry is the LRU victim when no accesses happened");
        }

        // -------------------------------------------------------------------
        // Clear() — the production escape hatch (Item C wires it to a UI button).
        // -------------------------------------------------------------------

        [Test]
        public void Clear_EmptiesTheStore()
        {
            CheckpointStore.Store(Entry("cp_a", "a", ts(0)));
            CheckpointStore.Store(Entry("cp_b", "b", ts(1)));
            Assert.AreEqual(2, CheckpointStore.Count);

            CheckpointStore.Clear();

            Assert.AreEqual(0, CheckpointStore.Count);
            Assert.IsNull(CheckpointStore.Get("cp_a"));
            Assert.IsNull(CheckpointStore.Get("cp_b"));
        }

        // ---- helpers --------------------------------------------------------

        // Deterministic, comparable ISO-8601 UTC timestamp. The numeric suffix
        // controls ordering so LRU tests don't depend on wall-clock ticks.
        private static string ts(int order) =>
            "2026-01-01T00:00:" + order.ToString("00") + ".0000000Z";

        private static CheckpointStoreEntry Entry(string id, string label, string timestamp)
        {
            return new CheckpointStoreEntry
            {
                CheckpointId = id,
                Timestamp = timestamp,
                Label = label,
                Paths = new[] { "Assets" },
                Categories = Array.Empty<string>(),
                Fingerprint = new CheckpointFingerprint(id, null)
            };
        }
    }
}
