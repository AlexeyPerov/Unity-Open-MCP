using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class GateIntelligenceToolsTests
    {
        // -------------------------------------------------------------------
        // impact_preview
        // -------------------------------------------------------------------

        [Test]
        public void ImpactPreview_MissingPathsHint_ReturnsMissingParameter()
        {
            var result = GateIntelligenceTools.ImpactPreview("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'paths_hint'", result.ErrorMessage);
        }

        [Test]
        public void ImpactPreview_EmptyPathsHint_ReturnsMissingParameter()
        {
            var result = GateIntelligenceTools.ImpactPreview("{\"paths_hint\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void ImpactPreview_PrefabScope_ReportsRulesAndRiskBand()
        {
            // The bridge demo project always has some Assets/ folder. Use a
            // non-existent .prefab path — the tool must still classify it by
            // extension and project the rules that would apply.
            var result = GateIntelligenceTools.ImpactPreview(
                "{\"paths_hint\":[\"Assets/__ImpactPreviewProbe.prefab\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);

            var json = result.Output;
            StringAssert.Contains("\"scope\":", json);
            StringAssert.Contains("\"pathsHintCount\":1", json);
            StringAssert.Contains("\"rulesProjected\":[", json);
            // .prefab auto-selects missing_references + dependencies + scene_prefab_health.
            StringAssert.Contains("\"missing_references\"", json);
            StringAssert.Contains("\"dependencies\"", json);
            StringAssert.Contains("\"risk\":{", json);
            StringAssert.Contains("\"band\":\"", json);
            StringAssert.Contains("\"confidence\":\"", json);
            StringAssert.Contains("\"perPath\":[", json);
            StringAssert.Contains("\"assetKind\":\"prefab\"", json);
            StringAssert.Contains("\"rulesForExtension\":[", json);
            // Missing-path confidence is downgraded.
            StringAssert.Contains("\"confidence\":\"low\"", json);
            // Heuristic boundary is stated.
            StringAssert.Contains("\"heuristicNote\":", json);
        }

        [Test]
        public void ImpactPreview_ExcludeRules_FiltersRulesProjected()
        {
            var result = GateIntelligenceTools.ImpactPreview(
                "{\"paths_hint\":[\"Assets/__ImpactPreviewProbe.prefab\"]," +
                "\"exclude_rules\":[\"missing_references\",\"dependencies\",\"scene_prefab_health\"]}");
            Assert.IsTrue(result.Success);
            // Filters narrow to nothing → empty rulesProjected array.
            StringAssert.Contains("\"rulesProjected\":[]", result.Output);
        }

        [Test]
        public void ImpactPreview_ProjectSettingsScope_ClassifiedByExtension()
        {
            // ProjectSettings/*.asset is a valid gate scope even though
            // AssetDatabase does not index it. The preview must classify it by
            // extension regardless of whether the file currently exists.
            var result = GateIntelligenceTools.ImpactPreview(
                "{\"paths_hint\":[\"ProjectSettings/ProjectSettings.asset\"]}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"assetKind\":\"scriptable_object\"", result.Output);
        }

        // -------------------------------------------------------------------
        // gate_budget_estimate
        // -------------------------------------------------------------------

        [Test]
        public void GateBudgetEstimate_MissingPathsHint_ReturnsMissingParameter()
        {
            var result = GateIntelligenceTools.GateBudgetEstimate("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'paths_hint'", result.ErrorMessage);
        }

        [Test]
        public void GateBudgetEstimate_HeuristicMode_LowConfidence()
        {
            // cache mode with no recent scan → heuristic basis, low confidence.
            // Use a scope that does not match a recent cache entry so the
            // cache snapshot (if any) does not inflate confidence.
            var result = GateIntelligenceTools.GateBudgetEstimate(
                "{\"paths_hint\":[\"Assets/__BudgetProbe.prefab\"],\"mode\":\"cache\"}");
            // Either heuristic (no cache) or verify_cache (cache present); both
            // are valid outcomes. Assert the common shape, not the basis.
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var json = result.Output;
            StringAssert.Contains("\"scope\":", json);
            StringAssert.Contains("\"estimatedAssetCount\":", json);
            StringAssert.Contains("\"rulesProjected\":[", json);
            StringAssert.Contains("\"estimate\":{", json);
            StringAssert.Contains("\"basis\":", json);
            StringAssert.Contains("\"confidence\":", json);
            StringAssert.Contains("\"estimatedDurationMs\":", json);
            StringAssert.Contains("\"estimatedIssueBudget\":", json);
            StringAssert.Contains("\"heuristicNote\":", json);
        }

        [Test]
        public void GateBudgetEstimate_SampleMode_HighConfidence()
        {
            var result = GateIntelligenceTools.GateBudgetEstimate(
                "{\"paths_hint\":[\"Assets/__BudgetProbe.prefab\"],\"mode\":\"sample\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Sample mode runs a real checkpoint scan → high confidence.
            StringAssert.Contains("\"basis\":\"sample_checkpoint\"", result.Output);
            StringAssert.Contains("\"confidence\":\"high\"", result.Output);
            // Duration must be a non-negative number.
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                result.Output, "\"estimatedDurationMs\":[0-9]+"),
                "Expected a non-negative duration: " + result.Output);
        }

        [Test]
        public void GateBudgetEstimate_ExcludeRules_StillReturnsShape()
        {
            var result = GateIntelligenceTools.GateBudgetEstimate(
                "{\"paths_hint\":[\"Assets/__BudgetProbe.prefab\"]," +
                "\"exclude_rules\":[\"missing_references\",\"dependencies\",\"scene_prefab_health\"]}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"rulesProjected\":[]", result.Output);
        }

        // -------------------------------------------------------------------
        // mutation_explain
        // -------------------------------------------------------------------

        [Test]
        public void MutationExplain_NoContext_ReturnsNoMutationContext()
        {
            // Clear history + checkpoint store so neither source is available.
            BridgeGateRunHistory.Clear();
            CheckpointStore.Clear();

            var result = GateIntelligenceTools.MutationExplain("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("no_mutation_context", result.ErrorCode);
        }

        [Test]
        public void MutationExplain_UnknownCheckpoint_ReturnsUnavailableWarning()
        {
            // Item F — a missing checkpoint is not a tool failure (checkpoints are
            // session-scoped and cleared on recompile/reload). The call must
            // succeed with an explicit `unavailable` flag and recovery guidance
            // so it does not block agent workflows (isError stays false).
            var result = GateIntelligenceTools.MutationExplain(
                "{\"checkpoint_id\":\"cp_doesnotexist\"}");
            Assert.IsTrue(result.Success, "missing checkpoint must not fail the tool call");
            Assert.IsNull(result.ErrorCode);
            StringAssert.Contains("\"unavailable\":true", result.Output);
            StringAssert.Contains("\"outcome\":\"unavailable\"", result.Output);
            StringAssert.Contains("\"agentNextSteps\":", result.Output);
        }

        [Test]
        public void MutationExplain_CheckpointCompare_ReturnsNarrativeAndSummary()
        {
            // Take a real checkpoint over the demo project Assets/ folder, then
            // explain it. The compare path recomputes a fresh delta against
            // current state and must surface the full narrative + summary shape.
            BridgeGateRunHistory.Clear();
            var cp = CheckpointCreateTool.Execute(
                "{\"paths\":[\"Assets\"],\"label\":\"explain-probe\"}");
            Assert.IsTrue(cp.Success, "checkpoint_create must succeed: " + cp.ErrorMessage);
            var cpJson = cp.Output;
            var idStart = cpJson.IndexOf("\"checkpointId\":\"") +
                          "\"checkpointId\":\"".Length;
            var idEnd = cpJson.IndexOf('"', idStart);
            var checkpointId = cpJson.Substring(idStart, idEnd - idStart);

            try
            {
                var result = GateIntelligenceTools.MutationExplain(
                    "{\"checkpoint_id\":\"" + checkpointId + "\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);

                var json = result.Output;
                StringAssert.Contains("\"narrative\":\"", json);
                StringAssert.Contains("\"summary\":{", json);
                StringAssert.Contains("\"outcome\":\"", json);
                StringAssert.Contains("\"newErrors\":", json);
                StringAssert.Contains("\"newWarnings\":", json);
                StringAssert.Contains("\"resolvedErrors\":", json);
                StringAssert.Contains("\"resolvedWarnings\":", json);
                StringAssert.Contains("\"delta\":{", json);
                StringAssert.Contains("\"checkpoint\":{", json);
                StringAssert.Contains("\"checkpointId\":\"" + checkpointId + "\"", json);
                // checkpoint-compare path uses a synthetic tool name + outcome.
                StringAssert.Contains("\"tool\":\"checkpoint_compare\"", json);
                StringAssert.Contains("\"heuristicNote\":", json);
            }
            finally
            {
                CheckpointStore.Clear();
            }
        }
    }
}
