using System;
using NUnit.Framework;
using UnityOpenMcpVerify;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class IssueKeyTests
    {
        [Test]
        public void Build_Error_ProducesCorrectFormat()
        {
            var key = IssueKey.Build("missing_references", VerifySeverity.Error,
                "Assets/Test.prefab", "missing_script");

            Assert.AreEqual("missing_references|ERROR|Assets/Test.prefab|missing_script", key);
        }

        [Test]
        public void Build_Warning_ProducesCorrectFormat()
        {
            var key = IssueKey.Build("scene_prefab_health", VerifySeverity.Warning,
                "Assets/Scenes/Main.unity", "deep_nesting");

            Assert.AreEqual("scene_prefab_health|WARN|Assets/Scenes/Main.unity|deep_nesting", key);
        }

        [Test]
        public void Build_FromIssue_MatchesDirectBuild()
        {
            var issue = new VerifyIssue("missing_references", VerifySeverity.Error,
                "Assets/Prefab.prefab", "missing_guid", "desc");

            Assert.AreEqual(IssueKey.Build(issue), IssueKey.Build(issue.RuleId, issue.Severity, issue.AssetPath, issue.IssueCode));
        }

        [Test]
        public void Build_NullRuleId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build(null, VerifySeverity.Error, "Assets/A.prefab", "code"));
        }

        [Test]
        public void Build_EmptyRuleId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("", VerifySeverity.Error, "Assets/A.prefab", "code"));
        }

        [Test]
        public void Build_EmptyAssetPath_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, "", "code"));
        }

        [Test]
        public void Build_EmptyIssueCode_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, "Assets/A.prefab", ""));
        }

        [Test]
        public void Build_RuleIdContainsPipe_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("bad|rule", VerifySeverity.Error, "Assets/A.prefab", "code"));
        }

        [Test]
        public void Build_AssetPathContainsPipe_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, "bad|path", "code"));
        }

        [Test]
        public void Build_IssueCodeContainsPipe_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, "Assets/A.prefab", "bad|code"));
        }

        [Test]
        public void TryParse_ValidErrorKey_ReturnsTrue()
        {
            var ok = IssueKey.TryParse("missing_references|ERROR|Assets/A.prefab|missing_script",
                out var ruleId, out var sev, out var path, out var code);

            Assert.IsTrue(ok);
            Assert.AreEqual("missing_references", ruleId);
            Assert.AreEqual(VerifySeverity.Error, sev);
            Assert.AreEqual("Assets/A.prefab", path);
            Assert.AreEqual("missing_script", code);
        }

        [Test]
        public void TryParse_ValidWarningKey_ReturnsTrue()
        {
            var ok = IssueKey.TryParse("scene_prefab_health|WARN|Assets/B.unity|deep_nesting",
                out var ruleId, out var sev, out var path, out var code);

            Assert.IsTrue(ok);
            Assert.AreEqual("scene_prefab_health", ruleId);
            Assert.AreEqual(VerifySeverity.Warning, sev);
            Assert.AreEqual("Assets/B.unity", path);
            Assert.AreEqual("deep_nesting", code);
        }

        [Test]
        public void TryParse_NullKey_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse(null, out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_EmptyKey_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("", out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_WrongPartCount_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("a|b|c", out _, out _, out _, out _));
            Assert.IsFalse(IssueKey.TryParse("a|b|c|d|e", out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_InvalidSeverity_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("rule|CRITICAL|Assets/A.prefab|code",
                out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_EmptyRuleId_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("|ERROR|Assets/A.prefab|code",
                out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_EmptyAssetPath_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("rule|ERROR||code",
                out _, out _, out _, out _));
        }

        [Test]
        public void TryParse_EmptyIssueCode_ReturnsFalse()
        {
            Assert.IsFalse(IssueKey.TryParse("rule|ERROR|Assets/A.prefab|",
                out _, out _, out _, out _));
        }

        [Test]
        public void ValidateKey_Valid_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                IssueKey.ValidateKey("missing_references|ERROR|Assets/A.prefab|missing_script"));
        }

        [Test]
        public void ValidateKey_Malformed_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() =>
                IssueKey.ValidateKey("bad-key"));
        }

        [Test]
        public void BuildRoundTrip_PreservesAllComponents()
        {
            var original = IssueKey.Build("scene_prefab_health", VerifySeverity.Warning,
                "Assets/Scenes/Game.unity", "override_explosion");

            Assert.IsTrue(IssueKey.TryParse(original, out var ruleId, out var sev, out var path, out var code));
            Assert.AreEqual("scene_prefab_health", ruleId);
            Assert.AreEqual(VerifySeverity.Warning, sev);
            Assert.AreEqual("Assets/Scenes/Game.unity", path);
            Assert.AreEqual("override_explosion", code);
        }
    }
}
