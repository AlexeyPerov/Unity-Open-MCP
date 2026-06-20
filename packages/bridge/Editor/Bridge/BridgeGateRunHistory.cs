using System;
using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    public class BridgeGateRunRecord
    {
        public string ToolName;
        public string RequestedMode;
        public string EffectiveMode;
        public GateOutcome Outcome;
        public bool GateRan;
        public bool GateFailed;
        public int NewErrors;
        public int NewWarnings;
        public int ResolvedErrors;
        public int ResolvedWarnings;
        public long CheckpointDurationMs;
        public long ValidationDurationMs;
        public long TotalGateDurationMs;
        public string[] CategoriesRun;
        public string[] AgentNextSteps;
        public string MutationError;
        public DateTime Timestamp;
    }

    public static class BridgeGateRunHistory
    {
        public const int Capacity = 20;

        private static readonly LinkedList<BridgeGateRunRecord> _records = new LinkedList<BridgeGateRunRecord>();
        private static BridgeGateRunRecord _latest;

        public static event Action Changed;

        public static BridgeGateRunRecord Latest => _latest;

        public static IReadOnlyList<BridgeGateRunRecord> Records
        {
            get
            {
                var list = new List<BridgeGateRunRecord>(_records.Count);
                foreach (var r in _records) list.Add(r);
                return list;
            }
        }

        public static int Count => _records.Count;

        public static void Record(BridgeGateRunRecord record)
        {
            if (record == null) return;
            _records.AddLast(record);
            while (_records.Count > Capacity)
            {
                _records.RemoveFirst();
            }
            _latest = record;
            try { Changed?.Invoke(); } catch { }
        }

        public static void Clear()
        {
            if (_records.Count == 0) return;
            _records.Clear();
            _latest = null;
            try { Changed?.Invoke(); } catch { }
        }
    }
}
