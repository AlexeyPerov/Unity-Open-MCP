// M20 Plan 4 / T20.4 — Terrain embedded domain tools.
//
// Shared helpers for the Terrain embedded domain tools: target resolution,
// JSON envelope builders, and the 2D-array parsing the heightmap / splatmap /
// tree-instance tools compose. The Terrain / TerrainData / TreePrototype /
// DetailPrototype / TerrainLayer types live in the built-in engine modules
// (UnityEngine.TerrainModule for Terrain / TerrainData, UnityEngine.CoreModule
// for the asset types) and are present in every Unity install, so this domain
// ships UNGATED — no UNITY_OPEN_MCP_EXT_TERRAIN define, no sub-asmdef
// defineConstraints. The `terrain` tool group is still hidden from ListTools
// until the session activates it via unity_open_mcp_manage_tools (group
// visibility is a session concern, independent of compile-gating).
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Terrain
{
    // Shared JSON envelope + escape helpers. Mirrors ConstraintsJson /
    // LightingJson / AudioJson so each embedded domain has a self-contained
    // helper it can evolve independently.
    static class TerrainJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
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
            sb.Append('"');
            return sb.ToString();
        }

        // Format a float in invariant culture (no thousands separators,
        // always a decimal point). Used for heightmap values in the response.
        public static string Num(float f)
            => f.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // Target resolver for Terrain tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name) so agents reuse the
    // same addressing they learned for gameobject_* / component_*.
    static class TerrainTargets
    {
        public static GameObject Resolve(int instanceId, string path, string name)
        {
            // instance_id wins.
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
            }

            // path (slash-separated hierarchy from a root).
            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null) return go;
            }

            // name-only fallback (first active match).
            if (!string.IsNullOrEmpty(name))
            {
                var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
                foreach (var root in roots)
                {
                    if (root.gameObject.name == name) return root.gameObject;
                }
            }

            return null;
        }

        public static GameObject FindByPath(string path)
        {
            var parts = path.Split('/');
            var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
            foreach (var root in roots)
            {
                if (root.gameObject.name == parts[0])
                {
                    var current = root.gameObject;
                    bool match = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var child = current.transform.Find(parts[i]);
                        if (child == null) { match = false; break; }
                        current = child.gameObject;
                    }
                    if (match) return current;
                }
            }
            return null;
        }

        public static string BuildPath(GameObject go)
        {
            var sb = new StringBuilder();
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        // Resolve a Terrain component from a host GameObject. Returns null when
        // the host has no Terrain (the caller surfaces a component_not_found
        // envelope). Terrain is the only component this domain reads/writes.
        public static Terrain ResolveTerrain(int instanceId, string path, string name)
        {
            var host = Resolve(instanceId, path, name);
            return host != null ? host.GetComponent<Terrain>() : null;
        }
    }

    // 2D-array parsing for heightmap + splatmap writes. Both ship a
    // row-major 2D array of floats (heightmap normalized 0-1, splatmap 0-1
    // weights). We accept either a JSON array-of-arrays (the natural shape)
    // or a flat array + explicit width/height (cheaper to author for large
    // regions). The parser returns a 2D float[rows][cols] (row-major) or null
    // on a parse error with a descriptive message.
    static class TerrainArrays
    {
        // Maximum dimension per side for a single heightmap/splat write. The
        // catalog minimum (T20.4 implementation notes) recommends tiling —
        // we refuse arrays larger than this per call with a clear tiling hint.
        public const int MaxDimension = 513;

        // Parse a 2D float array from a JSON string. Accepts:
        //   [[r0c0,r0c1,...],[r1c0,...],...]  (array-of-rows)
        // Returns null + sets error when the JSON is malformed, rows are
        // ragged, or the dimensions exceed MaxDimension.
        public static float[][] ParseFloat2D(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json))
            {
                error = "array is empty.";
                return null;
            }
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
            {
                error = "array must be a JSON array.";
                return null;
            }

            // Split into top-level row elements. Each row is a [...] block.
            var rows = new List<float[]>();
            int i = 1; // skip the opening [
            int depth = 0;
            int rowStart = -1;
            while (i < trimmed.Length - 1)
            {
                var c = trimmed[i];
                if (c == '[')
                {
                    if (depth == 0) rowStart = i + 1;
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0 && rowStart >= 0)
                    {
                        var rowBody = trimmed.Substring(rowStart, i - rowStart);
                        var row = ParseFloatRow(rowBody, out error);
                        if (row == null) return null;
                        rows.Add(row);
                        rowStart = -1;
                    }
                }
                i++;
            }

            if (depth != 0)
            {
                error = "unbalanced brackets in array.";
                return null;
            }
            if (rows.Count == 0)
            {
                error = "array has no rows.";
                return null;
            }
            if (rows.Count > MaxDimension)
            {
                error = $"array has {rows.Count} rows; the per-call cap is " +
                        $"{MaxDimension}x{MaxDimension}. Write in tiles " +
                        $"({MaxDimension}x{MaxDimension} or smaller regions) " +
                        "via x_offset / y_offset instead.";
                return null;
            }
            var cols = rows[0].Length;
            if (cols > MaxDimension)
            {
                error = $"array has {cols} columns; the per-call cap is " +
                        $"{MaxDimension}x{MaxDimension}. Write in tiles " +
                        $"({MaxDimension}x{MaxDimension} or smaller regions) " +
                        "via x_offset / y_offset instead.";
                return null;
            }
            // Verify ragged check.
            for (int r = 1; r < rows.Count; r++)
            {
                if (rows[r].Length != cols)
                {
                    error = $"ragged array: row 0 has {cols} columns but row " +
                            $"{r} has {rows[r].Length}. All rows must match.";
                    return null;
                }
            }
            return rows.ToArray();
        }

        // Parse one row body (the text between [ and ]). Returns null on
        // error. An empty body is an error (a row must have at least one
        // value).
        private static float[] ParseFloatRow(string body, out string error)
        {
            error = null;
            var trimmed = body.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                error = "empty row in array.";
                return null;
            }
            var parts = trimmed.Split(',');
            var row = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (!float.TryParse(p, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out row[i]))
                {
                    error = $"could not parse '{p}' as a float in row.";
                    return null;
                }
            }
            return row;
        }

        // Build a Unity heightmap 2D array from the parsed rows. Unity's
        // TerrainData.SetHeights expects float[y,x] (y is the row index,
        // x is the column index), which is exactly our row-major layout.
        public static float[,] ToHeightmap(float[][] rows)
        {
            var h = rows.Length;
            var w = rows[0].Length;
            var grid = new float[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    grid[y, x] = rows[y][x];
            return grid;
        }
    }
}
