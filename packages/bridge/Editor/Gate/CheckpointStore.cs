using System;
using System.Collections.Generic;
using UnityAgentVerify;

namespace UnityAgentBridge
{
    public class CheckpointStoreEntry
    {
        public string CheckpointId;
        public string Timestamp;
        public string Label;
        public string[] Paths;
        public string[] Categories;
        public CheckpointFingerprint Fingerprint;
    }

    public static class CheckpointStore
    {
        const int DefaultCapacity = 20;
        static readonly List<CheckpointStoreEntry> _entries = new();
        static readonly Dictionary<string, CheckpointStoreEntry> _index = new();

        public static int Count => _entries.Count;

        public static void Store(CheckpointStoreEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (_index.ContainsKey(entry.CheckpointId))
                return;

            _entries.Add(entry);
            _index[entry.CheckpointId] = entry;

            while (_entries.Count > DefaultCapacity)
            {
                var oldest = _entries[0];
                _entries.RemoveAt(0);
                _index.Remove(oldest.CheckpointId);
            }
        }

        public static CheckpointStoreEntry Get(string checkpointId)
        {
            if (checkpointId == null) return null;
            _index.TryGetValue(checkpointId, out var entry);
            return entry;
        }

        public static void Clear()
        {
            _entries.Clear();
            _index.Clear();
        }
    }
}
