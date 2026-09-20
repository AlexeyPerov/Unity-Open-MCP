using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityOpenMcpBridge
{
    internal static class BatchSchemaValidator
    {
        private static string Raw(string body, string key) => JsonBody.GetTopLevelRawValue(body, key);
        private static string Str(string body, string key) => JsonBody.GetString(JsonBody.TopLevelField(body, key), key);

        internal static void Validate(string value, string schema, string path, List<string> errors, bool root = false)
        {
            if (schema == null) return;
            foreach (var combinator in new[] { "anyOf", "oneOf", "allOf" })
            {
                var branches = Raw(schema, combinator);
                if (branches == null) continue;
                int matches = 0;
                var entries = JsonBody.GetArrayRawValues(branches);
                foreach (var branch in entries)
                {
                    var branchErrors = new List<string>();
                    Validate(value, branch, path, branchErrors);
                    if (branchErrors.Count == 0) matches++;
                }
                if ((combinator == "anyOf" && matches == 0) || (combinator == "oneOf" && matches != 1)
                    || (combinator == "allOf" && matches != entries.Count)) errors.Add(path + " does not satisfy " + combinator);
            }
            var types = Raw(schema, "type");
            if (types != null && types.StartsWith("["))
            {
                bool matches = false;
                foreach (var candidate in JsonBody.GetArrayRawValues(types))
                {
                    var candidateErrors = new List<string>();
                    Validate(value, "{\"type\":" + candidate + "}", path, candidateErrors);
                    if (candidateErrors.Count == 0) matches = true;
                }
                if (!matches) { errors.Add(path + " must match one of " + types); return; }
            }
            var type = types != null && types.StartsWith("[") ? null : Str(schema, "type");
            var trimmed = value?.Trim() ?? "null";
            bool number = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric);
            bool valid = type == null || (type == "object" && trimmed.StartsWith("{"))
                || (type == "array" && trimmed.StartsWith("[")) || (type == "string" && trimmed.StartsWith("\""))
                || (type == "boolean" && (trimmed == "true" || trimmed == "false"))
                || (type == "number" && number) || (type == "integer" && number && numeric == Math.Truncate(numeric))
                || (type == "null" && trimmed == "null");
            if (!valid) { errors.Add(path + " must be " + type); return; }
            var choices = Raw(schema, "enum");
            if (choices != null && !JsonBody.GetArrayRawValues(choices).Contains(trimmed)) errors.Add(path + " is not an allowed value");
            // Combinator branches commonly express only `required` (without
            // repeating type/properties). Required keys still constrain those
            // branches; otherwise every branch appears to match and valid
            // oneOf requests are rejected.
            foreach (var key in JsonBody.GetStringArray(JsonBody.TopLevelField(schema, "required"), "required") ?? Array.Empty<string>())
            {
                if (root && (key == "paths_hint" || key == "gate")) continue;
                if (Raw(value, key) == null) errors.Add(path + "." + key + " is required");
            }
            if (type == "object" || Raw(schema, "properties") != null)
            {
                var properties = Raw(schema, "properties") ?? "{}";
                if (Raw(properties, "game_object_path") != null)
                {
                    var selectors = new List<string>();
                    foreach (var selector in new[] { "instance_id", "game_object_path", "path", "target_path", "name" })
                        if (Raw(properties, selector) != null && Supplied(value, selector)) selectors.Add(selector);
                    if (selectors.Count > 1) errors.Add(path + ": supply only one GameObject selector (prefer game_object_path); ignored keys: " + string.Join(", ", selectors));
                    if (Supplied(value, "component_instance_id") && (Supplied(value, "component_type") || Supplied(value, "type_name")))
                        errors.Add(path + ": component_instance_id and component_type are alternatives");
                }
                foreach (var key in JsonBody.GetObjectKeys(value) ?? new List<string>())
                {
                    var child = Raw(properties, key);
                    if (child == null && Raw(schema, "additionalProperties") == "false") errors.Add(path + "." + key + " is unknown; allowed canonical keys: " + string.Join(", ", JsonBody.GetObjectKeys(properties)));
                    else if (child != null) {
                        var canonical = Str(child, "x-alias-for");
                        if (canonical != null)
                            foreach (var other in JsonBody.GetObjectKeys(value) ?? new List<string>())
                                if (other != key && (other == canonical || Str(Raw(properties, other) ?? "{}", "x-alias-for") == canonical))
                                { errors.Add(path + "." + key + " conflicts with another selector; use only " + canonical); break; }
                        Validate(Raw(value, key), child, path + "." + key, errors);
                    }
                }
            }
            if (type == "array")
            {
                var items = JsonBody.GetArrayRawValues(value);
                Bound(items.Count, schema, "minItems", "maxItems", path, errors);
                for (int i = 0; i < items.Count; i++) Validate(items[i], Raw(schema, "items"), path + "[" + i + "]", errors);
            }
            if (type == "string")
            {
                var decoded = Str("{\"v\":" + value + "}", "v") ?? "";
                Bound(decoded.Length, schema, "minLength", "maxLength", path, errors);
                var pattern = Str(schema, "pattern");
                if (pattern != null && !Regex.IsMatch(decoded, pattern)) errors.Add(path + " does not match the required pattern");
            }
            if (number)
            {
                Bound(numeric, schema, "minimum", "maximum", path, errors);
                if (double.TryParse(Raw(schema, "exclusiveMinimum"), NumberStyles.Float, CultureInfo.InvariantCulture, out var low) && numeric <= low)
                    errors.Add(path + " violates exclusiveMinimum");
                if (double.TryParse(Raw(schema, "exclusiveMaximum"), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) && numeric >= high)
                    errors.Add(path + " violates exclusiveMaximum");
            }
        }

        private static bool Supplied(string body, string key)
        {
            var raw = Raw(body, key);
            return raw != null && raw != "null" && raw != "0" && raw != "\"0\"" && raw != "\"\"";
        }

        internal static void ValidateRequest(string body, string schema, List<string> errors)
        {
            if (!BridgeJson.IsValidJsonObject(body)) { errors.Add("args must be valid JSON"); return; }
            // The HTTP dispatcher consumes these keys even when a tool has no parameters.
            var properties = Raw(schema, "properties") ?? "{}";
            var fields = new List<string>();
            foreach (var key in JsonBody.GetObjectKeys(body) ?? new List<string>())
            {
                if (Raw(properties, key) == null && (key == "timeout_ms" || key == "gate" || key == "ignore_scene_dirty" || key == "confirm_bypass")) continue;
                fields.Add(BridgeJson.EscapeString(key) + ":" + Raw(body, key));
            }
            Validate("{" + string.Join(",", fields) + "}", schema, "args", errors);
        }

        internal static List<string> Deprecations(string body, string schema)
        {
            var notes = new List<string>();
            var properties = Raw(schema, "properties") ?? "{}";
            foreach (var key in JsonBody.GetObjectKeys(body) ?? new List<string>())
            {
                var property = Raw(properties, key) ?? "{}";
                var canonical = Str(property, "x-alias-for");
                if (canonical != null) notes.Add(key + " is deprecated; use " + canonical);
                var value = Raw(body, key);
                var items = Raw(property, "items");
                if (items != null && value != null && value.TrimStart().StartsWith("["))
                    foreach (var item in JsonBody.GetArrayRawValues(value)) notes.AddRange(Deprecations(item, items));
            }
            var commands = Raw(body, "commands");
            if (commands != null && commands.TrimStart().StartsWith("["))
                foreach (var command in JsonBody.GetArrayRawValues(commands))
                    if (BridgeBatchSchemas.ByTool.TryGetValue(Str(command, "tool") ?? "", out var nestedSchema))
                        notes.AddRange(Deprecations(Raw(command, "params") ?? "{}", nestedSchema));
            return notes;
        }

        // Schema-owned aliases are normalized structurally: patch values are never rewritten.
        internal static string WireArguments(string value, string schema)
        {
            var properties = Raw(schema, "properties");
            if (value == null) return "null";
            if (value.TrimStart().StartsWith("[") && Raw(schema, "items") != null)
                return "[" + string.Join(",", JsonBody.GetArrayRawValues(value).ConvertAll(v => WireArguments(v, Raw(schema, "items")))) + "]";
            if (properties == null || !value.TrimStart().StartsWith("{")) return value;
            var fields = new List<string>();
            foreach (var key in JsonBody.GetObjectKeys(value) ?? new List<string>())
            {
                var property = Raw(properties, key) ?? "{}";
                var canonical = Str(property, "x-alias-for");
                var target = Str(property, "x-wire-key") ?? (canonical == null ? key : Str(Raw(properties, canonical) ?? "{}", "x-wire-key") ?? canonical);
                fields.Add(BridgeJson.EscapeString(target) + ":" + WireArguments(Raw(value, key), property));
            }
            return "{" + string.Join(",", fields) + "}";
        }

        private static void Bound(double value, string schema, string min, string max, string path, List<string> errors)
        {
            if (double.TryParse(Raw(schema, min), NumberStyles.Float, CultureInfo.InvariantCulture, out var low) && value < low)
                errors.Add(path + " violates " + min);
            if (double.TryParse(Raw(schema, max), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) && value > high)
                errors.Add(path + " violates " + max);
        }
    }
}
