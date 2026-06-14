using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UnityOpenMcpVerify
{
    public static class VerifyRunner
    {
        const long CheckpointBudgetMs = 2000;

        static readonly List<IVerifyRule> RegisteredRules = new();

        public static IReadOnlyList<IVerifyRule> Rules => RegisteredRules;

        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterDefaults()
        {
            if (RegisteredRules.Count == 0)
            {
                RegisteredRules.Add(new Rules.MissingReferencesRule());
                RegisteredRules.Add(new Rules.ScenePrefabHealthRule());
            }
        }

        public static void RegisterRule(IVerifyRule rule)
        {
            if (!RegisteredRules.Exists(r => r.Id == rule.Id))
                RegisteredRules.Add(rule);
        }

        public static void ClearRules()
        {
            RegisteredRules.Clear();
        }

        public static VerifyResult RunScoped(VerifyScope scope, string[] ruleIds, VerifyRunMode mode)
        {
            var sw = Stopwatch.StartNew();
            var issues = new List<VerifyIssue>();

            string[] unknownRuleIds;
            string[] availableRuleIds;
            List<IVerifyRule> rulesToRun;

            if (ruleIds != null && ruleIds.Length > 0)
            {
                var requested = new HashSet<string>(ruleIds);
                var known = new HashSet<string>(RegisteredRules.Select(r => r.Id));
                unknownRuleIds = requested.Where(id => !known.Contains(id)).ToArray();
                availableRuleIds = RegisteredRules.Select(r => r.Id).ToArray();
                rulesToRun = RegisteredRules.Where(r => requested.Contains(r.Id)).ToList();
            }
            else
            {
                unknownRuleIds = Array.Empty<string>();
                availableRuleIds = Array.Empty<string>();
                rulesToRun = RegisteredRules.ToList();
            }

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

            if (mode == VerifyRunMode.Checkpoint && sw.ElapsedMilliseconds > CheckpointBudgetMs)
            {
                UnityEngine.Debug.LogWarning(
                    $"[VerifyRunner] Checkpoint took {sw.ElapsedMilliseconds}ms " +
                    $"(budget: {CheckpointBudgetMs}ms) for paths: {string.Join(", ", scope.Paths ?? Array.Empty<string>())}");
            }

            return new VerifyResult(issues, categoriesRun, sw.ElapsedMilliseconds, unknownRuleIds, availableRuleIds);
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
