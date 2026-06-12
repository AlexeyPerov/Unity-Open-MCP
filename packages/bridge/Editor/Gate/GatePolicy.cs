namespace UnityAgentBridge
{
    public enum GateMode
    {
        Enforce,
        Warn,
        Off
    }

    public class DeltaData
    {
        public int NewErrors;
        public int NewWarnings;
        public int ResolvedErrors;
        public int ResolvedWarnings;
        public string[] NewIssueKeys;
        public string[] ResolvedIssueKeys;
    }

    public class GateDispatchResult
    {
        public ToolDispatchResult Mutation;
        public bool GateRan;
        public string CheckpointId;
        public string[] CategoriesRun;
        public long ValidationDurationMs;
        public DeltaData Delta;
        public bool GateFailed;
        public string[] AgentNextSteps;
    }

    public class FindReferencesResult
    {
        public string QueriedAssetPath;
        public string QueriedAssetGuid;
        public ReferencedByEntry[] ReferencedBy;
        public int TotalCount;
    }

    public class ReferencedByEntry
    {
        public string AssetPath;
        public string Guid;
    }
}
