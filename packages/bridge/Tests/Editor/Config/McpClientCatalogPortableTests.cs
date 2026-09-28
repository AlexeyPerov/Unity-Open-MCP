using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.Config;
using PortableStrategy = UnityOpenMcpBridge.Config.McpClientCatalog.PortableStrategy;

namespace UnityOpenMcpBridge.Tests
{
    // Pins the commit-safe slice of the Configure panel: which clients have a
    // portable form, where the entry goes for a Unity project inside a
    // repository, and that the snippet carries no machine path or bridge port —
    // the two values that differ on every teammate's machine.
    [TestFixture]
    public class McpClientCatalogPortableTests
    {
        private const string Repo = "/Users/dev/repo";
        private const string Project = Repo + "/Client";
        private static readonly string[] Args = { "-y", "unity-open-mcp@1.3.0" };

        // ---- strategy ------------------------------------------------------

        [Test]
        public void Strategy_MatchesTheSharedClientMatrix()
        {
            Assert.AreEqual(PortableStrategy.Interpolation, StrategyOf("cursor-project"));
            Assert.AreEqual(PortableStrategy.Interpolation, StrategyOf("vscodeCopilot"));
            Assert.AreEqual(PortableStrategy.Interpolation, StrategyOf("vsCopilot"));
            Assert.AreEqual(PortableStrategy.Args, StrategyOf("claudeCode"));
            Assert.AreEqual(PortableStrategy.Args, StrategyOf("gemini"));
            Assert.AreEqual(PortableStrategy.Args, StrategyOf("opencodeProject"));
            Assert.AreEqual(PortableStrategy.Args, StrategyOf("unityAi"));
            Assert.AreEqual(PortableStrategy.Wrapper, StrategyOf("codex"));
            Assert.AreEqual(PortableStrategy.Args, StrategyOf("zcodeProject"));
        }

        [Test]
        public void Strategy_GlobalConfigsHaveNoPortableForm()
        {
            foreach (var client in McpClientCatalog.Clients)
            {
                if (client.ScopeKind != McpClientCatalog.Scope.Global) continue;
                Assert.AreEqual(PortableStrategy.Absolute, McpClientCatalog.PortableStrategyFor(client), client.Id);
                Assert.IsNull(McpClientCatalog.ResolvePortablePlacement(client, Project, Repo), client.Id);
            }
        }

        // ---- placement -----------------------------------------------------

        [Test]
        public void Placement_MonorepoNamesTheUnitySubfolder()
        {
            var placement = Placement("cursor-project");
            Assert.AreEqual(Repo, placement.WorkspaceRoot);
            Assert.AreEqual("Client", placement.UnitySubpath);
            Assert.AreEqual("scripts/mcp/unity-open-mcp.sh", placement.WrapperRelativePath);
        }

        [Test]
        public void Placement_UnityProjectIsTheRepository()
        {
            var placement = McpClientCatalog.ResolvePortablePlacement(Find("cursor-project"), Project, Project).Value;
            Assert.AreEqual("", placement.UnitySubpath);
            Assert.AreEqual(".unity-open-mcp/mcp-wrapper.sh", placement.WrapperRelativePath);
        }

        [Test]
        public void Placement_NoRepositoryFallsBackToTheProjectFolder()
        {
            var placement = McpClientCatalog.ResolvePortablePlacement(Find("gemini"), Project, null).Value;
            Assert.AreEqual(Project, placement.WorkspaceRoot);
            Assert.AreEqual("", placement.UnitySubpath);
        }

        [Test]
        public void Placement_UnityAiStaysInTheUnityProject()
        {
            var placement = Placement("unityAi");
            Assert.AreEqual(Project, placement.WorkspaceRoot);
            Assert.AreEqual("", placement.UnitySubpath);
        }

        [Test]
        public void Placement_ProjectOutsideTheWorkspaceIsNotPortable()
        {
            Assert.IsNull(McpClientCatalog.ResolvePortablePlacement(Find("gemini"), "/elsewhere/Client", Repo));
            // A sibling that merely shares the prefix is outside too.
            Assert.IsNull(McpClientCatalog.ResolvePortablePlacement(Find("gemini"), Repo + "-old/Client", Repo));
        }

        [Test]
        public void TargetPath_IsUnderTheWorkspaceRoot()
        {
            var client = Find("cursor-project");
            Assert.AreEqual(Repo + "/.cursor/mcp.json",
                McpClientCatalog.ResolvePortableTargetPath(client, Placement("cursor-project")));
            Assert.IsNull(McpClientCatalog.ResolvePortableTargetPath(Find("claudeCode"), Placement("claudeCode")));
        }

        [Test]
        public void FindWorkspaceRoot_ReturnsTheNearestRepositoryAncestor()
        {
            Assert.AreEqual(Repo, McpClientCatalog.FindWorkspaceRoot(Project, "/Users/dev", d => d == Repo));
            Assert.AreEqual(Project, McpClientCatalog.FindWorkspaceRoot(Project, "/Users/dev", d => true));
            Assert.IsNull(McpClientCatalog.FindWorkspaceRoot(Project, "/Users/dev", d => false));
        }

        // ---- snippets ------------------------------------------------------

        [Test]
        public void Snippet_InterpolationUsesTheWorkspaceVariable()
        {
            var snippet = Snippet("cursor-project");
            StringAssert.Contains("\"UNITY_PROJECT_PATH\": \"${workspaceFolder}/Client\"", snippet);
            AssertNoMachineValues(snippet);
        }

        [Test]
        public void Snippet_ArgsAppendsTheResolutionFlags()
        {
            var snippet = Snippet("gemini");
            StringAssert.Contains("\"--project-from-cwd\",", snippet);
            StringAssert.Contains("\"--unity-subpath\",", snippet);
            StringAssert.Contains("\"env\": {}", snippet);
            AssertNoMachineValues(snippet);
        }

        [Test]
        public void Snippet_OpenCodeCarriesTheFlagsInItsCommandArray()
        {
            var snippet = Snippet("opencodeProject");
            StringAssert.Contains("\"--project-from-cwd\"", snippet);
            StringAssert.Contains("\"environment\": {}", snippet);
            AssertNoMachineValues(snippet);
        }

        [Test]
        public void Snippet_ClaudeCodeWritesTheProjectScopedFile()
        {
            Assert.AreEqual(
                "claude mcp add --scope project unity-open-mcp -- npx -y unity-open-mcp@1.3.0 " +
                "--project-from-cwd --unity-subpath Client",
                Snippet("claudeCode"));
        }

        [Test]
        public void Snippet_ClaudeCodeQuotesASubpathWithSpaces()
        {
            var placement = McpClientCatalog.ResolvePortablePlacement(
                Find("claudeCode"), Repo + "/My Client", Repo).Value;
            var snippet = McpClientCatalog.BuildSnippet(Find("claudeCode"), Project, 21234, "npx", Args, placement);
            StringAssert.EndsWith("--project-from-cwd --unity-subpath \"My Client\"", snippet);
        }

        [Test]
        public void ShellQuote_QuotesOnlyWhatTheShellWouldSplit()
        {
            Assert.AreEqual("Client", McpClientCatalog.ShellQuote("Client"));
            Assert.AreEqual("games/Client", McpClientCatalog.ShellQuote("games/Client"));
            Assert.AreEqual("\"My Client\"", McpClientCatalog.ShellQuote("My Client"));
            Assert.AreEqual("\"a\\\"b\"", McpClientCatalog.ShellQuote("a\"b"));
            Assert.AreEqual("\"\"", McpClientCatalog.ShellQuote(""));
        }

        [Test]
        public void Placement_WrapperAbsolutePathIsUnderTheWorkspaceRoot()
        {
            Assert.AreEqual(Repo + "/scripts/mcp/unity-open-mcp.sh", Placement("codex").WrapperAbsolutePath);
            var unityRoot = McpClientCatalog.ResolvePortablePlacement(Find("codex"), Project, Project).Value;
            Assert.AreEqual(Project + "/.unity-open-mcp/mcp-wrapper.sh", unityRoot.WrapperAbsolutePath);
        }

        [Test]
        public void Snippet_CodexRunsTheWrapper()
        {
            Assert.AreEqual(
                "[mcp_servers.unity-open-mcp]\n" +
                "enabled = true\n" +
                "command = \"bash\"\n" +
                "args = [\"scripts/mcp/unity-open-mcp.sh\"]\n",
                Snippet("codex"));
        }

        [Test]
        public void Snippet_ZcodeResolvesFromTheSessionDirectory()
        {
            // ZCode starts stdio servers in the session's working directory and
            // expands no ${...} templates in config files, so the args form fits.
            var snippet = Snippet("zcodeProject");
            StringAssert.Contains("\"mcp\": {", snippet);
            StringAssert.Contains("\"--project-from-cwd\",", snippet);
            StringAssert.Contains("\"--unity-subpath\",", snippet);
            StringAssert.DoesNotContain("bash", snippet);
            AssertNoMachineValues(snippet);
        }

        [Test]
        public void TargetPath_ZcodeProjectIsTheWorkspaceConfig()
        {
            Assert.AreEqual(Repo + "/.zcode/config.json",
                McpClientCatalog.ResolvePortableTargetPath(Find("zcodeProject"), Placement("zcodeProject")));
        }

        [Test]
        public void Snippet_WithoutPlacementKeepsTheAbsoluteForm()
        {
            var snippet = McpClientCatalog.BuildSnippet(Find("cursor-project"), Project, 21234, "npx", Args);
            StringAssert.Contains(Project, snippet);
            StringAssert.Contains("UNITY_OPEN_MCP_BRIDGE_PORT", snippet);
        }

        // ---- wrapper script ------------------------------------------------

        [Test]
        public void WrapperScript_RendersTheMonorepoLayout()
        {
            var body = McpWrapperScript.Render("1.3.0", "scripts/mcp/unity-open-mcp.sh", "Client");
            StringAssert.Contains("workspace_root=\"$(cd \"${script_dir}/../..\" && pwd)\"", body);
            StringAssert.Contains("subpath=\"${UNITY_SUBPATH-Client}\"", body);
            StringAssert.Contains("for marker in Assets Packages ProjectSettings; do", body);
            StringAssert.Contains("exec npx -y \"unity-open-mcp@1.3.0\" \"$@\"", body);
            StringAssert.DoesNotContain("__", body.Replace("${BASH_SOURCE[0]}", ""));
        }

        [Test]
        public void WrapperScript_UnityRootClimbsOneLevel()
        {
            var body = McpWrapperScript.Render("1.3.0", ".unity-open-mcp/mcp-wrapper.sh", "");
            StringAssert.Contains("\"${script_dir}/..\"", body);
            StringAssert.Contains("subpath=\"${UNITY_SUBPATH-}\"", body);
        }

        [Test]
        public void WrapperScript_AlwaysUsesLfLineEndings()
        {
            // The template is a verbatim literal: a CRLF checkout of this
            // source file must not produce a script bash cannot run.
            var body = McpWrapperScript.Render("1.3.0", "scripts/mcp/unity-open-mcp.sh", "Client");
            StringAssert.DoesNotContain("\r", body);
            StringAssert.StartsWith("#!/usr/bin/env bash\n", body);
        }

        [Test]
        public void WrapperScript_PinsTheBridgeVersion()
        {
            StringAssert.EndsWith(McpWrapperScript.PinnedVersion(), BridgeConstants.NpmPackage);
            StringAssert.DoesNotContain("@", McpWrapperScript.PinnedVersion());
        }

        [Test]
        public void WrapperScript_TemplateMirrorsTheServerTemplate()
        {
            var path = FindRepoFile("mcp-server/templates/mcp-wrapper.sh", 6);
            if (path == null)
            {
                Assert.Ignore("No mcp-server/templates/mcp-wrapper.sh above this project — no template to mirror.");
            }
            Assert.AreEqual(
                File.ReadAllText(path).Replace("\r\n", "\n"),
                McpWrapperScript.Template.Replace("\r\n", "\n"),
                "McpWrapperScript.Template must be a byte copy of mcp-server/templates/mcp-wrapper.sh.");
        }

        // ---- helpers -------------------------------------------------------

        private static void AssertNoMachineValues(string snippet)
        {
            StringAssert.DoesNotContain(Repo, snippet);
            StringAssert.DoesNotContain("UNITY_OPEN_MCP_BRIDGE_PORT", snippet);
        }

        private static string Snippet(string id)
        {
            return McpClientCatalog.BuildSnippet(Find(id), Project, 21234, "npx", Args, Placement(id));
        }

        private static McpClientCatalog.PortablePlacement Placement(string id)
        {
            var placement = McpClientCatalog.ResolvePortablePlacement(Find(id), Project, Repo);
            Assert.IsTrue(placement.HasValue, $"'{id}' should have a portable placement.");
            return placement.Value;
        }

        private static PortableStrategy StrategyOf(string id)
        {
            return McpClientCatalog.PortableStrategyFor(Find(id));
        }

        private static McpClientCatalog.ClientEntry Find(string id)
        {
            foreach (var c in McpClientCatalog.Clients)
            {
                if (c.Id == id) return c;
            }
            Assert.Fail($"Catalog has no client with id '{id}'.");
            return default;
        }

        private static string FindRepoFile(string relative, int maxLevels)
        {
            var dir = new DirectoryInfo(UnityEngine.Application.dataPath);
            for (var i = 0; i <= maxLevels && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
