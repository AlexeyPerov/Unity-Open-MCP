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
}
