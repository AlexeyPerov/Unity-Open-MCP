using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityOpenMcpVerify.Cache;
using UnityEngine;

namespace UnityOpenMcpVerify.Batch
{
    public static class VerifyBatchEntry
    {
        public const string OutputBegin = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
        public const string OutputEnd = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

        public const int ExitPass = 0;
        public const int ExitFail = 1;

        private static readonly string[] ValidOperations =
            { "scan_all", "baseline_create", "regression_check" };

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            int exitCode;
            string json;

            try
            {
                var result = Execute(args);
                exitCode = result.exitCode;
                json = result.json;
            }
            catch (Exception e)
            {
                Debug.LogError($"[VerifyBatchEntry] Unhandled exception: {e}");
                exitCode = ExitFail;
                json = ErrorJson("unhandled_exception", e.Message);
            }

            Console.WriteLine(OutputBegin);
            Console.WriteLine(json);
            Console.WriteLine(OutputEnd);

            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
        }

        internal static (int exitCode, string json) Execute(string[] allArgs)
        {
            var toolArgs = ExtractToolArgs(allArgs);

            if (toolArgs.Length == 0)
            {
                return Fail(
                    operation: null,
                    message: "No tool arguments found after '--'. " +
                             "Usage: Unity -batchmode -executeMethod " +
                             "UnityOpenMcpVerify.Batch.VerifyBatchEntry.Run -- " +
                             "<operation> [--platform-profile <p>] ..."
                );
            }

            var operation = toolArgs[0];
            var flagArgs = SliceAfter(toolArgs, 0);

            switch (operation)
            {
                case "scan_all":
                    return RunScanAll(flagArgs);
                case "baseline_create":
                    return RunBaselineCreate(flagArgs);
                case "regression_check":
                    return RunRegressionCheck(flagArgs);
                default:
                    return Fail(
                        operation: operation,
                        message: $"Unknown operation '{operation}'. " +
                                 $"Expected one of: {string.Join(", ", ValidOperations)}."
                    );
            }
        }

        private static (int exitCode, string json) RunScanAll(string[] args)
        {
            var parsed = ParseFlags(args, "scan_all");
            if (parsed.error != null)
                return Fail("scan_all", parsed.error);

            var profile = parsed.platformProfile;
            var threshold = SeverityThreshold.Parse(parsed.failOnSeverity);
            var outputPath = parsed.outputPath;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scope = new VerifyScope(null);
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Full);
            sw.Stop();

            VerifyCacheService.Record(result, VerifyCacheService.SourceScanAll);

            var batchResult = BuildBatchResult("scan_all", profile, result, sw.ElapsedMilliseconds);
            batchResult.failOnSeverity = SeverityThreshold.ToString(threshold);
            batchResult.outputPath = outputPath;

            bool shouldFail = SeverityThreshold.ShouldFail(threshold, result);
            batchResult.exitCode = shouldFail ? ExitFail : ExitPass;

            var json = JsonUtility.ToJson(batchResult, true);

            if (!string.IsNullOrEmpty(outputPath))
                WriteOutputFile(outputPath, json);

            return (batchResult.exitCode, json);
        }

        private static (int exitCode, string json) RunBaselineCreate(string[] args)
        {
            var parsed = ParseFlags(args, "baseline_create");
            if (parsed.error != null)
                return Fail("baseline_create", parsed.error);

            if (string.IsNullOrEmpty(parsed.baselinePath))
            {
                return Fail("baseline_create",
                    "Required argument --baseline-path is missing.");
            }

            var profile = parsed.platformProfile;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scope = new VerifyScope(null);
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Full);
            sw.Stop();

            VerifyCacheService.Record(result, VerifyCacheService.SourceScanAll);

            var baseline = BaselineStore.CreateFromResult(result, profile);

            try
            {
                BaselineStore.Save(baseline, ResolveProjectPath(parsed.baselinePath));
            }
            catch (Exception e)
            {
                return Fail("baseline_create",
                    $"Failed to write baseline: {e.Message}");
            }

            var batchResult = BuildBatchResult("baseline_create", profile, result, sw.ElapsedMilliseconds);
            batchResult.baselinePath = parsed.baselinePath;
            batchResult.exitCode = ExitPass;

            var json = JsonUtility.ToJson(batchResult, true);
            return (batchResult.exitCode, json);
        }

        private static (int exitCode, string json) RunRegressionCheck(string[] args)
        {
            var parsed = ParseFlags(args, "regression_check");
            if (parsed.error != null)
                return Fail("regression_check", parsed.error);

            if (string.IsNullOrEmpty(parsed.baselinePath))
            {
                return Fail("regression_check",
                    "Required argument --baseline-path is missing.");
            }

            var profile = parsed.platformProfile;
            int threshold = parsed.regressionThreshold;

            BaselineFile baseline;
            try
            {
                baseline = BaselineStore.Load(ResolveProjectPath(parsed.baselinePath));
            }
            catch (Exception e)
            {
                return Fail("regression_check",
                    $"Failed to load baseline: {e.Message}");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scope = new VerifyScope(null);
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Full);
            sw.Stop();

            VerifyCacheService.Record(result, VerifyCacheService.SourceScanAll);

            var current = BaselineStore.CreateFromResult(result, profile);
            // Per-category thresholds are optional; null map reduces to the
            // historical global-only comparison.
            var regression = BaselineStore.Compare(
                current, baseline, threshold, parsed.perCategoryThresholds);

            var batchResult = BuildBatchResult("regression_check", profile, result, sw.ElapsedMilliseconds);
            batchResult.baselinePath = parsed.baselinePath;
            batchResult.regression = regression;
            batchResult.exitCode = regression.regressed ? ExitFail : ExitPass;

            var json = JsonUtility.ToJson(batchResult, true);
            return (batchResult.exitCode, json);
        }

        private static BatchResult BuildBatchResult(
            string operation,
            string platformProfile,
            VerifyResult verifyResult,
            long durationMs)
        {
            var br = new BatchResult
            {
                operation = operation,
                platformProfile = platformProfile,
                durationMs = durationMs
            };

            if (verifyResult == null)
            {
                br.summary = new SeveritySummary();
                return br;
            }

            int errorCount = 0;
            int warnCount = 0;
            foreach (var issue in verifyResult.Issues)
            {
                if (issue.Severity == VerifySeverity.Error) errorCount++;
                else if (issue.Severity == VerifySeverity.Warning) warnCount++;
            }

            br.summary = new SeveritySummary(errorCount, warnCount, 0);

            foreach (var ruleId in verifyResult.CategoriesRun)
            {
                var ruleIssues = verifyResult.Issues
                    .Where(i => i.RuleId == ruleId)
                    .ToList();

                br.rules.Add(new BatchRuleSummary
                {
                    ruleId = ruleId,
                    error = ruleIssues.Count(i => i.Severity == VerifySeverity.Error),
                    warn = ruleIssues.Count(i => i.Severity == VerifySeverity.Warning),
                    info = 0,
                    durationMs = 0
                });
            }

            foreach (var issue in verifyResult.Issues)
            {
                br.issues.Add(new IssueEntry
                {
                    ruleId = issue.RuleId,
                    severity = issue.Severity == VerifySeverity.Error ? "Error" : "Warning",
                    assetPath = issue.AssetPath,
                    issueCode = issue.IssueCode,
                    description = issue.Description
                });
            }

            return br;
        }

        #region Argument parsing

        class ParsedFlags
        {
            public string platformProfile = "desktop";
            public string failOnSeverity = "warn";
            public string outputPath;
            public string baselinePath;
            public int regressionThreshold;
            // Per-rule error thresholds. Null when the caller did not pass any
            // --per-category-threshold flag; the comparison then uses the global
            // --regression-threshold only.
            public Dictionary<string, int> perCategoryThresholds;
            public string error;
        }

        private static ParsedFlags ParseFlags(string[] args, string operation)
        {
            var p = new ParsedFlags();

            for (int i = 0; i < args.Length; i++)
            {
                var flag = args[i];

                switch (flag)
                {
                    case "--platform-profile":
                        if (i + 1 >= args.Length)
                        {
                            p.error = $"--platform-profile requires a value. " +
                                      $"Expected one of: mobile, console, desktop.";
                            return p;
                        }
                        p.platformProfile = args[++i];
                        if (!IsValidProfile(p.platformProfile))
                        {
                            p.error = $"Invalid platform-profile '{p.platformProfile}'. " +
                                      $"Expected one of: mobile, console, desktop.";
                            return p;
                        }
                        break;

                    case "--fail-on-severity":
                        if (i + 1 >= args.Length)
                        {
                            p.error = $"--fail-on-severity requires a value. " +
                                      $"Expected one of: {string.Join(", ", SeverityThreshold.ValidValues)}.";
                            return p;
                        }
                        p.failOnSeverity = args[++i];
                        try
                        {
                            SeverityThreshold.Parse(p.failOnSeverity);
                        }
                        catch (ArgumentException)
                        {
                            p.error = $"Invalid fail-on-severity '{p.failOnSeverity}'. " +
                                      $"Expected one of: {string.Join(", ", SeverityThreshold.ValidValues)}.";
                            return p;
                        }
                        break;

                    case "--output-path":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--output-path requires a value.";
                            return p;
                        }
                        p.outputPath = args[++i];
                        break;

                    case "--baseline-path":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--baseline-path requires a value.";
                            return p;
                        }
                        p.baselinePath = args[++i];
                        break;

                    case "--regression-threshold":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--regression-threshold requires an integer value.";
                            return p;
                        }
                        if (!int.TryParse(args[++i], out int threshold))
                        {
                            p.error = $"Invalid regression-threshold '{args[i]}'. " +
                                      "Expected a non-negative integer.";
                            return p;
                        }
                        if (threshold < 0)
                        {
                            p.error = $"Invalid regression-threshold '{threshold}'. " +
                                      "Expected a non-negative integer.";
                            return p;
                        }
                        p.regressionThreshold = threshold;
                        break;

                    case "--per-category-threshold":
                        // Repeatable: --per-category-threshold <ruleId>=<int>.
                        // A per-rule override for the regression gate; rules not
                        // named here fall back to --regression-threshold.
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--per-category-threshold requires a value of the form <ruleId>=<int>.";
                            return p;
                        }
                        {
                            var raw = args[++i];
                            var eq = raw.IndexOf('=');
                            if (eq <= 0 || eq == raw.Length - 1)
                            {
                                p.error = $"Invalid --per-category-threshold '{raw}'. " +
                                          "Expected form <ruleId>=<int>.";
                                return p;
                            }
                            var ruleId = raw.Substring(0, eq);
                            if (!int.TryParse(raw.Substring(eq + 1), out int perRuleThreshold) || perRuleThreshold < 0)
                            {
                                p.error = $"Invalid --per-category-threshold '{raw}'. " +
                                          "Threshold must be a non-negative integer.";
                                return p;
                            }
                            if (p.perCategoryThresholds == null)
                                p.perCategoryThresholds = new Dictionary<string, int>();
                            p.perCategoryThresholds[ruleId] = perRuleThreshold;
                        }
                        break;

                    default:
                        if (flag.StartsWith("--"))
                        {
                            p.error = $"Unknown argument '{flag}' for operation '{operation}'.";
                            return p;
                        }
                        break;
                }
            }

            return p;
        }

        private static bool IsValidProfile(string profile)
        {
            return profile == "mobile" || profile == "console" || profile == "desktop";
        }

        #endregion

        #region CLI extraction helpers

        private static string[] ExtractToolArgs(string[] allArgs)
        {
            for (int i = 0; i < allArgs.Length; i++)
            {
                if (allArgs[i] == "--")
                    return SliceAfter(allArgs, i);
            }

            for (int i = 0; i < allArgs.Length - 1; i++)
            {
                if (allArgs[i] == "-executeMethod")
                    return SliceAfter(allArgs, i + 1);
            }

            return Array.Empty<string>();
        }

        private static string[] SliceAfter(string[] source, int index)
        {
            if (index + 1 >= source.Length)
                return Array.Empty<string>();

            var result = new string[source.Length - index - 1];
            Array.Copy(source, index + 1, result, 0, result.Length);
            return result;
        }

        #endregion

        #region Output helpers

        internal static string ResolveProjectPath(string relativeOrAbsolute)
        {
            if (Path.IsPathRooted(relativeOrAbsolute))
                return relativeOrAbsolute;

            // Directory.GetParent(dataPath) is the project root — the same
            // pattern ApplyFixGateRunner.PredictTouchedPaths uses. The old
            // `.Replace("/Assets", "")` replaced ALL occurrences of "/Assets"
            // in the path, corrupting project paths like
            // "/Users/MyAssets/Projects/MyGame/Assets" into
            // "/Users/My/Projects/MyGame".
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                projectRoot = Application.dataPath;
            return Path.Combine(projectRoot, relativeOrAbsolute);
        }

        private static void WriteOutputFile(string outputPath, string json)
        {
            try
            {
                var fullPath = ResolveProjectPath(outputPath);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(fullPath, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VerifyBatchEntry] Failed to write output file '{outputPath}': {e.Message}");
            }
        }

        private static (int exitCode, string json) Fail(string operation, string message)
        {
            var result = new BatchResult
            {
                operation = operation,
                exitCode = ExitFail,
                error = message
            };
            return (ExitFail, JsonUtility.ToJson(result, true));
        }

        private static string ErrorJson(string operation, string message)
        {
            var result = new BatchResult
            {
                operation = operation,
                exitCode = ExitFail,
                error = message
            };
            return JsonUtility.ToJson(result, true);
        }

        #endregion
    }
}
