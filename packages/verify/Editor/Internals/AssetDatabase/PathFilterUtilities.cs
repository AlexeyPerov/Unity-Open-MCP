using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Internals.AssetDatabase
{
    public static class PathFilterUtilities
    {
        // A filter matches a path when:
        //   1. The filter equals a path SEGMENT (case-insensitive) — e.g. filter
        //      "Art" matches "Assets/Art/Textures/x.mat" because "Art" is a
        //      segment, but NOT "Assets/Party/x.mat" (no "Art" segment).
        //   2. The path starts with the filter as a prefix followed by a
        //      separator — e.g. filter "Assets/Art" matches
        //      "Assets/Art/Textures/x.mat".
        //   3. The path equals the filter exactly (case-insensitive).
        // The old substring match (`path.IndexOf(filter) >= 0`) matched "Art"
        // inside "Assets/Party/..." — a false positive that inflated filtered
        // sets and mis-scoped scans.
        public static bool PathMatchesFilter(string path, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;
            if (string.IsNullOrEmpty(path))
                return false;

            // Normalize separators so the logic works cross-platform.
            var normPath = path.Replace('\\', '/');
            var normFilter = filter.Replace('\\', '/');

            // Exact match.
            if (normPath.Equals(normFilter, StringComparison.OrdinalIgnoreCase))
                return true;

            // Prefix match — filter is a directory prefix.
            if (normPath.StartsWith(normFilter + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            // Segment match — the filter is a single path segment that appears
            // as a complete directory/file name in the path.
            var segments = normPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals(normFilter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static List<System.Text.RegularExpressions.Regex> CompilePatterns(List<string> patterns)
        {
            var compiled = new List<System.Text.RegularExpressions.Regex>(patterns.Count);
            foreach (var pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern))
                    compiled.Add(new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
            }

            return compiled;
        }

        public static bool IsValidForOutput(string path, List<System.Text.RegularExpressions.Regex> compiledPatterns)
        {
            foreach (var t in compiledPatterns)
            {
                if (t.IsMatch(path))
                    return false;
            }

            return true;
        }

        public static bool IsIncludedInAnalysis(string path, List<string> ignorePatterns)
        {
            return ignorePatterns.All(pattern
                => string.IsNullOrEmpty(pattern) || !System.Text.RegularExpressions.Regex.Match(path, pattern).Success);
        }
    }
}
