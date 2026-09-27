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
        public void Classify_MatchingAndForeignPort_IsMixedAndNotRewritten()
        {
            // Two entries distinguished ONLY by port (no project env): the
            // file serves two projects. A rewrite is whole-file, so moving
            // our pin would move the other entry's pin too — the same hazard
            // as the project-env Mixed case, one level harder to see.
            var port = InstancePortResolver.ComputePort(Project);
            var foreignPort = port > 30000 ? port - 1 : port + 1;
            var body =
                "\"UNITY_OPEN_MCP_BRIDGE_PORT\": \"" + port + "\"\n" +
                "\"UNITY_OPEN_MCP_BRIDGE_PORT\": " + foreignPort;
            var scope = UpgradeEntryScope.Classify(body, Project);

            Assert.AreEqual(Ownership.Mixed, scope.Kind);
            Assert.IsTrue(scope.ViaPort);
            Assert.IsNull(scope.ForeignProject, "A port names no project to report.");
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: true));
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: false));
            StringAssert.Contains("another project", scope.SkipReason);
        }

        [Test]
        public void Classify_SamePortTwice_IsStillMatchedViaPort()
        {
            // Duplicate entries for ONE project (same port) are not mixed —
            // this is the normal "two clients, one project" shape.
            var port = InstancePortResolver.ComputePort(Project);
            var body =
                "\"UNITY_OPEN_MCP_BRIDGE_PORT\": \"" + port + "\"\n" +
                "\"UNITY_OPEN_MCP_BRIDGE_PORT\": " + port;
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

        // ---- portable entries: anchored at the workspace root ---------------

        private const string Repo = "/Users/dev/repo";
        private const string Sibling = Repo + "/ClientB";

        private static string WorkspaceFolderEntry(string subpath)
        {
            return
                "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": {\n" +
                "      \"command\": \"npx\",\n" +
                "      \"args\": [\"-y\", \"unity-open-mcp@1.3.0\"],\n" +
                "      \"env\": { \"UNITY_PROJECT_PATH\": \"${workspaceFolder}/" + subpath + "\" }\n" +
                "    }\n  }\n}";
        }

        [Test]
        public void Classify_WorkspaceFolderEntry_ResolvesAgainstTheWorkspaceRoot()
        {
            // A committed Cursor config at <repo>/.cursor/mcp.json names the
            // Unity folder through the client's workspace variable; the
            // directory it was found under is that workspace.
            var scope = UpgradeEntryScope.Classify(WorkspaceFolderEntry("Client"), Project, Repo);

            Assert.AreEqual(Ownership.Matched, scope.Kind);
            CollectionAssert.AreEqual(new[] { Project }, scope.ProjectPaths);
            Assert.IsFalse(scope.ViaPort);
        }

        [Test]
        public void Classify_WorkspaceFolderEntryOfASiblingProject_IsForeign()
        {
            // Two Unity projects in one repository: ClientB's Editor must not
            // move ClientA's committed pin.
            var scope = UpgradeEntryScope.Classify(WorkspaceFolderEntry("Client"), Sibling, Repo);

            Assert.AreEqual(Ownership.OtherProject, scope.Kind);
            CollectionAssert.AreEqual(new[] { Project }, scope.ProjectPaths);
            Assert.IsFalse(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: false));
        }

        [Test]
        public void Classify_BareWorkspaceFolder_IsTheWorkspaceRoot()
        {
            var body = "\"UNITY_PROJECT_PATH\": \"${workspaceFolder}\"";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Repo, Repo).Kind);
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Project, Repo).Kind);
        }

        [Test]
        public void Classify_WorkspaceFolderEntryWithoutARoot_IsUnknownAndRewrittenInProject()
        {
            // No workspace root to anchor the value (a caller that only has
            // the body): the entry claims nothing, and inside the repository
            // the file's location is the ownership claim.
            var scope = UpgradeEntryScope.Classify(WorkspaceFolderEntry("Client"), Project);

            Assert.AreEqual(Ownership.Unknown, scope.Kind);
            Assert.IsEmpty(scope.ProjectPaths);
            Assert.IsTrue(UpgradeEntryScope.ShouldRewrite(scope, isHomeScoped: false));
        }

        [Test]
        public void Classify_RelativeProjectPath_ResolvesAgainstTheWorkspaceRoot()
        {
            var body = "\"UNITY_PROJECT_PATH\": \"Client\"";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Project, Repo).Kind);
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Sibling, Repo).Kind);
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void Classify_UnitySubpathArg_ResolvesAgainstTheWorkspaceRoot()
        {
            // The args form (Gemini, Claude Code's .mcp.json, OpenCode …):
            // no env at all, the subfolder rides in the launch arguments.
            var body =
                "\"args\": [\"-y\", \"unity-open-mcp@1.3.0\", \"--project-from-cwd\", \"--unity-subpath\", \"Client\"],\n" +
                "\"env\": {}";
            var scope = UpgradeEntryScope.Classify(body, Project, Repo);
            Assert.AreEqual(Ownership.Matched, scope.Kind);
            CollectionAssert.AreEqual(new[] { Project }, scope.ProjectPaths);
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Sibling, Repo).Kind);
        }

        [Test]
        public void Classify_ProjectFromCwdAlone_ClaimsTheWorkspaceRoot()
        {
            var body = "\"args\": [\"-y\", \"unity-open-mcp@1.3.0\", \"--project-from-cwd\"]";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Repo, Repo).Kind);
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Project, Repo).Kind);
        }

        [Test]
        public void Classify_WrapperScript_ResolvesItsBakedSubpath()
        {
            // The committed wrapper exports a shell variable (unresolvable)
            // but bakes the Unity subfolder it defaults to.
            var body =
                "subpath=\"${UNITY_SUBPATH-Client}\"\n" +
                "export UNITY_PROJECT_PATH=\"${project_path}\"\n" +
                "exec npx -y \"unity-open-mcp@1.3.0\" \"$@\"\n";
            var scope = UpgradeEntryScope.Classify(body, Project, Repo);
            Assert.AreEqual(Ownership.Matched, scope.Kind);
            CollectionAssert.AreEqual(new[] { Project }, scope.ProjectPaths);
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Sibling, Repo).Kind);
            Assert.AreEqual(Ownership.Unknown, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void Classify_UnityRootWrapper_ClaimsTheWorkspaceRoot()
        {
            var body = "subpath=\"${UNITY_SUBPATH-}\"\nexport UNITY_PROJECT_PATH=\"${project_path}\"\n";
            Assert.AreEqual(Ownership.Matched, UpgradeEntryScope.Classify(body, Repo, Repo).Kind);
        }

        [Test]
        public void Classify_PortableEntryNextToForeignProject_IsMixed()
        {
            // Our resolved portable entry plus an absolute foreign one share
            // the file — a whole-file rewrite would move both, so skip.
            var body =
                "\"UNITY_PROJECT_PATH\": \"${workspaceFolder}/Client\"\n" +
                "\"UNITY_PROJECT_PATH\": \"" + Other + "\"";
            Assert.AreEqual(Ownership.Mixed, UpgradeEntryScope.Classify(body, Project, Repo).Kind);
            // Without a root the portable entry claims nothing and the
            // foreign entry decides.
            Assert.AreEqual(Ownership.OtherProject, UpgradeEntryScope.Classify(body, Project).Kind);
        }

        [Test]
        public void ResolveClaim_HandlesEveryForm()
        {
            Assert.AreEqual(Other, UpgradeEntryScope.ResolveClaim(Other, Repo));
            Assert.AreEqual(Other, UpgradeEntryScope.ResolveClaim(Other, null));
            Assert.AreEqual(Repo + "/Client", UpgradeEntryScope.ResolveClaim("${workspaceFolder}/Client", Repo));
            Assert.AreEqual(Repo + "/Client", UpgradeEntryScope.ResolveClaim("${workspaceFolder}\\Client", Repo + "/"));
            Assert.AreEqual(Repo, UpgradeEntryScope.ResolveClaim("${workspaceFolder}", Repo));
            Assert.AreEqual(Repo + "/Client", UpgradeEntryScope.ResolveClaim("Client", Repo));
            Assert.IsNull(UpgradeEntryScope.ResolveClaim("${workspaceFolder}Client", Repo));
            Assert.IsNull(UpgradeEntryScope.ResolveClaim("${project_path}", Repo));
            Assert.IsNull(UpgradeEntryScope.ResolveClaim("$HOME/game", Repo));
            Assert.IsNull(UpgradeEntryScope.ResolveClaim("Client", null));
            Assert.IsNull(UpgradeEntryScope.ResolveClaim("", Repo));
        }

        [Test]
        public void IsMachinePath_RecognizesAbsoluteForms()
        {
            Assert.IsTrue(UpgradeEntryScope.IsMachinePath("/Users/dev/repo/Client"));
            Assert.IsTrue(UpgradeEntryScope.IsMachinePath("C:\\\\work\\\\Client"));
            Assert.IsTrue(UpgradeEntryScope.IsMachinePath("C:/work/Client"));
            Assert.IsTrue(UpgradeEntryScope.IsMachinePath("\\\\\\\\server\\\\share"));
            Assert.IsFalse(UpgradeEntryScope.IsMachinePath("${workspaceFolder}/Client"));
            Assert.IsFalse(UpgradeEntryScope.IsMachinePath("$HOME/game"));
            Assert.IsFalse(UpgradeEntryScope.IsMachinePath("Client"));
            Assert.IsFalse(UpgradeEntryScope.IsMachinePath(""));
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
