// Extracted from Unity-Scanner: Editor/Utilities/RegexPatterns/USSharedRegex.cs

using System.Text.RegularExpressions;

namespace UnityOpenMcpVerify.Internals.RegexPatterns
{
    public static class SharedRegex
    {
        public static readonly Regex ExternalFileAndGuid = new Regex(
            @"fileID: (\d+), guid: ([a-f0-9]{32})",
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
            @"m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32})",
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
    }
}
