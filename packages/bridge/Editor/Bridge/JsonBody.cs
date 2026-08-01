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

        /// <summary>
        /// Reports whether <paramref name="json"/> carries a top-level entry
        /// for <paramref name="key"/>. Unlike <see cref="GetString"/>, this
        /// distinguishes a missing key (false) from <c>"key": null</c> (true),
        /// which callers need when an explicit null must not fall through to a
        /// secondary resolution key (e.g. <c>name_target</c> vs <c>name</c>).
        /// </summary>
        public static bool HasKey(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return false;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            return colonIdx >= 0;
        }

        /// <summary>
        /// Reports whether <paramref name="json"/> carries a top-level entry
        /// for <paramref name="key"/> whose value is NOT an explicit JSON
        /// <c>null</c>. This is the "the caller actually supplied a value"
        /// predicate: it returns false for both a missing key and
        /// <c>"key": null</c>, and true only when a key is present with a
        /// non-null value (a string, number, object, array, bool).
        ///
        /// <para><b>B-N23.</b> LLM tool callers commonly include optional
        /// string fields as <c>null</c> to mean "not specified". A bare
        /// <see cref="HasKey"/> treats <c>"parent_path": null</c> as present,
        /// which made <c>gameobject_set_parent</c> detach to scene root
        /// instead of returning <c>missing_parameter</c>. Callers that want
        /// "the field was provided with a real value" should use this helper,
        /// not <see cref="HasKey"/>.</para>
        /// </summary>
        public static bool HasKeyAndNotNull(string json, string key)
        {
            if (!HasKey(json, key)) return false;
            // Re-locate the value start the same way GetString does and check
            // whether the literal token at that position is `null`.
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return false;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start + 3 < json.Length
                && json[start] == 'n' && json[start + 1] == 'u'
                && json[start + 2] == 'l' && json[start + 3] == 'l')
            {
                return false;
            }
            // Also treat an explicit absent-after-colon tail as not-provided.
            return start < json.Length;
        }

        /// <summary>
        /// Resolve a string field that distinguishes "missing key" from an
        /// explicit <c>null</c>. <paramref name="present"/> is set to true when
        /// the key exists (even if its value is null); false when it is absent.
        /// Use this for precedence chains where an explicit null must win over a
        /// fallback key — <see cref="GetString"/> collapses both cases to null.
        /// </summary>
        public static string TryGetString(string json, string key, out bool present)
        {
            present = HasKey(json, key);
            if (!present) return null;
            return GetString(json, key);
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

        // Return a synthetic JSON object containing ONLY the top-level (depth-1)
        // selector keys and their values, with every patch-array key
        // ("fields"/"entries"/"patches"/"jsonPatches"/"deletes") and its contents
        // dropped. Selector reads (instance_id/path/name/component_instance_id/
        // ...) then never collide with the same key names nested INSIDE a patch
        // value, regardless of the order keys appear in the body.
        //
        // Fixes feedback-fable-31-07 §2: component_modify resolved the host
        // GameObject from the nested fields[].value.instance_id because the
        // substring search in GetRawValue/GetLongFlexible matched the first
        // occurrence anywhere in the body. The original SelectorScope trimmed the
        // body at the first patch-array key, which worked only when the patch
        // array followed every selector — a body emitted with the array first
        // (e.g. {"fields":[...],"instance_id":5}) silently lost the selector.
        // This depth-aware walk collects selector keys wherever they appear at
        // depth 1, so key order no longer matters.
        //
        // Returns the original body when there is no patch-array key (read-only
        // tools, or bodies without patches) so non-mutating callers are
        // unaffected and no allocation happens on the hot read path.
        public static string SelectorScope(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            // Fast path: if no patch-array key appears at all, the body is the
            // scope (read-only tools). IndexOf is safe for the membership check
            // — a false positive (a patch key name nested inside a string) just
            // routes us through the depth-aware walk, which still emits only
            // depth-1 selector pairs.
            bool anyPatchKey = false;
            foreach (var key in PatchArrayKeys)
            {
                if (json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0)
                {
                    anyPatchKey = true;
                    break;
                }
            }
            if (!anyPatchKey) return json;

            // Depth-aware walk. We track whether we are inside a string (so
            // braces/brackets/colons inside string values do not affect depth)
            // and the object/array nesting depth. A key token is a top-level
            // selector only when it sits at object depth 1.
            var scope = new StringBuilder(json.Length);
            scope.Append('{');
            bool first = true;
            int depth = 0;
            bool inString = false;
            bool escape = false;
            int i = 0;
            // Track the start of the current key token (the quoted string right
            // after a '{' or ',') so we can read its name at depth 1.
            int keyStart = -1;

            while (i < json.Length)
            {
                char c = json[i];

                if (inString)
                {
                    if (escape) { escape = false; }
                    else if (c == '\\') { escape = true; }
                    else if (c == '"') { inString = false; }
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    // A quoted string that opens at depth 1 right after '{' or
                    // ',' is a top-level key. Record its token span.
                    if (depth == 1 && keyStart < 0)
                        keyStart = i;
                    i++;
                    continue;
                }

                if (c == '{' || c == '[') { depth++; i++; continue; }
                if (c == '}' || c == ']') { depth--; i++; continue; }

                // A colon at depth 1 separates a top-level key from its value.
                if (c == ':' && depth == 1 && keyStart >= 0)
                {
                    var (keyName, keyEnd) = ReadQuotedToken(json, keyStart);
                    // The value starts after the colon (`i` points at ':' here).
                    var valueStart = i + 1;
                    if (IsPatchArrayKey(keyName))
                    {
                        // Skip this key AND its value (a value may be a nested
                        // object/array/string — scan to its end at depth 1).
                        i = ScanValueEnd(json, valueStart);
                        // After skipping, advance past the trailing comma if any
                        // (handled by the generic comma path below).
                        keyStart = -1;
                        continue;
                    }
                    // Selector key: copy "key":<value> into the scope. Copy from
                    // the key's opening quote through the value end (spanning the
                    // key, colon, and value verbatim) so the emitted pair is
                    // valid JSON without re-serialization.
                    var valueEnd = ScanValueEnd(json, valueStart);
                    if (!first) scope.Append(',');
                    first = false;
                    scope.Append(json, keyStart, valueEnd - keyStart);
                    i = valueEnd;
                    keyStart = -1;
                    continue;
                }

                // Reset the pending-key marker on commas at depth 1 so the next
                // quoted string is recognized as a sibling key.
                if (c == ',' && depth == 1) keyStart = -1;

                i++;
            }

            scope.Append('}');
            return scope.ToString();
        }

        // Read a quoted JSON string token starting at the opening quote `start`
        // (json[start] == '"'). Returns the decoded name and the index just past
        // the closing quote.
        private static (string name, int end) ReadQuotedToken(string json, int start)
        {
            var i = start + 1;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                var c = json[i];
                if (c == '\\')
                {
                    if (i + 1 < json.Length) { sb.Append(json[i + 1]); i += 2; continue; }
                    i++; continue;
                }
                if (c == '"') return (sb.ToString(), i + 1);
                sb.Append(c);
                i++;
            }
            return (sb.ToString(), i);
        }

        // Given a position right after a top-level key's colon, find the index
        // where the value ends (the position of the comma at depth 1 separating
        // it from the next sibling, or the end of the object). Honors strings
        // and nested containers so a value containing ',' '}' ']' is not split.
        private static int ScanValueEnd(string json, int afterColon)
        {
            var i = afterColon;
            // Skip whitespace between colon and value.
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return i;

            char c = json[i];
            if (c == '"')
            {
                i++;
                while (i < json.Length)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"') { i++; break; }
                    i++;
                }
                return i;
            }
            if (c == '{' || c == '[')
            {
                var open = c;
                var close = open == '{' ? '}' : ']';
                var depth = 1;
                i++;
                while (i < json.Length && depth > 0)
                {
                    var cc = json[i];
                    if (cc == '"')
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
                    if (cc == open) depth++;
                    else if (cc == close) depth--;
                    i++;
                }
                return i;
            }
            // Primitive (number/bool/null): read until a depth-1 separator.
            while (i < json.Length)
            {
                var cc = json[i];
                if (cc == ',' || cc == '}' || cc == ']') break;
                i++;
            }
            return i;
        }

        private static bool IsPatchArrayKey(string key)
        {
            foreach (var k in PatchArrayKeys)
                if (k == key) return true;
            return false;
        }

        // Top-level patch-array keys whose contents must be excluded from
        // selector reads. "fields" (component_modify/object_modify),
        // "patches"/"jsonPatches" (gameobject_modify/material), "entries"
        // (assets_copy/assets_move), "deletes" (assets_delete). component_add's
        // "component_types" is a bare-string array (no nested objects), so it
        // cannot shadow a selector and is intentionally not listed.
        private static readonly string[] PatchArrayKeys =
            { "fields", "entries", "patches", "jsonPatches", "deletes" };
    }
}
