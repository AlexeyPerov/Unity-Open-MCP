using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.Config;
using Envelope = UnityOpenMcpBridge.Config.McpClientCatalog.Envelope;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 4 — pins the pure, filesystem-free slice of the configure-client
    // panel: the "is the target file already configured?" content check and the
    // target-path display resolution. The IMGUI panel itself cannot render in
    // EditMode, but the policy it consults every repaint is pure, so we pin it
    // here to catch drift in the matching / path logic without a live Editor.
    //
    // Scope:
    //   - IsConfiguredEntry(envelope, body) — the substring match the panel
    //     runs against the target file's contents.
    //   - ResolveDisplayPath(client, project) — how the panel renders the
    //     target path for a project-scoped vs global vs CLI-only client.
    //   - ResolveSearchPaths(client, project, home, levels) — the ordered
    //     candidate list the configured check walks, including the ancestors of
    //     a Unity project nested inside a larger repository.
    // The full snippet bytes are already covered by the Hub parity checks;
    // these tests focus on the detection + display surface this plan added.
    [TestFixture]
    public class McpClientCatalogTests
    {
        // ---- IsConfiguredEntry -------------------------------------------

        [Test]
        public void IsConfiguredEntry_Json_WithServerKey_IsTrue()
        {
            var body = "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": {\n      \"command\": \"npx\"\n    }\n  }\n}";
            Assert.IsTrue(McpClientCatalog.IsConfiguredEntry(Envelope.McpServersStdio, body));
        }

        [Test]
        public void IsConfiguredEntry_Json_WithoutServerKey_IsFalse()
        {
            var body = "{\n  \"mcpServers\": {\n    \"other-server\": {}\n  }\n}";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(Envelope.McpServersStdio, body));
        }

        [Test]
        public void IsConfiguredEntry_Codex_TomlTableHeader_IsTrue()
        {
            // Codex writes a full TOML table block; the header is the stable
            // marker the detection looks for.
            var body = "[mcp_servers.unity-open-mcp]\nenabled = true\ncommand = \"npx\"\n";
            Assert.IsTrue(McpClientCatalog.IsConfiguredEntry(Envelope.Codex, body));
        }

        [Test]
        public void IsConfiguredEntry_Codex_WithoutTableHeader_IsFalse()
        {
            var body = "[mcp_servers.other]\nenabled = true\n";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(Envelope.Codex, body));
        }

        [Test]
        public void IsConfiguredEntry_CliOnly_NeverConfiguredFromContent()
        {
            // CLI-only (Claude Code) has no target file — content scan must
            // never report configured even if the body mentions the server.
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.CliOnly, "[unity-open-mcp] whatever"));
        }

        [Test]
        public void IsConfiguredEntry_Manual_NeverConfiguredFromContent()
        {
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.Manual, "anything"));
        }

        [Test]
        public void IsConfiguredEntry_EmptyOrNullBody_IsFalse()
        {
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(Envelope.McpServersStdio, ""));
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(Envelope.McpServersStdio, null));
        }

        // ---- IsConfiguredEntry — merge-key gating (wrong-section guard) -----
        //
        // When the client's merge key is supplied, the body must contain BOTH
        // the merge key and the server key. This catches the common config bug
        // of pasting the entry under a section the client does not read (e.g.
        // `servers` for a client that reads `mcpServers`) — without this gate
        // the panel would report "configured" and silence troubleshooting.

        [Test]
        public void IsConfiguredEntry_WithMergeKey_BothPresent_IsTrue()
        {
            var body = "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": {}\n  }\n}";
            Assert.IsTrue(McpClientCatalog.IsConfiguredEntry(
                Envelope.McpServersStdio, body, "mcpServers"));
        }

        [Test]
        public void IsConfiguredEntry_WithMergeKey_ServerKeyUnderWrongSection_IsFalse()
        {
            // Server key present, but under `servers` — the client reads
            // `mcpServers`, so it will never see this entry. Must NOT report
            // configured.
            var body = "{\n  \"servers\": {\n    \"unity-open-mcp\": {}\n  }\n}";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.McpServersStdio, body, "mcpServers"));
        }

        [Test]
        public void IsConfiguredEntry_WithMergeKey_MissingServerKey_IsFalse()
        {
            var body = "{\n  \"mcpServers\": {\n    \"other\": {}\n  }\n}";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.McpServersStdio, body, "mcpServers"));
        }

        [Test]
        public void IsConfiguredEntry_NullMergeKey_FallsBackToServerKeyOnly()
        {
            // Backward-compatible 2-arg behavior: no merge key → server key
            // substring suffices (the wizard writes the key verbatim).
            var body = "{\n  \"servers\": {\n    \"unity-open-mcp\": {}\n  }\n}";
            Assert.IsTrue(McpClientCatalog.IsConfiguredEntry(
                Envelope.McpServersStdio, body, null));
        }

        // ---- ResolveDisplayPath ------------------------------------------

        [Test]
        public void ResolveDisplayPath_ProjectScope_CombinesProjectAndTemplate()
        {
            var client = FindById("cursor-project");
            var path = McpClientCatalog.ResolveDisplayPath(client, "/proj");
            // Forward-slash normalized so it renders consistently on Windows.
            Assert.AreEqual("/proj/.cursor/mcp.json", path.Replace('\\', '/'));
        }

        [Test]
        public void ResolveDisplayPath_GlobalScope_ResolvesHomeAndKeepsAbsolute()
        {
            var client = FindById("cursor");
            var path = McpClientCatalog.ResolveDisplayPath(client, "/proj");
            // $HOME must be substituted; the result is independent of the
            // project path because this is a global-scope client.
            Assert.IsFalse(path.Contains("$HOME"), "$HOME should be resolved.");
            Assert.IsTrue(path.EndsWith("/.cursor/mcp.json"));
        }

        [Test]
        public void ResolveDisplayPath_CliOnly_IsNull()
        {
            var client = FindById("claudeCode");
            Assert.IsNull(McpClientCatalog.ResolveDisplayPath(client, "/proj"));
        }

        [Test]
        public void ResolveDisplayPath_Manual_IsNull()
        {
            var client = FindById("manual");
            Assert.IsNull(McpClientCatalog.ResolveDisplayPath(client, "/proj"));
        }

        [Test]
        public void IsConfiguredEntry_DottedMergeKey_MatchesNestedSections()
        {
            // ZCode nests the server under `mcp` → `servers`. The dotted key
            // never appears verbatim in a config file, so it has to be matched
            // segment by segment or the client could never report configured.
            var body =
                "{\n  \"mcp\": {\n    \"servers\": {\n      \"unity-open-mcp\": {}\n    }\n  }\n}";
            Assert.IsTrue(McpClientCatalog.IsConfiguredEntry(
                Envelope.ZcodeStdio, body, "mcp.servers"));
        }

        [Test]
        public void IsConfiguredEntry_DottedMergeKey_MissingSegmentIsFalse()
        {
            // Server key present but not under `mcp` — the client will not read
            // it, so this must stay a negative.
            var body = "{\n  \"servers\": {\n    \"unity-open-mcp\": {}\n  }\n}";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.ZcodeStdio, body, "mcp.servers"));
        }

        [Test]
        public void IsConfiguredEntry_MergeKeyIsMatchedAsQuotedKey()
        {
            // `servers` must not match inside `"mcpServers"` — a VS Code Copilot
            // entry pasted under Cursor's section is exactly the wrong-section
            // bug the merge-key gate exists to catch.
            var body = "{\n  \"mcpServers\": {\n    \"unity-open-mcp\": {}\n  }\n}";
            Assert.IsFalse(McpClientCatalog.IsConfiguredEntry(
                Envelope.McpServersStdio, body, "servers"));
        }

        // ---- ResolveSearchPaths (walk-up candidates) ---------------------
        //
        // A Unity project is frequently a subfolder of the repository holding
        // the agent config (`<repo>/Client`). The panel therefore searches the
        // project folder AND its ancestors; these pin the candidate list, its
        // order, and the two boundaries (level cap, home directory).

        [Test]
        public void ResolveSearchPaths_ProjectScope_ProjectFirstThenAncestors()
        {
            var client = FindById("cursor-project");
            var paths = McpClientCatalog.ResolveSearchPaths(
                client, "/Users/dev/repo/Client", "/Users/dev", 4);
            // Nearest first, stopping before the home directory (/Users/dev).
            Assert.AreEqual(
                new[] { "/Users/dev/repo/Client/.cursor/mcp.json", "/Users/dev/repo/.cursor/mcp.json" },
                Normalize(paths));
        }

        [Test]
        public void ResolveSearchPaths_ProjectScope_StopsBeforeHomeDirectory()
        {
            // A project directly in $HOME has no ancestor worth searching:
            // $HOME/.cursor/mcp.json is the separate global-scope row, and
            // treating it as a project config would report every project on the
            // machine as configured.
            var client = FindById("cursor-project");
            var paths = McpClientCatalog.ResolveSearchPaths(
                client, "/Users/dev/Game", "/Users/dev", 4);
            Assert.AreEqual(new[] { "/Users/dev/Game/.cursor/mcp.json" }, Normalize(paths));
        }

        [Test]
        public void ResolveSearchPaths_ProjectScope_HonorsAncestorLevelCap()
        {
            var client = FindById("codex");
            var paths = McpClientCatalog.ResolveSearchPaths(
                client, "/a/b/c/d/e/Client", "/nowhere", 2);
            // Project + 2 ancestors = 3 candidates, nearest first.
            Assert.AreEqual(
                new[]
                {
                    "/a/b/c/d/e/Client/.codex/config.toml",
                    "/a/b/c/d/e/.codex/config.toml",
                    "/a/b/c/d/.codex/config.toml",
                },
                Normalize(paths));
        }

        [Test]
        public void ResolveSearchPaths_ProjectScope_StopsAtFilesystemRoot()
        {
            var client = FindById("cursor-project");
            var paths = McpClientCatalog.ResolveSearchPaths(client, "/Client", "/nowhere", 4);
            Assert.AreEqual(new[] { "/Client/.cursor/mcp.json" }, Normalize(paths));
        }

        [Test]
        public void ResolveSearchPaths_ProjectScope_TrailingSeparatorIsIgnored()
        {
            var client = FindById("cursor-project");
            var paths = McpClientCatalog.ResolveSearchPaths(
                client, "/Users/dev/repo/Client/", "/Users/dev", 4);
            Assert.AreEqual(
                new[] { "/Users/dev/repo/Client/.cursor/mcp.json", "/Users/dev/repo/.cursor/mcp.json" },
                Normalize(paths));
        }

        [Test]
        public void ResolveSearchPaths_GlobalScope_IsSingleAbsolutePath()
        {
            // Global clients already point at one machine-wide file — walking up
            // from the project would be meaningless.
            var client = FindById("cursor");
            var paths = McpClientCatalog.ResolveSearchPaths(client, "/proj", "/Users/dev", 4);
            Assert.AreEqual(1, paths.Length);
            Assert.IsTrue(paths[0].Replace('\\', '/').EndsWith("/.cursor/mcp.json"));
        }

        [Test]
        public void ResolveSearchPaths_CliAndManual_AreEmpty()
        {
            Assert.IsEmpty(McpClientCatalog.ResolveSearchPaths(
                FindById("claudeCode"), "/proj", "/Users/dev", 4));
            Assert.IsEmpty(McpClientCatalog.ResolveSearchPaths(
                FindById("manual"), "/proj", "/Users/dev", 4));
        }

        [Test]
        public void ResolveSearchPaths_EmptyProjectPath_IsEmpty()
        {
            var client = FindById("cursor-project");
            Assert.IsEmpty(McpClientCatalog.ResolveSearchPaths(client, "", "/Users/dev", 4));
            Assert.IsEmpty(McpClientCatalog.ResolveSearchPaths(client, null, "/Users/dev", 4));
        }

        [Test]
        public void ResolveSearchPaths_FirstCandidateMatchesResolveDisplayPath()
        {
            // The nearest candidate is the write target the panel renders, so
            // the two must not drift apart for any project-scoped client.
            foreach (var client in McpClientCatalog.Clients)
            {
                if (client.ScopeKind != McpClientCatalog.Scope.Project) continue;
                var paths = McpClientCatalog.ResolveSearchPaths(client, "/repo/Client", "/nowhere", 4);
                Assert.AreEqual(
                    McpClientCatalog.ResolveDisplayPath(client, "/repo/Client"),
                    paths[0],
                    $"Client '{client.Id}' search list must start at its display path.");
            }
        }

        // ---- catalog shape (guards for the actions added this plan) ------

        [Test]
        public void FileBackedClients_HavePathTemplate()
        {
            // The Copy path / Open target actions only render when a path
            // resolves; every file-backed client must therefore resolve to one
            // — either from a $HOME/project template or, for the clients whose
            // location is OS-specific, from the OsPath resolver. Claude Desktop
            // and Cline are the second kind: before M32 Plan 4 they had neither
            // and their buttons were silently disabled.
            foreach (var client in McpClientCatalog.Clients)
            {
                if (!client.IsFileBacked) continue;
                Assert.IsTrue(
                    !string.IsNullOrEmpty(client.PathTemplate)
                    || client.OsPathKind != McpClientCatalog.OsPath.None,
                    $"File-backed client '{client.Id}' must declare a PathTemplate or an OsPath.");
                Assert.IsFalse(
                    string.IsNullOrEmpty(McpClientCatalog.ResolveDisplayPath(client, "/proj", "/Users/dev")),
                    $"File-backed client '{client.Id}' must resolve to a target path.");
            }
        }

        [Test]
        public void CliAndManualClients_AreNotFileBacked()
        {
            foreach (var client in McpClientCatalog.Clients)
            {
                if (client.EnvelopeKind == Envelope.CliOnly || client.EnvelopeKind == Envelope.Manual)
                {
                    Assert.IsFalse(client.IsFileBacked,
                        $"Client '{client.Id}' must not be file-backed.");
                }
            }
        }

        // ---- ResolveSearchDirectories (shared walk-up policy) ------------
        //
        // M32 Plan 4 extracted the walk from ResolveSearchPaths so the project
        // updater's prose scan visits exactly the directories a client config
        // could live in. These pin that the two stayed one policy.

        [Test]
        public void ResolveSearchDirectories_ProjectFirstThenAncestors()
        {
            var dirs = McpClientCatalog.ResolveSearchDirectories(
                "/Users/dev/repo/Client", "/Users/dev", 4);

            Assert.AreEqual(new[] { "/Users/dev/repo/Client", "/Users/dev/repo" }, Normalize(dirs));
        }

        [Test]
        public void ResolveSearchDirectories_AgreesWithResolveSearchPaths()
        {
            var client = FindById("cursor-project");
            var dirs = McpClientCatalog.ResolveSearchDirectories("/Users/dev/a/b/Client", "/Users/dev", 4);
            var paths = Normalize(McpClientCatalog.ResolveSearchPaths(
                client, "/Users/dev/a/b/Client", "/Users/dev", 4));

            Assert.AreEqual(dirs.Length, paths.Length);
            for (var i = 0; i < dirs.Length; i++)
            {
                Assert.AreEqual(dirs[i].Replace('\\', '/') + "/" + client.PathTemplate, paths[i]);
            }
        }

        [Test]
        public void ResolveSearchDirectories_EmptyProjectPath_IsEmpty()
        {
            Assert.IsEmpty(McpClientCatalog.ResolveSearchDirectories("", "/Users/dev", 4));
        }

        // ---- OS-specific global paths ------------------------------------
        //
        // Claude Desktop and Cline have no single $HOME-relative template; the
        // Hub wizard resolves them per platform and the bridge has to agree, or
        // "configured" detection and the updater would target different files.

        [Test]
        public void ResolveOsPath_ClaudeDesktop_IsUnderTheGivenHome()
        {
            var path = McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.ClaudeDesktop, "/Users/dev");

            Assert.IsTrue(path.StartsWith("/Users/dev/"), $"Unexpected root: {path}");
            Assert.IsTrue(path.EndsWith("/Claude/claude_desktop_config.json"), $"Unexpected tail: {path}");
        }

        [Test]
        public void ResolveOsPath_Cline_PointsAtTheExtensionSettings()
        {
            var path = McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.Cline, "/Users/dev");

            Assert.IsTrue(path.StartsWith("/Users/dev/"), $"Unexpected root: {path}");
            Assert.IsTrue(
                path.EndsWith("/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json"),
                $"Unexpected tail: {path}");
        }

        [Test]
        public void ResolveOsPath_None_IsNull()
        {
            Assert.IsNull(McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.None, "/Users/dev"));
        }

        [Test]
        public void ResolveDisplayPath_OsSpecificGlobalClient_ResolvesInsteadOfNull()
        {
            var path = McpClientCatalog.ResolveDisplayPath(FindById("claudeDesktop"), "/proj", "/Users/dev");

            Assert.IsNotNull(path, "Claude Desktop has an OS-resolved path, not a null template.");
            Assert.IsTrue(path.EndsWith("/claude_desktop_config.json"));
        }

        [Test]
        public void ResolveSearchPaths_OsSpecificGlobalClient_IsSingleAbsolutePath()
        {
            var paths = McpClientCatalog.ResolveSearchPaths(FindById("cline"), "/proj", "/Users/dev", 4);

            Assert.AreEqual(1, paths.Length);
            Assert.AreEqual(
                McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.Cline, "/Users/dev"), paths[0]);
        }

        // ---- helper ------------------------------------------------------

        /// <summary>Forward-slash the candidate list so assertions read the same
        /// on Windows, where Path.Combine emits backslashes.</summary>
        private static string[] Normalize(string[] paths)
        {
            var copy = new string[paths.Length];
            for (var i = 0; i < paths.Length; i++) copy[i] = paths[i].Replace('\\', '/');
            return copy;
        }

        private static McpClientCatalog.ClientEntry FindById(string id)
        {
            foreach (var c in McpClientCatalog.Clients)
            {
                if (c.Id == id) return c;
            }
            Assert.Fail($"Catalog has no client with id '{id}'.");
            return default;
        }
    }
}
