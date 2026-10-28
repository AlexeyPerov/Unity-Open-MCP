// Version gate shared by every adapter over Unity Editor internals (legacy
// input injection, Game view / Device Simulator control).
//
// An adapter that reaches internal Editor types by reflection declares the
// Editor lines it was verified on. An Editor outside every rule refuses with
// `unsupported_editor_version` — it never guesses. A line is added only after a
// walkthrough on it; each adapter still probes its members before use.
using System.Globalization;

namespace UnityOpenMcpBridge
{
    internal sealed class EditorVersionTable
    {
        internal sealed class Rule
        {
            public readonly int FromMajor, FromMinor, ToMajorExclusive;
            public readonly string VerifiedOn;

            public Rule(int fromMajor, int fromMinor, int toMajorExclusive, string verifiedOn)
            {
                FromMajor = fromMajor;
                FromMinor = fromMinor;
                ToMajorExclusive = toMajorExclusive;
                VerifiedOn = verifiedOn;
            }
        }

        // Human name of the gated feature, used in refusal messages.
        public readonly string Feature;
        public readonly Rule[] Rules;

        public EditorVersionTable(string feature, params Rule[] rules)
        {
            Feature = feature;
            Rules = rules ?? new Rule[0];
        }

        // "6000.3.21f1" → (6000, 3). False for anything unparsable.
        internal static bool TryParse(string version, out int major, out int minor)
        {
            major = minor = 0;
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)) return false;
            int digits = 0;
            while (digits < parts[1].Length && char.IsDigit(parts[1][digits])) digits++;
            return digits > 0 && int.TryParse(parts[1].Substring(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out minor);
        }

        internal bool IsSupported(string version, out string detail)
        {
            if (!TryParse(version, out var major, out var minor))
            {
                detail = "Unrecognised Unity version '" + version + "'.";
                return false;
            }
            foreach (var rule in Rules)
            {
                bool atLeast = major > rule.FromMajor || (major == rule.FromMajor && minor >= rule.FromMinor);
                if (atLeast && major < rule.ToMajorExclusive)
                {
                    detail = "Unity " + version + " is in the verified range (verified on " + rule.VerifiedOn + ").";
                    return true;
                }
            }
            detail = "Unity " + version + " is not in the " + Feature + " version table (supported: Unity 6 line" +
                     (Rules.Length > 0 ? ", verified on " + Rules[0].VerifiedOn : "") +
                     "). 2022.3 and other lines stay disabled until verified.";
            return false;
        }
    }
}
