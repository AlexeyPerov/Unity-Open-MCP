using System;
using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.Update;

namespace UnityOpenMcpBridge.Tests.Update
{
    public class ProjectUpgradeRunnerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uomcp-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Assets"));
            Directory.CreateDirectory(Path.Combine(_root, ".cursor"));
            File.WriteAllText(Path.Combine(_root, ".cursor", "mcp.json"),
                "{\"mcpServers\":{\"unity-open-mcp\":{\"args\":[\"unity-open-mcp@1.0.0\"]}}}");
            File.WriteAllText(Path.Combine(_root, "MCP.md"), "Run unity-open-mcp@1.0.0 for this project.");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void FormatReport_UnreadableUpmCandidateDoesNotThrow()
        {
            var plan = new ProjectUpgradeRunner.PlanResult
            {
                TargetVersion = "1.2.4",
                UpmEnabled = false,
                UpmReason = "bridge package information is unavailable",
            };
            plan.Files.Add(new ProjectUpgradeRunner.FilePlan
            {
                Path = Path.Combine(_root, "Packages", "manifest.json"),
                Kind = UpgradeScanner.CandidateKind.UpmManifest,
                SkipReason = "could not read: access denied",
            });

            var report = ProjectUpgradeRunner.FormatReport(plan);

            StringAssert.Contains("could not read: access denied", report);
        }

        [Test]
        public void Plan_IsDryRun_AndApplyWritesBackupsAndSelectedFiles()
        {
            var config = Path.Combine(_root, ".cursor", "mcp.json");
            var prose = Path.Combine(_root, "MCP.md");
            var options = new ProjectUpgradeRunner.Options(false, true, false, true);

            var plan = ProjectUpgradeRunner.Plan(_root, "1.2.3", options);

            StringAssert.Contains("@1.0.0", File.ReadAllText(config));
            StringAssert.Contains("@1.0.0", File.ReadAllText(prose));
            Assert.AreEqual(2, plan.WriteCount);

            var applied = ProjectUpgradeRunner.Apply(plan, scheduleUpm: false);

            Assert.IsTrue(applied.Success);
            Assert.AreEqual(2, applied.FilesWritten);
            StringAssert.Contains("@1.2.3", File.ReadAllText(config));
            StringAssert.Contains("@1.2.3", File.ReadAllText(prose));
            Assert.IsTrue(File.Exists(config + ".bak"));
            Assert.IsTrue(File.Exists(prose + ".bak"));
            StringAssert.Contains("@1.0.0", File.ReadAllText(config + ".bak"));
        }

        [Test]
        public void HomeConfigForOtherProject_IsReportedAndUntouched()
        {
            var home = Path.Combine(_root, "home");
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            var path = Path.Combine(home, ".codex", "config.toml");
            File.WriteAllText(path,
                "[mcp_servers.unity-open-mcp]\nargs=[\"unity-open-mcp@1.0.0\"]\n" +
                "[mcp_servers.unity-open-mcp.env]\nUNITY_PROJECT_PATH=\"/another/project\"\n");

            // Exercise ownership policy directly because production home-path
            // resolution intentionally uses the real OS home directory.
            var body = File.ReadAllText(path);
            var scope = UpgradeEntryScope.Classify(body, _root);
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: true));
            StringAssert.Contains("another project", scope.SkipReason);
            StringAssert.Contains("@1.0.0", File.ReadAllText(path));
        }

        [Test]
        public void EmbeddedBridge_DisablesUpmButLeavesConfigRewriteAvailable()
        {
            var plan = ProjectUpgradeRunner.Plan(
                _root, "1.2.3", new ProjectUpgradeRunner.Options(true, true, false, false));

            Assert.IsFalse(plan.UpmEnabled);
            StringAssert.Contains("not Git", plan.UpmReason);
            Assert.AreEqual(1, plan.WriteCount);
        }

        [Test]
        public void Apply_RefusesAFileChangedAfterPreviewBeforeAnyWrite()
        {
            var config = Path.Combine(_root, ".cursor", "mcp.json");
            var prose = Path.Combine(_root, "MCP.md");
            var plan = ProjectUpgradeRunner.Plan(
                _root, "1.2.3", new ProjectUpgradeRunner.Options(false, true, false, true));
            File.AppendAllText(prose, "\nconcurrent edit");

            var applied = ProjectUpgradeRunner.Apply(plan, scheduleUpm: false);

            Assert.IsFalse(applied.Success);
            StringAssert.Contains("changed since preview", applied.Message);
            StringAssert.Contains("@1.0.0", File.ReadAllText(config));
            Assert.IsFalse(File.Exists(config + ".bak"));
        }
    }
}
