// Extracted from Unity-Scanner: Editor/Utilities/AssetDatabase/USPathFilterUtilities.cs

using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Internals.AssetDatabase
{
    public static class PathFilterUtilities
    {
        public static bool PathMatchesFilter(string path, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;
            return path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
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
