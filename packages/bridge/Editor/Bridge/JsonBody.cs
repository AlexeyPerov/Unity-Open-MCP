using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityOpenMcpBridge
{
    public static class JsonBody
    {
        public static string GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;
            if (start + 3 < json.Length && json[start] == 'n' && json[start + 1] == 'u' && json[start + 2] == 'l' && json[start + 3] == 'l')
                return null;
            if (json[start] != '"') return null;
            start++;
            return ReadQuotedString(json, ref start);
        }

        private static string ReadQuotedString(string json, ref int i)
        {
            var sb = new StringBuilder(64);
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    var e = json[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                sb.Append((char)Convert.ToUInt16(json.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static string[] GetStringArray(string json, string key)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw == "null") return null;
            if (!raw.StartsWith("[")) return null;
            var items = new List<string>();
            var i = 1;
            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                if (i >= raw.Length || raw[i] == ']') break;
                if (raw[i] == '"')
                {
                    i++;
                    var val = ReadQuotedString(raw, ref i);
                    items.Add(val);
                }
                else if (raw[i] == 'n' && i + 3 < raw.Length && raw[i + 1] == 'u' && raw[i + 2] == 'l' && raw[i + 3] == 'l')
                {
                    items.Add(null);
                    i += 4;
                }
                else
                {
                    var start = i;
                    while (i < raw.Length && raw[i] != ',' && raw[i] != ']') i++;
                    items.Add(raw.Substring(start, i - start).Trim());
                }
                while (i < raw.Length && (raw[i] == ',' || char.IsWhiteSpace(raw[i]))) i++;
            }
            return items.ToArray();
        }

        public static bool GetBool(string json, string key, bool defaultValue = false)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return defaultValue;
            raw = raw.Trim();
            if (raw == "true") return true;
            if (raw == "false") return false;
            return defaultValue;
        }

        public static int GetInt(string json, string key, int defaultValue = 0)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return defaultValue;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var val)) return val;
            return defaultValue;
        }

        public static float GetFloat(string json, string key, float defaultValue = 0f)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return defaultValue;
            if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) return val;
            return defaultValue;
        }

        public static long GetLong(string json, string key, long defaultValue = 0)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return defaultValue;
            if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var val)) return val;
            return defaultValue;
        }

        /// <summary>
        /// Parse an instance-ID-shaped field that may arrive as a JSON number
        /// OR a JSON string (the canonical form on Unity 6000.5+, where the
        /// 8-byte EntityId exceeds JS Number.MAX_SAFE_INTEGER and is serialized
        /// as a quoted string for lossless round-trip). Returns the parsed
        /// long, or <paramref name="defaultValue"/> when missing/unparseable.
        /// </summary>
        public static long GetLongFlexible(string json, string key, long defaultValue = 0)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return defaultValue;
            var s = raw.Trim();
            // Strip surrounding quotes if present (JSON string form).
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val)) return val;
            return defaultValue;
        }

        /// <summary>
        /// Returns the raw JSON string for each element of an array value whose
        /// elements are objects (e.g. <c>[{"a":1},{"b":2}]</c>). Each returned
        /// string is the inner object text (without surrounding whitespace) and
        /// can be fed back through <see cref="GetString"/>/<see cref="GetLong"/>
        /// etc. Returns null when the key is missing or the value is not an array.
        /// </summary>
        public static string[] GetObjectArray(string json, string key)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw == "null" || !raw.StartsWith("[")) return null;

            var items = new List<string>();
            var i = 0;
            // Skip the opening '['.
            i++;
            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                if (i >= raw.Length || raw[i] == ']') break;

                if (raw[i] == '{')
                {
                    var depth = 1;
                    var start = i;
                    i++;
                    while (i < raw.Length && depth > 0)
                    {
                        if (raw[i] == '"')
                        {
                            i++;
                            while (i < raw.Length)
                            {
                                if (raw[i] == '\\') { i += 2; continue; }
                                if (raw[i] == '"') { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (raw[i] == '{') depth++;
                        else if (raw[i] == '}') depth--;
                        i++;
                    }
                    items.Add(raw.Substring(start, i - start));
                }
                else
                {
                    // Non-object element — skip to the next comma.
                    while (i < raw.Length && raw[i] != ',') i++;
                }

                while (i < raw.Length && (raw[i] == ',' || char.IsWhiteSpace(raw[i]))) i++;
            }
            return items.ToArray();
        }

        public static string GetRawValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;

            if (json[start] == '"')
            {
                var i = start + 1;
                while (i < json.Length)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"') { i++; break; }
                    i++;
                }
                return json.Substring(start, i - start);
            }

            if (json[start] == '[' || json[start] == '{')
            {
                var open = json[start];
                var close = open == '[' ? ']' : '}';
                var depth = 1;
                var i = start + 1;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == '"')
                    {
                        i++;
                        while (i < json.Length)
                        {
                            if (json[i] == '\\') { i += 2; continue; }
                            if (json[i] == '"') { i++; break; }
                            i++;
                        }
                        continue;
                    }
                    if (json[i] == open) depth++;
                    else if (json[i] == close) depth--;
                    i++;
                }
                return json.Substring(start, i - start);
            }

            var end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
                end++;
            return json.Substring(start, end - start);
        }

        /// <summary>
        /// Enumerate the top-level keys of a JSON object value. Used by the
        /// three-surface gameobject_modify form (T22.1.4) to turn a RFC 7396
        /// merge-patch object like <c>{"mass": 2.0, "useGravity": false}</c> into
        /// the per-field <c>{name, value}</c> entries ApplyFieldPatches consumes.
        /// Returns null when <paramref name="json"/> is not a non-empty object.
        /// Keys are read unescaped (mirrors ReadQuotedString); duplicate keys are
        /// preserved in encounter order.
        /// </summary>
        public static List<string> GetObjectKeys(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var i = 0;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;
            // Empty object "{}".
            var afterOpen = i + 1;
            var j = afterOpen;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j < json.Length && json[j] == '}') return new List<string>(0);

            var keys = new List<string>();
            i = afterOpen;
            while (i < json.Length)
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] == '}') break;
                if (json[i] != '"') { i++; continue; }
                i++;
                var key = ReadQuotedString(json, ref i);
                keys.Add(key);

                // Skip the value: ':' + a balanced JSON value token.
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i < json.Length && json[i] == ':') i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;

                // Consume one value (string / array / object / scalar).
                if (json[i] == '"')
                {
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') { i++; break; }
                        i++;
                    }
                }
                else if (json[i] == '[' || json[i] == '{')
                {
                    var open = json[i];
                    var close = open == '[' ? ']' : '}';
                    var depth = 1;
                    i++;
                    while (i < json.Length && depth > 0)
                    {
                        if (json[i] == '"')
                        {
                            i++;
                            while (i < json.Length)
                            {
                                if (json[i] == '\\') { i += 2; continue; }
                                if (json[i] == '"') { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (json[i] == open) depth++;
                        else if (json[i] == close) depth--;
                        i++;
                    }
                }
                else
                {
                    while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
                }

                while (i < json.Length && (json[i] == ',' || char.IsWhiteSpace(json[i]))) i++;
            }
            return keys.Count == 0 ? null : keys;
        }

        public static List<object> ParseArgsArray(string json, string key)
        {
            var raw = GetRawValue(json, key);
            if (raw == null || raw.Trim() == "null") return null;
            return ParseJsonValues(raw.Trim());
        }

        private static List<object> ParseJsonValues(string jsonArray)
        {
            var result = new List<object>();
            if (!jsonArray.StartsWith("[")) return result;
            var i = 1;
            while (i < jsonArray.Length)
            {
                while (i < jsonArray.Length && char.IsWhiteSpace(jsonArray[i])) i++;
                if (i >= jsonArray.Length || jsonArray[i] == ']') break;
                var (val, next) = ReadJsonValue(jsonArray, i);
                result.Add(val);
                i = next;
                while (i < jsonArray.Length && (jsonArray[i] == ',' || char.IsWhiteSpace(jsonArray[i]))) i++;
            }
            return result;
        }

        private static (object, int) ReadJsonValue(string json, int start)
        {
            var i = start;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return (null, i);

            if (json[i] == '"')
            {
                i++;
                var sb = new StringBuilder(64);
                while (i < json.Length)
                {
                    if (json[i] == '\\')
                    {
                        i++;
                        if (i < json.Length)
                        {
                            switch (json[i])
                            {
                                case '"': sb.Append('"'); break;
                                case '\\': sb.Append('\\'); break;
                                case 'n': sb.Append('\n'); break;
                                case 'r': sb.Append('\r'); break;
                                case 't': sb.Append('\t'); break;
                                default: sb.Append(json[i]); break;
                            }
                        }
                        i++;
                    }
                    else if (json[i] == '"')
                    {
                        return (sb.ToString(), i + 1);
                    }
                    else
                    {
                        sb.Append(json[i]);
                        i++;
                    }
                }
                return (sb.ToString(), i);
            }

            if (json[i] == 't') return (true, i + 4);
            if (json[i] == 'f') return (false, i + 5);
            if (json[i] == 'n') return (null, i + 4);

            if (json[i] == '-' || char.IsDigit(json[i]))
            {
                var end = i;
                while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == 'e' || json[end] == 'E' || json[end] == '+'))
                    end++;
                var numStr = json.Substring(i, end - i);
                if (numStr.Contains('.') || numStr.Contains('e') || numStr.Contains('E'))
                {
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        return (d, end);
                }
                else
                {
                    if (long.TryParse(numStr, out var l))
                        return (l, end);
                }
                return (numStr, end);
            }

            if (json[i] == '{' || json[i] == '[')
            {
                var open = json[i];
                var close = open == '{' ? '}' : ']';
                var depth = 1;
                var end = i + 1;
                while (end < json.Length && depth > 0)
                {
                    if (json[end] == '"')
                    {
                        end++;
                        while (end < json.Length)
                        {
                            if (json[end] == '\\') { end += 2; continue; }
                            if (json[end] == '"') { end++; break; }
                            end++;
                        }
                        continue;
                    }
                    if (json[end] == open) depth++;
                    else if (json[end] == close) depth--;
                    end++;
                }
                return (json.Substring(i, end - i), end);
            }

            return (null, i + 1);
        }
    }
}
