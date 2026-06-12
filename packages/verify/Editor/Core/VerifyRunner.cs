using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UnityAgentVerify
{
    public static class VerifyRunner
    {
        static readonly List<IVerifyRule> RegisteredRules = new();

        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterDefaults()
        {
            if (RegisteredRules.Count == 0)
                RegisteredRules.Add(new Rules.MissingReferencesRule());
        }

        public static void RegisterRule(IVerifyRule rule)
        {
            if (!RegisteredRules.Exists(r => r.Id == rule.Id))
                RegisteredRules.Add(rule);
        }

        public static VerifyResult RunScoped(VerifyScope scope, string[] ruleIds, VerifyRunMode mode)
        {
            var sw = Stopwatch.StartNew();
            var issues = new List<VerifyIssue>();

            var rulesToRun = ruleIds != null && ruleIds.Length > 0
                ? RegisteredRules.Where(r => ruleIds.Contains(r.Id)).ToList()
                : RegisteredRules.ToList();

            var categoriesRun = rulesToRun.Select(r => r.Id).ToArray();

            foreach (var rule in rulesToRun)
            {
                try
                {
                    rule.Scan(scope, mode, issues);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[VerifyRunner] Rule '{rule.Id}' threw: {e.Message}");
                }
            }

            sw.Stop();
            return new VerifyResult(issues, categoriesRun, sw.ElapsedMilliseconds);
        }

        public static CheckpointFingerprint CreateCheckpoint(VerifyScope scope, string[] ruleIds)
        {
            var result = RunScoped(scope, ruleIds, VerifyRunMode.Checkpoint);
            var id = $"cp_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var fingerprints = new Dictionary<string, RuleFingerprint>();

            foreach (var category in result.CategoriesRun)
            {
                var categoryIssues = result.Issues.Where(i => i.RuleId == category).ToList();
                var errors = categoryIssues.Count(i => i.Severity == VerifySeverity.Error);
                var warnings = categoryIssues.Count(i => i.Severity == VerifySeverity.Warning);
                var keys = new HashSet<string>(categoryIssues.Select(IssueKey.Build));
                fingerprints[category] = new RuleFingerprint(errors, warnings, keys);
            }

            return new CheckpointFingerprint(id, fingerprints);
        }
    }
}
