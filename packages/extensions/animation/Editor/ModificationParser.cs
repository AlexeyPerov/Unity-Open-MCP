using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityOpenMcpExtensions.AnimationExt
{
    // M16 Plan 10 — modification DTOs + JSON parser shared by
    // AnimationClipTools.Modify and AnimatorTools.Modify.
    //
    // Each modification kind is dispatched by `Type` against a flat
    // `Modification` record. The parser is a hand-rolled shallow JSON reader
    // (the bridge's JsonBody helpers are not visible outside the bridge
    // assembly). Nested arrays (keyframes, conditions) are handled, but nested
    // objects are flattened to {field: value} so the agent surface stays
    // simple and predictable.
    //
    // Used by:
    //   - AnimationClipTools.Modify   (SetCurve / RemoveCurve / ClearCurves /
    //                                  SetFrameRate / SetWrapMode / SetLegacy /
    //                                  AddEvent / ClearEvents)
    //   - AnimatorTools.Modify        (AddParameter / RemoveParameter / AddLayer /
    //                                  RemoveLayer / AddState / RemoveState /
    //                                  SetDefaultState / AddTransition /
    //                                  RemoveTransition / AddAnyStateTransition /
    //                                  SetStateMotion / SetStateSpeed)

    public class Modification
    {
        public string Type;
        // AnimationClip shared fields.
        public string ComponentType;
        public string PropertyName;
        public string RelativePath;
        public List<KeyframeSpec> Keyframes;
        public float? FrameRate;
        public WrapMode? WrapMode;
        public bool? Legacy;
        // AnimationEvent fields.
        public float? Time;
        public string FunctionName;
        public float? FloatParameter;
        public int? IntParameter;
        public string StringParameter;
        // Animator shared fields.
        public string LayerName;
        public string StateName;
        public string SourceStateName;
        public string DestinationStateName;
        public string ParameterName;
        public string ParameterType;
        public float? DefaultFloat;
        public int? DefaultInt;
        public bool? DefaultBool;
        public string MotionAssetPath;
        public float? Speed;
        public bool? HasExitTime;
        public float? ExitTime;
        public float? Duration;
        public bool? HasFixedDuration;
        public List<ConditionSpec> Conditions;
    }

    public class KeyframeSpec
    {
        public float Time;
        public float Value;
        public float InTangent;
        public float OutTangent;
    }

    public class ConditionSpec
    {
        public string Parameter;
        public string Mode;
        public float? Threshold;
    }

    static class ModificationParser
    {
        // Parse a JSON array of modification objects into a List<Modification>.
        // Returns null on a malformed outer array.
        public static List<Modification> ParseArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;

            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            var mods = new List<Modification>();
            if (inner.Length == 0) return mods;

            // Walk the array, splitting on top-level commas (depth-aware so
            // nested arrays/objects are not split).
            int i = 0;
            while (i < inner.Length)
            {
                while (i < inner.Length && (inner[i] == ',' || char.IsWhiteSpace(inner[i]))) i++;
                if (i >= inner.Length) break;
                if (inner[i] != '{') return null;

                int depth = 0;
                int start = i;
                while (i < inner.Length)
                {
                    if (inner[i] == '{') depth++;
                    else if (inner[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }

                var body = inner.Substring(start, i - start);
                var mod = ParseObject(body);
                if (mod == null) return null;
                mods.Add(mod);
            }
            return mods;
        }

        // Parse a single { ... } modification object. Each field is read by
        // name; the parser tolerates unknown fields and reorders.
        private static Modification ParseObject(string obj)
        {
            if (string.IsNullOrEmpty(obj)) return null;
            obj = obj.Trim();
            if (!obj.StartsWith("{") || !obj.EndsWith("}")) return null;
            var body = obj.Substring(1, obj.Length - 2);

            var fields = new Dictionary<string, string>();
            var nested = new Dictionary<string, string>(); // for arrays (keyframes, conditions)
            int i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && (body[i] == ',' || char.IsWhiteSpace(body[i]))) i++;
                if (i >= body.Length) break;
                if (body[i] != '"') return null;

                int keyStart = i + 1;
                int keyEnd = keyStart;
                while (keyEnd < body.Length)
                {
                    if (body[keyEnd] == '\\' && keyEnd + 1 < body.Length) { keyEnd += 2; continue; }
                    if (body[keyEnd] == '"') break;
                    keyEnd++;
                }
                var key = Unescape(body.Substring(keyStart, keyEnd - keyStart));
                i = keyEnd + 1;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length || body[i] != ':') return null;
                i++;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;

                // Value: string, scalar, or array.
                if (body[i] == '"')
                {
                    int vEnd = i + 1;
                    while (vEnd < body.Length)
                    {
                        if (body[vEnd] == '\\' && vEnd + 1 < body.Length) { vEnd += 2; continue; }
                        if (body[vEnd] == '"') break;
                        vEnd++;
                    }
                    fields[key] = "\"" + body.Substring(i + 1, vEnd - i - 1) + "\"";
                    i = vEnd + 1;
                }
                else if (body[i] == '[')
                {
                    int depth = 0;
                    int vStart = i;
                    while (i < body.Length)
                    {
                        if (body[i] == '[') depth++;
                        else if (body[i] == ']')
                        {
                            depth--;
                            if (depth == 0) { i++; break; }
                        }
                        i++;
                    }
                    nested[key] = body.Substring(vStart, i - vStart);
                }
                else if (body[i] == '{')
                {
                    int depth = 0;
                    int vStart = i;
                    while (i < body.Length)
                    {
                        if (body[i] == '{') depth++;
                        else if (body[i] == '}')
                        {
                            depth--;
                            if (depth == 0) { i++; break; }
                        }
                        i++;
                    }
                    // Flatten single-level objects into their own field set.
                    nested[key] = body.Substring(vStart, i - vStart);
                }
                else
                {
                    int vStart = i;
                    while (i < body.Length && body[i] != ',' && body[i] != '}') i++;
                    fields[key] = body.Substring(vStart, i - vStart).Trim();
                }
            }

            var mod = new Modification();
            mod.Type = Str(fields, "type");
            mod.ComponentType = Str(fields, "componentType");
            mod.PropertyName = Str(fields, "propertyName");
            mod.RelativePath = Str(fields, "relativePath");
            mod.FrameRate = Float(fields, "frameRate");
            mod.WrapMode = Enum<WrapMode>(fields, "wrapMode");
            mod.Legacy = Bool(fields, "legacy");
            mod.Time = Float(fields, "time");
            mod.FunctionName = Str(fields, "functionName");
            mod.FloatParameter = Float(fields, "floatParameter");
            mod.IntParameter = Int(fields, "intParameter");
            mod.StringParameter = Str(fields, "stringParameter");
            mod.LayerName = Str(fields, "layerName");
            mod.StateName = Str(fields, "stateName");
            mod.SourceStateName = Str(fields, "sourceStateName");
            mod.DestinationStateName = Str(fields, "destinationStateName");
            mod.ParameterName = Str(fields, "parameterName");
            mod.ParameterType = Str(fields, "parameterType");
            mod.DefaultFloat = Float(fields, "defaultFloat");
            mod.DefaultInt = Int(fields, "defaultInt");
            mod.DefaultBool = Bool(fields, "defaultBool");
            mod.MotionAssetPath = Str(fields, "motionAssetPath");
            mod.Speed = Float(fields, "speed");
            mod.HasExitTime = Bool(fields, "hasExitTime");
            mod.ExitTime = Float(fields, "exitTime");
            mod.Duration = Float(fields, "duration");
            mod.HasFixedDuration = Bool(fields, "hasFixedDuration");

            if (nested.TryGetValue("keyframes", out var kfJson))
                mod.Keyframes = ParseKeyframes(kfJson);
            if (nested.TryGetValue("conditions", out var condJson))
                mod.Conditions = ParseConditions(condJson);
            return mod;
        }

        private static List<KeyframeSpec> ParseKeyframes(string arrayJson)
        {
            var result = new List<KeyframeSpec>();
            if (string.IsNullOrEmpty(arrayJson)) return result;
            var trimmed = arrayJson.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return result;
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            int i = 0;
            while (i < inner.Length)
            {
                while (i < inner.Length && (inner[i] == ',' || char.IsWhiteSpace(inner[i]))) i++;
                if (i >= inner.Length) break;
                if (inner[i] != '{') break;
                int depth = 0;
                int start = i;
                while (i < inner.Length)
                {
                    if (inner[i] == '{') depth++;
                    else if (inner[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                var fields = ParseInlineObject(inner.Substring(start, i - start));
                result.Add(new KeyframeSpec
                {
                    Time = Float(fields, "time") ?? 0f,
                    Value = Float(fields, "value") ?? 0f,
                    InTangent = Float(fields, "inTangent") ?? 0f,
                    OutTangent = Float(fields, "outTangent") ?? 0f,
                });
            }
            return result;
        }

        private static List<ConditionSpec> ParseConditions(string arrayJson)
        {
            var result = new List<ConditionSpec>();
            if (string.IsNullOrEmpty(arrayJson)) return result;
            var trimmed = arrayJson.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return result;
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            int i = 0;
            while (i < inner.Length)
            {
                while (i < inner.Length && (inner[i] == ',' || char.IsWhiteSpace(inner[i]))) i++;
                if (i >= inner.Length) break;
                if (inner[i] != '{') break;
                int depth = 0;
                int start = i;
                while (i < inner.Length)
                {
                    if (inner[i] == '{') depth++;
                    else if (inner[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                var fields = ParseInlineObject(inner.Substring(start, i - start));
                result.Add(new ConditionSpec
                {
                    Parameter = Str(fields, "parameter"),
                    Mode = Str(fields, "mode"),
                    Threshold = Float(fields, "threshold"),
                });
            }
            return result;
        }

        // Parse a flat {field: scalar} object into a Dictionary. Used for
        // keyframes / conditions (no further nesting expected).
        private static Dictionary<string, string> ParseInlineObject(string obj)
        {
            var fields = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(obj)) return fields;
            obj = obj.Trim();
            if (!obj.StartsWith("{") || !obj.EndsWith("}")) return fields;
            var body = obj.Substring(1, obj.Length - 2);
            int i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && (body[i] == ',' || char.IsWhiteSpace(body[i]))) i++;
                if (i >= body.Length) break;
                if (body[i] != '"') break;
                int keyStart = i + 1;
                int keyEnd = keyStart;
                while (keyEnd < body.Length)
                {
                    if (body[keyEnd] == '\\' && keyEnd + 1 < body.Length) { keyEnd += 2; continue; }
                    if (body[keyEnd] == '"') break;
                    keyEnd++;
                }
                var key = Unescape(body.Substring(keyStart, keyEnd - keyStart));
                i = keyEnd + 1;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length || body[i] != ':') break;
                i++;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;

                if (body[i] == '"')
                {
                    int vEnd = i + 1;
                    while (vEnd < body.Length)
                    {
                        if (body[vEnd] == '\\' && vEnd + 1 < body.Length) { vEnd += 2; continue; }
                        if (body[vEnd] == '"') break;
                        vEnd++;
                    }
                    fields[key] = "\"" + body.Substring(i + 1, vEnd - i - 1) + "\"";
                    i = vEnd + 1;
                }
                else
                {
                    int vStart = i;
                    while (i < body.Length && body[i] != ',' && body[i] != '}') i++;
                    fields[key] = body.Substring(vStart, i - vStart).Trim();
                }
            }
            return fields;
        }

        // -----------------------------------------------------------------
        // Typed accessors over the {field: token} dictionary.
        // Tokens keep their JSON form (quoted for strings, bare for scalars).
        // -----------------------------------------------------------------

        private static string Str(Dictionary<string, string> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return null;
            if (v == null) return null;
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return Unescape(v.Substring(1, v.Length - 2));
            return v;
        }

        private static bool? Bool(Dictionary<string, string> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return null;
            if (v == "true") return true;
            if (v == "false") return false;
            return null;
        }

        private static int? Int(Dictionary<string, string> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return null;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        }

        private static float? Float(Dictionary<string, string> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return null;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (float?)null;
        }

        private static T? Enum<T>(Dictionary<string, string> d, string key) where T : struct
        {
            var s = Str(d, key);
            if (s == null) return null;
            return System.Enum.TryParse(s, true, out T v) ? v : (T?)null;
        }

        private static string Unescape(string s)
            => string.IsNullOrEmpty(s) ? s : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}
