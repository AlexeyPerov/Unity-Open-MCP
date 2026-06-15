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
    }
}
