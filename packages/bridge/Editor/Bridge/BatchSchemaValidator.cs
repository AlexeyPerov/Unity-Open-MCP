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
            if (type == "object" || Raw(schema, "properties") != null)
            {
                var properties = Raw(schema, "properties") ?? "{}";
                foreach (var key in JsonBody.GetStringArray(JsonBody.TopLevelField(schema, "required"), "required") ?? Array.Empty<string>())
                {
                    if (root && (key == "paths_hint" || key == "gate")) continue;
                    if (Raw(value, key) == null) errors.Add(path + "." + key + " is required");
                }
                foreach (var key in JsonBody.GetObjectKeys(value) ?? new List<string>())
                {
                    var child = Raw(properties, key);
                    if (child == null && Raw(schema, "additionalProperties") == "false") errors.Add(path + "." + key + " is unknown");
                    else if (child != null) Validate(Raw(value, key), child, path + "." + key, errors);
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

        private static void Bound(double value, string schema, string min, string max, string path, List<string> errors)
        {
            if (double.TryParse(Raw(schema, min), NumberStyles.Float, CultureInfo.InvariantCulture, out var low) && value < low)
                errors.Add(path + " violates " + min);
            if (double.TryParse(Raw(schema, max), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) && value > high)
                errors.Add(path + " violates " + max);
        }
    }
}
