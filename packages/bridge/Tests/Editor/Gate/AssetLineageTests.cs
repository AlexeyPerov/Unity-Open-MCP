using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.Tests
{
    // A gated call that makes an asset from another (copy, move, save-as)
    // reports the destination's issues the source already had as inherited,
    // not new; a move does not read as "resolved at the old path + new at the
    // new path"; an issue only the destination has still counts and fails.
    [TestFixture]
    public class AssetLineageTests
    {
        private static string Key(VerifySeverity severity, string path, string code, string rule = "missing_references")
            => IssueKey.Build(rule, severity, path, code);

        private static VerifyIssue Issue(VerifySeverity severity, string path, string code, string rule = "missing_references")
            => new VerifyIssue(rule, severity, path, code, "test");

        private static CheckpointFingerprint Before(params string[] keys)
        {
            var errors = keys.Count(k => k.Split('|')[1] == "ERROR");
            return new CheckpointFingerprint("cp_lineage", new Dictionary<string, RuleFingerprint>
            {
                ["missing_references"] = new RuleFingerprint(errors, keys.Length - errors, new HashSet<string>(keys))
            });
        }

        private static VerifyResult After(params VerifyIssue[] issues)
            => new VerifyResult(issues.ToList(), new[] { "missing_references" }, 0);

        // ------------------------------------------------------------------
        // Key mapping
        // ------------------------------------------------------------------

        [Test]
        public void Inherits_PrefabCopy_MapsPathAndRootName()
        {
            var d = new AssetDerivation("Assets/A/Src.prefab", "Assets/B/Dst.prefab", AssetDerivation.Copy, new[]
            {
                Key(VerifySeverity.Warning, "Assets/A/Src.prefab", "empty_local_ref:Src/Child:target"),
                Key(VerifySeverity.Error, "Assets/A/Src.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed"),
                Key(VerifySeverity.Warning, "Assets/A/Src.prefab", "duplicate_component:BoxCollider:Src"),
            });
            Assert.IsTrue(d.Inherits(Key(VerifySeverity.Warning, "Assets/B/Dst.prefab", "empty_local_ref:Dst/Child:target")));
            Assert.IsTrue(d.Inherits(Key(VerifySeverity.Error, "Assets/B/Dst.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed")));
            Assert.IsTrue(d.Inherits(Key(VerifySeverity.Warning, "Assets/B/Dst.prefab", "duplicate_component:BoxCollider:Dst")));
        }

        [Test]
        public void Inherits_RejectsADifferentCodeSeverityRuleOrObject()
        {
            var d = new AssetDerivation("Assets/Src.prefab", "Assets/Dst.prefab", AssetDerivation.Copy, new[]
            {
                Key(VerifySeverity.Warning, "Assets/Src.prefab", "empty_local_ref:Src/Child:target"),
            });
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Warning, "Assets/Dst.prefab", "empty_local_ref:Dst/Child:other")));
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Error, "Assets/Dst.prefab", "empty_local_ref:Dst/Child:target")));
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Warning, "Assets/Dst.prefab", "empty_local_ref:Dst/Child:target", "dependencies")));
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Warning, "Assets/Dst.prefab", "empty_local_ref:Dst/Other:target")));
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Warning, "Assets/Elsewhere.prefab", "empty_local_ref:Src/Child:target")),
                "a key outside the destination is not the derivation's");
        }

        [Test]
        public void Inherits_FolderCopy_MapsThePathPrefixOnly()
        {
            var d = new AssetDerivation("Assets/Fx", "Assets/FxCopy/", AssetDerivation.Copy, new[]
            {
                Key(VerifySeverity.Error, "Assets/Fx/Sub/Boom.prefab", "missing_guid:fedcba9876543210fedcba9876543210"),
                Key(VerifySeverity.Warning, "Assets/Fx/Sub/Boom.prefab", "empty_local_ref:Boom/Fx:emitter"),
            });
            Assert.AreEqual("Assets/FxCopy", d.Destination, "paths are normalized");
            Assert.IsTrue(d.Inherits(Key(VerifySeverity.Error, "Assets/FxCopy/Sub/Boom.prefab", "missing_guid:fedcba9876543210fedcba9876543210")));
            Assert.IsTrue(d.Inherits(Key(VerifySeverity.Warning, "Assets/FxCopy/Sub/Boom.prefab", "empty_local_ref:Boom/Fx:emitter")));
            Assert.IsFalse(d.Inherits(Key(VerifySeverity.Error, "Assets/FxCopyOther/Sub/Boom.prefab", "missing_guid:fedcba9876543210fedcba9876543210")),
                "a sibling path that only shares the prefix is not under the destination");
        }

        [Test]
        public void RenameRootSegment_TouchesOnlyTheFirstObjectPathSegment()
        {
            Assert.AreEqual("empty_local_ref:Src/Dst:m_Mesh",
                AssetDerivation.RenameRootSegment("empty_local_ref:Dst/Dst:m_Mesh", "Dst", "Src"));
            Assert.AreEqual("empty_local_ref:Src:m_Mesh#1",
                AssetDerivation.RenameRootSegment("empty_local_ref:Dst:m_Mesh#1", "Dst", "Src"));
            Assert.AreEqual("empty_local_ref:DstX/Child:target",
                AssetDerivation.RenameRootSegment("empty_local_ref:DstX/Child:target", "Dst", "Src"));
        }

        [Test]
        public void SourceScope_MapsTheScopeEntriesThatReachTheDestination()
        {
            CollectionAssert.AreEqual(new[] { "Assets/Src.prefab" },
                AssetLineage.SourceScope(new[] { "Assets/Dst.prefab", "Assets/Other.prefab" }, "Assets/Src.prefab", "Assets/Dst.prefab"));
            CollectionAssert.AreEqual(new[] { "Assets/Src.prefab" },
                AssetLineage.SourceScope(new[] { "Assets" }, "Assets/Src.prefab", "Assets/Dst.prefab"),
                "a parent folder of the destination covers the whole source");
            CollectionAssert.AreEqual(new[] { "Assets/Fx/a.prefab" },
                AssetLineage.SourceScope(new[] { "Assets/FxCopy/a.prefab" }, "Assets/Fx", "Assets/FxCopy"));
            CollectionAssert.IsEmpty(
                AssetLineage.SourceScope(new[] { "Assets/Unrelated.prefab" }, "Assets/Src.prefab", "Assets/Dst.prefab"));
        }

        // ------------------------------------------------------------------
        // Delta
        // ------------------------------------------------------------------

        [Test]
        public void ComputeDelta_Copy_InheritedIssuesAreNotNew_AGenuineNewOneIs()
        {
            var lineage = new[]
            {
                new AssetDerivation("Assets/Src.prefab", "Assets/Dst.prefab", AssetDerivation.Copy, new[]
                {
                    Key(VerifySeverity.Error, "Assets/Src.prefab", "missing_guid:fedcba9876543210fedcba9876543210"),
                    Key(VerifySeverity.Warning, "Assets/Src.prefab", "empty_local_ref:Src/Child:target"),
                })
            };
            var after = After(
                Issue(VerifySeverity.Error, "Assets/Dst.prefab", "missing_guid:fedcba9876543210fedcba9876543210"),
                Issue(VerifySeverity.Warning, "Assets/Dst.prefab", "empty_local_ref:Dst/Child:target"),
                // The copy was then broken at the destination.
                Issue(VerifySeverity.Error, "Assets/Dst.prefab", "missing_guid:abcdefabcdefabcdefabcdefabcdefab"));

            var delta = VerifyGateAdapter.ComputeDelta(Before(), after, lineage);

            Assert.AreEqual(1, delta.NewErrors);
            Assert.AreEqual(0, delta.NewWarnings);
            CollectionAssert.AreEqual(new[] { Key(VerifySeverity.Error, "Assets/Dst.prefab", "missing_guid:abcdefabcdefabcdefabcdefabcdefab") },
                delta.NewIssueKeys);
            Assert.AreEqual(1, delta.InheritedErrors);
            Assert.AreEqual(1, delta.InheritedWarnings);
            Assert.AreEqual(2, delta.InheritedIssueKeys.Length);
            Assert.AreEqual(1, delta.Derivations.Length);
            Assert.AreEqual("Assets/Src.prefab", delta.Derivations[0].Source);
            Assert.AreEqual(1, delta.Derivations[0].InheritedErrors);
            Assert.AreEqual(GateOutcome.Failed, GatePolicy.ResolveOutcome(GateMode.Enforce, delta).outcome);
        }

        [Test]
        public void ComputeDelta_Move_NetsToZero_ButARealResolutionStillCounts()
        {
            var lineage = new[]
            {
                new AssetDerivation("Assets/Old.prefab", "Assets/New.prefab", AssetDerivation.Move, new[]
                {
                    Key(VerifySeverity.Error, "Assets/Old.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed"),
                    Key(VerifySeverity.Warning, "Assets/Old.prefab", "empty_local_ref:Old/Child:target"),
                    Key(VerifySeverity.Warning, "Assets/Old.prefab", "empty_local_ref:Old/Child:gone"),
                })
            };
            var before = Before(
                Key(VerifySeverity.Error, "Assets/Old.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed"),
                Key(VerifySeverity.Warning, "Assets/Old.prefab", "empty_local_ref:Old/Child:target"),
                Key(VerifySeverity.Warning, "Assets/Old.prefab", "empty_local_ref:Old/Child:gone"));
            var after = After(
                Issue(VerifySeverity.Error, "Assets/New.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed"),
                Issue(VerifySeverity.Warning, "Assets/New.prefab", "empty_local_ref:New/Child:target"));

            var delta = VerifyGateAdapter.ComputeDelta(before, after, lineage);

            Assert.AreEqual(0, delta.NewErrors);
            Assert.AreEqual(0, delta.NewWarnings);
            Assert.AreEqual(0, delta.ResolvedErrors, "the missing script moved with the asset");
            Assert.AreEqual(1, delta.ResolvedWarnings, "the 'gone' reference is not at the new path: it was resolved");
            Assert.AreEqual(1, delta.InheritedErrors);
            Assert.AreEqual(1, delta.InheritedWarnings);
        }

        [Test]
        public void ComputeDelta_WithoutLineage_KeepsTheHistoricalShape()
        {
            var delta = VerifyGateAdapter.ComputeDelta(Before(),
                After(Issue(VerifySeverity.Error, "Assets/Dst.prefab", "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed")));
            Assert.AreEqual(1, delta.NewErrors);
            Assert.IsNull(delta.Derivations);
            Assert.AreEqual(0, delta.InheritedIssueKeys.Length);
        }

        // ------------------------------------------------------------------
        // Gate plumbing: the recorder is open during the mutation and scans
        // the source with the same seam the validation uses.
        // ------------------------------------------------------------------

        [Test]
        public void Execute_CopyInsideTheGate_PassesAndReportsInheritedIssues()
        {
            const string src = "Assets/__MCPTest_Lineage_Src.prefab";
            const string dst = "Assets/__MCPTest_Lineage_Dst.prefab";
            var scanned = new List<string[]>();
            GatePolicy.ValidatePathsOverride = (paths, _, __) =>
            {
                scanned.Add(paths);
                var issues = new List<VerifyIssue>();
                foreach (var p in paths)
                {
                    var root = System.IO.Path.GetFileNameWithoutExtension(p);
                    issues.Add(Issue(VerifySeverity.Error, p, "missing_script:d00dfeedd00dfeedd00dfeedd00dfeed"));
                    issues.Add(Issue(VerifySeverity.Warning, p, "empty_local_ref:" + root + "/Child:target"));
                }
                return new VerifyResult(issues, new[] { "missing_references" }, 0);
            };
            try
            {
                Assert.IsNull(AssetLineage.Capture(src, dst, AssetDerivation.Copy), "no recorder outside a gate");
                var result = GatePolicy.Execute(GateMode.Enforce, new[] { dst }, () =>
                {
                    AssetLineage.Capture(src, dst, AssetDerivation.Copy)?.Commit();
                    return ToolDispatchResult.Ok("{\"copied\":[\"" + dst + "\"]}");
                });

                Assert.IsNull(AssetLineage.Current, "the recorder closes with the mutation");
                Assert.AreEqual(GateOutcome.Passed, result.Outcome);
                Assert.IsFalse(result.GateFailed);
                Assert.AreEqual(0, result.Delta.NewErrors);
                Assert.AreEqual(1, result.Delta.InheritedErrors);
                Assert.AreEqual(1, result.Delta.InheritedWarnings);
                CollectionAssert.Contains(scanned.Select(s => string.Join(",", s)).ToList(), src,
                    "the source was scanned for the destination's baseline");
                Assert.IsTrue(result.AgentNextSteps.Any(s => s.Contains("inherited from the source")
                                                             && s.Contains(src) && s.Contains(dst)),
                    string.Join(" | ", result.AgentNextSteps));

                var envelope = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.EditorSettle);
                StringAssert.Contains("\"inheritedErrors\":1", envelope);
                StringAssert.Contains("\"inheritedWarnings\":1", envelope);
                StringAssert.Contains("\"inheritedIssues\":[", envelope);
                StringAssert.Contains("\"inheritedFrom\":[{\"source\":\"" + src + "\",\"destination\":\"" + dst
                                      + "\",\"operation\":\"copy\",\"errors\":1,\"warnings\":1}]", envelope);
            }
            finally
            {
                GatePolicy.ValidatePathsOverride = null;
            }
        }

        [Test]
        public void Envelope_WithoutDerivations_HasNoInheritedFields()
        {
            GatePolicy.ValidatePathsOverride = (_, __, ___) =>
                new VerifyResult(new List<VerifyIssue>(), new[] { "missing_references" }, 0);
            try
            {
                var result = GatePolicy.Execute(GateMode.Enforce, new[] { "Assets/__MCPTest_Lineage_None.prefab" },
                    () => ToolDispatchResult.Ok("{}"));
                var envelope = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.EditorSettle);
                StringAssert.DoesNotContain("inherited", envelope);
            }
            finally
            {
                GatePolicy.ValidatePathsOverride = null;
            }
        }
    }
}
