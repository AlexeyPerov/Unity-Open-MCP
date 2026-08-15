using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UnityOpenMcpVerify
{
    public static class VerifyRunner
    {
        private const long CheckpointBudgetMs = 2000;

        private static readonly List<IVerifyRule> RegisteredRules = new();

        public static IReadOnlyList<IVerifyRule> Rules => RegisteredRules;

        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterDefaults()
        {
            if (RegisteredRules.Count == 0)
            {
                RegisteredRules.Add(new Rules.MissingReferencesRule());
                RegisteredRules.Add(new Rules.ScenePrefabHealthRule());
                RegisteredRules.Add(new Rules.DependenciesRule());
                // M25 Plan 1 — wave-1 rule families.
                RegisteredRules.Add(new Rules.AsmdefAuditRule());
                RegisteredRules.Add(new Rules.ProjectHealthRule());
                RegisteredRules.Add(new Rules.MaterialsRule());
                RegisteredRules.Add(new Rules.AnimationAnalysisRule());
                RegisteredRules.Add(new Rules.ShaderAnalysisRule());
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

            // A rule that throws must not read as "ran clean": its issues are
            // simply absent from the list, which previously made an incomplete
            // scan indistinguishable from a healthy one (a crashing rule
            // produced a vacuous all-clear on both sides of the gate delta).
            // Record the failure per rule id and surface it on the result so
            // consumers (gate, scan_paths, batch entry) can distrust the
            // incomplete data instead of trusting silence.
            var rulesFailed = new List<string>();
            foreach (var rule in rulesToRun)
            {
                try
                {
                    rule.Scan(scope, mode, issues);
                }
                catch (Exception e)
                {
                    rulesFailed.Add(rule.Id);
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

            return new VerifyResult(issues, categoriesRun, sw.ElapsedMilliseconds, unknownRuleIds, availableRuleIds,
                rulesFailed.Count == 0 ? null : rulesFailed.ToArray());
        }

        public static CheckpointFingerprint CreateCheckpoint(VerifyScope scope, string[] ruleIds)
        {
            var result = RunScoped(scope, ruleIds, VerifyRunMode.Checkpoint);
            var id = $"cp_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var fingerprints = new Dictionary<string, RuleFingerprint>();

            foreach (var category in result.CategoriesRun)
            {
                // A rule that threw contributed no issues — its fingerprint
                // would claim a clean baseline the rule never established.
                // Skip failed rules here; the RulesFailed carry-through lets
                // the gate distrust the checkpoint instead.
                if (result.RulesFailed.Contains(category)) continue;
                var categoryIssues = result.Issues.Where(i => i.RuleId == category).ToList();
                var errors = categoryIssues.Count(i => i.Severity == VerifySeverity.Error);
                var warnings = categoryIssues.Count(i => i.Severity == VerifySeverity.Warning);
                var keys = new HashSet<string>(categoryIssues.Select(IssueKey.Build));
                fingerprints[category] = new RuleFingerprint(errors, warnings, keys);
            }

            return new CheckpointFingerprint(id, fingerprints, result.RulesFailed);
        }
    }
}
