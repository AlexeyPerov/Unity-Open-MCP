using System;
using System.Collections.Generic;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge
{
    public class CheckpointStoreEntry
    {
        public string CheckpointId;
        public string Timestamp;
        // LRU access clock. Seeded from Timestamp on Store(); refreshed on every
        // Get() so a checkpoint an agent is actively delta-comparing against is
        // not evicted purely by insert count. ISO-8601 UTC so string comparison
        // is a valid ordering.
        public string LastAccessedUtc;
        public string Label;
        public string[] Paths;
        public string[] Categories;
        public CheckpointFingerprint Fingerprint;
    }

    public static class CheckpointStore
    {
        private const int DefaultCapacity = 20;
        private static readonly List<CheckpointStoreEntry> _entries = new();
        private static readonly Dictionary<string, CheckpointStoreEntry> _index = new();

        public static int Count => _entries.Count;

        public static IReadOnlyList<CheckpointStoreEntry> Recent
        {
            get
            {
                // Returned in chronological order (oldest insert first) so the UI
                // can render the latest entry at the bottom; reverse as needed at
                // the call site. Note: this is insertion order, not access order
                // — LRU affects only eviction, not display position.
                return _entries.AsReadOnly();
            }
        }

        public static void Store(CheckpointStoreEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // Item A — collision handling. Previously a duplicate CheckpointId was
            // a silent no-op (the first entry won). That could discard a freshly
            // re-captured fingerprint with no error surfaced. Now overwrite: treat
            // a re-submission as "latest data wins" and refresh recency so the
            // overwritten entry is moved to the tail (most-recent) position.
            if (_index.TryGetValue(entry.CheckpointId, out var existing))
            {
                _entries.Remove(existing);
                _index.Remove(entry.CheckpointId);
            }

            if (string.IsNullOrEmpty(entry.LastAccessedUtc))
                entry.LastAccessedUtc = entry.Timestamp ?? DateTime.UtcNow.ToString("o");

            _entries.Add(entry);
            _index[entry.CheckpointId] = entry;

            while (_entries.Count > DefaultCapacity)
            {
                // Item B — LRU eviction. Drop the entry with the oldest
                // LastAccessedUtc (string comparison is valid for ISO-8601 UTC),
                // not blindly the first-inserted one. This keeps checkpoints an
                // agent is actively delta-comparing against alive even when many
                // newer inserts arrive (e.g. gate-run mirrors).
                var oldest = _entries[0];
                for (int i = 1; i < _entries.Count; i++)
                {
                    if (string.CompareOrdinal(_entries[i].LastAccessedUtc, oldest.LastAccessedUtc) < 0)
                        oldest = _entries[i];
                }
                _entries.Remove(oldest);
                _index.Remove(oldest.CheckpointId);
            }
        }

        public static CheckpointStoreEntry Get(string checkpointId)
        {
            if (checkpointId == null) return null;
            _index.TryGetValue(checkpointId, out var entry);
            // Item B — bump the access clock so active checkpoints survive LRU
            // eviction under pressure.
            if (entry != null)
                entry.LastAccessedUtc = DateTime.UtcNow.ToString("o");
            return entry;
        }

        public static void Clear()
        {
            _entries.Clear();
            _index.Clear();
        }
    }
}
