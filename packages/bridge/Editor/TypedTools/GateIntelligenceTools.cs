using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Cache;
using UnityEditor;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 8 — gate intelligence tools. Three read-only, gate-free,
    // direct-response tools that compose existing gate/validate/checkpoint/
    // delta/verify foundations to make edits safer and easier to reason about
    // before and after mutation. They do NOT add new verify rules and do NOT
    // re-implement validate_edit / checkpoint_create / delta — they project
    // those foundations into compact, agent-actionable shapes.
    //
    //   - ImpactPreview        — pre-mutation risk from paths_hint scope.
    //                            Heuristic; confidence bounds are surfaced.
    //   - GateBudgetEstimate   — forecast validation duration / issue budget.
    //                            Heuristic; optionally runs a cheap checkpoint
    //                            scan to ground the estimate.
    //   - MutationExplain      — post-mutation human-readable narrative built
    //                            from the latest gate run (or an explicit
    //                            checkpoint_id + delta pair).
    //
    // All three accept the same scope-first `paths_hint` vocabulary the gate
    // uses, so an agent can plan with impact_preview + gate_budget_estimate,
    // run the mutation, and then explain the result with mutation_explain —
    // all against the same scope.
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool
    // alongside the other M16 typed tools (gate-free direct-response tools,
    // see DirectResponseTools).
    public static class GateIntelligenceTools
    {
        // ---------------------------------------------------------------------
        // impact_preview
        // ---------------------------------------------------------------------

        // Pre-mutation impact projection. Pure composition over:
        //   - VerifyGateAdapter.ResolveRuleIds  (which rules apply to the scope)
        //   - AssetDatabase                     (does each path exist, what kind)
        //   - the static extension -> asset-kind map below
        //
        // The tool intentionally does NOT run a rule scan — that is what
        // validate_edit is for. It answers "if I mutate this scope, what will
        // the gate look at, and how big is the surface?" so an agent can size
        // the risk before paying for a checkpoint.
        public static ToolDispatchResult ImpactPreview(string body)
        {
            var pathsHint = JsonBody.GetStringArray(body, "paths_hint");
            // Optional explicit rule narrowing, mirroring validate_edit /
            // scan_paths. When omitted, rules are auto-selected from the paths.
            var categories = JsonBody.GetStringArray(body, "categories");
            var includeRules = JsonBody.GetStringArray(body, "include_rules");
            var excludeRules = JsonBody.GetStringArray(body, "exclude_rules");

            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths_hint' is required and must be a non-empty array. " +
                    "Gate intelligence is scope-first — there is no whole-project fallback.");

            string[] rulesApplied;
            try
            {
                rulesApplied = VerifyGateAdapter.ResolveRuleIds(
                    pathsHint, categories, includeRules, excludeRules);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("rule_resolution_error", e.Message);
            }

            // ResolveRuleIds returns null when filters narrow the set to nothing
            // (distinct from "run all"). Surface that as an explicit empty set.
            if (rulesApplied == null)
                rulesApplied = Array.Empty<string>();

            var perPath = new List<PathImpact>(pathsHint.Length);
            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingCount = 0;
            foreach (var rawPath in pathsHint)
            {
                var path = NormalizeScopePath(rawPath);
                if (string.IsNullOrEmpty(path))
                {
                    missingCount++;
                    continue;
                }

                var exists = AssetExists(path, out var assetKind, out var isFolder);
                if (!exists) missingCount++;

                if (!string.IsNullOrEmpty(assetKind))
                {
                    kindCounts.TryGetValue(assetKind, out var c);
                    kindCounts[assetKind] = c + 1;
                }

                perPath.Add(new PathImpact
                {
                    Path = path,
                    Exists = exists,
                    IsFolder = isFolder,
                    AssetKind = assetKind ?? "unknown",
                    RulesForExtension = RulesForPath(path, rulesApplied),
                });
            }

            // Heuristic risk band. This is intentionally a coarse classifier,
            // not a guarantee — see `confidence` below.
            var risk = ClassifyRisk(perPath.Count, rulesApplied.Length, missingCount, kindCounts);

            return ToolDispatchResult.Ok(BuildImpactJson(
                pathsHint, rulesApplied, perPath, kindCounts, missingCount, risk));
        }

        private static string NormalizeScopePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // AssetDatabase paths are forward-slashed and Assets/-rooted. The
            // gate accepts both styles; normalize so the impact projection
            // matches what AssetDatabase.Exists / LoadAssetAtPath will see.
            var p = raw.Replace('\\', '/').Trim();
            return p;
        }

        private static bool AssetExists(string path, out string assetKind, out bool isFolder)
        {
            assetKind = null;
            isFolder = false;
            if (string.IsNullOrEmpty(path)) return false;

            // AssetDatabase.LoadMainAssetAtPath returns null for missing assets
            // and for directories; AssetDatabase.IsValidFolder covers folders.
            // Only Assets/-rooted folders are indexed by AssetDatabase — gate
            // the call so Package / ProjectSettings paths fall through to the
            // file-existence branch below.
            if (FolderRootOf(path) != null && AssetDatabase.IsValidFolder(path))
            {
                isFolder = true;
                assetKind = "folder";
                return true;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            assetKind = AssetKindForExtension(ext);

            // ProjectSettings / Packages manifest paths are valid gate scopes
            // even though AssetDatabase does not index them as assets. The
            // asset kind is already derived from the extension above; here we
            // only need to confirm the file exists on disk.
            if (IsProjectFile(path))
                return File.Exists(ResolveDiskPath(path));

            return !string.IsNullOrEmpty(ext) && AssetDatabase.LoadMainAssetAtPath(path) != null;
        }

        private static string FolderRootOf(string path)
        {
            // Only treat paths under Assets/ as potential folders — Package /
            // ProjectSettings folder scopes are file scopes, not asset folders.
            if (path == null) return null;
            var norm = path.Replace('\\', '/');
            if (norm.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return "Assets";
            return null;
        }

        private static bool IsProjectFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var norm = path.Replace('\\', '/');
            return norm.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveDiskPath(string projectRelativePath)
        {
            try
            {
                var dataPath = UnityEngine.Application.dataPath;
                var projectRoot = Directory.GetParent(dataPath)?.FullName ?? dataPath;
                return Path.GetFullPath(Path.Combine(projectRoot,
                    projectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return null;
            }
        }

        // Mirrors the extension -> asset-kind projection the verify rule catalog
        // advertises (mcp-server/src/capabilities/rule-catalog.ts) and that the
        // bridge gate adapter uses to auto-select rules. Kept here so the impact
        // preview classifies scopes without going through the rule catalog.
        private static string AssetKindForExtension(string ext)
        {
            switch (ext)
            {
                case ".prefab": return "prefab";
                case ".unity": return "scene";
                case ".mat": return "material";
                case ".shader":
                case ".shadergraph": return "shader";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga": return "texture";
                case ".controller": return "animator_controller";
                case ".anim": return "animation_clip";
                case ".asset": return "scriptable_object";
                case ".cs": return "script";
                case ".asmdef": return "assembly_definition";
                case ".wav":
                case ".mp3":
                case ".ogg": return "audio";
                case ".json": return "json";
                case "": return null;
                default: return "other";
            }
        }

        private static string[] RulesForPath(string path, string[] allRules)
        {
            if (allRules == null || allRules.Length == 0) return Array.Empty<string>();
            var auto = VerifyGateAdapter.SelectRuleIds(new[] { path });
            if (auto == null || auto.Length == 0) return Array.Empty<string>();
            var set = new HashSet<string>(auto);
            set.IntersectWith(allRules);
            var arr = new string[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return arr;
        }

        private static RiskBand ClassifyRisk(int pathCount, int ruleCount, int missingCount,
            Dictionary<string, int> kindCounts)
        {
            // Coarse heuristic — surfaced with an explicit confidence band so
            // agents treat it as guidance, not ground truth.
            //
            // - surface size: how many paths / rule categories touch the scope
            // - asset kinds that historically carry cross-asset fallout
            //   (prefab / scene / scriptable_object): weighted higher
            // - missing paths: the gate cannot validate what is not there yet
            //   (a create op); not risky on its own but lowers confidence
            var highFalloutKinds = 0;
            foreach (var kvp in kindCounts)
            {
                if (kvp.Key == "prefab" || kvp.Key == "scene"
                    || kvp.Key == "scriptable_object" || kvp.Key == "animator_controller")
                {
                    highFalloutKinds += kvp.Value;
                }
            }

            var score = 0;
            if (pathCount >= 1) score += 1;
            if (pathCount >= 4) score += 1;
            if (pathCount >= 12) score += 1;
            if (ruleCount >= 1) score += 1;
            if (ruleCount >= 3) score += 1;
            score += Math.Min(highFalloutKinds, 4);

            string band;
            string confidence;
            if (score <= 2)
            {
                band = "low";
                confidence = "medium";
            }
            else if (score <= 5)
            {
                band = "moderate";
                confidence = "medium";
            }
            else
            {
                band = "high";
                confidence = "medium";
            }

            // Missing paths mean the impact preview cannot fully ground itself
            // against AssetDatabase — drop the confidence band one step so the
            // agent knows to re-run after the create op lands.
            if (missingCount > 0 && confidence == "medium")
                confidence = "low";

            return new RiskBand { Band = band, Confidence = confidence, Score = score };
        }

        private static string BuildImpactJson(
            string[] pathsHint,
            string[] rulesApplied,
            List<PathImpact> perPath,
            Dictionary<string, int> kindCounts,
            int missingCount,
            RiskBand risk)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"scope\":{");
            sb.Append("\"pathsHintCount\":").Append(pathsHint.Length);
            sb.Append(",\"missingCount\":").Append(missingCount);
            sb.Append(",\"assetKinds\":{");
            var first = true;
            foreach (var kvp in kindCounts)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(OutputSerializer.EscapeJsonString(kvp.Key)).Append("\":")
                  .Append(kvp.Value);
            }
            sb.Append("}}");

            sb.Append(",\"rulesProjected\":[");
            if (rulesApplied != null)
            {
                for (int i = 0; i < rulesApplied.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(rulesApplied[i])).Append('"');
                }
            }
            sb.Append(']');

            sb.Append(",\"risk\":{\"band\":\"").Append(risk.Band)
              .Append("\",\"confidence\":\"").Append(risk.Confidence)
              .Append("\",\"score\":").Append(risk.Score).Append('}');

            sb.Append(",\"perPath\":[");
            for (int i = 0; i < perPath.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = perPath[i];
                sb.Append('{');
                sb.Append("\"path\":\"").Append(OutputSerializer.EscapeJsonString(p.Path)).Append("\",");
                sb.Append("\"exists\":").Append(p.Exists ? "true" : "false");
                if (p.IsFolder) sb.Append(",\"isFolder\":true");
                sb.Append(",\"assetKind\":\"").Append(OutputSerializer.EscapeJsonString(p.AssetKind)).Append("\",");
                sb.Append("\"rulesForExtension\":[");
                if (p.RulesForExtension != null)
                {
                    for (int j = 0; j < p.RulesForExtension.Length; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(p.RulesForExtension[j])).Append('"');
                    }
                }
                sb.Append("]}");
            }
            sb.Append(']');

            sb.Append(",\"heuristicNote\":\"impact_preview is a heuristic projection of gate scope — it does NOT run a rule scan. Run validate_edit to confirm actual issues before or after mutating.\"");
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // gate_budget_estimate
        // ---------------------------------------------------------------------

        // Forecast validation cost before a mutation. The estimate is grounded
        // in two ways, selected by `mode`:
        //
        //   - "cache"  (default): inspect the most recent VerifyCacheService
        //                snapshot. Cheap and deterministic, but coarse — the
        //                cache is a single global snapshot (not keyed by scope),
        //                so it reflects "the last thing the gate saw", not
        //                necessarily this scope. `confidence: low` when the
        //                snapshot is stale or missing.
        //   - "sample": run a cheap Checkpoint-mode scan over the resolved
        //                scope and time it. More accurate, pays one scan.
        //
        // Either way the result is a forecast, not a measurement of the live
        // gate path — checkpoint-mode is lighter than Validate-mode (no issue
        // detail collection beyond what the rule needs for fingerprinting), so
        // `estimatedDurationMs` is a lower bound. `confidence` says how tight.
        public static ToolDispatchResult GateBudgetEstimate(string body)
        {
            var pathsHint = JsonBody.GetStringArray(body, "paths_hint");
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths_hint' is required and must be a non-empty array.");

            var categories = JsonBody.GetStringArray(body, "categories");
            var includeRules = JsonBody.GetStringArray(body, "include_rules");
            var excludeRules = JsonBody.GetStringArray(body, "exclude_rules");
            var modeRaw = JsonBody.GetString(body, "mode") ?? "cache";
            var mode = modeRaw.Equals("sample", StringComparison.OrdinalIgnoreCase)
                ? BudgetMode.Sample : BudgetMode.Cache;

            string[] rulesApplied;
            try
            {
                rulesApplied = VerifyGateAdapter.ResolveRuleIds(
                    pathsHint, categories, includeRules, excludeRules);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("rule_resolution_error", e.Message);
            }
            if (rulesApplied == null) rulesApplied = Array.Empty<string>();

            long measuredMs = -1;
            int measuredIssues = -1;
            if (mode == BudgetMode.Sample && rulesApplied.Length > 0)
            {
                // Only sample when there is at least one rule to run. Passing
                // null/empty ruleIds to VerifyRunner.RunScoped means "run ALL
                // registered rules" — wrong for a budget on a scope the caller
                // explicitly narrowed to nothing. An empty rule set falls
                // through to the heuristic path below.
                try
                {
                    var scope = new VerifyScope(pathsHint);
                    var sw = Stopwatch.StartNew();
                    var sample = VerifyRunner.RunScoped(
                        scope, rulesApplied, VerifyRunMode.Checkpoint);
                    sw.Stop();
                    measuredMs = sw.ElapsedMilliseconds;
                    measuredIssues = sample.Issues.Count;
                }
                catch (Exception e)
                {
                    return ToolDispatchResult.Fail("sample_scan_error", e.Message);
                }
            }

            // The cache is a single global snapshot (see VerifyCacheService) —
            // it is NOT keyed by scope. Surface it as a coarse signal and let
            // the confidence band tell the agent how much to trust it.
            HealthSummarySnapshot snapshot = null;
            if (mode == BudgetMode.Cache)
            {
                snapshot = VerifyCacheService.GetSnapshot();
                if (snapshot == null || snapshot.IsEmpty || snapshot.Status == HealthSummaryStatus.NoData)
                    snapshot = null;
            }

            // Heuristic forecast derived from scope size + rule breadth, used
            // as the baseline when no measured / cached number is available.
            var pathCount = CountScopeAssets(pathsHint);
            var heuristicMs = EstimateDurationMs(pathCount, rulesApplied.Length);
            var heuristicBudget = EstimateIssueBudget(pathCount, rulesApplied.Length);

            long estimatedMs;
            int estimatedBudget;
            string confidence;
            string basis;
            if (measuredMs >= 0)
            {
                estimatedMs = measuredMs;
                estimatedBudget = measuredIssues >= 0 ? measuredIssues : heuristicBudget;
                confidence = "high";
                basis = "sample_checkpoint";
            }
            else if (snapshot != null && snapshot.Summary != null)
            {
                // The cache carries issue counts but not duration — fall back
                // to the heuristic for the time estimate, but ground the issue
                // budget in the recorded snapshot.
                estimatedMs = heuristicMs;
                estimatedBudget = snapshot.Summary.error + snapshot.Summary.warn;
                confidence = VerifyCacheService.IsStale() ? "low" : "medium";
                basis = "verify_cache";
            }
            else
            {
                estimatedMs = heuristicMs;
                estimatedBudget = heuristicBudget;
                confidence = "low";
                basis = "heuristic";
            }

            return ToolDispatchResult.Ok(BuildBudgetJson(
                pathsHint, rulesApplied, pathCount, mode, basis, confidence,
                estimatedMs, estimatedBudget, snapshot));
        }

        private static int CountScopeAssets(string[] pathsHint)
        {
            // Best-effort count of concrete assets the gate would scan. Folder
            // scopes expand to their asset count; explicit asset paths count 1.
            // Unknown / missing paths still count 1 so the heuristic never
            // under-estimates an empty scope.
            var total = 0;
            foreach (var raw in pathsHint)
            {
                var path = NormalizeScopePath(raw);
                if (string.IsNullOrEmpty(path)) { total++; continue; }

                if (AssetDatabase.IsValidFolder(path))
                {
                    var guids = AssetDatabase.FindAssets(string.Empty, new[] { path });
                    total += guids?.Length ?? 1;
                    continue;
                }

                total++;
            }
            return Math.Max(total, 1);
        }

        private static long EstimateDurationMs(int assetCount, int ruleCount)
        {
            // Rough per-asset / per-rule cost. Tuned to over-estimate on
            // purpose — `confidence: low` already signals this is a heuristic.
            const long perAssetPerRuleMs = 2;
            var rules = Math.Max(ruleCount, 1);
            var estimate = assetCount * rules * perAssetPerRuleMs;
            // Floor at a few ms so tiny scopes don't read as zero.
            return Math.Max(estimate, 5);
        }

        private static int EstimateIssueBudget(int assetCount, int ruleCount)
        {
            // Conservative upper bound on issues the gate might surface — used
            // to size token budgets for follow-up validate_edit calls. One
            // issue per asset per rule is a generous ceiling.
            return assetCount * Math.Max(ruleCount, 1);
        }

        private static string BuildBudgetJson(
            string[] pathsHint, string[] rulesApplied, int pathCount,
            BudgetMode mode, string basis, string confidence,
            long estimatedMs, int estimatedBudget,
            HealthSummarySnapshot snapshot)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"scope\":{");
            sb.Append("\"pathsHintCount\":").Append(pathsHint.Length);
            sb.Append(",\"estimatedAssetCount\":").Append(pathCount);
            sb.Append("}");

            sb.Append(",\"rulesProjected\":[");
            if (rulesApplied != null)
            {
                for (int i = 0; i < rulesApplied.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(rulesApplied[i])).Append('"');
                }
            }
            sb.Append(']');

            sb.Append(",\"estimate\":{");
            sb.Append("\"basis\":\"").Append(basis).Append("\",");
            sb.Append("\"mode\":\"").Append(mode == BudgetMode.Sample ? "sample" : "cache").Append("\",");
            sb.Append("\"confidence\":\"").Append(confidence).Append("\",");
            sb.Append("\"estimatedDurationMs\":").Append(estimatedMs);
            sb.Append(",\"estimatedIssueBudget\":").Append(estimatedBudget);
            sb.Append('}');

            if (snapshot != null && snapshot.Summary != null)
            {
                // The cache is a single global snapshot — it is NOT keyed by
                // scope. Surface the as-of timestamp + source so the agent can
                // judge whether it is relevant to the current scope.
                sb.Append(",\"cacheSource\":{");
                sb.Append("\"source\":\"").Append(OutputSerializer.EscapeJsonString(snapshot.Source ?? "")).Append("\",");
                sb.Append("\"asOf\":\"").Append(OutputSerializer.EscapeJsonString(snapshot.AsOf ?? "")).Append("\",");
                sb.Append("\"errorCount\":").Append(snapshot.Summary.error);
                sb.Append(",\"warnCount\":").Append(snapshot.Summary.warn);
                sb.Append(",\"stale\":").Append(VerifyCacheService.IsStale() ? "true" : "false");
                sb.Append(",\"scopeNote\":\"cache is global — counts reflect the last scan, not necessarily this scope\"");
                sb.Append('}');
            }

            sb.Append(",\"heuristicNote\":\"gate_budget_estimate forecasts gate cost — checkpoint-mode is lighter than full validation, so estimatedDurationMs is a lower bound on the real gate path. Run validate_edit to measure actuals.\"");
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // mutation_explain
        // ---------------------------------------------------------------------

        // Post-mutation narrative. Composes:
        //   - BridgeGateRunHistory.Latest  (the most recent gate run, if any)
        //   - CheckpointStore              (an explicit checkpoint_id, if given)
        //   - GatePolicy delta math        (already recorded on the run record)
        //
        // The output is two parallel surfaces so it serves both humans and
        // downstream tooling:
        //   - `narrative`: a short prose paragraph summarising what happened.
        //   - `summary`: structured counts + outcome + agentNextSteps so an
        //     agent can branch without re-parsing prose.
        public static ToolDispatchResult MutationExplain(string body)
        {
            var checkpointId = JsonBody.GetString(body, "checkpoint_id");
            var toolFilter = JsonBody.GetString(body, "tool_name");

            // Source resolution. Two contracts:
            //   - checkpoint_id provided → compare that checkpoint against the
            //     CURRENT project state (fresh delta). This is the authoritative
            //     "what happened to this scope?" answer.
            //   - no checkpoint_id      → project the most recent gate run
            //     record (optionally filtered by tool_name) into a narrative.
            //     Uses the delta captured at mutation time.
            CheckpointStoreEntry explicitCheckpoint = null;
            if (!string.IsNullOrEmpty(checkpointId))
            {
                explicitCheckpoint = CheckpointStore.Get(checkpointId);
                if (explicitCheckpoint == null)
                    return ToolDispatchResult.Fail("checkpoint_not_found",
                        $"No checkpoint found with id '{checkpointId}'.");
            }

            BridgeGateRunRecord record = null;
            if (explicitCheckpoint == null)
            {
                if (!string.IsNullOrEmpty(toolFilter))
                {
                    // Records is oldest-first; walk it in reverse so we pick
                    // the LATEST matching run, not the first. Lets an agent say
                    // "explain the last prefab_apply run" without holding a
                    // checkpoint id.
                    var records = BridgeGateRunHistory.Records;
                    for (int i = records.Count - 1; i >= 0; i--)
                    {
                        var r = records[i];
                        if (r == null) continue;
                        if (string.Equals(r.ToolName, toolFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            record = r;
                            break;
                        }
                    }
                }
                else
                {
                    record = BridgeGateRunHistory.Latest;
                }

                if (record == null)
                    return ToolDispatchResult.Fail("no_mutation_context",
                        "No gate run recorded yet and no checkpoint_id provided. " +
                        "Run a mutating tool first, or pass a checkpoint_id from checkpoint_create.");
            }

            // Project whichever source we resolved into the shared explain shape.
            DeltaData delta = null;
            string[] categoriesRun = null;
            long checkpointMs = -1;
            long validationMs = -1;
            long totalMs = -1;
            string[] agentNextSteps = null;
            string outcomeLabel = null;
            string toolName = null;
            string[] scopePaths = null;

            if (explicitCheckpoint != null)
            {
                scopePaths = explicitCheckpoint.Paths;
                try
                {
                    var paths = explicitCheckpoint.Paths ?? Array.Empty<string>();
                    var current = VerifyGateAdapter.ValidatePaths(
                        paths, explicitCheckpoint.Categories);
                    delta = VerifyGateAdapter.ComputeDelta(
                        explicitCheckpoint.Fingerprint, current);
                    categoriesRun = current.CategoriesRun;
                    validationMs = current.DurationMs;
                    // No recorded outcome for a checkpoint-comparison path; the
                    // narrative derives the outcome from the delta instead.
                    outcomeLabel = delta.NewErrors > 0 ? "would_fail" : "clean";
                    toolName = "checkpoint_compare";
                }
                catch (FormatException e)
                {
                    return ToolDispatchResult.Fail("delta_error",
                        $"Delta computation failed: {e.Message}");
                }
                catch (Exception e)
                {
                    return ToolDispatchResult.Fail("validation_error", e.Message);
                }
            }
            else
            {
                // BridgeGateRunRecord stores the delta flattened (counts only —
                // issue keys are NOT retained on the record). Reconstruct a
                // partial DeltaData so the shared explain shape works for both
                // sources; the issue-key arrays stay empty for the run-record
                // path. Pass checkpoint_id to get populated issue keys.
                delta = new DeltaData
                {
                    NewErrors = record.NewErrors,
                    NewWarnings = record.NewWarnings,
                    ResolvedErrors = record.ResolvedErrors,
                    ResolvedWarnings = record.ResolvedWarnings,
                    NewIssueKeys = Array.Empty<string>(),
                    ResolvedIssueKeys = Array.Empty<string>(),
                };
                categoriesRun = record.CategoriesRun;
                checkpointMs = record.CheckpointDurationMs;
                validationMs = record.ValidationDurationMs;
                totalMs = record.TotalGateDurationMs;
                agentNextSteps = record.AgentNextSteps;
                outcomeLabel = OutcomeLabel(record.Outcome);
                toolName = record.ToolName;
            }

            var narrative = BuildNarrative(
                toolName, outcomeLabel, delta, totalMs, scopePaths);

            return ToolDispatchResult.Ok(BuildExplainJson(
                toolName, outcomeLabel, delta, categoriesRun,
                checkpointMs, validationMs, totalMs,
                agentNextSteps, scopePaths, narrative,
                explicitCheckpoint, record));
        }

        private static string OutcomeLabel(GateOutcome outcome)
        {
            switch (outcome)
            {
                case GateOutcome.Passed: return "passed";
                case GateOutcome.Warned: return "warned";
                case GateOutcome.Failed: return "failed";
                case GateOutcome.Skipped: return "skipped";
                default: return outcome.ToString().ToLowerInvariant();
            }
        }

        private static string BuildNarrative(
            string toolName, string outcome, DeltaData delta,
            long totalMs, string[] scopePaths)
        {
            var sb = new StringBuilder(256);
            if (!string.IsNullOrEmpty(toolName))
                sb.Append(toolName).Append(" ");
            else
                sb.Append("Mutation ");

            if (string.IsNullOrEmpty(outcome))
            {
                sb.Append("ran; ");
            }
            else
            {
                switch (outcome)
                {
                    case "passed":
                        sb.Append("passed the gate");
                        break;
                    case "warned":
                        sb.Append("passed the gate with warnings");
                        break;
                    case "failed":
                        sb.Append("failed the gate");
                        break;
                    case "skipped":
                        sb.Append("ran with the gate skipped");
                        break;
                    case "would_fail":
                        sb.Append("would fail the gate if run now");
                        break;
                    case "clean":
                        sb.Append("would pass the gate if run now");
                        break;
                    default:
                        sb.Append("ran (").Append(outcome).Append(")");
                        break;
                }
                sb.Append("; ");
            }

            if (delta != null)
            {
                sb.Append(delta.NewErrors).Append(" new error(s), ");
                sb.Append(delta.NewWarnings).Append(" new warning(s), ");
                sb.Append(delta.ResolvedErrors).Append(" resolved error(s), ");
                sb.Append(delta.ResolvedWarnings).Append(" resolved warning(s)");
            }
            else
            {
                sb.Append("no delta recorded");
            }

            if (totalMs >= 0)
                sb.Append("; gate took ").Append(totalMs).Append(" ms");

            if (scopePaths != null && scopePaths.Length > 0)
            {
                sb.Append("; scope: ");
                if (scopePaths.Length <= 3)
                    sb.Append(string.Join(", ", scopePaths));
                else
                    sb.Append(scopePaths.Length).Append(" paths");
            }

            return sb.ToString();
        }

        private static string BuildExplainJson(
            string toolName, string outcome, DeltaData delta,
            string[] categoriesRun, long checkpointMs, long validationMs, long totalMs,
            string[] agentNextSteps, string[] scopePaths, string narrative,
            CheckpointStoreEntry explicitCheckpoint, BridgeGateRunRecord record)
        {
            var sb = new StringBuilder(768);
            sb.Append('{');

            sb.Append("\"narrative\":\"").Append(OutputSerializer.EscapeJsonString(narrative)).Append("\",");

            sb.Append("\"summary\":{");
            sb.Append("\"tool\":").Append(toolName == null ? "null" : "\"" + OutputSerializer.EscapeJsonString(toolName) + "\"");
            sb.Append(",\"outcome\":\"").Append(OutputSerializer.EscapeJsonString(outcome ?? "")).Append("\"");
            if (delta != null)
            {
                sb.Append(",\"newErrors\":").Append(delta.NewErrors);
                sb.Append(",\"newWarnings\":").Append(delta.NewWarnings);
                sb.Append(",\"resolvedErrors\":").Append(delta.ResolvedErrors);
                sb.Append(",\"resolvedWarnings\":").Append(delta.ResolvedWarnings);
            }
            if (totalMs >= 0) sb.Append(",\"totalGateDurationMs\":").Append(totalMs);
            if (checkpointMs >= 0) sb.Append(",\"checkpointDurationMs\":").Append(checkpointMs);
            if (validationMs >= 0) sb.Append(",\"validationDurationMs\":").Append(validationMs);
            sb.Append('}');

            if (delta != null)
            {
                sb.Append(",\"delta\":{");
                sb.Append("\"newIssues\":[");
                if (delta.NewIssueKeys != null)
                {
                    for (int i = 0; i < delta.NewIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(delta.NewIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("],\"resolvedIssues\":[");
                if (delta.ResolvedIssueKeys != null)
                {
                    for (int i = 0; i < delta.ResolvedIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(delta.ResolvedIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("]}");
            }

            if (categoriesRun != null && categoriesRun.Length > 0)
            {
                sb.Append(",\"categoriesRun\":[");
                for (int i = 0; i < categoriesRun.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(categoriesRun[i])).Append('"');
                }
                sb.Append(']');
            }

            if (scopePaths != null && scopePaths.Length > 0)
            {
                sb.Append(",\"scope\":[");
                for (int i = 0; i < scopePaths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(scopePaths[i])).Append('"');
                }
                sb.Append(']');
            }

            if (agentNextSteps != null && agentNextSteps.Length > 0)
            {
                sb.Append(",\"agentNextSteps\":[");
                for (int i = 0; i < agentNextSteps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(agentNextSteps[i])).Append('"');
                }
                sb.Append(']');
            }

            if (explicitCheckpoint != null)
            {
                sb.Append(",\"checkpoint\":{");
                sb.Append("\"checkpointId\":\"").Append(OutputSerializer.EscapeJsonString(explicitCheckpoint.CheckpointId)).Append("\",");
                sb.Append("\"timestamp\":\"").Append(OutputSerializer.EscapeJsonString(explicitCheckpoint.Timestamp ?? "")).Append("\",");
                sb.Append("\"label\":").Append(explicitCheckpoint.Label == null
                    ? "null" : "\"" + OutputSerializer.EscapeJsonString(explicitCheckpoint.Label) + "\"");
                sb.Append('}');
            }

            if (record != null && !string.IsNullOrEmpty(record.MutationError))
            {
                sb.Append(",\"mutationError\":\"")
                  .Append(OutputSerializer.EscapeJsonString(record.MutationError)).Append("\"");
            }

            sb.Append(",\"heuristicNote\":\"mutation_explain projects the recorded gate run into a narrative. When the gate was skipped (gate=off) or the run predates the current editor state, the delta may be empty — pass an explicit checkpoint_id to compare against a known baseline.\"");
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // Shared internal types
        // ---------------------------------------------------------------------

        enum BudgetMode { Cache, Sample }

        struct PathImpact
        {
            public string Path;
            public bool Exists;
            public bool IsFolder;
            public string AssetKind;
            public string[] RulesForExtension;
        }

        struct RiskBand
        {
            public string Band;
            public string Confidence;
            public int Score;
        }
    }
}
