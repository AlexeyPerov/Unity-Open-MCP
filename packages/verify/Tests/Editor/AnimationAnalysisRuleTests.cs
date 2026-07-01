using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class AnimationAnalysisRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/AnimationAnalysis";

        private AnimationAnalysisRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new AnimationAnalysisRule();
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureDirectory(FixtureRoot);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(FixtureRoot))
            {
                AssetDatabase.DeleteAsset(FixtureRoot);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("animation_analysis", rule.Id);
        }

        [Test]
        public void Scan_EmptyPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new string[0]);
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NonAnimationPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomePrefab.prefab" });
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenClipReference_ReportsMissingClip()
        {
            var path = FixtureRoot + "/BrokenClip.controller";
            File.WriteAllText(path, MinimalControllerWithBrokenMotion(
                "1234567890abcdef1234567890abcdef"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingClip = sink.FirstOrDefault(i => i.IssueCode == "missing_clip");
            Assert.IsNotNull(missingClip,
                $"Expected missing_clip. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingClip.Severity);
            Assert.AreEqual("animation_analysis", missingClip.RuleId);
            Assert.AreEqual(path, missingClip.AssetPath);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_EmptyClip_ReportsEmptyClip()
        {
            var path = FixtureRoot + "/EmptyClip.anim";
            File.WriteAllText(path, EmptyAnimClip());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var emptyClip = sink.FirstOrDefault(i => i.IssueCode == "empty_clip");
            Assert.IsNotNull(emptyClip,
                $"Expected empty_clip. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Warning, emptyClip.Severity);
            Assert.AreEqual("animation_analysis", emptyClip.RuleId);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.controller";
            File.WriteAllText(path, MinimalControllerWithBrokenMotion(
                "fedcba0987654321fedcba0987654321"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        // -------------------------------------------------------------------
        // Fixture builders
        // -------------------------------------------------------------------

        private static string MinimalControllerWithBrokenMotion(string brokenGuid)
        {
            return "%YAML 1.1\n" +
                   "%TAG !u! tag:unity3d.com,2011:\n" +
                   "--- !u!91 &9100000\n" +
                   "AnimatorController:\n" +
                   "  m_Name: BrokenClip\n" +
                   "  m_AnimatorParameters: []\n" +
                   "  m_AnyStateTransitions: []\n" +
                   "--- !u!1101 &110100000\n" +
                   "AnimatorStateMachine:\n" +
                   "  m_Name: Base Layer\n" +
                   "  m_StateMachineTransitions: {}\n" +
                   "  m_States: []\n" +
                   "  m_ChildStates: []\n" +
                   "--- !u!1102 &110200000\n" +
                   "AnimatorState:\n" +
                   "  m_Name: State\n" +
                   "  m_Motion: {fileID: 7400000, guid: " + brokenGuid + ", type: 2}\n";
        }

        // A clip that declares every curve group as an empty array — the rule
        // must classify it as animating nothing.
        private static string EmptyAnimClip()
        {
            return "%YAML 1.1\n" +
                   "%TAG !u! tag:unity3d.com,2011:\n" +
                   "--- !u!74 &7400000\n" +
                   "AnimationClip:\n" +
                   "  m_Name: EmptyClip\n" +
                   "  serializedVersion: 7\n" +
                   "  m_RotationCurves: []\n" +
                   "  m_CompressedRotationCurves: []\n" +
                   "  m_EulerCurves: []\n" +
                   "  m_PositionCurves: []\n" +
                   "  m_ScaleCurves: []\n" +
                   "  m_FloatCurves: []\n" +
                   "  m_PPtrCurves: []\n";
        }

        private static void EnsureDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
