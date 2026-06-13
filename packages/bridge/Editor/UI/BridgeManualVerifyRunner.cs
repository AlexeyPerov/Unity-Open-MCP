// Bridge-side facade for ad-hoc manual verify runs driven from the Editor UI (M4.5-8).
//
// Wraps VerifyGateAdapter.ValidatePaths with a small result-shape so the bridge window
// can render the manual validate outcome without leaking verify-package types into the
// UI layer. Manual verify is **non-mutating**: it does not create a gate transaction,
// does not create a checkpoint, and does not touch the gate precedence rules — it is a
// pure read-only scan over the supplied paths.
//
// Paths come from either the Project window selection (default) or a user-typed list
// (one per line). The runner normalizes/validates paths and surfaces an empty result
// for empty/invalid input so the UI can render a clean no-data state.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityAgentVerify;
using UnityEditor;

namespace UnityAgentBridge
{
    public class BridgeManualIssue
    {
        public string RuleId;
        public string Severity;     // "error" / "warning"
        public string AssetPath;
        public string IssueCode;
        public string Description;
    }

    public class BridgeManualAssetGroup
    {
        public string AssetPath;
        public int ErrorCount;
        public int WarningCount;
        public List<BridgeManualIssue> Issues = new List<BridgeManualIssue>();
    }

    public class BridgeManualValidateResult
    {
        public bool Ran;
        public string ErrorMessage;
        public string[] InputPaths;
        public string[] CategoriesRun;
        public long DurationMs;
        public int TotalErrors;
        public int TotalWarnings;
        public int TotalAssets;
        public List<BridgeManualAssetGroup> Groups = new List<BridgeManualAssetGroup>();
    }

    public static class BridgeManualVerifyRunner
    {
        public static string[] GetSelectionAssetPaths()
        {
            var paths = new List<string>();
            var objects = Selection.objects;
            if (objects == null) return paths.ToArray();

            foreach (var obj in objects)
            {
                if (obj == null) continue;
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                paths.Add(path);
            }
            return paths.ToArray();
        }

        public static string[] ParsePathList(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                var trimmed = raw.Trim().Trim('"');
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!trimmed.StartsWith("Assets/", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    // Accept anyway; verify will surface unknown-path cases via its own rules.
                }
                seen.Add(trimmed);
            }
            return seen.ToArray();
        }

        public static BridgeManualValidateResult Run(string[] paths)
        {
            var result = new BridgeManualValidateResult
            {
                InputPaths = paths ?? Array.Empty<string>()
            };

            if (paths == null || paths.Length == 0)
            {
                result.Ran = false;
                result.ErrorMessage = null;
                return result;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                VerifyResult verify;
                try
                {
                    verify = VerifyGateAdapter.ValidatePaths(paths, null);
                }
                finally
                {
                    sw.Stop();
                }

                result.Ran = true;
                result.DurationMs = sw.ElapsedMilliseconds;
                result.CategoriesRun = verify?.CategoriesRun;

                var groupsByPath = new Dictionary<string, BridgeManualAssetGroup>(StringComparer.Ordinal);
                int totalErrors = 0;
                int totalWarnings = 0;

                if (verify?.Issues != null)
                {
                    foreach (var issue in verify.Issues)
                    {
                        if (issue == null) continue;
                        var assetPath = string.IsNullOrEmpty(issue.AssetPath) ? "(no asset)" : issue.AssetPath;
                        if (!groupsByPath.TryGetValue(assetPath, out var group))
                        {
                            group = new BridgeManualAssetGroup { AssetPath = assetPath };
                            groupsByPath[assetPath] = group;
                        }

                        var severity = issue.Severity == VerifySeverity.Error ? "error" : "warning";
                        if (issue.Severity == VerifySeverity.Error) totalErrors++;
                        else totalWarnings++;

                        group.Issues.Add(new BridgeManualIssue
                        {
                            RuleId = issue.RuleId,
                            Severity = severity,
                            AssetPath = assetPath,
                            IssueCode = issue.IssueCode,
                            Description = issue.Description
                        });

                        if (issue.Severity == VerifySeverity.Error) group.ErrorCount++;
                        else group.WarningCount++;
                    }
                }

                foreach (var g in groupsByPath.Values)
                {
                    g.Issues.Sort((a, b) =>
                    {
                        int sev = SeverityRank(a.Severity).CompareTo(SeverityRank(b.Severity));
                        if (sev != 0) return sev;
                        return string.CompareOrdinal(a.IssueCode, b.IssueCode);
                    });
                }

                var sortedGroups = groupsByPath.Values
                    .OrderByDescending(g => g.ErrorCount)
                    .ThenByDescending(g => g.WarningCount)
                    .ThenBy(g => g.AssetPath, StringComparer.Ordinal)
                    .ToList();

                result.Groups = sortedGroups;
                result.TotalErrors = totalErrors;
                result.TotalWarnings = totalWarnings;
                result.TotalAssets = sortedGroups.Count;
                return result;
            }
            catch (Exception e)
            {
                result.Ran = false;
                result.ErrorMessage = e.Message;
                return result;
            }
        }

        static int SeverityRank(string severity)
        {
            return severity == "error" ? 0 : 1;
        }
    }
}
