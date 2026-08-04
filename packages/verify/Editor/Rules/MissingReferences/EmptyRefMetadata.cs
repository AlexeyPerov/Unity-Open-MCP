using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    // feedback-04-08-opus §4 / §5 — per-anchor metadata for a single asset's
    // top-level YAML objects, built in a pre-pass so empty_local_ref issues can
    // be keyed by transform PATH (content-addressed, survives renumbering) and
    // classified by field OWNER (user script vs built-in).
    //
    // A prefab rebuild renumbers every `&NNN` anchor, so the anchor-keyed
    // scheme from feedback-fable-31-07 §7 still produces a full 729/729 churn
    // on an idempotent rebuild. The transform path
    // ("LobbyChrome/LeftOffersTab/Content") + property does not, because object
    // NAMES and hierarchy are content, not serialization bookkeeping. The
    // script-GUID owner classification lets a real empty ref on a user-script
    // field stay a Warning while empty-by-default built-in fields
    // (m_SelectOn*, sprite swaps, TMP material/style) are demoted to Info,
    // removing the ~98 % noise floor that buried a genuine shipping
    // NullReferenceException.
    internal sealed class AnchorMetadata
    {
        // The top-level anchor (fileID) of this YAML object.
        public long Anchor;

        // Unity class id from the header `--- !u!<classId> &<anchor>`. 1 =
        // GameObject, 114 = MonoBehaviour, 224 = RectTransform, 4 = Transform,
        // etc. Used to tell MonoBehaviours (carry m_Script) from transforms
        // (carry m_Father) and GameObjects (carry m_Name).
        public int ClassId;

        // GameObject's m_Name (when this anchor is a GameObject). null otherwise.
        public string Name;

        // For a component (MonoBehaviour / Transform / RectTransform): the
        // fileID of the GameObject it is attached to (m_GameObject: {fileID: N}).
        // 0 when not captured.
        public long GameObjectId;

        // For a RectTransform / Transform: the parent transform's fileID
        // (m_Father: {fileID: N}). 0 when this is a root transform or not captured.
        public long FatherId;

        // For a MonoBehaviour: the script GUID from m_Script: {fileID: N, guid: ...}.
        // null when this anchor is not a MonoBehaviour or has no script ref.
        public string ScriptGuid;
    }

    // Build the per-anchor metadata map and resolve transform paths + script
    // owners for empty-ref sites. All static; no instance state.
    internal static class EmptyRefMetadata
    {
        // Unity class ids we care about. MonoBehaviour fields are the ones whose
        // emptiness can be a real bug; built-in component fields are almost
        // always empty-by-default noise.
        private const int ClassIdGameObject = 1;
        private const int ClassIdMonoBehaviour = 114;
        private const int ClassIdTransform = 4;
        private const int ClassIdRectTransform = 224;

        // `--- !u!114 &-8234567890123456789 MonoBehaviour` — captures classId
        // and anchor. Reuses the anchor group shape of ObjectHeaderAnchor.
        private static readonly Regex HeaderClassAndAnchor = new Regex(
            @"^---\s*!u!(\d+)\s*&(-?\d+)",
            RegexOptions.Compiled);

        // `  m_Name: LobbyChrome` — leading indentation varies; capture the name
        // up to end of line (Unity names can contain spaces but not newlines).
        private static readonly Regex NameField = new Regex(
            @"^\s*m_Name:\s*(.*)$",
            RegexOptions.Compiled);

        // `  m_GameObject: {fileID: 123}` — capture the referenced GameObject fileID.
        private static readonly Regex GameObjectRef = new Regex(
            @"^\s*m_GameObject:\s*\{fileID:\s*(-?\d+)\}",
            RegexOptions.Compiled);

        // `  m_Father: {fileID: 123}` — capture the parent transform fileID.
        private static readonly Regex FatherRef = new Regex(
            @"^\s*m_Father:\s*\{fileID:\s*(-?\d+)\}",
            RegexOptions.Compiled);

        // Reuse SharedRegex.ScriptGuid for the script GUID (m_Script: {fileID: N, guid: <32hex>}).
        private const int MetadataScanLines = 12;

        /// <summary>
        /// Build the anchor → metadata map for one asset's YAML. Walks the lines
        /// once, opening a new metadata entry on each `--- !u!<classId> &<anchor>`
        /// header and capturing the first occurrence of m_Name / m_GameObject /
        /// m_Father / m_Script within the first few lines of each object (they
        /// are always near the top of a Unity-serialized object). Returns a map
        /// keyed by anchor fileID; entries with no useful fields are still
        /// included (cheap, and a component with no m_GameObject ref is itself a
        /// signal).</summary>
        public static Dictionary<long, AnchorMetadata> Build(string[] lines, HashSet<long> declaredFileIDs)
        {
            var map = new Dictionary<long, AnchorMetadata>();
            if (lines == null) return map;

            AnchorMetadata current = null;
            int scanBudget = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith("---", System.StringComparison.Ordinal))
                {
                    var hm = HeaderClassAndAnchor.Match(line);
                    if (hm.Success && long.TryParse(hm.Groups[2].Value, out var anchor))
                    {
                        int.TryParse(hm.Groups[1].Value, out var classId);
                        current = new AnchorMetadata { Anchor = anchor, ClassId = classId };
                        map[anchor] = current;
                        scanBudget = MetadataScanLines;
                        continue;
                    }
                    current = null;
                    continue;
                }

                if (current == null || scanBudget <= 0) continue;
                // A blank line or a dedent to column 0 ends the object's
                // interesting header region; stop scanning to save work.
                if (line.Length == 0) { scanBudget--; continue; }

                if (current.Name == null && current.ClassId == ClassIdGameObject)
                {
                    var nm = NameField.Match(line);
                    if (nm.Success)
                    {
                        current.Name = nm.Groups[1].Value.Trim();
                    }
                }

                if (current.GameObjectId == 0 && current.ClassId != ClassIdGameObject)
                {
                    var gm = GameObjectRef.Match(line);
                    if (gm.Success && long.TryParse(gm.Groups[1].Value, out var goId))
                    {
                        current.GameObjectId = goId;
                    }
                }

                if (current.FatherId == 0
                    && (current.ClassId == ClassIdTransform || current.ClassId == ClassIdRectTransform))
                {
                    var fm = FatherRef.Match(line);
                    if (fm.Success && long.TryParse(fm.Groups[1].Value, out var fatherId))
                    {
                        current.FatherId = fatherId;
                    }
                }

                if (current.ScriptGuid == null && current.ClassId == ClassIdMonoBehaviour)
                {
                    var sm = SharedRegex.ScriptGuid.Match(line);
                    if (sm.Success)
                    {
                        current.ScriptGuid = sm.Groups[1].Value;
                    }
                }

                scanBudget--;
            }

            return map;
        }

        /// <summary>
        /// Resolve the transform path of the GameObject that OWNS the component
        /// at <paramref name="componentAnchor"/>. Walks m_Father up the
        /// transform chain, joining GameObject names with '/'. Returns null when
        /// the owning GameObject or its transform chain cannot be resolved
        /// (missing m_GameObject ref, a transform cycle guard hit, or the
        /// anchors are absent from the map). Bounded by MaxPathDepth to defeat
        /// pathological/cyclic hierarchies.</summary>
        public static string ResolveTransformPath(
            long componentAnchor,
            Dictionary<long, AnchorMetadata> map)
        {
            if (map == null || map.Count == 0 || !map.TryGetValue(componentAnchor, out var comp))
                return null;

            // The component's owning GameObject.
            long goId = comp.ClassId == ClassIdGameObject ? comp.Anchor : comp.GameObjectId;
            if (goId == 0 || !map.TryGetValue(goId, out var go)) return null;

            // Find the transform/RectTransform whose m_GameObject == goId to
            // start the father walk. GameObjects themselves carry no m_Father;
            // their sibling Transform object does.
            long transformId = 0;
            foreach (var kvp in map)
            {
                var meta = kvp.Value;
                if ((meta.ClassId == ClassIdTransform || meta.ClassId == ClassIdRectTransform)
                    && meta.GameObjectId == goId)
                {
                    transformId = meta.Anchor;
                    break;
                }
            }

            // Walk m_Father up, collecting names. Guard against cycles and depth.
            const int maxPathDepth = 64;
            var names = new List<string>(8);
            long cursor = transformId;
            var visited = new HashSet<long>();
            while (cursor != 0 && map.TryGetValue(cursor, out var tmeta))
            {
                if (!visited.Add(cursor)) break; // cycle guard
                if (names.Count >= maxPathDepth) break;
                // The transform's GameObject gives us the name at this level.
                if (tmeta.GameObjectId != 0 && map.TryGetValue(tmeta.GameObjectId, out var tgo) && tgo.Name != null)
                    names.Add(tgo.Name);
                cursor = tmeta.FatherId;
            }

            if (names.Count == 0) return go.Name;
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
