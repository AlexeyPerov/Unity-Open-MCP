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

    // M13 T4.1 — in-memory checkpoint store for the gate delta flow.
    //
    // STORAGE CONTRACT (v1): checkpoints are process-lifetime and in-memory.
    // The store uses static fields (_entries / _index), so ANY domain reload
    // wipes every checkpoint. The gate flow itself triggers reloads:
    //   - asmdef_create / asmdef_modify force a recompile + domain reload.
    //   - package_add / package_remove resolve assemblies → domain reload.
    //   - reimport_package calls RequestScriptCompilation → domain reload.
    //   - execute_csharp / invoke_method can recompile.
    // A script edit picked up by the asset database also forces a reload.
    //
    // After a reload, any checkpoint_id an agent holds is GONE. DeltaTool
    // surfaces this as `checkpointLostOnReload: true` (when the store is empty)
    // so the agent can distinguish "wiped by reload" from "id was never created"
    // and re-establish the baseline via checkpoint_create. Disk persistence
    // across reloads is explicitly backlog (see backlog-bridge-reload-
    // recovery.md) — the v1 contract is: honest error + doc, not persistence.
    //
    // LRU eviction at capacity 20 (DefaultCapacity) is a SEPARATE concern: it
    // drops the least-recently-accessed entry when the store is full, NOT on
    // reload. Reload empties the store entirely; LRU only trims under pressure.
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
