using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    /// <summary>
    /// M25 Plan 3 — explainability tests. Verifies:
    ///   - The IssueExplainability taxonomy covers every (ruleId, issueCode)
    ///     the implemented rule mappers emit (no class left without a
    ///     rootCause + remediation).
    ///   - Root-cause codes are stable (the documented machine-readable set).
    ///   - Remediation copy is clean of internal IDs (no milestone / spec /
    ///     execution-plan references leak into user-visible text).
    ///   - VerifyIssue.Evidence round-trips through the 6-arg constructor.
    ///   - FixProviderRegistry.CandidatesForIssue returns the expected fixes
    ///     with accurate Safe flags.
    /// </summary>
    [TestFixture]
    public class IssueExplainabilityTests
    {
        // The full set of (ruleId, issueCode) pairs the implemented mappers
        // emit, sourced from Editor/Rules/*/IssueMapper.cs. When a new code is
        // added to a mapper, add it here so the taxonomy-coverage gate fails
        // until IssueExplainability has an entry for it.
        private static readonly (string ruleId, string code)[] EmittedIssueCodes =
        {
            // missing_references
            ("missing_references", "missing_guid"),
            ("missing_references", "missing_fileid"),
            ("missing_references", "missing_local_fileid"),
            ("missing_references", "empty_local_ref"),
            ("missing_references", "missing_method"),
            ("missing_references", "type_mismatch"),
            ("missing_references", "missing_script"),
            ("missing_references", "duplicate_component"),
            ("missing_references", "invalid_layer"),
            // scene_prefab_health
            ("scene_prefab_health", "broken_reference"),
            ("scene_prefab_health", "high_risk_bootstrap"),
            ("scene_prefab_health", "scene_object_count"),
            ("scene_prefab_health", "component_hotspot"),
            ("scene_prefab_health", "inactive_expensive"),
            ("scene_prefab_health", "inactive_heavy"),
            ("scene_prefab_health", "deep_nesting"),
            ("scene_prefab_health", "override_explosion"),
            // dependencies
            ("dependencies", "broken_dependency"),
            ("dependencies", "dependency_cycle"),
            // project_health
            ("project_health", "orphan_meta"),
            ("project_health", "duplicate_guid"),
            ("project_health", "missing_project_setting"),
            ("project_health", "project_empty_folder"),
            ("project_health", "project_meta_only_folder"),
            ("project_health", "project_deep_nesting"),
            ("project_health", "project_large_folder"),
            ("project_health", "project_broken_asset"),
            ("project_health", "project_empty_scene"),
            // asmdef_audit
            ("asmdef_audit", "broken_asmdef_reference"),
            ("asmdef_audit", "asmdef_missing_name"),
            ("asmdef_audit", "malformed_asmdef"),
            ("asmdef_audit", "asmdef_duplicate_name"),
            ("asmdef_audit", "asmdef_circular_reference"),
            ("asmdef_audit", "asmdef_editor_in_runtime"),
            ("asmdef_audit", "asmdef_auto_referenced_orphan"),
            ("asmdef_audit", "asmdef_platform_filter_broad"),
            ("asmdef_audit", "asmdef_platform_filter_contradict"),
            ("asmdef_audit", "asmdef_version_define_invalid"),
            // materials
            ("materials", "missing_shader"),
            ("materials", "missing_texture"),
            ("materials", "builtin_shader"),
            ("materials", "builtin_texture"),
            ("materials", "render_queue_override"),
            ("materials", "unable_to_load"),
            ("materials", "duplicate_material"),
            ("materials", "unused_material"),
            ("materials", "variant_parent_invalid"),
            ("materials", "variant_deep_chain"),
            ("materials", "variant_heavy_overrides"),
            ("materials", "gpu_instancing_off"),
            ("materials", "srp_batcher_incompatible"),
            ("materials", "null_material"),
            ("materials", "null_material_slot"),
            ("materials", "builtin_material"),
            // animation_analysis
            ("animation_analysis", "missing_clip"),
            ("animation_analysis", "empty_clip"),
            ("animation_analysis", "unreachable_state"),
            ("animation_analysis", "complexity_over_threshold"),
            ("animation_analysis", "anystate_overuse"),
            ("animation_analysis", "parameter_mismatch"),
            ("animation_analysis", "expensive_curves_density"),
            ("animation_analysis", "expensive_curves_count"),
            ("animation_analysis", "duplicate_clip"),
            // shader_analysis
            ("shader_analysis", "shader_compile_error"),
            ("shader_analysis", "missing_shader_asset"),
            ("shader_analysis", "variant_explosion"),
            ("shader_analysis", "pass_count_exceeded"),
            ("shader_analysis", "fallback_shader"),
            ("shader_analysis", "expensive_feature_platform"),
            ("shader_analysis", "platform_keyword_mismatch"),
            ("shader_analysis", "duplicate_keyword_profiles"),
        };

        // The documented, stable root-cause code set (mirrors the
        // IssueExplainability class-doc taxonomy). Branching on any other
        // string is a bug.
        private static readonly HashSet<string> StableRootCauses = new HashSet<string>
        {
            "missing_guid_reference",
            "missing_fileid_reference",
            "missing_script_class",
            "missing_dependency",
            "orphaned_meta",
            "duplicate_guid",
            "structural_complexity",
            "configuration_mismatch",
            "resource_missing",
            "build_blocker",
        };

        // Forbidden tokens in user-visible remediation copy (AGENTS.md
        // §No internal references). None should ever appear in remediation.
        private static readonly string[] ForbiddenInternalTokens =
        {
            "M25", "M24", "M1", "M4", "M9", "M12", "M18", "M22",
            "execution-plan", "specs/", "backlog-", "Plan 1", "Plan 2", "Plan 3",
        };

        [Test]
        public void EveryEmittedIssueCode_HasExplainabilityEntry()
        {
            var missing = new List<string>();
            foreach (var (ruleId, code) in EmittedIssueCodes)
            {
                if (!IssueExplainability.TryGet(ruleId, code, out var entry))
                    missing.Add($"{ruleId}|{code}");
                else
                {
                    Assert.That(entry.RootCause, Is.Not.Null.And.Not.Empty,
                        $"{ruleId}|{code} rootCause must not be null/empty");
                    Assert.That(entry.Remediation, Is.Not.Null.And.Not.Empty,
                        $"{ruleId}|{code} remediation must not be null/empty");
                }
            }
            Assert.IsEmpty(missing,
                "These emitted issue codes have no IssueExplainability entry: " +
                string.Join(", ", missing));
        }

        [Test]
        public void EveryRootCause_IsInStableSet()
        {
            foreach (var (ruleId, code) in EmittedIssueCodes)
            {
                if (!IssueExplainability.TryGet(ruleId, code, out var entry)) continue;
                Assert.That(StableRootCauses, Does.Contain(entry.RootCause),
                    $"{ruleId}|{code} rootCause '{entry.RootCause}' is not in the stable taxonomy");
            }
        }

        [Test]
        public void Remediation_IsCleanOfInternalIds()
        {
            foreach (var (ruleId, code) in EmittedIssueCodes)
            {
                if (!IssueExplainability.TryGet(ruleId, code, out var entry)) continue;
                foreach (var token in ForbiddenInternalTokens)
                {
                    StringAssert.DoesNotContain(token, entry.Remediation,
                        $"{ruleId}|{code} remediation leaks internal token '{token}'");
                }
            }
        }

        [Test]
        public void VerifyIssue_Evidence_RoundTripsThroughConstructor()
        {
            var evidence = new Dictionary<string, string>
            {
                ["guid"] = "abc123",
                ["line"] = "42",
            };
            var issue = new VerifyIssue("missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid", "desc", evidence);

            Assert.IsNotNull(issue.Evidence);
            Assert.AreEqual(2, issue.Evidence.Count);
            Assert.AreEqual("abc123", issue.Evidence["guid"]);
            Assert.AreEqual("42", issue.Evidence["line"]);
        }

        [Test]
        public void VerifyIssue_FiveArgConstructor_EvidenceIsNull()
        {
            // Backwards compat: the original 5-arg constructor still works and
            // leaves Evidence null.
            var issue = new VerifyIssue("missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid", "desc");

            Assert.IsNull(issue.Evidence);
        }

        [Test]
        public void TryGet_UnknownCode_ReturnsFalse()
        {
            Assert.IsFalse(IssueExplainability.TryGet("missing_references", "totally_made_up", out var entry));
            Assert.IsNull(entry);
        }

        [Test]
        public void TryGet_NullOrEmptyArgs_ReturnsFalse()
        {
            Assert.IsFalse(IssueExplainability.TryGet(null, "missing_guid", out _));
            Assert.IsFalse(IssueExplainability.TryGet("missing_references", null, out _));
            Assert.IsFalse(IssueExplainability.TryGet("", "missing_guid", out _));
        }

        [Test]
        public void CandidatesForIssue_MissingScript_ReturnsSafeCandidate()
        {
            var candidates = FixProviderRegistry.CandidatesForIssue("missing_references", "missing_script");
            Assert.That(candidates.Length, Is.GreaterThan(0),
                "missing_script should have at least one fix candidate");
            var hasRemoveMissingScript = false;
            foreach (var c in candidates)
            {
                if (c.FixId == "remove_missing_script")
                {
                    Assert.IsTrue(c.Safe, "remove_missing_script should be safe");
                    hasRemoveMissingScript = true;
                }
            }
            Assert.IsTrue(hasRemoveMissingScript, "remove_missing_script must be a candidate");
        }

        [Test]
        public void CandidatesForIssue_RelinkeBrokenGuid_ReturnsUnsafeCandidate()
        {
            // missing_guid (missing_references) + broken_dependency (dependencies)
            // both resolve to relink_broken_guid, which is unsafe.
            var fromMissingRef = FixProviderRegistry.CandidatesForIssue("missing_references", "missing_guid");
            var fromDeps = FixProviderRegistry.CandidatesForIssue("dependencies", "broken_dependency");

            AssertRelinkUnsafe(fromMissingRef);
            AssertRelinkUnsafe(fromDeps);
        }

        private static void AssertRelinkUnsafe(FixCandidate[] candidates)
        {
            bool found = false;
            foreach (var c in candidates)
            {
                if (c.FixId == "relink_broken_guid")
                {
                    Assert.IsFalse(c.Safe, "relink_broken_guid should be unsafe");
                    found = true;
                }
            }
            Assert.IsTrue(found, "relink_broken_guid must be a candidate");
        }

        [Test]
        public void CandidatesForIssue_UnknownCode_ReturnsEmpty()
        {
            var candidates = FixProviderRegistry.CandidatesForIssue("missing_references", "totally_made_up");
            Assert.AreEqual(0, candidates.Length);
        }

        [Test]
        public void CandidatesForIssue_NullOrEmptyArgs_ReturnsEmpty()
        {
            Assert.AreEqual(0, FixProviderRegistry.CandidatesForIssue(null, "code").Length);
            Assert.AreEqual(0, FixProviderRegistry.CandidatesForIssue("missing_references", null).Length);
        }
    }
}
