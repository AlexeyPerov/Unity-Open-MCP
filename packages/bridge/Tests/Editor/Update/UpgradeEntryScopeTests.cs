using NUnit.Framework;
using UnityOpenMcpBridge.Update;
using Ownership = UnityOpenMcpBridge.Update.UpgradeEntryScope.Ownership;

namespace UnityOpenMcpBridge.Tests
{
    // M32 Plan 4 / T32.4.3 — pins the rule that makes rewriting a machine-wide
    // client config safe. `~/.codex/config.toml` and Claude Desktop's config
    // hold entries for every project on the machine; the updater may only move
    // the entry that names THIS project. These tests pin both directions:
    // a matching entry is claimed, a foreign one is not, and a config with no
    // project marker is claimed only when its own location says it is ours.
    [TestFixture]
    public class UpgradeEntryScopeTests
    {
        private const string Project = "/Users/dev/repo/Client";
        private const string Other = "/Users/dev/other-game/Client";

        [Test]
        public void Classify_JsonEntryNamingThisProject_IsMatched()
        {
            var body =
                "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": {\n" +
                "      \"command\": \"npx\",\n" +
                "      \"args\": [\"-y\", \"unity-open-mcp@1.1.0\"],\n" +
                "      \"env\": { \"UNITY_PROJECT_PATH\": \"" + Project + "\" }\n" +
                "    }\n  }\n}";

            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Matched, scope.Kind);
            Assert.IsFalse(scope.ViaPort, "The project path itself established ownership.");
        }

        [Test]
        public void Classify_TomlEntryNamingThisProject_IsMatched()
        {
            // Codex's envelope: unquoted key, `=` separator.
            var body =
                "[mcp_servers.unity-open-mcp]\n" +
                "command = \"npx\"\n" +
                "args = [ \"-y\", \"unity-open-mcp@0.8.4\" ]\n\n" +
                "[mcp_servers.unity-open-mcp.env]\n" +
                "UNITY_PROJECT_PATH = \"" + Project + "\"\n";

            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void Classify_TrailingSeparatorStillMatches()
        {
            var body = "\"UNITY_PROJECT_PATH\": \"" + Project + "/\"";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void Classify_WindowsSeparatorsStillMatch()
        {
            var body = "\"UNITY_PROJECT_PATH\": \"C:\\\\work\\\\repo\\\\Client\"";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, "C:/work/repo/Client").Kind);
        }

        [Test]
        public void Classify_EntryForAnotherProject_IsNotOurs()
        {
            var body = "\"UNITY_PROJECT_PATH\": \"" + Other + "\"";
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.OtherProject, scope.Kind);
            Assert.AreEqual(new[] { Other }, scope.ProjectPaths);
            StringAssert.Contains(Other, scope.SkipReason);
        }

        [Test]
        public void Classify_SharedFileWithAnotherProject_IsMixedAndNotRewritten()
        {
            // The dangerous home-config shape: two Unity servers side by side,
            // one per project. A rewrite is whole-file, so moving our pin would
            // move theirs too — and their bridge may still be on the old
            // version. Report it and let the operator edit that entry.
            var body =
                "\"UNITY_PROJECT_PATH\": \"" + Other + "\"\n" +
                "\"UNITY_PROJECT_PATH\": \"" + Project + "\"";
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Mixed, scope.Kind);
            Assert.AreEqual(Other, scope.ForeignProject);
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: true));
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: false));
            StringAssert.Contains(Other, scope.SkipReason);
        }

        [Test]
        public void Classify_TwoEntriesForTheSameProject_IsMatched()
        {
            // Two clients configured for one project is normal, not mixed.
            var body =
                "\"UNITY_PROJECT_PATH\": \"" + Project + "\"\n" +
                "\"UNITY_PROJECT_PATH\": \"" + Project + "/\"";
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Matched, scope.Kind);
            Assert.IsNull(scope.ForeignProject);
        }

        [Test]
        public void Classify_NoProjectEnvButMatchingPort_IsMatchedViaPort()
        {
            var port = InstancePortResolver.ComputePort(Project);
            var body = "\"UNITY_OPEN_MCP_BRIDGE_PORT\": \"" + port + "\"";
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Matched, scope.Kind);
            Assert.IsTrue(scope.ViaPort);
        }

        [Test]
        public void Classify_NoProjectEnvAndForeignPort_IsUnknown()
        {
            var foreignPort = InstancePortResolver.ComputePort(Other);
            var body = "\"UNITY_OPEN_MCP_BRIDGE_PORT\": " + foreignPort;
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void Classify_NoMarkersAtAll_IsUnknown()
        {
            var body = "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": { \"command\": \"npx\" }\n  }\n}";
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Unknown, scope.Kind);
            Assert.IsNotEmpty(scope.SkipReason);
        }

        [Test]
        public void Classify_EmptyInputs_AreUnknown()
        {
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify("", Project).Kind);
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify("anything", "").Kind);
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify(null, Project).Kind);
        }

        // ---- ShouldRewrite: where the file lives decides the unknown case ---

        [Test]
        public void ShouldRewrite_UnknownEntry_DependsOnScope()
        {
            var unknown = UpgradeEntryScope.Classify("no markers here", Project);

            Assert.IsFalse(
                UpgradeEntryScope.ShouldRewrite(unknown, isHomeScoped: true),
                "A shared home config with no owner is left alone.");
            Assert.IsTrue(
                UpgradeEntryScope.ShouldRewrite(unknown, isHomeScoped: false),
                "A config inside the project is ours by location.");
        }

        [Test]
        public void ShouldRewrite_MatchedAlwaysTrue_ForeignAlwaysFalse()
        {
            var matched = UpgradeEntryScope.Classify("\"UNITY_PROJECT_PATH\": \"" + Project + "\"", Project);
            var foreign = UpgradeEntryScope.Classify("\"UNITY_PROJECT_PATH\": \"" + Other + "\"", Project);

            Assert.IsTrue(UpgradeEntryScope.ShouldRewrite(matched, isHomeScoped: true));
            Assert.IsTrue(UpgradeEntryScope.ShouldRewrite(matched, isHomeScoped: false));
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(foreign, isHomeScoped: true));
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(foreign, isHomeScoped: false));
        }
    }
}
