using System;
using System.Collections.Generic;
using System.IO;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge.Update
{
    /// <summary>
    /// Collects the files that can carry a Unity Open MCP version pin for one
    /// project: MCP client configs (project-scoped and <c>$HOME</c>-scoped) and
    /// the agent-facing prose that documents the launch command.
    ///
    /// The scan is deliberately <b>path-directed, not recursive</b>. Every
    /// candidate is derived from a known layout — the client catalog's path
    /// templates walked up from the Unity project, the same walk for a short
    /// allowlist of prose filenames, and the per-client skill file location.
    /// Two reasons:
    ///
    ///   • A Unity project is the worst possible tree to walk: <c>Assets/</c>
    ///     and <c>Library/</c> dwarf everything a pin could hide in.
    ///   • Recursion would sweep up copies that are not this project's — most
    ///     concretely a git worktree under <c>.claude/worktrees/&lt;name&gt;/</c>,
    ///     which is a separate checkout with its own bridge and its own
    ///     upgrade decision. Not descending is how those stay untouched.
    ///
    /// Prose is in scope because agents read it: a project's <c>MCP.md</c> or
    /// a copied <c>SKILL.md</c> that still says <c>unity-open-mcp@1.0.0</c>
    /// will have an agent launch that server, no matter what the config says.
    ///
    /// Collect() only reports files that exist; it never opens or writes them.
    /// Deciding whether a config's entry belongs to this project is
    /// <see cref="UpgradeEntryScope"/>, and rewriting is
    /// <see cref="VersionPinRewriter"/>.
    /// </summary>
    internal static class UpgradeScanner
    {
        internal enum CandidateKind
        {
            /// <summary>Client config inside the project or one of its ancestors.</summary>
            ProjectConfig,
            /// <summary>Machine-wide client config under <c>$HOME</c>.</summary>
            HomeConfig,
            /// <summary>Agent-facing markdown / example file.</summary>
            Prose,
        }

        internal readonly struct Candidate
        {
            public readonly string Path;
            public readonly CandidateKind Kind;
            /// <summary>Catalog id of the client this file configures;
            /// <c>null</c> for prose.</summary>
            public readonly string ClientId;

            public Candidate(string path, CandidateKind kind, string clientId)
            {
                Path = path;
                Kind = kind;
                ClientId = clientId;
            }

            /// <summary>Home-scoped files are shared across every project on
            /// the machine, which is what makes entry scoping mandatory before
            /// a rewrite (see <see cref="UpgradeEntryScope.ShouldRewrite"/>).</summary>
            public bool IsHomeScoped => Kind == CandidateKind.HomeConfig;
        }

        internal readonly struct ScanOptions
        {
            public readonly bool ProjectConfigs;
            public readonly bool HomeConfigs;
            public readonly bool Prose;
            /// <summary>Home directory override. Exists so the scan is
            /// testable against a fixture tree; production leaves it null.</summary>
            public readonly string HomeOverride;
            public readonly int MaxAncestorLevels;

            public ScanOptions(
                bool projectConfigs,
                bool homeConfigs,
                bool prose,
                string homeOverride = null,
                int maxAncestorLevels = McpClientCatalog.MaxAncestorLevels)
            {
                ProjectConfigs = projectConfigs;
                HomeConfigs = homeConfigs;
                Prose = prose;
                HomeOverride = homeOverride;
                MaxAncestorLevels = maxAncestorLevels;
            }

            public static ScanOptions All => new ScanOptions(true, true, true);
        }

        /// <summary>
        /// Agent-facing documents that carry a launch command in prose. Kept to
        /// an explicit allowlist rather than "every .md": a changelog or review
        /// note that mentions an old version is a record, not a stale pin, and
        /// rewriting it would falsify history.
        /// </summary>
        private static readonly string[] ProseFileNames =
        {
            "MCP.md",
            "CLAUDE.md",
            "AGENTS.md",
        };

        /// <summary>
        /// Agent home directories that can hold an installed copy of the core
        /// skill. Source of truth for the mapping is
        /// <c>skills/client-paths.json</c> (consumed by the Hub and
        /// <c>generate_skill</c>); this list mirrors its client directories so
        /// the bridge can find installed copies without shipping that file.
        /// </summary>
        private static readonly string[] SkillClientDirs =
        {
            ".cursor", ".claude", ".opencode", ".agents", ".cline", ".gemini",
            ".kilocode", ".roo", ".agent", ".junie", ".vscode", ".vs", ".github",
        };

        private const string SkillRelativePath = "skills/unity-open-mcp/SKILL.md";

        /// <summary>Suffix of a committed sample config (e.g.
        /// <c>.cursor/mcp.json.example</c>) — not read by any client, but
        /// copied by humans and agents, so a stale pin there propagates.</summary>
        private const string ExampleSuffix = ".example";

        /// <summary>
        /// Every existing file that could carry a pin for this project, nearest
        /// first: project configs, then home configs, then prose.
        /// </summary>
        internal static List<Candidate> Collect(string projectPath, ScanOptions options)
        {
            var found = new List<Candidate>();
            if (string.IsNullOrEmpty(projectPath)) return found;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = McpClientCatalog.ResolveSearchDirectories(
                projectPath, options.HomeOverride, options.MaxAncestorLevels);

            if (options.ProjectConfigs)
            {
                foreach (var client in McpClientCatalog.Clients)
                {
                    if (!client.IsFileBacked) continue;
                    if (client.ScopeKind != McpClientCatalog.Scope.Project) continue;
                    foreach (var path in McpClientCatalog.ResolveSearchPaths(
                                 client, projectPath, options.HomeOverride, options.MaxAncestorLevels))
                    {
                        Add(found, seen, path, CandidateKind.ProjectConfig, client.Id);
                    }
                }
            }

            if (options.HomeConfigs)
            {
                foreach (var client in McpClientCatalog.Clients)
                {
                    if (!client.IsFileBacked) continue;
                    if (client.ScopeKind != McpClientCatalog.Scope.Global) continue;
                    var path = McpClientCatalog.ResolveDisplayPath(
                        client, projectPath, options.HomeOverride);
                    Add(found, seen, path, CandidateKind.HomeConfig, client.Id);
                }
            }

            if (options.Prose)
            {
                foreach (var dir in dirs)
                {
                    foreach (var name in ProseFileNames)
                    {
                        Add(found, seen, Combine(dir, name), CandidateKind.Prose, null);
                    }
                    foreach (var skillDir in SkillClientDirs)
                    {
                        Add(found, seen, Combine(dir, skillDir + "/" + SkillRelativePath),
                            CandidateKind.Prose, null);
                    }
                }

                // `<client config>.example` sits next to the config it samples,
                // so it rides the same catalog templates rather than a second
                // path list. Resolved even when the real config is absent — a
                // repository often ships only the example.
                foreach (var client in McpClientCatalog.Clients)
                {
                    if (!client.IsFileBacked) continue;
                    if (client.ScopeKind != McpClientCatalog.Scope.Project) continue;
                    foreach (var path in McpClientCatalog.ResolveSearchPaths(
                                 client, projectPath, options.HomeOverride, options.MaxAncestorLevels))
                    {
                        Add(found, seen, path + ExampleSuffix, CandidateKind.Prose, null);
                    }
                }
            }

            return found;
        }

        // One file can be the target of several clients (`.mcp.json` is shared
        // by more than one catalog row); the first claim wins so the report
        // never lists the same path twice.
        private static void Add(
            List<Candidate> found, HashSet<string> seen, string path, CandidateKind kind, string clientId)
        {
            if (string.IsNullOrEmpty(path)) return;
            var normalized = path.Replace('\\', '/');
            if (!seen.Add(normalized)) return;
            if (!FileExists(normalized)) return;
            found.Add(new Candidate(normalized, kind, clientId));
        }

        // A candidate path is user-controlled (it can be long, malformed, or on
        // an unreadable volume); File.Exists swallows most of that, but the
        // argument-shape exceptions still escape, and a bad path must never
        // take down the scan.
        private static bool FileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Combine(string dir, string relative)
        {
            return Path.Combine(dir, relative).Replace('\\', '/');
        }
    }
}
