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
    /// A committed (portable) entry names the project without a machine path:
    /// <c>${workspaceFolder}/Client</c>, a relative <c>Client</c>, the
    /// <c>--unity-subpath Client</c> flag, or the wrapper script's default
    /// <c>UNITY_SUBPATH</c>. Each is relative to the workspace root the client
    /// is opened on — the directory the config file was found under — so with
    /// that root known they resolve to a concrete folder and take part in the
    /// comparison like an absolute path. This is what keeps one Unity project
    /// of a monorepo from moving a sibling project's committed pin.
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
            /// <summary>No project marker found (no env, no matching port) —
            /// including a portable entry that cannot be resolved here: no
            /// workspace root was supplied, or the value is a shell variable
            /// the file itself expands (<c>${project_path}</c>).</summary>
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
            /// project sharing this file. <c>null</c> otherwise — including a
            /// port-only Mixed body, where the foreign entry names no project
            /// we can report.</summary>
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

        // Portable markers that name the project relative to the workspace
        // root. `"--unity-subpath", "Client"` is the args form in JSON and TOML
        // arrays alike; a bare `"--project-from-cwd"` claims the root itself.
        private static readonly Regex UnitySubpathArgPattern = new Regex(
            "\"--unity-subpath\"\\s*,\\s*\"([^\"]*)\"");
        private static readonly Regex ProjectFromCwdArgPattern = new Regex("\"--project-from-cwd\"");
        // The committed wrapper bakes its default Unity subfolder into
        // `subpath="${UNITY_SUBPATH-Client}"`; empty means the workspace root.
        private static readonly Regex WrapperSubpathPattern = new Regex(
            "\\$\\{UNITY_SUBPATH-([^}\"]*)\\}");

        /// <summary>
        /// Classify a config body against a Unity project path.
        /// <paramref name="workspaceRoot"/> is the directory the config was
        /// found under (the workspace an AI client opens, and the folder a
        /// committed wrapper resolves to); it anchors portable markers. Pass
        /// <c>null</c> for a home-scoped file, whose portable markers cannot be
        /// resolved and are ignored.
        /// </summary>
        internal static ScopeResult Classify(string body, string projectPath, string workspaceRoot = null)
        {
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(projectPath))
            {
                return new ScopeResult(Ownership.Unknown, new string[0], false);
            }

            var target = NormalizeForCompare(projectPath);
            var claimed = new List<string>();
            var matched = false;
            string foreign = null;

            foreach (var resolved in CollectClaims(body, workspaceRoot))
            {
                if (!claimed.Contains(resolved)) claimed.Add(resolved);
                // OrdinalIgnoreCase: Windows paths are case-insensitive and
                // macOS volumes usually are too. Two real projects differing
                // only by case is not a case worth breaking the common one for.
                if (string.Equals(NormalizeForCompare(resolved), target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                }
                else if (foreign == null)
                {
                    foreign = resolved;
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
            // project's port is this project's entry. But a port does not
            // name its project: two entries distinguished only by port can
            // share one file (one per project), and a rewrite is whole-file,
            // so a body carrying ours AND another port is Mixed. A body with
            // only our port is Matched — with a residual hash-collision risk
            // (the resolver buckets into 10k ports), which errs toward
            // rewriting and cannot be removed without the project env.
            // (An explicit port override is the documented escape hatch and
            // can produce a false negative here — that only costs a skip.)
            var expectedPort = InstancePortResolver.ComputePort(projectPath);
            var sawExpectedPort = false;
            var sawForeignPort = false;
            foreach (Match m in PortPattern.Matches(body))
            {
                int port;
                if (!int.TryParse(m.Groups[1].Value, out port)) continue;
                if (port == expectedPort) sawExpectedPort = true;
                else sawForeignPort = true;
            }

            if (sawExpectedPort)
            {
                return sawForeignPort
                    ? new ScopeResult(Ownership.Mixed, new string[0], true)
                    : new ScopeResult(Ownership.Matched, new string[0], true);
            }

            return new ScopeResult(Ownership.Unknown, new string[0], false);
        }

        /// <summary>
        /// Every project folder the body claims, resolved to an absolute path
        /// in first-seen order: each <c>UNITY_PROJECT_PATH</c> value
        /// (absolute, or portable and anchored at
        /// <paramref name="workspaceRoot"/>), then the args-form flags, then
        /// the wrapper's baked subfolder. Values that cannot be resolved are
        /// left out — they neither claim nor disown the file.
        /// </summary>
        private static IEnumerable<string> CollectClaims(string body, string workspaceRoot)
        {
            foreach (Match m in ProjectPathPattern.Matches(body))
            {
                var resolved = ResolveClaim(m.Groups[1].Value, workspaceRoot);
                if (resolved != null) yield return resolved;
            }
            if (string.IsNullOrEmpty(workspaceRoot)) yield break;

            var sawSubpath = false;
            foreach (Match m in UnitySubpathArgPattern.Matches(body))
            {
                sawSubpath = true;
                yield return JoinWorkspace(workspaceRoot, m.Groups[1].Value);
            }
            // `--project-from-cwd` alone: the workspace root is the project.
            // With a subpath the flag adds nothing — the server's fallback to
            // cwd only applies when the subfolder is not a Unity project, and
            // the config still asks for the subfolder.
            if (!sawSubpath && ProjectFromCwdArgPattern.IsMatch(body))
            {
                yield return workspaceRoot;
            }
            foreach (Match m in WrapperSubpathPattern.Matches(body))
            {
                yield return JoinWorkspace(workspaceRoot, m.Groups[1].Value);
            }
        }

        /// <summary>
        /// Absolute folder a <c>UNITY_PROJECT_PATH</c> value names, or
        /// <c>null</c> when it cannot be resolved: a portable value with no
        /// workspace root to anchor it, or a shell variable the file expands
        /// itself (<c>${project_path}</c> in the wrapper, <c>$HOME/…</c>).
        /// </summary>
        internal static string ResolveClaim(string raw, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (IsMachinePath(raw)) return raw;
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            if (raw.StartsWith(McpClientCatalog.WorkspaceFolderVar, StringComparison.Ordinal))
            {
                var rest = raw.Substring(McpClientCatalog.WorkspaceFolderVar.Length);
                if (rest.Length == 0) return workspaceRoot;
                if (rest[0] != '/' && rest[0] != '\\') return null;
                return JoinWorkspace(workspaceRoot, rest.Substring(1));
            }
            if (raw.IndexOf('$') >= 0) return null;
            // Relative: the server resolves it against the spawn directory,
            // which for a committed config is the workspace root.
            return JoinWorkspace(workspaceRoot, raw);
        }

        private static string JoinWorkspace(string workspaceRoot, string relative)
        {
            var root = workspaceRoot.Replace('\\', '/').TrimEnd('/');
            var rel = relative.Replace('\\', '/').Trim('/');
            return rel.Length == 0 ? root : root + "/" + rel;
        }

        /// <summary>
        /// True for a value that names a concrete folder on this machine, as
        /// opposed to a portable form (<c>${workspaceFolder}/Client</c>,
        /// <c>Client</c>) or a shell variable (<c>${project_path}</c>).
        /// </summary>
        internal static bool IsMachinePath(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.IndexOf('$') >= 0) return false;
            if (raw[0] == '/' || raw[0] == '\\') return true;
            return raw.Length >= 2 && raw[1] == ':' && char.IsLetter(raw[0]);
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
