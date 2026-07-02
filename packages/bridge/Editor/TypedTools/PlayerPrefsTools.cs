// M20 Plan 9 / T20.9.2 — Key-Value preferences: PlayerPrefs + EditorPrefs tools.
//
// Six typed tools covering the per-project KV preferences surface:
//
//   playerprefs_get    — read-only: get a value by key. Type (int/float/string)
//                        is inferred from the stored value when `type` is
//                        omitted (probes int → float → string in that order).
//   playerprefs_set    — mutate: set a key to a typed value and persist.
//   playerprefs_delete — mutate: delete a single key.
//   editorprefs_get    — read-only: same shape over UnityEditor.EditorPrefs.
//   editorprefs_set    — mutate: same shape (writes through immediately).
//   editorprefs_delete — mutate: delete a single key.
//
// PlayerPrefs + EditorPrefs are built-in (UnityEngine.CoreModule /
// UnityEditor.CoreModule) and present in every Unity install, so these tools
// ship UNGATED — no package define, no sub-asmdef. They belong to the
// `build-settings` tool group (KV preferences ride alongside the existing
// ProjectSettings mutators so the whole "project configuration" surface
// activates together).
//
// Gate routing: prefs write to the registry / Library/PlayerPreferences, NOT
// to project assets — the gate (asset-reference validation) has nothing to
// validate. These mirror the existing non-asset editor-state mutators
// (editor_undo / editor_redo / editor_set_state): they are mutating editor
// state (catalog / toggle / activity reflect the mutation) but route as
// direct-response tools (gate-free). playerprefs_set calls PlayerPrefs.Save()
// so the change persists; EditorPrefs writes through immediately. No
// paths_hint is required — there is no project-asset scope to bind.
//
// Deliberate omission: `playerprefs_delete_all`. An irreversible project-wide
// wipe with no key filter is too dangerous for a single-call tool — route it
// through execute_csharp with an explicit confirm. Documented in the skill.
//
// Naming: `unity_open_mcp_playerprefs_<action>` / `editorprefs_<action>`
// (snake_case domain prefixes). NOT registry-discovered: wired into
// BridgeHttpServer.DispatchTool so the snake_case JSON parses the same way as
// the other settings_* mutators.
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.TypedTools
{
    // M20 Plan 9 / T20.9.2 — typed KV preferences tools. PlayerPrefs + EditorPrefs
    // share one implementation; the EditorPrefs variants swap the backing store.
    public static class PlayerPrefsTools
    {
        // ============================ PlayerPrefs ==========================

        // Read-only: get a value by key. Type is inferred (int → float → string)
        // when omitted. Gate-free direct-response read.
        public static ToolDispatchResult PlayerPrefsGet(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            var requestedType = JsonBody.GetString(body, "type");

            return ReadPref(key, requestedType, isEditor: false);
        }

        // Mutating: set a key to a typed value and persist. Direct-response
        // (no asset write — see file header). Calls PlayerPrefs.Save().
        public static ToolDispatchResult PlayerPrefsSet(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            var type = JsonBody.GetString(body, "type");
            var valueRaw = JsonBody.GetRawValue(body, "value");
            if (valueRaw == null)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'value' is required.");
            if (!TryNormalizeType(ref type))
                return ToolDispatchResult.Fail("invalid_type",
                    $"'type' must be 'int', 'float', or 'string' (got '{type}').");

            try
            {
                switch (type)
                {
                    case "int":
                        PlayerPrefs.SetInt(key, AsInt(valueRaw));
                        break;
                    case "float":
                        PlayerPrefs.SetFloat(key, AsFloat(valueRaw));
                        break;
                    default:
                        PlayerPrefs.SetString(key, AsString(valueRaw));
                        break;
                }
                // Record the type in a companion key so a later type-less Get
                // can recover it. PlayerPrefs stores int and float in the same
                // 4-byte slot with no type tag, so probing the value cannot
                // reliably distinguish them (3.14 read as int ≈ 1e9; 42 read as
                // float ≈ 5.9e-44). The side channel is the source of truth.
                PlayerPrefs.SetString(TypeTagKey(key), type);
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }

            return BuildSetOk(key, type, valueRaw, isEditor: false);
        }

        // Mutating: delete a single key and persist. Direct-response.
        public static ToolDispatchResult PlayerPrefsDelete(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            try
            {
                bool existed = PlayerPrefs.HasKey(key);
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.DeleteKey(TypeTagKey(key));
                PlayerPrefs.Save();
                var sb = new StringBuilder(96);
                sb.Append("{\"status\":\"ok\",\"store\":\"playerprefs\"");
                sb.Append(",\"key\":").Append(Q(key));
                sb.Append(",\"existed\":").Append(existed ? "true" : "false");
                sb.Append(",\"deleted\":true}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ============================ EditorPrefs ==========================

        // Read-only: get an EditorPrefs value by key (writes through, no Save).
        public static ToolDispatchResult EditorPrefsGet(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            var requestedType = JsonBody.GetString(body, "type");

            return ReadPref(key, requestedType, isEditor: true);
        }

        // Mutating: set an EditorPrefs key (writes through immediately).
        public static ToolDispatchResult EditorPrefsSet(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            var type = JsonBody.GetString(body, "type");
            var valueRaw = JsonBody.GetRawValue(body, "value");
            if (valueRaw == null)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'value' is required.");
            if (!TryNormalizeType(ref type))
                return ToolDispatchResult.Fail("invalid_type",
                    $"'type' must be 'int', 'float', or 'string' (got '{type}').");

            try
            {
                switch (type)
                {
                    case "int":
                        EditorPrefs.SetInt(key, AsInt(valueRaw));
                        break;
                    case "float":
                        EditorPrefs.SetFloat(key, AsFloat(valueRaw));
                        break;
                    default:
                        EditorPrefs.SetString(key, AsString(valueRaw));
                        break;
                }
                // Record the type in a companion key (see PlayerPrefsSet for the
                // rationale: the same 4-byte slot ambiguity applies to EditorPrefs).
                EditorPrefs.SetString(TypeTagKey(key), type);
                // EditorPrefs writes through on every Set call — no Save().
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }

            return BuildSetOk(key, type, valueRaw, isEditor: true);
        }

        // Mutating: delete a single EditorPrefs key.
        public static ToolDispatchResult EditorPrefsDelete(string body)
        {
            var key = JsonBody.GetString(body, "key");
            if (string.IsNullOrEmpty(key))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'key' is required.");
            try
            {
                bool existed = EditorPrefs.HasKey(key);
                EditorPrefs.DeleteKey(key);
                EditorPrefs.DeleteKey(TypeTagKey(key));
                var sb = new StringBuilder(96);
                sb.Append("{\"status\":\"ok\",\"store\":\"editorprefs\"");
                sb.Append(",\"key\":").Append(Q(key));
                sb.Append(",\"existed\":").Append(existed ? "true" : "false");
                sb.Append(",\"deleted\":true}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ============================ Shared helpers =======================

        // Read a pref, inferring the type when omitted. PlayerPrefs.HasKey is
        // type-agnostic, so when no type is given we probe int → float →
        // string and report whichever hit. EditorPrefs works the same way.
        private static ToolDispatchResult ReadPref(string key, string requestedType, bool isEditor)
        {
            string store = isEditor ? "editorprefs" : "playerprefs";
            bool hasKey = isEditor ? EditorPrefs.HasKey(key) : PlayerPrefs.HasKey(key);
            if (!hasKey)
                return ToolDispatchResult.Fail("key_not_found",
                    $"No {store} key '{key}'.");

            string type = requestedType;
            if (string.IsNullOrEmpty(type))
            {
                // Prefer the companion type tag written by Set — it is the only
                // reliable signal, since PlayerPrefs/EditorPrefs store int and
                // float in the same 4-byte slot with no type discrimination.
                var tag = isEditor ? EditorPrefs.GetString(TypeTagKey(key), "")
                                   : PlayerPrefs.GetString(TypeTagKey(key), "");
                if (tag == "int" || tag == "float" || tag == "string")
                {
                    type = tag;
                }
                else
                {
                    // Fall back to value-probing for keys written by other tools
                    // (no type tag). Order matters: a value stored as int also
                    // satisfies float, so probe int first.
                    if (isEditor)
                    {
                        type = EditorPrefProbeInt(key) ? "int"
                            : EditorPrefProbeFloat(key) ? "float"
                            : "string";
                    }
                    else
                    {
                        type = PlayerPrefsProbeInt(key) ? "int"
                            : PlayerPrefsProbeFloat(key) ? "float"
                            : "string";
                    }
                }
            }
            else
            {
                if (!TryNormalizeType(ref type))
                    return ToolDispatchResult.Fail("invalid_type",
                        $"'type' must be 'int', 'float', or 'string' (got '{requestedType}').");
            }

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"store\":").Append(Q(store));
            sb.Append(",\"key\":").Append(Q(key));
            sb.Append(",\"type\":").Append(Q(type));
            sb.Append(",\"value\":");
            switch (type)
            {
                case "int":
                    sb.Append(isEditor ? EditorPrefs.GetInt(key) : PlayerPrefs.GetInt(key));
                    break;
                case "float":
                    sb.Append(Num(isEditor ? EditorPrefs.GetFloat(key) : PlayerPrefs.GetFloat(key)));
                    break;
                default:
                    sb.Append(Q(isEditor ? EditorPrefs.GetString(key) : PlayerPrefs.GetString(key)));
                    break;
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        private static ToolDispatchResult BuildSetOk(string key, string type, string valueRaw, bool isEditor)
        {
            string store = isEditor ? "editorprefs" : "playerprefs";
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"store\":").Append(Q(store));
            sb.Append(",\"key\":").Append(Q(key));
            sb.Append(",\"type\":").Append(Q(type));
            sb.Append(",\"value\":");
            switch (type)
            {
                case "int":
                    sb.Append(AsInt(valueRaw));
                    break;
                case "float":
                    sb.Append(Num(AsFloat(valueRaw)));
                    break;
                default:
                    sb.Append(Q(AsString(valueRaw)));
                    break;
            }
            sb.Append(",\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Namespaced companion key recording the type a value was stored as, so
        // a type-less read can recover it without the unreliable value-probe.
        // The prefix is namespaced to avoid colliding with user keys.
        private static string TypeTagKey(string key) => key + "__uom_type";

        // PlayerPrefs.HasKey is type-agnostic; the only type-discriminating
        // API is the typed getter. We probe by reading and comparing back —
        // PlayerPrefs round-trips ints/floats exactly, so if SetInt(k,v) /
        // GetInt(k) matches we treat it as an int slot. This mirrors the
        // "inferred from the stored value" contract.
        private static bool PlayerPrefsProbeInt(string key)
        {
            int v = PlayerPrefs.GetInt(key, int.MinValue);
            return PlayerPrefs.GetInt(key, int.MinValue) == v
                && PlayerPrefs.GetString(key, "__probe__") != PlayerPrefs.GetString(key, "");
        }

        private static bool PlayerPrefsProbeFloat(string key)
        {
            float v = PlayerPrefs.GetFloat(key, float.NaN);
            return !float.IsNaN(v)
                && PlayerPrefs.GetString(key, "__probe__") != PlayerPrefs.GetString(key, "");
        }

        private static bool EditorPrefProbeInt(string key)
        {
            int v = EditorPrefs.GetInt(key, int.MinValue);
            return EditorPrefs.GetInt(key, int.MinValue) == v
                && EditorPrefs.GetString(key, "__probe__") != EditorPrefs.GetString(key, "");
        }

        private static bool EditorPrefProbeFloat(string key)
        {
            float v = EditorPrefs.GetFloat(key, float.NaN);
            return !float.IsNaN(v)
                && EditorPrefs.GetString(key, "__probe__") != EditorPrefs.GetString(key, "");
        }

        private static bool TryNormalizeType(ref string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            var t = type.Trim().Trim('"').ToLowerInvariant();
            if (t == "int" || t == "integer") { type = "int"; return true; }
            if (t == "float" || t == "double") { type = "float"; return true; }
            if (t == "string" || t == "str") { type = "string"; return true; }
            return false;
        }

        // Value coercion helpers (mirror BuildSettingsTools — same raw-JSON
        // value shape: strings arrive quoted, numbers/bools bare).
        private static string AsString(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return "";
            var v = valueRaw.Trim();
            if (v == "null") return "";
            if (v.StartsWith("\"") && v.EndsWith("\"") && v.Length >= 2)
                return v.Substring(1, v.Length - 2);
            return v;
        }

        private static int AsInt(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return 0;
            if (int.TryParse(valueRaw.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v)) return v;
            if (float.TryParse(valueRaw.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var f)) return (int)f;
            return 0;
        }

        private static float AsFloat(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return 0f;
            if (float.TryParse(valueRaw.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v)) return v;
            return 0f;
        }

        private static string Num(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Q(string s) => s == null ? "\"\"" : "\"" + EscStr(s) + "\"";

        private static string EscStr(string s)
        {
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
