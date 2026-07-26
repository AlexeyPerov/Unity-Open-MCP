using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // V11 regression — CandidatesForIssue / TryGetFixInfo used to call
    // Describe() to read each provider's Safe flag. For the materials and
    // duplicate_guid providers Describe() runs a full project-wide asset sweep
    // (FindAssets("t:Shader"), GetAllAssetPaths) that has no bearing on the
    // safety verdict. On a URP project with a few hundred materials issues that
    // is hundreds of full search-index queries per scan. Both call sites now
    // route through IFixProvider.IsSafe(), which returns the same verdict with
    // no asset I/O. These tests pin the contract: IsSafe() MUST agree with
    // Describe().Safe for every registered provider, and the registry helpers
    // MUST surface that same value.
    [TestFixture]
    public class FixProviderRegistryTests
    {
        // The (ruleId, issueCode, expectedSafe) matrix for every shipped
        // provider. Derived from each provider's Describe() output. If a
        // provider's safety verdict ever changes, both Describe() and IsSafe()
        // must move together — this test is the tripwire.
        private static readonly TestCaseData[] s_safeMatrix =
        {
            new TestCaseData("missing_references", "missing_script", true)
                .SetName("missing_script (synthetic __test__.prefab key is Safe)"),
            new TestCaseData("missing_references", "missing_guid", false)
                .SetName("missing_guid is unsafe"),
            new TestCaseData("dependencies", "broken_dependency", false)
                .SetName("broken_dependency is unsafe"),
            new TestCaseData("project_health", "orphan_meta", true)
                .SetName("orphan_meta is safe"),
            new TestCaseData("project_health", "duplicate_guid", false)
                .SetName("duplicate_guid is unsafe"),
            new TestCaseData("offline_integrity", "orphan_meta", true)
                .SetName("offline_integrity orphan_meta is safe"),
            new TestCaseData("offline_integrity", "duplicate_guid", false)
                .SetName("offline_integrity duplicate_guid is unsafe"),
            new TestCaseData("materials", "missing_shader", false)
                .SetName("missing_shader is unsafe"),
        };

        // IsSafe() must agree with Describe().Safe for the synthetic key the
        // registry uses — this is the V11 invariant. If a provider ever drifts,
        // scan_paths / validate_edit would advertise a different Safe flag than
        // apply_fix's dry-run describes.
        [TestCaseSource(nameof(s_safeMatrix))]
        public void IsSafe_AgreesWithDescribe_ForEveryProvider(
            string ruleId, string issueCode, bool expectedSafe)
        {
            var testKey = IssueKey.Build(
                ruleId, VerifySeverity.Error,
                "__test__.prefab", issueCode);

            foreach (var provider in FixProviderRegistry.AvailableProviders())
            {
                if (!provider.CanFix(testKey)) continue;

                Assert.AreEqual(
                    provider.Describe(testKey).Safe,
                    provider.IsSafe(testKey),
                    $"{provider.FixId}: IsSafe() must equal Describe().Safe");
                Assert.AreEqual(
                    expectedSafe,
                    provider.IsSafe(testKey),
                    $"{provider.FixId}: IsSafe() verdict drift");
                return;
            }

            // Before V11 this would have run FindAssets("t:Shader") + several
            // LoadAssetAtPath calls just to learn `false`. Now it is a constant
            // return — assert a provider claimed this code at all.
            Assert.Fail(
                $"No provider claims {ruleId}/{issueCode} — safeMatrix is stale");
        }

        // TryGetFixInfo must surface IsSafe(), not a hardwired value. This is
        // the exact regression V11 fixes: the value comes from the cheap path,
        // but it must still be correct.
        [TestCaseSource(nameof(s_safeMatrix))]
        public void TryGetFixInfo_SurfacesIsSafeVerdict(
            string ruleId, string issueCode, bool expectedSafe)
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                ruleId, issueCode, out _, out var safe);

            Assert.IsTrue(ok, $"{ruleId}/{issueCode} must resolve to a fix");
            Assert.AreEqual(expectedSafe, safe,
                $"{ruleId}/{issueCode}: TryGetFixInfo must surface IsSafe verdict");
        }

        // CandidatesForIssue must carry the same Safe flag per candidate.
        [TestCaseSource(nameof(s_safeMatrix))]
        public void CandidatesForIssue_SurfacesIsSafeVerdict(
            string ruleId, string issueCode, bool expectedSafe)
        {
            var candidates = FixProviderRegistry.CandidatesForIssue(ruleId, issueCode);

            Assert.GreaterOrEqual(candidates.Length, 1,
                $"{ruleId}/{issueCode} must have at least one candidate fix");
            foreach (var c in candidates)
            {
                Assert.AreEqual(expectedSafe, c.Safe,
                    $"{c.FixId}: candidate Safe must match IsSafe verdict");
            }
        }

        [Test]
        public void AvailableProviders_IncludesEveryShippedFix()
        {
            // If a provider is added or removed, this list and the safe matrix
            // above must move together — V11's contract is per-provider.
            CollectionAssert.AreEquivalent(
                new[] {
                    "remove_missing_script",
                    "relink_broken_guid",
                    "remove_orphan_meta",
                    "fix_duplicate_guid",
                    "reassign_missing_shader",
                },
                FixProviderRegistry.AvailableFixIds());
        }
    }
}
