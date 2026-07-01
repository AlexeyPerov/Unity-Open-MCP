using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityOpenMcpVerify.Internals.AssetDatabase;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public static class Scanner
    {
        // Curve property groups on an AnimationClip. When every group is empty
        // the clip animates nothing — a structurally broken clip.
        private static readonly string[] CurveGroups =
        {
            "m_RotationCurves",
            "m_CompressedRotationCurves",
            "m_EulerCurves",
            "m_PositionCurves",
            "m_ScaleCurves",
            "m_FloatCurves",
            "m_PPtrCurves",
        };

        public static void ScanPaths(string[] paths, List<AnimationData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!AssetTypeUtilities.IsTextSerializedYaml(path)) continue;

                var isController = path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase);
                var isAnim = path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);
                if (!isController && !isAnim) continue;
                if (!File.Exists(path)) continue;

                var data = new AnimationData(path, isController);

                if (isController)
                    CollectControllerClipReferences(path, data);
                else
                    AnalyzeClipCurves(path, data);

                sink.Add(data);
            }
        }

        // -------------------------------------------------------------------
        // Controller — motion / clip references
        // -------------------------------------------------------------------

        // AnimatorControllerState objects reference their motion (an
        // AnimationClip or a BlendTree) via m_Motion: {fileID, guid, type}.
        // An unresolved GUID means the referenced clip was deleted or never
        // committed — the controller state plays nothing.
        private static void CollectControllerClipReferences(string assetPath, AnimationData data)
        {
            string[] lines;
            try { lines = File.ReadAllLines(assetPath); }
            catch { return; }

            var pptr = SharedRegex.ExternalFileAndGuid;
            var stateIndent = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // AnimatorControllerState objects open with a YAML document
                // marker; we don't need to parse the type header precisely —
                // m_Motion only appears on state objects, so any m_Motion line
                // is a state-level motion ref.
                if (line.Contains("m_Motion:"))
                {
                    var match = pptr.Match(line);
                    if (match.Success)
                    {
                        var guid = match.Groups[2].Value;
                        if (IsRealGuid(guid))
                        {
                            var resolves = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
                            data.ClipReferences.Add(new AnimationClipReference("m_Motion", guid, i + 1, resolves));
                        }
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Clip — curve analysis
        // -------------------------------------------------------------------

        // Count keyframes and flag empty clips. An AnimationClip that declares
        // no curve entries at all (every curve group is `[]` or absent) cannot
        // animate anything. We approximate by counting `m_Curve:` blocks and
        // the presence of any non-empty curve array.
        private static void AnalyzeClipCurves(string assetPath, AnimationData data)
        {
            string[] lines;
            try { lines = File.ReadAllLines(assetPath); }
            catch { return; }

            var keyframeCount = 0;
            var hasAnyCurveGroup = false;
            var hasAnyNonEmptyGroup = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // A curve group header, e.g. "  m_RotationCurves:" or
                // "  m_RotationCurves: []". We look for any of the known
                // curve-group property names at the start of a trimmed line.
                foreach (var group in CurveGroups)
                {
                    if (!trimmed.StartsWith(group + ":", StringComparison.Ordinal)) continue;
                    hasAnyCurveGroup = true;

                    // Inline empty: "m_RotationCurves: []"
                    if (trimmed.Contains("[]")) continue;

                    // Block form: the next non-blank line either opens the
                    // list ("- curve:") or closes it. If we see a "-" entry
                    // before a "]" the group is non-empty.
                    for (var j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
                    {
                        var next = lines[j].TrimStart();
                        if (next.Length == 0) continue;
                        if (next.StartsWith("- ", StringComparison.Ordinal))
                        {
                            hasAnyNonEmptyGroup = true;
                            break;
                        }
                        if (next.StartsWith(group + ":", StringComparison.Ordinal)) break;
                        // A new property key (not a list entry) closes the group.
                        if (Regex.IsMatch(next, @"^m_\w+:") || Regex.IsMatch(next, @"^\w+:")) break;
                    }
                    break;
                }

                // Count keyframes by counting `serializedVersion: 3` blocks
                // inside m_Curve arrays. Each keyframe is a `- serializedVersion:`
                // entry — counting those gives the keyframe total.
                if (trimmed.StartsWith("- serializedVersion:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("serializedVersion: 3", StringComparison.Ordinal))
                {
                    keyframeCount++;
                }
            }

            data.KeyframeCount = keyframeCount;
            data.HasNoCurves = hasAnyCurveGroup && !hasAnyNonEmptyGroup;
        }

        private static bool IsRealGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            if (guid.Length != 32) return false;
            if (guid.StartsWith("0000000000", StringComparison.Ordinal)) return false;
            for (var i = 0; i < 32; i++)
            {
                var c = guid[i];
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }
    }
}
