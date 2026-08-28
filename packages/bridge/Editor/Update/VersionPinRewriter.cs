using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
    /// The patterns mirror <c>scripts/switch-project-version.mjs</c>, which
    /// owns the same rewrite for projects the maintainer points it at from a
    /// checkout. Keep the two in step: a pattern that matches here and not
    /// there (or vice versa) means the window and the script disagree about
    /// what a project's version is.
    ///
    /// Everything here is a pure string transform: no filesystem, no Unity
    /// API, no policy about WHICH files to open (that is
    /// <see cref="UpgradeScanner"/>) or whether an entry belongs to this
    /// project (that is <see cref="UpgradeEntryScope"/>).
    /// </summary>
    internal static class VersionPinRewriter
    {
        /// <summary>The npm package every client config launches.</summary>
        internal const string NpmPackageName = "unity-open-mcp";

        /// <summary>UPM id of the verify package (the bridge's hard dependency).</summary>
        internal const string VerifyPackageId = "com.alexeyperov.unity-open-mcp-verify";

        internal enum PinKind
        {
            /// <summary><c>unity-open-mcp@X.Y.Z</c> in a client config's command/args.</summary>
            Npm,
            /// <summary><c>#bridge-vX.Y.Z</c> / <c>#verify-vX.Y.Z</c> in a UPM git URL.</summary>
            UpmTag,
            /// <summary>The bridge → verify version pin recorded in <c>packages-lock.json</c>.</summary>
            VerifyDependency,
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

        // Group 1 is the literal prefix, group 2 the X.Y.Z, group 3 (where
        // present) the suffix. `@latest` and the bare package name are
        // deliberately unmatched: converting a floating pin into a fixed one
        // would change a user's update policy, not just their version.
        private static readonly Regex NpmPinPattern =
            new Regex(@"(unity-open-mcp@)(\d+\.\d+\.\d+)");

        private static readonly Regex UpmTagPattern =
            new Regex(@"(#(?:bridge|verify)-v)(\d+\.\d+\.\d+)");

        private static readonly Regex VerifyDependencyPattern =
            new Regex(
                "(\"" + @"com\.alexeyperov\.unity-open-mcp-verify" + "\"\\s*:\\s*\")(\\d+\\.\\d+\\.\\d+)(\")");

        private static readonly Regex FloatingNpmPinPattern =
            new Regex(@"unity-open-mcp@latest");

        private static readonly Regex VersionPattern = new Regex(@"^\d+\.\d+\.\d+$");

        /// <summary>True for a plain <c>X.Y.Z</c>. Pre-release and build
        /// metadata are rejected: the trio only ever ships plain triples, and
        /// accepting more here would let a typo through into a git tag that
        /// does not exist.</summary>
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

        /// <summary>
        /// Rewrite every pin in <paramref name="body"/> to
        /// <paramref name="version"/>. Idempotent: a second pass over the
        /// result changes nothing and reports no changes.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="version"/> is not a plain <c>X.Y.Z</c>. This is a
        /// programming error — resolve and validate the target version before
        /// planning a rewrite, never per file.
        /// </exception>
        internal static RewriteResult Rewrite(string body, string version)
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

            var changes = new List<PinChange>(3);
            var pinsFound = 0;
            var next = body;

            next = ApplyPattern(next, NpmPinPattern, PinKind.Npm, version, changes, ref pinsFound);
            next = ApplyPattern(next, UpmTagPattern, PinKind.UpmTag, version, changes, ref pinsFound);
            next = ApplyPattern(
                next, VerifyDependencyPattern, PinKind.VerifyDependency, version, changes, ref pinsFound);

            var floating = FloatingNpmPinPattern.Matches(body).Count;
            return new RewriteResult(next, changes.ToArray(), pinsFound, floating);
        }

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
