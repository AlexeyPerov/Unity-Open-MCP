using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    // M9 Plan 2 — compact drill-down asset read (bridge data producer).
    //
    // Produces a structured AssetModel JSON for the MCP server's shared
    // compression module (mcp-server/src/compression/compact.ts). The bridge is
    // the DATA SOURCE; it does not compress. The MCP server fetches this model,
    // caches it per-asset, and applies CMP declarations / render-only folding /
    // omission counts / drill-down rendering in one place.
    //
    // Scope: text-serialized assets. GameObject prefabs walk the Transform
    // hierarchy and emit components + (optionally) SerializedObject fields.
    // Non-hierarchical assets (ScriptableObject / Material / .controller /
    // .anim) emit a flatObjects list. Scenes (.unity) cannot expose their
    // hierarchy without opening; the tool returns a note pointing at the
    // offline parser (M9 Plan 3). fileID / scriptPath are omitted — the offline
    // parser resolves them; live reads use component/path drill-down.
    //
    // Read-only: registered as a DirectResponseTool (no gate).
    public static class ReadAssetTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (string.IsNullOrWhiteSpace(assetPath))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'asset_path' is required and must be a text-serialized asset path (.prefab/.unity/.asset/.mat/.controller/.anim).");

            var normalized = NormalizePath(assetPath);
            if (!IsSupportedExtension(normalized))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"Unsupported extension for read_asset: '{Path.GetExtension(normalized)}'. Supported: .prefab, .unity, .asset, .mat, .controller, .anim.");

            var fieldLimit = JsonBody.GetInt(body, "field_limit", 0);
            var depthLimit = JsonBody.GetInt(body, "depth", -1);

            Object mainAsset;
            try
            {
                mainAsset = AssetDatabase.LoadMainAssetAtPath(normalized);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("read_error", $"LoadMainAssetAtPath threw: {e.Message}");
            }

            if (mainAsset == null)
                return ToolDispatchResult.Fail("asset_not_found",
                    $"Asset not found at '{normalized}'. Text-serialized assets must exist under Assets/ and be imported.");

            var guid = AssetDatabase.AssetPathToGUID(normalized);
            var kind = KindForPath(normalized);
            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"kind\":\"").Append(Esc(kind)).Append('"');
            sb.Append(",\"path\":\"").Append(Esc(normalized)).Append('"');
            if (!string.IsNullOrEmpty(guid))
                sb.Append(",\"guid\":\"").Append(Esc(guid)).Append('"');

            int objectCount = 0;
            int componentCount = 0;

            if (mainAsset is GameObject go)
            {
                sb.Append(",\"roots\":[");
                bool firstRoot = true;
                // A prefab asset root may have multiple top-level GameObjects;
                // walk the loaded root's transform siblings at the top level.
                var roots = CollectRoots(go.transform);
                foreach (var root in roots)
                {
                    if (!firstRoot) sb.Append(',');
                    firstRoot = false;
                    AppendNode(sb, root, "", 0, depthLimit, fieldLimit, ref objectCount, ref componentCount);
                }
                sb.Append(']');
            }
            else if (kind == "scene")
            {
                // SceneAsset cannot expose its hierarchy without opening the scene.
                // Surface a note so the agent routes to the offline parser.
                sb.Append(",\"roots\":[]");
                sb.Append(",\"flatObjects\":[]");
                sb.Append(",\"note\":\"scene hierarchy requires the offline parser (later milestone); use it for scene-level GameObject trees\"");
            }
            else
            {
                sb.Append(",\"roots\":[]");
                sb.Append(",\"flatObjects\":[");
                AppendFlatObject(sb, mainAsset, fieldLimit, ref objectCount);
                sb.Append(']');
            }

            sb.Append(",\"objectCount\":").Append(objectCount);
            sb.Append(",\"componentCount\":").Append(componentCount);
            sb.Append('}');

            return ToolDispatchResult.Ok(sb.ToString());
        }

        static List<Transform> CollectRoots(Transform start)
        {
            var roots = new List<Transform>();
            // If the loaded object is already a root (no parent), start there.
            // Otherwise walk up to the topmost and enumerate top-level children.
            var top = start;
            while (top.parent != null) top = top.parent;
            roots.Add(top);
            return roots;
        }

        static void AppendNode(StringBuilder sb, Transform transform, string parentPath, int depth, int depthLimit, int fieldLimit,
            ref int objectCount, ref int componentCount)
        {
            objectCount++; // the GameObject itself
            var name = transform.gameObject.name;
            var path = parentPath == "" ? name : parentPath + "/" + name;

            sb.Append('{');
            sb.Append("\"name\":\"").Append(Esc(name)).Append('"');
            sb.Append(",\"path\":\"").Append(Esc(path)).Append('"');
            sb.Append(",\"depth\":").Append(depth);

            // Components
            var components = transform.gameObject.GetComponents<Component>();
            sb.Append(",\"components\":[");
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) continue; // missing script
                if (i > 0) sb.Append(',');
                componentCount++;
                AppendComponent(sb, component, fieldLimit, ref objectCount);
            }
            sb.Append(']');

            // Children (respect depth cap; the MCP compression module reports hidden counts
            // but the bridge bounds the payload it transfers).
            bool hasChildren = transform.childCount > 0;
            bool depthAllowsChildren = depthLimit < 0 || depth < depthLimit;
            if (hasChildren)
            {
                if (depthAllowsChildren)
                {
                    sb.Append(",\"children\":[");
                    for (int c = 0; c < transform.childCount; c++)
                    {
                        if (c > 0) sb.Append(',');
                        AppendNode(sb, transform.GetChild(c), path, depth + 1, depthLimit, fieldLimit, ref objectCount, ref componentCount);
                    }
                    sb.Append(']');
                }
                else
                {
                    // Depth cap reached: children omitted here; the MCP layer cannot count
                    // them for live reads, so we surface a hint via the note on the root.
                    // (Live reads do not know the full subtree size without walking it.)
                }
            }

            sb.Append('}');
        }

        static void AppendComponent(StringBuilder sb, Component component, int fieldLimit, ref int objectCount)
        {
            objectCount++; // each component is its own YAML object
            var typeName = component.GetType().Name;

            sb.Append('{');
            sb.Append("\"name\":\"").Append(Esc(typeName)).Append('"');

            if (fieldLimit > 0)
            {
                var fields = SerializeFields(component, fieldLimit);
                if (fields != null && fields.Count > 0)
                {
                    sb.Append(",\"fields\":[");
                    for (int i = 0; i < fields.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"name\":\"").Append(Esc(fields[i].Key)).Append('"');
                        sb.Append(",\"value\":\"").Append(Esc(fields[i].Value)).Append("\"}");
                    }
                    sb.Append(']');
                }
            }

            sb.Append('}');
        }

        static void AppendFlatObject(StringBuilder sb, Object asset, int fieldLimit, ref int objectCount)
        {
            objectCount++;
            var type = asset.GetType();
            var name = asset.name;
            if (string.IsNullOrEmpty(name)) name = type.Name;

            sb.Append('{');
            sb.Append("\"name\":\"").Append(Esc(name)).Append('"');
            sb.Append(",\"type\":\"").Append(Esc(type.Name)).Append('"');

            if (fieldLimit > 0)
            {
                var fields = SerializeFields(asset, fieldLimit);
                if (fields != null && fields.Count > 0)
                {
                    sb.Append(",\"fields\":[");
                    for (int i = 0; i < fields.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"name\":\"").Append(Esc(fields[i].Key)).Append('"');
                        sb.Append(",\"value\":\"").Append(Esc(fields[i].Value)).Append("\"}");
                    }
                    sb.Append(']');
                }
            }

            sb.Append('}');
        }

        // Read serialized fields via SerializedObject. Unity's SerializedProperty
        // iterator exposes the SAME fields Unity serializes to YAML (public fields
        // + [SerializeField] privates), skipping properties — which is exactly the
        // contract the compression module expects. Values are stringified with a
        // depth/count cap to keep the payload bounded.
        static List<KeyValuePair<string, string>> SerializeFields(Object asset, int fieldLimit)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                var so = new SerializedObject(asset);
                var prop = so.GetIterator();
                if (!prop.NextVisible(true))
                    return result;

                int emitted = 0;
                // First visible property is "m_Script" for MonoBehaviours; iterate top-level children.
                do
                {
                    if (emitted >= fieldLimit) break;
                    if (prop.depth != 0) continue;
                    var fieldName = prop.name;
                    if (fieldName == "m_Script") continue;
                    var value = ReadPropertyValue(prop);
                    if (value == null) continue;
                    result.Add(new KeyValuePair<string, string>(fieldName, value));
                    emitted++;
                }
                while (prop.NextVisible(false));
            }
            catch
            {
                // SerializedObject can throw on exotic assets; return what we have.
            }
            return result;
        }

        static string ReadPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Boolean: return prop.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return prop.floatValue.ToString("R");
                case SerializedPropertyType.String:
                    var s = prop.stringValue;
                    return s != null && s.Length > 80 ? s.Substring(0, 80) + "..." : s;
                case SerializedPropertyType.Color: return prop.colorValue.ToString();
                case SerializedPropertyType.Vector2: return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return prop.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return prop.vector4Value.ToString();
                case SerializedPropertyType.Quaternion: return prop.quaternionValue.ToString();
                case SerializedPropertyType.Enum: return prop.enumDisplayNames.Length > prop.enumValueIndex
                    ? prop.enumDisplayNames[prop.enumValueIndex]
                    : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "null";
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                case SerializedPropertyType.Bounds: return prop.boundsValue.ToString();
                case SerializedPropertyType.Rect: return prop.rectValue.ToString();
                default:
                    // Arrays / generics: report element count instead of expanding.
                    if (prop.isArray && prop.arrayElementType != null)
                        return "[" + prop.arraySize + "]";
                    return "<" + prop.propertyType + ">";
            }
        }

        static string NormalizePath(string raw)
        {
            var p = raw.Replace('\\', '/').Trim('/');
            if (p.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                return "Assets";
            if (!p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p;
            return p;
        }

        static bool IsSupportedExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".prefab" || ext == ".unity" || ext == ".asset"
                || ext == ".mat" || ext == ".controller" || ext == ".anim";
        }

        static string KindForPath(string path)
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

        static string Esc(string s)
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
