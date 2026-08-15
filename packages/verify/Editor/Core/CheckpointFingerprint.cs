using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    public class CheckpointFingerprint
    {
        public string CheckpointId { get; }
        public Dictionary<string, RuleFingerprint> Fingerprints { get; }
        // Rule ids that threw while the checkpoint was captured. Their
        // fingerprints are absent, so a delta against this checkpoint cannot
        // trust those rules in either direction (pre-existing issues from a
        // failed rule would read as "new" post-mutation, or as "resolved" if
        // the rule fails again on validate). GatePolicy uses this to distrust
        // the delta. Empty on a clean capture.
        public string[] RulesFailed { get; }

        public CheckpointFingerprint(string checkpointId, Dictionary<string, RuleFingerprint> fingerprints,
            string[] rulesFailed = null)
        {
            CheckpointId = checkpointId;
            Fingerprints = fingerprints;
            RulesFailed = rulesFailed ?? System.Array.Empty<string>();
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
