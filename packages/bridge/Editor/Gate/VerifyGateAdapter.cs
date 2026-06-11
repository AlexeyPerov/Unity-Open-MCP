using System.Collections.Generic;
using System.Linq;
using UnityAgentVerify;

namespace UnityAgentBridge
{
    public static class VerifyGateAdapter
    {
        static readonly string[] DefaultRuleIds = { "missing_references" };

        public static CheckpointFingerprint CreateCheckpoint(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? DefaultRuleIds;
            return VerifyRunner.CreateCheckpoint(scope, ids);
        }

        public static VerifyResult ValidatePaths(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? DefaultRuleIds;
            return VerifyRunner.RunScoped(scope, ids, VerifyRunMode.Validate);
        }

        public static DeltaData ComputeDelta(CheckpointFingerprint before, VerifyResult after)
        {
            var beforeKeys = new HashSet<string>();
            foreach (var fp in before.Fingerprints.Values)
                beforeKeys.UnionWith(fp.IssueKeys);

            var afterKeys = new HashSet<string>(after.Issues.Select(IssueKey.Build));

            var newKeys = new HashSet<string>(afterKeys);
            newKeys.ExceptWith(beforeKeys);

            var resolvedKeys = new HashSet<string>(beforeKeys);
            resolvedKeys.ExceptWith(afterKeys);

            var newErrors = 0;
            var newWarnings = 0;
            foreach (var issue in after.Issues)
            {
                var key = IssueKey.Build(issue);
                if (newKeys.Contains(key))
                {
                    if (issue.Severity == VerifySeverity.Error) newErrors++;
                    else newWarnings++;
                }
            }

            var resolvedErrors = 0;
            var resolvedWarnings = 0;
            foreach (var key in resolvedKeys)
            {
                var parts = key.Split('|');
                if (parts.Length >= 2)
                {
                    if (parts[1] == "ERROR") resolvedErrors++;
                    else if (parts[1] == "WARN") resolvedWarnings++;
                }
            }

            return new DeltaData
            {
                NewErrors = newErrors,
                NewWarnings = newWarnings,
                ResolvedErrors = resolvedErrors,
                ResolvedWarnings = resolvedWarnings,
                NewIssueKeys = newKeys.ToArray(),
                ResolvedIssueKeys = resolvedKeys.ToArray()
            };
        }
    }
}
