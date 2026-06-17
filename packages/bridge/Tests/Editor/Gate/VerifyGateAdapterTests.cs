using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.Tests
{
    public static class VerifyGateAdapterTests
    {
        [Test]
        public static void ComputeDelta_NoChange_ZeroDeltas()
        {
            var before = new CheckpointFingerprint("cp_test1", new Dictionary<string, RuleFingerprint>
            {
                {
                    "missing_references", new RuleFingerprint(0, 0,
                        new HashSet<string>())
                }
            });

            var after = new VerifyResult(
                new List<VerifyIssue>(),
                new[] { "missing_references" },
                10);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);
            Assert.AreEqual(0, delta.NewErrors);
            Assert.AreEqual(0, delta.NewWarnings);
            Assert.AreEqual(0, delta.ResolvedErrors);
            Assert.AreEqual(0, delta.ResolvedWarnings);
            Assert.AreEqual(0, delta.NewIssueKeys.Length);
            Assert.AreEqual(0, delta.ResolvedIssueKeys.Length);
        }

        [Test]
        public static void ComputeDelta_NewErrors_Detected()
        {
            var before = new CheckpointFingerprint("cp_test2", new Dictionary<string, RuleFingerprint>
            {
                {
                    "missing_references", new RuleFingerprint(0, 0,
                        new HashSet<string>())
                }
            });

            var after = new VerifyResult(
                new List<VerifyIssue>
                {
                    new VerifyIssue("missing_references", VerifySeverity.Error,
                        "Assets/Test.prefab", "MISSING_SCRIPT",
                        "Missing script on 'Root'")
                },
                new[] { "missing_references" },
                10);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);
            Assert.AreEqual(1, delta.NewErrors);
            Assert.AreEqual(0, delta.NewWarnings);
            Assert.AreEqual(1, delta.NewIssueKeys.Length);
        }

        [Test]
        public static void ComputeDelta_ResolvedErrors_Detected()
        {
            var before = new CheckpointFingerprint("cp_test3", new Dictionary<string, RuleFingerprint>
            {
                {
                    "missing_references", new RuleFingerprint(1, 0,
                        new HashSet<string>
                        {
                            "missing_references|ERROR|Assets/Broken.prefab|MISSING_SCRIPT"
                        })
                }
            });

            var after = new VerifyResult(
                new List<VerifyIssue>(),
                new[] { "missing_references" },
                10);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);
            Assert.AreEqual(0, delta.NewErrors);
            Assert.AreEqual(1, delta.ResolvedErrors);
            Assert.AreEqual(1, delta.ResolvedIssueKeys.Length);
        }

        [Test]
        public static void ComputeDelta_WarningsOnly_NoNewErrors()
        {
            var before = new CheckpointFingerprint("cp_test4", new Dictionary<string, RuleFingerprint>
            {
                {
                    "missing_references", new RuleFingerprint(0, 0,
                        new HashSet<string>())
                }
            });

            var after = new VerifyResult(
                new List<VerifyIssue>
                {
                    new VerifyIssue("missing_references", VerifySeverity.Warning,
                        "Assets/Test.prefab", "WEAK_REF",
                        "Weak reference detected")
                },
                new[] { "missing_references" },
                10);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);
            Assert.AreEqual(0, delta.NewErrors);
            Assert.AreEqual(1, delta.NewWarnings);
        }

        [Test]
        public static void ComputeDelta_MixedChanges()
        {
            var before = new CheckpointFingerprint("cp_test5", new Dictionary<string, RuleFingerprint>
            {
                {
                    "missing_references", new RuleFingerprint(1, 1,
                        new HashSet<string>
                        {
                            "missing_references|ERROR|Assets/Old.prefab|MISSING_SCRIPT",
                            "missing_references|WARN|Assets/Old.prefab|WEAK_REF"
                        })
                }
            });

            var after = new VerifyResult(
                new List<VerifyIssue>
                {
                    new VerifyIssue("missing_references", VerifySeverity.Error,
                        "Assets/New.prefab", "MISSING_SCRIPT",
                        "Missing script on 'Child'"),
                    new VerifyIssue("missing_references", VerifySeverity.Warning,
                        "Assets/New.prefab", "MISSING_REF",
                        "Missing ref on property")
                },
                new[] { "missing_references" },
                10);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);
            Assert.AreEqual(1, delta.NewErrors);
            Assert.AreEqual(1, delta.NewWarnings);
            Assert.AreEqual(1, delta.ResolvedErrors);
            Assert.AreEqual(1, delta.ResolvedWarnings);
        }

        // -------------------------------------------------------------------
        // SelectRuleIds — extension → rule-set routing
        // -------------------------------------------------------------------

        [Test]
        public static void SelectRuleIds_NullPaths_ReturnsFallback()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(null);
            CollectionAssert.AreEquivalent(new[] { "missing_references", "dependencies" }, ids);
        }

        [Test]
        public static void SelectRuleIds_EmptyPaths_ReturnsFallback()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new string[0]);
            CollectionAssert.AreEquivalent(new[] { "missing_references", "dependencies" }, ids);
        }

        [TestCase("Assets/P.prefab", new[] { "missing_references", "scene_prefab_health", "dependencies" })]
        [TestCase("Assets/S.unity", new[] { "missing_references", "scene_prefab_health", "dependencies" })]
        [TestCase("Assets/S.cs", new[] { "missing_references", "asmdef_audit" })]
        [TestCase("Assets/A.asmdef", new[] { "missing_references", "asmdef_audit" })]
        [TestCase("Assets/M.mat", new[] { "missing_references", "dependencies", "materials", "shader_analysis" })]
        [TestCase("Assets/Sh.shader", new[] { "missing_references", "dependencies", "materials", "shader_analysis" })]
        [TestCase("Assets/G.shadergraph", new[] { "missing_references", "dependencies", "materials", "shader_analysis" })]
        [TestCase("Assets/T.png", new[] { "textures", "sprite_2d_analysis" })]
        [TestCase("Assets/T.jpg", new[] { "textures", "sprite_2d_analysis" })]
        [TestCase("Assets/Ac.controller", new[] { "animation_analysis", "missing_references", "dependencies" })]
        [TestCase("Assets/An.anim", new[] { "animation_analysis", "missing_references", "dependencies" })]
        [TestCase("Assets/So.asset", new[] { "missing_references", "dependencies" })]
        [TestCase("Assets/S.wav", new[] { "audio_analysis" })]
        [TestCase("Assets/S.mp3", new[] { "audio_analysis" })]
        public static void SelectRuleIds_KnownExtension_RoutesToExpectedRules(string path, string[] expected)
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[] { path });
            CollectionAssert.AreEquivalent(expected, ids);
        }

        [Test]
        public static void SelectRuleIds_UnknownExtension_ReturnsFallback()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[] { "Assets/readme.txt" });
            CollectionAssert.AreEquivalent(new[] { "missing_references", "dependencies" }, ids);
        }

        [Test]
        public static void SelectRuleIds_ExtensionlessPath_ReturnsFallback()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[] { "Assets/SomeFolder" });
            CollectionAssert.AreEquivalent(new[] { "missing_references", "dependencies" }, ids);
        }

        [Test]
        public static void SelectRuleIds_MultiplePaths_UnionsRuleSets()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[]
            {
                "Assets/P.prefab",   // missing_references + scene_prefab_health + dependencies
                "Assets/T.png",      // textures + sprite_2d_analysis
            });

            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health", "dependencies", "textures", "sprite_2d_analysis" },
                ids);
        }

        [Test]
        public static void SelectRuleIds_MixedKnownAndUnknown_PreservesKnownRules()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[]
            {
                "Assets/P.prefab",       // known
                "Assets/unknown.xyz",    // unknown -> would normally fall back, but a known path exists
            });

            CollectionAssert.AreEquivalent(new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        [Test]
        public static void SelectRuleIds_IsCaseInsensitive_OnExtension()
        {
            var ids = VerifyGateAdapter.SelectRuleIds(new[] { "Assets/P.PREFAB" });
            CollectionAssert.AreEquivalent(new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        // -------------------------------------------------------------------
        // ResolveRuleIds — include / exclude filter composition (T2.6)
        // -------------------------------------------------------------------

        [Test]
        public static void ResolveRuleIds_NoFilters_MatchesSelectRuleIds()
        {
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" }, null, null, null);
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_NullFilters_NoOp()
        {
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                new[] { "missing_references", "scene_prefab_health", "dependencies" },
                null, null);
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_EmptyFilters_NoOp()
        {
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                null,
                new string[0],
                new string[0]);
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_IncludeWithoutExplicit_IsAdditive()
        {
            // include_rules is additive when no explicit categories list — the
            // union of the auto-selected set and the include list.
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                null,
                new[] { "textures", "audio_analysis" },
                null);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "missing_references", "scene_prefab_health", "dependencies",
                    "textures", "audio_analysis"
                }, ids);
        }

        [Test]
        public static void ResolveRuleIds_IncludeWithoutExplicit_CanAddToAndNarrowWithAutoSelect()
        {
            // When the include list is a subset of auto-select, the union is
            // unchanged (auto-select already covers it).
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                null,
                new[] { "missing_references" },
                null);
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health", "dependencies" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_IncludeWithExplicit_NarrowsToIntersection()
        {
            // Explicit categories + include -> intersection only.
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                new[] { "missing_references", "scene_prefab_health", "dependencies" },
                new[] { "missing_references", "scene_prefab_health" },
                null);
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_ExcludeWins_OverAutoSelect()
        {
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                null,
                null,
                new[] { "scene_prefab_health" });
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "dependencies" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_ExcludeWins_OverExplicitAndInclude()
        {
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                new[] { "missing_references", "scene_prefab_health", "dependencies" },
                new[] { "missing_references", "scene_prefab_health", "dependencies" },
                new[] { "missing_references", "dependencies" });
            CollectionAssert.AreEquivalent(new[] { "scene_prefab_health" }, ids);
        }

        [Test]
        public static void ResolveRuleIds_EmptyAfterFilter_ReturnsNullSentinel()
        {
            // Excluding everything must return null, NOT an empty array. The
            // tools check this sentinel to avoid falling into VerifyRunner's
            // "null ruleIds = run all" branch.
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                null,
                null,
                new[] { "missing_references", "scene_prefab_health", "dependencies" });
            Assert.IsNull(ids);
        }

        [Test]
        public static void ResolveRuleIds_IncludeNarrowingEverything_ReturnsNullSentinel()
        {
            // Explicit list + include with no overlap -> empty -> null.
            var ids = VerifyGateAdapter.ResolveRuleIds(
                new[] { "Assets/P.prefab" },
                new[] { "missing_references" },
                new[] { "textures" },
                null);
            Assert.IsNull(ids);
        }
    }
}
