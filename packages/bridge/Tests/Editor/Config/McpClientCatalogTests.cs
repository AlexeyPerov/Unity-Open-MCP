using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.Config;

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

        // ---- catalog shape (guards for the actions added this plan) ------

        [Test]
        public void FileBackedClients_HavePathTemplate()
        {
            // The Copy path / Open target actions only render when a path
            // resolves; every file-backed client must carry a template so
            // the buttons are not silently disabled for a Tier A client.
            foreach (var client in McpClientCatalog.Clients)
            {
                if (client.IsFileBacked)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(client.PathTemplate),
                        $"File-backed client '{client.Id}' must declare a PathTemplate.");
                }
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

        // ---- helper ------------------------------------------------------

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
