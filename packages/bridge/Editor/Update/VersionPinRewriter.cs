using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge.Update
{
    /// <summary>
    /// Pure rewriter for the trio version pins a consuming project carries.
    ///
    /// A release moves one number, but a project records it in several
    /// unrelated file formats at once: an npm pin inside every MCP client
    /// config (JSON and TOML), the two UPM git-URL tags in
    /// <c>Packages/manifest.json</c> / <c>packages-lock.json</c>, and the
    /// bridge's recorded dependency on verify inside the lock file. All three
    /// are literal substitutions, so this rewriter is deliberately regex-based
    /// and format-agnostic — parsing four config dialects to change one
    /// substring would add failure modes without adding precision.
    ///
    /// The one non-substitution step is the lock file's resolved
    /// <c>hash</c>: UPM records the commit it resolved a git dependency to,
    /// and moving only the tag would leave manifest and lock agreeing on the
    /// new tag while the lock still names the OLD commit — UPM reuses it and
    /// the upgrade silently does not happen. So a lock-file rewrite also drops
    /// the <c>hash</c> of every entry whose version it moved (see
    /// <see cref="PinKind.LockHash"/>); UPM writes the new value back on the
    /// next open.
    ///
    /// The patterns and the hash pass mirror
    /// <c>scripts/switch-project-version.mjs</c>, which owns the same rewrite
    /// for projects the maintainer points it at from a checkout. Keep the two
    /// in step: a pattern that matches here and not there (or vice versa)
    /// means the window and the script disagree about what a project's version
    /// is.
    ///
    /// Everything here is a pure string transform: no filesystem, no Unity
    /// API, no policy about WHICH files to open (that is
    /// <see cref="UpgradeScanner"/>) or whether an entry belongs to this
    /// project (that is <see cref="UpgradeEntryScope"/>).
    /// </summary>
    internal static class VersionPinRewriter
    {
        /// <summary>The npm package every client config launches, WITHOUT the
        /// version suffix. Derived from <see cref="BridgeConstants.NpmPackage"/>
        /// (which is <c>name@version</c>) rather than re-spelled, so a rename
        /// cannot leave this file matching a package that no longer exists —
        /// every pattern below is built from it.</summary>
        internal static readonly string NpmPackageName = StripNpmVersion(BridgeConstants.NpmPackage);

        /// <summary>UPM id of the bridge package.</summary>
        internal const string BridgePackageId = "com.alexeyperov.unity-open-mcp-bridge";

        /// <summary>UPM id of the verify package (the bridge's hard dependency).</summary>
        internal const string VerifyPackageId = "com.alexeyperov.unity-open-mcp-verify";

        /// <summary>File name of the UPM lock file, whose resolved
        /// <c>hash</c> entries must be dropped alongside a version move.</summary>
        internal const string PackagesLockFileName = "packages-lock.json";

        internal enum PinKind
        {
            /// <summary><c>unity-open-mcp@X.Y.Z</c> in a client config's command/args.</summary>
            Npm,
            /// <summary><c>#bridge-vX.Y.Z</c> / <c>#verify-vX.Y.Z</c> in a UPM git URL.</summary>
            UpmTag,
            /// <summary>The bridge → verify version pin recorded in <c>packages-lock.json</c>.</summary>
            VerifyDependency,
            /// <summary>Not a pin but its consequence: the stale resolved
            /// <c>hash</c> dropped from a lock entry whose version moved, so
            /// UPM re-resolves the tag instead of reusing the old commit.</summary>
            LockHash,
        }

        // `unity-open-mcp@1.1.0` → `unity-open-mcp`; `@scope/pkg@1.1.0` →
        // `@scope/pkg`. The leading `@` of a scoped name is not a separator,
        // hence LastIndexOf from index 1.
        private static string StripNpmVersion(string nameAtVersion)
        {
            if (string.IsNullOrEmpty(nameAtVersion)) return nameAtVersion;
            var at = nameAtVersion.LastIndexOf('@');
            return at > 0 ? nameAtVersion.Substring(0, at) : nameAtVersion;
        }

        /// <summary>One stale pin family found in a body: which kind, which
        /// versions it carried, and how many literals were rewritten.</summary>
        internal readonly struct PinChange
        {
            public readonly PinKind Kind;
            /// <summary>Distinct pre-existing versions, in first-seen order.</summary>
            public readonly string[] From;
            /// <summary>How many literals were rewritten (not how many distinct versions).</summary>
            public readonly int Count;

            public PinChange(PinKind kind, string[] from, int count)
            {
                Kind = kind;
                From = from ?? new string[0];
                Count = count;
            }

            public string Label
            {
                get
                {
                    switch (Kind)
                    {
                        case PinKind.Npm: return "npm pin";
                        case PinKind.UpmTag: return "UPM git pin";
                        case PinKind.VerifyDependency: return "verify dependency pin";
                        case PinKind.LockHash: return "stale resolved hash dropped";
                        default: return Kind.ToString();
                    }
                }
            }
        }

        /// <summary>Outcome of a rewrite: the new body plus what it says about
        /// the old one. A body with no pin at all is reported through
        /// <see cref="HasPin"/> so callers can drop it from the report instead
        /// of listing every scanned file as "already current".</summary>
        internal readonly struct RewriteResult
        {
            public readonly string Body;
            /// <summary>Stale families only — a body already on the target
            /// version rewrites to itself and reports no changes.</summary>
            public readonly PinChange[] Changes;
            /// <summary>Every version-shaped pin seen, stale or current.</summary>
            public readonly int PinsFound;
            /// <summary><c>unity-open-mcp@latest</c> occurrences. Never
            /// rewritten (they already track the newest release), but worth
            /// reporting so an operator understands why nothing changed.</summary>
            public readonly int FloatingPins;

            public RewriteResult(string body, PinChange[] changes, int pinsFound, int floatingPins)
            {
                Body = body;
                Changes = changes ?? new PinChange[0];
                PinsFound = pinsFound;
                FloatingPins = floatingPins;
            }

            public bool Changed => Changes.Length > 0;
            public bool HasPin => PinsFound > 0 || FloatingPins > 0;
        }

        private const string Triple = @"(\d+\.\d+\.\d+)";

        // Group 1 is the literal prefix, group 2 the X.Y.Z, group 3 (where
        // present) the suffix. `@latest` and the bare package name are
        // deliberately unmatched: converting a floating pin into a fixed one
        // would change a user's update policy, not just their version.
        private static readonly Regex NpmPinPattern =
            new Regex("(" + Regex.Escape(NpmPackageName) + "@)" + Triple);

        private static readonly Regex UpmTagPattern =
            new Regex(@"(#(?:bridge|verify)-v)" + Triple);

        private static readonly Regex VerifyDependencyPattern =
            new Regex("(\"" + Regex.Escape(VerifyPackageId) + "\"\\s*:\\s*\")" + Triple + "(\")");

        private static readonly Regex FloatingNpmPinPattern =
            new Regex(Regex.Escape(NpmPackageName) + "@latest");

        // \A…\z, NOT ^…$: in .NET `$` also matches immediately before a
        // TRAILING newline, so `^\d+\.\d+\.\d+$` accepted "1.2.3\n" — a
        // version read from a file or a `git describe` capture without a trim.
        // That passed validation and then substituted a raw newline into every
        // JSON/TOML string literal it rewrote, corrupting the file.
        private static readonly Regex VersionPattern = new Regex(@"\A\d+\.\d+\.\d+\z");

        /// <summary>True for a plain <c>X.Y.Z</c>. Pre-release, build metadata
        /// and any surrounding whitespace (including a trailing newline) are
        /// rejected: the trio only ever ships plain triples, and accepting more
        /// here would let a typo through into a git tag that does not exist.
        /// Use <see cref="NormalizeVersion"/> first when the value came from a
        /// file or a command's stdout.</summary>
        internal static bool IsVersion(string version)
        {
            return !string.IsNullOrEmpty(version) && VersionPattern.IsMatch(version);
        }

        /// <summary>Strip one leading <c>v</c> (<c>v1.1.0</c> → <c>1.1.0</c>)
        /// so a version pasted from a git tag is accepted. Returns the input
        /// unchanged when it is not tag-shaped.</summary>
        internal static string NormalizeVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            var trimmed = version.Trim();
            if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed.Substring(1);
            return trimmed;
        }

        /// <summary>True when this path is the UPM lock file, i.e. when
        /// <see cref="Rewrite(string,string,bool)"/> must also drop the stale
        /// resolved hashes. Accepts either separator.</summary>
        internal static bool IsPackagesLockPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var normalized = path.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            var name = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            return string.Equals(name, PackagesLockFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Rewrite every pin in <paramref name="body"/> to
        /// <paramref name="version"/>. Idempotent: a second pass over the
        /// result changes nothing and reports no changes.
        /// </summary>
        /// <param name="isPackagesLock">
        /// True for <c>Packages/packages-lock.json</c>. Enables the hash pass:
        /// every entry whose version literal this call moved also loses its
        /// resolved <c>hash</c>, so UPM re-resolves the new tag instead of
        /// reusing the commit it cached for the old one. Without it the file
        /// would name the new version and still install the old code.
        /// Callers normally pass <see cref="IsPackagesLockPath"/> of the file.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="version"/> is not a plain <c>X.Y.Z</c>. This is a
        /// programming error — resolve and validate the target version before
        /// planning a rewrite, never per file.
        /// </exception>
        internal static RewriteResult Rewrite(string body, string version, bool isPackagesLock = false)
        {
            if (!IsVersion(version))
            {
                throw new ArgumentException(
                    $"Target version must be a plain X.Y.Z; got '{version}'.", nameof(version));
            }
            if (string.IsNullOrEmpty(body))
            {
                return new RewriteResult(body, new PinChange[0], 0, 0);
            }

            var changes = new List<PinChange>(4);
            var pinsFound = 0;
            var next = body;

            next = ApplyPattern(next, NpmPinPattern, PinKind.Npm, version, changes, ref pinsFound);
            next = ApplyPattern(next, UpmTagPattern, PinKind.UpmTag, version, changes, ref pinsFound);
            next = ApplyPattern(
                next, VerifyDependencyPattern, PinKind.VerifyDependency, version, changes, ref pinsFound);

            if (isPackagesLock)
            {
                var dropped = 0;
                foreach (var packageId in new[] { BridgePackageId, VerifyPackageId })
                {
                    // Only entries this call actually moved: dropping the hash
                    // of an already-current entry would force a pointless
                    // re-resolve (a network round-trip) on the next open.
                    if (string.Equals(
                            LockEntryVersion(body, packageId),
                            LockEntryVersion(next, packageId),
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string stripped;
                    if (TryStripLockHash(next, packageId, out stripped))
                    {
                        next = stripped;
                        dropped++;
                    }
                }
                if (dropped > 0)
                {
                    changes.Add(new PinChange(PinKind.LockHash, new string[0], dropped));
                }
            }

            var floating = FloatingNpmPinPattern.Matches(body).Count;
            return new RewriteResult(next, changes.ToArray(), pinsFound, floating);
        }

        // ---- packages-lock.json hash invalidation ---------------------------
        //
        // Ported from stripLockHash / findObjectSpan / lockEntryVersion in
        // scripts/switch-project-version.mjs. Brace matching rather than a
        // regex for the entry span, because the lock file names the verify
        // package id TWICE — once as the bridge entry's dependency (a version
        // string) and once as its own top-level entry — and only the latter is
        // the object whose hash we may drop.

        /// <summary>The <c>version</c> string of one lock entry, or
        /// <c>null</c> when the entry (or the field) is absent.</summary>
        private static string LockEntryVersion(string body, string packageId)
        {
            int start, end;
            if (!TryFindObjectSpan(body, packageId, out start, out end)) return null;
            var match = LockVersionPattern.Match(body.Substring(start, end - start));
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Remove the <c>"hash": "…"</c> property from one lock entry,
        /// preserving the surrounding formatting whether it is the entry's last
        /// property or a middle one. False when the entry or the property is
        /// absent (then <paramref name="result"/> is the input).</summary>
        private static bool TryStripLockHash(string body, string packageId, out string result)
        {
            result = body;
            int start, end;
            if (!TryFindObjectSpan(body, packageId, out start, out end)) return false;

            var entry = body.Substring(start, end - start);
            // Trailing property: take the preceding comma with it.
            var next = TrailingHashPattern.Replace(entry, "", 1);
            if (string.Equals(next, entry, StringComparison.Ordinal))
            {
                // Middle property: take its own trailing comma + newline.
                next = MiddleHashPattern.Replace(entry, "", 1);
            }
            if (string.Equals(next, entry, StringComparison.Ordinal)) return false;

            result = body.Substring(0, start) + next + body.Substring(end);
            return true;
        }

        /// <summary>Span of the <c>{ … }</c> object value of
        /// <c>"key": { … }</c>, located by string-aware brace matching. False
        /// when the key is absent or followed by something other than an
        /// object (the dependency-string occurrence of the same id).</summary>
        private static bool TryFindObjectSpan(string body, string key, out int start, out int end)
        {
            start = 0;
            end = 0;
            var opener = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{").Match(body);
            if (!opener.Success) return false;

            var open = opener.Index + opener.Length - 1;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = open; i < body.Length; i++)
            {
                var c = body[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        start = open;
                        end = i + 1;
                        return true;
                    }
                }
            }
            return false;
        }

        private static readonly Regex LockVersionPattern =
            new Regex("\"version\"\\s*:\\s*\"([^\"]*)\"");

        private static readonly Regex TrailingHashPattern =
            new Regex(",[ \\t]*\\r?\\n[ \\t]*\"hash\"[ \\t]*:[ \\t]*\"[^\"]*\"");

        private static readonly Regex MiddleHashPattern =
            new Regex("[ \\t]*\"hash\"[ \\t]*:[ \\t]*\"[^\"]*\",[ \\t]*\\r?\\n");

        // Replace every match of one pattern, recording the distinct versions
        // it displaced. `pinsFound` counts current pins too — a file already on
        // the target version still carries a pin, and reporting it as "no pin"
        // would hide it from the operator's review.
        private static string ApplyPattern(
            string body,
            Regex pattern,
            PinKind kind,
            string version,
            List<PinChange> changes,
            ref int pinsFound)
        {
            var stale = new List<string>();
            var staleCount = 0;
            var matched = 0;

            var rewritten = pattern.Replace(body, match =>
            {
                matched++;
                var prefix = match.Groups[1].Value;
                var found = match.Groups[2].Value;
                var suffix = match.Groups.Count > 3 ? match.Groups[3].Value : "";
                if (!string.Equals(found, version, StringComparison.Ordinal))
                {
                    staleCount++;
                    if (!stale.Contains(found)) stale.Add(found);
                }
                return prefix + version + suffix;
            });

            pinsFound += matched;
            if (staleCount > 0)
            {
                changes.Add(new PinChange(kind, stale.ToArray(), staleCount));
            }
            return rewritten;
        }
    }
}
