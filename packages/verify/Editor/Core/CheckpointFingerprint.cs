using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    public class CheckpointFingerprint
    {
        public string CheckpointId { get; }
        public Dictionary<string, RuleFingerprint> Fingerprints { get; }

        public CheckpointFingerprint(string checkpointId, Dictionary<string, RuleFingerprint> fingerprints)
        {
            CheckpointId = checkpointId;
            Fingerprints = fingerprints;
        }
    }

    public class RuleFingerprint
    {
        public int Errors { get; }
        public int Warnings { get; }
        public HashSet<string> IssueKeys { get; }

        public RuleFingerprint(int errors, int warnings, HashSet<string> issueKeys)
        {
            Errors = errors;
            Warnings = warnings;
            IssueKeys = issueKeys;
        }
    }
}
