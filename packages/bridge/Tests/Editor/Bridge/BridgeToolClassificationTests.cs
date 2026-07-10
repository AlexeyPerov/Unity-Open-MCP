using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Guards the gate-routing classification sets in BridgeToolClassification.
    // These sets drive HTTP dispatch and whether the gate envelope runs:
    //
    //   - MutatingTools       → run the full gate path; paths_hint required.
    //   - DirectResponseTools → gate-free; return flat JSON directly.
    //   - KnownTools          → every compiled-in tool name (superset).
    //
    // A wrong entry here is a silent security/dispatch regression — a mutating
    // tool that lands in DirectResponseTools skips the gate entirely; a read-only
    // tool that lands in MutatingTools forces a needless gate cycle and rejects
    // calls with empty paths_hint. These tests encode the intended boundary so
    // adding a tool to the wrong set fails loudly.
    public static class BridgeToolClassificationTests
    {
        // -------------------------------------------------------------------------
        // Set-containment invariants (the structural contract)
        // -------------------------------------------------------------------------

        [Test]
        public static void DirectResponseTools_AreSubsetOfKnownTools()
        {
            // With one documented exception: unity_open_mcp_dependencies is
            // registry-discovered and intentionally NOT in KnownTools, but stays
            // in DirectResponseTools so it returns flat JSON (no gate envelope).
            foreach (var tool in BridgeToolClassification.DirectResponseTools)
            {
                if (tool == "unity_open_mcp_dependencies") continue;
                Assert.IsTrue(
                    BridgeToolClassification.KnownTools.Contains(tool),
                    $"DirectResponseTools entry '{tool}' must also be in KnownTools " +
                    "(only unity_open_mcp_dependencies is exempt — registry-discovered).");
            }
        }

        [Test]
        public static void MutatingTools_AreSubsetOfKnownTools()
        {
            foreach (var tool in BridgeToolClassification.MutatingTools)
            {
                Assert.IsTrue(
                    BridgeToolClassification.KnownTools.Contains(tool),
                    $"MutatingTools entry '{tool}' must also be in KnownTools " +
                    "(every mutating tool is a known compiled-in tool).");
            }
        }

        [Test]
        public static void DirectResponseTools_And_MutatingTools_AreDisjoint()
        {
            // The core routing contract: a tool is EITHER gate-free (direct) OR
            // gate-routed (mutating), never both. An overlap would be ambiguous
            // dispatch — the gate path and the direct path would both claim it.
            var intersection = BridgeToolClassification.DirectResponseTools
                .Intersect(BridgeToolClassification.MutatingTools)
                .ToArray();
            Assert.IsEmpty(
                intersection,
                "DirectResponseTools and MutatingTools must be disjoint. " +
                $"Overlap: {string.Join(", ", intersection)}");
        }

        // -------------------------------------------------------------------------
        // Specific high-impact tools — pin the classification of the tools where
        // a misclassification is most dangerous.
        // -------------------------------------------------------------------------

        [Test]
        public static void PowerTools_AreMutating_NeverDirectResponse()
        {
            // execute_csharp / invoke_method / execute_menu / batch_execute are
            // the most powerful tools — they must route through the gate (and
            // through the deny list). A regression that moved them to
            // DirectResponseTools would bypass asset-reference validation.
            var powerTools = new[]
            {
                "unity_open_mcp_execute_csharp",
                "unity_open_mcp_invoke_method",
                "unity_open_mcp_execute_menu",
                "unity_open_mcp_batch_execute",
            };
            foreach (var t in powerTools)
            {
                Assert.IsTrue(
                    BridgeToolClassification.MutatingTools.Contains(t),
                    $"{t} must be in MutatingTools (gate-routed).");
                Assert.IsFalse(
                    BridgeToolClassification.DirectResponseTools.Contains(t),
                    $"{t} must NOT be in DirectResponseTools (would skip the gate).");
            }
        }

        [Test]
        public static void ApplyFix_And_Reserialize_AreMutating()
        {
            // apply_fix + reserialize both mutate assets and must run the gate.
            foreach (var t in new[] { "unity_open_mcp_apply_fix", "unity_open_mcp_reserialize" })
            {
                Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains(t),
                    $"{t} must be in MutatingTools.");
            }
        }

        [Test]
        public static void ReadOnlyTools_AreDirectResponse_NeverMutating()
        {
            // Representative read-only tools that must skip the gate.
            var readOnly = new[]
            {
                "unity_open_mcp_validate_edit",
                "unity_open_mcp_find_references",
                "unity_open_mcp_scan_paths",
                "unity_open_mcp_read_asset",
                "unity_open_mcp_search_assets",
                "unity_open_mcp_dependencies",
                "unity_open_mcp_gameobject_find",
                "unity_open_mcp_component_get",
                "unity_open_mcp_scene_get_data",
                "unity_open_mcp_editor_get_tags",
                "unity_open_mcp_editor_get_layers",
                "unity_open_mcp_type_schema",
                "unity_open_mcp_object_get_data",
                "unity_open_mcp_impact_preview",
                "unity_open_mcp_gate_budget_estimate",
                "unity_open_mcp_mutation_explain",
                "unity_open_mcp_build_get_targets",
                "unity_open_mcp_list_assets_of_type",
                "unity_open_mcp_asmdef_list",
                "unity_open_mcp_asmdef_get",
                "unity_open_mcp_settings_get_time",
                "unity_open_mcp_settings_get_render_pipeline",
            };
            foreach (var t in readOnly)
            {
                // unity_open_mcp_dependencies is the one DirectResponseTools
                // entry that is NOT in KnownTools (registry-discovered) —
                // assert DirectResponse membership but skip the KnownTools check
                // for it.
                if (t != "unity_open_mcp_dependencies")
                {
                    Assert.IsTrue(BridgeToolClassification.KnownTools.Contains(t),
                        $"{t} must be in KnownTools.");
                }
                Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains(t),
                    $"{t} must be in DirectResponseTools (gate-free read).");
                Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains(t),
                    $"{t} must NOT be in MutatingTools (read-only).");
            }
        }

        // -------------------------------------------------------------------------
        // The classification is exhaustive: every KnownTool is either direct,
        // mutating, or a documented gate-path non-mutating exception. This
        // catches a NEW tool added to KnownTools but forgotten in the routing
        // sets (which would silently fall through the dispatcher).
        // -------------------------------------------------------------------------

        // find_members is read-only but NOT in DirectResponseTools — it routes
        // through DispatchWithGateCore with isMutating=false (a gate-path non-
        // mutating tool). This is the only such exception; pinning it here means
        // a second one appearing is a deliberate decision, not an oversight.
        private static readonly System.Collections.Generic.HashSet<string>
            GatePathNonMutatingExceptions = new()
            {
                "unity_open_mcp_find_members",
            };

        [Test]
        public static void EveryKnownTool_IsDirect_Mutating_OrDocumentedException()
        {
            var routed = new System.Collections.Generic.HashSet<string>(
                BridgeToolClassification.DirectResponseTools
                    .Concat(BridgeToolClassification.MutatingTools));
            foreach (var tool in BridgeToolClassification.KnownTools)
            {
                if (GatePathNonMutatingExceptions.Contains(tool)) continue;
                Assert.IsTrue(
                    routed.Contains(tool),
                    $"KnownTools entry '{tool}' is in neither DirectResponseTools, " +
                    "MutatingTools, nor the documented gate-path-non-mutating " +
                    "exception list — every known tool must have an explicit " +
                    "routing classification.");
            }
        }

        [Test]
        public static void GatePathNonMutatingExceptions_AreActuallyUnrouted()
        {
            // The exception list is meaningless if its entries are ALSO in a
            // routing set. Confirm each exception is genuinely in neither.
            foreach (var tool in GatePathNonMutatingExceptions)
            {
                Assert.IsTrue(
                    BridgeToolClassification.KnownTools.Contains(tool),
                    $"{tool} is listed as a gate-path exception but is not in KnownTools.");
                Assert.IsFalse(
                    BridgeToolClassification.DirectResponseTools.Contains(tool),
                    $"{tool} is listed as a gate-path exception but IS in DirectResponseTools.");
                Assert.IsFalse(
                    BridgeToolClassification.MutatingTools.Contains(tool),
                    $"{tool} is listed as a gate-path exception but IS in MutatingTools.");
            }
        }

        // -------------------------------------------------------------------------
        // Editor-state mutators that write no assets (gate-free by design).
        // These are subtle: they ARE mutating in a colloquial sense (they change
        // editor state) but route as DirectResponse because the gate validates
        // asset-reference fallout, which doesn't apply. Pinning them prevents a
        // well-meaning refactor from "fixing" them into MutatingTools.
        // -------------------------------------------------------------------------

        [Test]
        public static void EditorStateMutators_AreDirectResponse()
        {
            var editorStateMutators = new[]
            {
                "unity_open_mcp_console_clear",
                "unity_open_mcp_console_log",
                "unity_open_mcp_editor_set_state",
                "unity_open_mcp_selection_set",
                "unity_open_mcp_editor_undo",
                "unity_open_mcp_editor_redo",
                "unity_open_mcp_profiler_start",
                "unity_open_mcp_profiler_stop",
                "unity_open_mcp_profiler_set_config",
                "unity_open_mcp_playerprefs_set",
                "unity_open_mcp_playerprefs_delete",
                "unity_open_mcp_editorprefs_set",
                "unity_open_mcp_editorprefs_delete",
            };
            foreach (var t in editorStateMutators)
            {
                Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains(t),
                    $"{t} mutates editor state but writes no assets — must be gate-free.");
                Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains(t),
                    $"{t} must NOT be in MutatingTools (no asset write to validate).");
            }
        }

        [Test]
        public static void TagManagerMutators_AreMutating()
        {
            // editor_add_tag / editor_add_layer rewrite ProjectSettings/TagManager.asset
            // and MUST run the gate (unlike the other Plan 5 editor-state tools).
            foreach (var t in new[] { "unity_open_mcp_editor_add_tag", "unity_open_mcp_editor_add_layer" })
            {
                Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains(t),
                    $"{t} writes TagManager.asset and must be in MutatingTools.");
                Assert.IsFalse(BridgeToolClassification.DirectResponseTools.Contains(t),
                    $"{t} must NOT be in DirectResponseTools.");
            }
        }
    }
}
