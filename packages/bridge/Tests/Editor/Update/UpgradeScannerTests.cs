using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge.Config;
using UnityOpenMcpBridge.Update;
using CandidateKind = UnityOpenMcpBridge.Update.UpgradeScanner.CandidateKind;
using ScanOptions = UnityOpenMcpBridge.Update.UpgradeScanner.ScanOptions;

namespace UnityOpenMcpBridge.Tests
{
    // M32 Plan 4 / T32.4.2 — pins which files the project updater will offer to
    // rewrite. The fixture reproduces the layout that motivated the feature: a
    // Unity project nested in a repository (`<repo>/Client`), agent configs at
    // the repository root, a machine-wide config in $HOME, agent-facing prose,
    // and a git worktree that must stay out of the result.
    [TestFixture]
    public class UpgradeScannerTests
    {
        private string _root;
        private string _home;
        private string _repo;
        private string _project;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uom-scan-" + Path.GetRandomFileName())
                .Replace('\\', '/');
            _home = _root + "/home";
            _repo = _home + "/repo";
            _project = _repo + "/Client";

            // The Unity project itself.
            Directory.CreateDirectory(_project + "/Assets");

            // Project-scoped client config, one level above the Unity folder —
            // the normal layout for a repo whose Unity project is in Client/.
            Write(_repo + "/.cursor/mcp.json", "{\"mcpServers\":{\"unity-open-mcp\":{}}}");
            // Committed sample next to it: not read by any client, but copied.
            Write(_repo + "/.cursor/mcp.json.example", "unity-open-mcp@1.0.0");
            // Agent-facing prose.
            Write(_repo + "/MCP.md", "npx -y unity-open-mcp@1.0.0");
            Write(_repo + "/.claude/skills/unity-open-mcp/SKILL.md", "unity-open-mcp@1.0.0");
            // A git worktree: a separate checkout with its own bridge.
            Write(_repo + "/.claude/worktrees/wt/MCP.md", "unity-open-mcp@0.9.0");

            // The UPM pins — the OTHER half of a version move. These live in
            // the Unity project itself, never in an ancestor.
            Write(_project + "/Packages/manifest.json",
                "{\"dependencies\":{\"com.alexeyperov.unity-open-mcp-bridge\":" +
                "\"https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.0.0\"}}");
            Write(_project + "/Packages/packages-lock.json",
                "{\"dependencies\":{\"com.alexeyperov.unity-open-mcp-bridge\":{\"version\":\"1.0.0\"}}}");

            // Machine-wide configs.
            Write(_home + "/.cursor/mcp.json", "{\"mcpServers\":{\"unity-open-mcp\":{}}}");
            Write(McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.ClaudeDesktop, _home),
                "{\"mcpServers\":{\"unity-open-mcp\":{}}}");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static void Write(string path, string body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, body);
        }

        private string[] Paths(ScanOptions options)
        {
            return UpgradeScanner.Collect(_project, options).Select(c => c.Path).ToArray();
        }

        private ScanOptions All => new ScanOptions(true, true, true, true, _home);

        // ---- What is collected --------------------------------------------

        [Test]
        public void Collect_FindsProjectConfigInAnAncestorDirectory()
        {
            var hit = UpgradeScanner.Collect(_project, All)
                .FirstOrDefault(c => c.Path == _repo + "/.cursor/mcp.json");

            Assert.AreEqual(_repo + "/.cursor/mcp.json", hit.Path);
            Assert.AreEqual(CandidateKind.ProjectConfig, hit.Kind);
            Assert.AreEqual("cursor-project", hit.ClientId);
            Assert.IsFalse(hit.IsHomeScoped);
        }

        [Test]
        public void Collect_FindsHomeScopedConfigs()
        {
            var candidates = UpgradeScanner.Collect(_project, All)
                .Where(c => c.Kind == CandidateKind.HomeConfig)
                .Select(c => c.Path)
                .ToArray();

            CollectionAssert.Contains(candidates, _home + "/.cursor/mcp.json");
            CollectionAssert.Contains(
                candidates,
                McpClientCatalog.ResolveOsPath(McpClientCatalog.OsPath.ClaudeDesktop, _home),
                "Claude Desktop's OS-specific config must be reachable, not just $HOME templates.");
            Assert.IsTrue(
                UpgradeScanner.Collect(_project, All)
                    .Where(c => c.Kind == CandidateKind.HomeConfig)
                    .All(c => c.IsHomeScoped));
        }

        [Test]
        public void Collect_FindsProseAndExampleFiles()
        {
            var prose = UpgradeScanner.Collect(_project, All)
                .Where(c => c.Kind == CandidateKind.Prose)
                .Select(c => c.Path)
                .ToArray();

            CollectionAssert.Contains(prose, _repo + "/MCP.md");
            CollectionAssert.Contains(prose, _repo + "/.claude/skills/unity-open-mcp/SKILL.md");
            CollectionAssert.Contains(prose, _repo + "/.cursor/mcp.json.example");
        }

        [Test]
        public void Collect_FindsTheProjectsUpmManifestAndLock()
        {
            // Without these, two of the three pin families VersionPinRewriter
            // can move (the #bridge-v / #verify-v git tags and the lock's
            // bridge→verify dependency) have no source file: an "upgrade"
            // moves the npm pin and silently leaves the Editor on the bridge
            // version it already had.
            var upm = UpgradeScanner.Collect(_project, All)
                .Where(c => c.Kind == CandidateKind.UpmManifest)
                .Select(c => c.Path)
                .ToArray();

            CollectionAssert.Contains(upm, _project + "/Packages/manifest.json");
            CollectionAssert.Contains(upm, _project + "/Packages/packages-lock.json");
            Assert.IsTrue(
                UpgradeScanner.Collect(_project, All)
                    .Where(c => c.Kind == CandidateKind.UpmManifest)
                    .All(c => !c.IsHomeScoped),
                "the UPM files are project-scoped — Unknown ownership must still rewrite");
        }

        [Test]
        public void Collect_DoesNotWalkUpForUpmManifests()
        {
            // Packages/ belongs to exactly ONE Unity project, so an ancestor's
            // copy (another project in the same repo) is not ours to move.
            Write(_repo + "/Packages/manifest.json", "{\"dependencies\":{}}");

            CollectionAssert.DoesNotContain(Paths(All), _repo + "/Packages/manifest.json");
        }

        [Test]
        public void Collect_UpmManifestsToggleOff()
        {
            var kinds = UpgradeScanner.Collect(_project, new ScanOptions(true, true, true, false, _home))
                .Select(c => c.Kind)
                .Distinct()
                .ToArray();

            CollectionAssert.DoesNotContain(kinds, CandidateKind.UpmManifest);
        }

        [Test]
        public void Collect_SkipsGitWorktreeCopies()
        {
            // Not a prune rule but a consequence of the scan being
            // path-directed: nothing enumerates a worktree, so its own
            // MCP.md stays that checkout's problem.
            CollectionAssert.DoesNotContain(Paths(All), _repo + "/.claude/worktrees/wt/MCP.md");
        }

        [Test]
        public void Collect_ReturnsOnlyExistingFiles()
        {
            Assert.IsTrue(Paths(All).All(File.Exists));
        }

        [Test]
        public void Collect_NeverRepeatsAPath()
        {
            var paths = Paths(All);
            Assert.AreEqual(paths.Length, paths.Distinct().Count());
        }

        // ---- Category toggles ----------------------------------------------

        [Test]
        public void Collect_ProseOnly_LeavesConfigsAlone()
        {
            var kinds = UpgradeScanner.Collect(_project, new ScanOptions(false, false, true, false, _home))
                .Select(c => c.Kind)
                .Distinct()
                .ToArray();

            Assert.AreEqual(new[] { CandidateKind.Prose }, kinds);
        }

        [Test]
        public void Collect_ProjectConfigsOnly_ExcludesHomeConfigs()
        {
            var paths = Paths(new ScanOptions(true, false, false, false, _home));

            CollectionAssert.Contains(paths, _repo + "/.cursor/mcp.json");
            CollectionAssert.DoesNotContain(paths, _home + "/.cursor/mcp.json");
        }

        [Test]
        public void Collect_HomeConfigsOnly_ExcludesProjectConfigs()
        {
            var paths = Paths(new ScanOptions(false, true, false, false, _home));

            CollectionAssert.Contains(paths, _home + "/.cursor/mcp.json");
            CollectionAssert.DoesNotContain(paths, _repo + "/.cursor/mcp.json");
        }

        // ---- Boundaries -----------------------------------------------------

        [Test]
        public void Collect_WalkUpStopsBeforeTheHomeDirectory()
        {
            // $HOME/.cursor/mcp.json is a global-scope row of its own; reaching
            // it through the project walk-up would report it twice and, worse,
            // treat a machine-wide file as a project config.
            var projectScoped = UpgradeScanner.Collect(_project, All)
                .Where(c => c.Kind == CandidateKind.ProjectConfig)
                .Select(c => c.Path);

            CollectionAssert.DoesNotContain(projectScoped, _home + "/.cursor/mcp.json");
        }

        [Test]
        public void Collect_EmptyProjectPath_IsEmpty()
        {
            Assert.IsEmpty(UpgradeScanner.Collect("", All));
            Assert.IsEmpty(UpgradeScanner.Collect(null, All));
        }

        [Test]
        public void Collect_ProjectWithNothingInstalled_IsEmpty()
        {
            var bare = _root + "/bare/Client";
            Directory.CreateDirectory(bare + "/Assets");

            Assert.IsEmpty(UpgradeScanner.Collect(bare, new ScanOptions(true, false, true, true, _home)));
        }
    }
}
