using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    // M9 Plan 2 — compact asset search (bridge data producer).
    //
    // Produces a structured SearchModel JSON for the MCP server's shared
    // compression module. The bridge is the DATA SOURCE: it runs
    // AssetDatabase.FindAssets + (optionally) structured GameObject/component
    // inspection and tags each match with the reasons it matched. The MCP
    // server compacts paths (Assets/ prefix dropped), declares an EXT table
    // once, caps object listings, and renders the compact result.
    //
    // Match reasons: file-name / gameobject / component / guid. A file may match
    // for more than one reason (reasons is an array).
    //
    // Read-only: registered as a DirectResponseTool (no gate).
    public static class SearchAssetsTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var name = JsonBody.GetString(body, "name") ?? "";
            var component = JsonBody.GetString(body, "component") ?? "";
            var guid = JsonBody.GetString(body, "guid") ?? "";
            var typeFilter = JsonBody.GetString(body, "type") ?? "";
            var folder = JsonBody.GetString(body, "folder") ?? "Assets";
            var objectLimit = JsonBody.GetInt(body, "object_limit", 12);
            var maxResults = JsonBody.GetInt(body, "max_results", 50);

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(component)
                && string.IsNullOrEmpty(guid))
            {
                return ToolDispatchResult.Fail("missing_parameter",
                    "At least one of 'name', 'component', or 'guid' is required.");
            }

            var guids = FindAssets(folder, typeFilter);
            var matches = new List<MatchRecord>();
            int truncated = 0;

            foreach (var foundGuid in guids)
            {
                if (matches.Count + truncated >= maxResults) { truncated++; continue; }

                var assetPath = AssetDatabase.GUIDToAssetPath(foundGuid);
                if (string.IsNullOrEmpty(assetPath)) continue;

                var record = BuildMatch(assetPath, foundGuid, name, component, guid, objectLimit);
                if (record == null) continue; // no reason matched

                matches.Add(record);
            }

            return ToolDispatchResult.Ok(BuildResult(matches, truncated, name, component, guid, typeFilter));
        }

        class MatchRecord
        {
            public string Path;
            public string Guid;
            public string Kind;
            public List<string> Reasons = new List<string>();
            public List<ObjectMatch> Objects;
        }

        class ObjectMatch
        {
            public string Path;
            public List<string> Components = new List<string>();
        }

        private static string[] FindAssets(string folder, string typeFilter)
        {
            var filter = BuildFilter(typeFilter);
            var guids = AssetDatabase.FindAssets(filter, new[] { folder });
            return guids;
        }

        private static string BuildFilter(string typeFilter)
        {
            // AssetDatabase.FindAssets takes a single filter string. When a type
            // filter is supplied, prefix with "t:Kind" terms. Otherwise search
            // text-serialized asset kinds only (prefab/scene/asset/mat/controller/anim).
            if (!string.IsNullOrEmpty(typeFilter))
            {
                var parts = typeFilter.Split(',');
                var sb = new StringBuilder();
                for (int i = 0; i < parts.Length; i++)
                {
                    var kind = parts[i].Trim();
                    if (kind.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append("t:").Append(kind);
                }
                return sb.ToString();
            }
            // Default: search YAML asset kinds (not scripts/textures/etc) to keep the
            // result set focused on assets an agent would drill into.
            return "t:Prefab t:Scene t:Material t:ScriptableObject t:AnimatorController t:AnimationClip";
        }

        private static MatchRecord BuildMatch(string assetPath, string guid, string name, string component, string guidQuery, int objectLimit)
        {
            var record = new MatchRecord
            {
                Path = assetPath,
                Guid = guid,
                Kind = KindForPath(assetPath),
            };

            bool nameOnFile = !string.IsNullOrEmpty(name) && ContainsFold(assetPath, name);
            bool guidHit = false;

            if (!string.IsNullOrEmpty(guidQuery))
            {
                // Match if the asset's own GUID equals the query, or if the file
                // text references it (cheap text scan for binary formats we skip).
                if (string.Equals(guid, guidQuery, System.StringComparison.OrdinalIgnoreCase))
                {
                    guidHit = true;
                }
                else if (IsTextAsset(assetPath) && FileContainsGuid(assetPath, guidQuery))
                {
                    guidHit = true;
                    record.Reasons.Add("guid");
                }
            }

            if (nameOnFile) record.Reasons.Add("file-name");

            // Structured (gameobject / component) search only for prefabs/scenes.
            var structuredObjects = new List<ObjectMatch>();
            bool structuredMatch = false;
            if (record.Kind == "prefab" || record.Kind == "scene")
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset is GameObject go)
                {
                    WalkForStructured(go.transform, "", name, component, objectLimit, structuredObjects,
                        ref structuredMatch);
                }
                // scenes: hierarchy not walkable without opening; rely on file-name/guid only.
            }

            if (structuredMatch)
            {
                if (!string.IsNullOrEmpty(component) && !string.IsNullOrEmpty(name))
                {
                    // ambiguous which triggered; keep both tags conservative
                }
                if (!string.IsNullOrEmpty(component)) record.Reasons.Add("component");
                if (!string.IsNullOrEmpty(name)) record.Reasons.Add("gameobject");
            }
            else if (!string.IsNullOrEmpty(component) && !string.IsNullOrEmpty(name) && !nameOnFile && !guidHit)
            {
                // component requested but no structured hit and no file-name hit: skip.
                return null;
            }

            if (guidHit && record.Kind != "prefab" && record.Kind != "scene")
            {
                // guid references resolved at file level already tagged above.
            }

            if (record.Reasons.Count == 0
                && !string.IsNullOrEmpty(name) && !nameOnFile
                && !structuredMatch)
            {
                return null;
            }

            if (structuredObjects.Count > 0) record.Objects = structuredObjects;
            return record;
        }

        private static void WalkForStructured(Transform transform, string parentPath, string name, string component,
            int objectLimit, List<ObjectMatch> objects, ref bool matched)
        {
            if (objects.Count >= objectLimit) return;
            var goName = transform.gameObject.name;
            var path = parentPath == "" ? goName : parentPath + "/" + goName;

            var components = transform.gameObject.GetComponents<Component>();
            var compNames = new List<string>();
            bool nameMatch = !string.IsNullOrEmpty(name) && ContainsFold(goName, name);
            bool compMatch = false;
            foreach (var c in components)
            {
                if (c == null) continue;
                var cn = c.GetType().Name;
                compNames.Add(cn);
                if (!string.IsNullOrEmpty(component) && ContainsFold(cn, component)) compMatch = true;
            }

            if (nameMatch || compMatch)
            {
                matched = true;
                if (objects.Count < objectLimit)
                {
                    objects.Add(new ObjectMatch { Path = path, Components = compNames });
                }
            }

            for (int c = 0; c < transform.childCount; c++)
                WalkForStructured(transform.GetChild(c), path, name, component, objectLimit, objects, ref matched);
        }

        private static bool FileContainsGuid(string assetPath, string guid)
        {
            try
            {
                var full = assetPath;
                if (!File.Exists(full)) return false;
                var text = File.ReadAllText(full);
                return text.IndexOf(guid, System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTextAsset(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".prefab" || ext == ".unity" || ext == ".asset"
                || ext == ".mat" || ext == ".controller" || ext == ".anim";
        }

        private static bool ContainsFold(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            return haystack != null && haystack.ToLowerInvariant().Contains(needle.ToLowerInvariant());
        }

        private static string KindForPath(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".prefab": return "prefab";
                case ".unity": return "scene";
                case ".asset": return "asset";
                case ".mat": return "material";
                case ".controller": return "controller";
                case ".anim": return "animation";
                default: return "other";
            }
        }

        private static string BuildResult(List<MatchRecord> matches, int truncated, string name, string component, string guid, string typeFilter)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"query\":{");
            bool first = true;
            if (!string.IsNullOrEmpty(name)) { sb.Append("\"name\":\"").Append(Esc(name)).Append('"'); first = false; }
            if (!string.IsNullOrEmpty(component)) { if (!first) sb.Append(','); sb.Append("\"component\":\"").Append(Esc(component)).Append('"'); first = false; }
            if (!string.IsNullOrEmpty(guid)) { if (!first) sb.Append(','); sb.Append("\"guid\":\"").Append(Esc(guid)).Append('"'); first = false; }
            if (!string.IsNullOrEmpty(typeFilter)) { if (!first) sb.Append(','); sb.Append("\"type\":\"").Append(Esc(typeFilter)).Append('"'); first = false; }
            sb.Append('}');

            sb.Append(",\"matchCount\":").Append(matches.Count + truncated);
            sb.Append(",\"matches\":[");
            for (int i = 0; i < matches.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = matches[i];
                sb.Append('{');
                sb.Append("\"path\":\"").Append(Esc(m.Path)).Append('"');
                sb.Append(",\"kind\":\"").Append(Esc(m.Kind)).Append('"');
                sb.Append(",\"guid\":\"").Append(Esc(m.Guid)).Append('"');
                sb.Append(",\"reasons\":[");
                for (int r = 0; r < m.Reasons.Count; r++)
                {
                    if (r > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(m.Reasons[r])).Append('"');
                }
                sb.Append(']');
                if (m.Objects != null && m.Objects.Count > 0)
                {
                    sb.Append(",\"objects\":[");
                    for (int o = 0; o < m.Objects.Count; o++)
                    {
                        if (o > 0) sb.Append(',');
                        var om = m.Objects[o];
                        sb.Append("{\"path\":\"").Append(Esc(om.Path)).Append('"');
                        sb.Append(",\"components\":[");
                        for (int c = 0; c < om.Components.Count; c++)
                        {
                            if (c > 0) sb.Append(',');
                            sb.Append('"').Append(Esc(om.Components[c])).Append('"');
                        }
                        sb.Append("]}");
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
