using System.Text.RegularExpressions;

namespace UnityOpenMcpVerify.Internals.RegexPatterns
{
    public static class SharedRegex
    {
        public static readonly Regex ExternalFileAndGuid = new Regex(
            @"fileID: (\d+), guid: ([a-fA-F0-9]{32})",
            RegexOptions.Compiled);

        public static readonly Regex LocalFileId = new Regex(
            @"{fileID: \d+}",
            RegexOptions.Compiled);

        public static readonly Regex FieldTypeStart = new Regex(
            @"^[a-zA-Z0-9_ ]+:",
            RegexOptions.Compiled);

        public static readonly Regex Guid32Hex = new Regex(
            @"^[a-fA-F0-9]{32}$",
            RegexOptions.Compiled);

        public static readonly Regex AssetReferenceGuid = new Regex(
            @"m_AssetGUID:\s*([0-9a-fA-F]{32})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static readonly Regex ScriptGuid = new Regex(
            @"m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([a-fA-F0-9]{32})",
            RegexOptions.Compiled);

        public static readonly Regex LayerIndex = new Regex(
            @"^\s*m_Layer:\s*(\d+)\s*$",
            RegexOptions.Compiled);

        public static readonly Regex UnityEventTargetType = new Regex(
            @"m_TargetAssemblyTypeName:\s*([\w.]+)",
            RegexOptions.Compiled);

        public static readonly Regex UnityEventMethodName = new Regex(
            @"m_MethodName:\s*(\w+)",
            RegexOptions.Compiled);

        public static readonly Regex UnityEventArgType = new Regex(
            @"m_ObjectArgumentAssemblyTypeName:\s*([\w.]+)",
            RegexOptions.Compiled);

        public static readonly Regex GuidInputNormalize = new Regex(
            @"[\s\-{}]",
            RegexOptions.Compiled);

        // A top-level YAML object header as Unity writes it:
        //   "--- !u!114 &-8234567890123456789 MonoBehaviour"
        // The anchor (the `&<digits>` fileID) keys empty_local_ref issues so
        // they are stable across unrelated edits (feedback-fable-31-07 §7).
        public static readonly Regex ObjectHeaderAnchor = new Regex(
            @"^---\s*!u!\d+\s*&(-?\d+)",
            RegexOptions.Compiled);

        // The serialized property key on a `key: {fileID: 0}` line, e.g.
        // "m_Father" in "  m_Father: {fileID: 0}". Captured so empty_local_ref
        // issues can be keyed by owning anchor + property.
        //
        // The optional leading "- " matches YAML list-item entries, which is
        // how Unity serializes UnityEvent persistent-call targets:
        //   "      - m_Target: {fileID: 0}"
        // Without it the leading "-" (not in the key char class) made the match
        // fail for the single most common empty-ref source, collapsing every
        // UnityEvent m_Target onto the same fallback key and reintroducing the
        // positional-ordinal churn feedback-fable-31-07 §7 set out to remove.
        public static readonly Regex PropertyKeyBeforeFileId = new Regex(
            @"^\s*(?:-\s*)?([A-Za-z0-9_.\[\]]+):\s*\{fileID:",
            RegexOptions.Compiled);
    }
}
