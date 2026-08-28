using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge.Update
{
    /// <summary>
    /// Decides whether the <c>unity-open-mcp</c> entry inside a config body
    /// configures THIS project.
    ///
    /// This is what makes rewriting a <c>$HOME</c>-scoped client config safe.
    /// A file like <c>~/.codex/config.toml</c> or
    /// <c>~/Library/Application Support/Claude/claude_desktop_config.json</c>
    /// is machine-wide: moving its npm pin moves it for every project that
    /// file launches. But the entries themselves are project-bound — the
    /// launch command carries <c>UNITY_PROJECT_PATH</c> (and the deterministic
    /// per-project bridge port), because one MCP server process talks to one
    /// Editor. So the safe unit is not the file, it is the entry.
    ///
    /// Policy the caller is expected to apply:
    ///   • <see cref="Ownership.Matched"/> — rewrite.
    ///   • <see cref="Ownership.OtherProject"/> — skip, and say which project
    ///     it points at, so the operator sees why it was left alone.
    ///   • <see cref="Ownership.Mixed"/> — this project AND another share the
    ///     file. Also a skip: a rewrite is whole-file, so moving our pin here
    ///     would move the other project's pin with it, silently upgrading a
    ///     project whose bridge may still be on the old version.
    ///   • <see cref="Ownership.Unknown"/> — no project marker at all. Skip in
    ///     <c>$HOME</c> (we cannot establish an owner for a shared file) and
    ///     rewrite in a project-scoped config, where the file's location is
    ///     itself the ownership claim.
    ///
    /// Pure string math over the raw body: JSON and TOML both spell the env
    /// pair as <c>KEY … "value"</c>, so one pattern covers both dialects
    /// without a parser.
    /// </summary>
    internal static class UpgradeEntryScope
    {
        internal enum Ownership
        {
            /// <summary>Every project marker in the file names this project.</summary>
            Matched,
            /// <summary>Entries exist, none of them name this project.</summary>
            OtherProject,
            /// <summary>This project and at least one other share the file.</summary>
            Mixed,
            /// <summary>No project marker found (no env, no matching port).</summary>
            Unknown,
        }

        internal readonly struct ScopeResult
        {
            public readonly Ownership Kind;
            /// <summary>Every project path the body claims, in first-seen
            /// order. Empty when the body carries no project env.</summary>
            public readonly string[] ProjectPaths;
            /// <summary>True when ownership was established from the
            /// deterministic port rather than the project path.</summary>
            public readonly bool ViaPort;
            /// <summary>For <see cref="Ownership.Mixed"/>: the first foreign
            /// project sharing this file. <c>null</c> otherwise.</summary>
            public readonly string ForeignProject;

            public ScopeResult(Ownership kind, string[] projectPaths, bool viaPort, string foreignProject = null)
            {
                Kind = kind;
                ProjectPaths = projectPaths ?? new string[0];
                ViaPort = viaPort;
                ForeignProject = foreignProject;
            }

            /// <summary>Short reason for the report when this body is skipped.</summary>
            public string SkipReason
            {
                get
                {
                    switch (Kind)
                    {
                        case Ownership.OtherProject:
                            return ProjectPaths.Length > 0
                                ? $"configures another project ({ProjectPaths[0]})"
                                : "configures another project";
                        case Ownership.Mixed:
                            return "also configures another project" +
                                   (ForeignProject != null ? $" ({ForeignProject})" : "") +
                                   " — a whole-file rewrite would move that pin too; edit this entry by hand";
                        case Ownership.Unknown:
                            return "no project marker in the entry";
                        default:
                            return "";
                    }
                }
            }
        }

        // `"UNITY_PROJECT_PATH": "/abs/path"` (JSON) and
        // `UNITY_PROJECT_PATH = "/abs/path"` (TOML) differ only in the quoting
        // of the key and the separator character.
        private static readonly Regex ProjectPathPattern = new Regex(
            "\"?" + Regex.Escape(BridgeConstants.ProjectPathEnvVar) + "\"?\\s*[:=]\\s*\"([^\"]*)\"");

        // The port may be a quoted string (Codex writes strings) or a bare
        // number (some JSON clients), so the quotes are optional on both sides.
        private static readonly Regex PortPattern = new Regex(
            "\"?" + Regex.Escape(BridgeConstants.PortEnvVar) + "\"?\\s*[:=]\\s*\"?(\\d+)\"?");

        /// <summary>
        /// Classify a config body against a Unity project path.
        /// </summary>
        internal static ScopeResult Classify(string body, string projectPath)
        {
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(projectPath))
            {
                return new ScopeResult(Ownership.Unknown, new string[0], false);
            }

            var target = NormalizeForCompare(projectPath);
            var claimed = new List<string>();
            var matched = false;
            string foreign = null;

            foreach (Match m in ProjectPathPattern.Matches(body))
            {
                var raw = m.Groups[1].Value;
                if (string.IsNullOrEmpty(raw)) continue;
                if (!claimed.Contains(raw)) claimed.Add(raw);
                // OrdinalIgnoreCase: Windows paths are case-insensitive and
                // macOS volumes usually are too. Two real projects differing
                // only by case is not a case worth breaking the common one for.
                if (string.Equals(NormalizeForCompare(raw), target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                }
                else if (foreign == null)
                {
                    foreign = raw;
                }
            }

            if (matched)
            {
                // A rewrite replaces every pin literal in the file, so a file
                // shared with another project cannot be moved as a unit.
                return foreign == null
                    ? new ScopeResult(Ownership.Matched, claimed.ToArray(), false)
                    : new ScopeResult(Ownership.Mixed, claimed.ToArray(), false, foreign);
            }
            if (claimed.Count > 0)
            {
                return new ScopeResult(Ownership.OtherProject, claimed.ToArray(), false);
            }

            // No project env. The deterministic port is the second marker: it
            // is derived from the project path, so an entry pinning this
            // project's port is this project's entry. (An explicit port
            // override is the documented escape hatch and can produce a false
            // negative here — that only costs a skip, never a wrong rewrite.)
            var expectedPort = InstancePortResolver.ComputePort(projectPath);
            foreach (Match m in PortPattern.Matches(body))
            {
                int port;
                if (!int.TryParse(m.Groups[1].Value, out port)) continue;
                if (port == expectedPort)
                {
                    return new ScopeResult(Ownership.Matched, new string[0], true);
                }
            }

            return new ScopeResult(Ownership.Unknown, new string[0], false);
        }

        // Path comparison form. On top of the shared normalization (backslash
        // → slash, trailing separator trimmed) this collapses separator runs:
        // a config records a Windows path with escaped separators
        // ("C:\\work\\Client"), and we read the raw file rather than
        // unescaping four config dialects, so the value arrives doubled. The
        // comparison should be about the path, not its escaping.
        private static string NormalizeForCompare(string path)
        {
            var norm = InstancePortResolver.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) return norm;
            var sb = new StringBuilder(norm.Length);
            var previousWasSeparator = false;
            foreach (var c in norm)
            {
                if (c == '/')
                {
                    if (previousWasSeparator) continue;
                    previousWasSeparator = true;
                }
                else
                {
                    previousWasSeparator = false;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Should this body be rewritten, given where the file lives?
        /// A project-scoped config with no marker is ours by location; a
        /// home-scoped one is not.
        /// </summary>
        internal static bool ShouldRewrite(ScopeResult scope, bool isHomeScoped)
        {
            switch (scope.Kind)
            {
                case Ownership.Matched: return true;
                case Ownership.OtherProject: return false;
                case Ownership.Mixed: return false;
                default: return !isHomeScoped;
            }
        }
    }
}
