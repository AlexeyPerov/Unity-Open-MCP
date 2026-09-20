using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge
{
    // One preflight/binding seam shared by HTTP dispatch and future job execution.
    internal static class ProjectCommandInvocation
    {
        internal const string ToolName = "unity_open_mcp_project_commands";
        internal static string Field(string body, string key) => JsonBody.GetString(JsonBody.TopLevelField(body, key), key);
        internal static bool Resolve(string body, out ProjectCommandCatalog.Entry entry)
            => ProjectCommandCatalog.TryGet(Field(body, "command_id"), out entry);

        internal static bool ValidScope(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')) return false;
            var parts = path.Split('/');
            return parts.Length > 1 && (parts[0] == "Assets" || parts[0] == "Packages")
                && parts.All(p => p.Length > 0 && p != "." && p != "..");
        }

        internal static string[] ScopedPaths(string body)
        {
            var supplied = JsonBody.GetStringArray(JsonBody.TopLevelField(body, "paths_hint"), "paths_hint") ?? Array.Empty<string>();
            return Resolve(body, out var entry)
                ? entry.Attribute.PathsHint.Concat(supplied).Distinct(StringComparer.Ordinal).ToArray() : supplied;
        }

        internal static ToolDispatchResult Preflight(string body, out ProjectCommandCatalog.Entry entry, out object[] values, ProjectCommandContext context = null)
        {
            entry = null;
            values = null;
            if (!BridgeJson.IsValidJsonObject(body) || Field(body, "action") != "invoke")
                return ToolDispatchResult.Fail("invalid_arguments", "POST requires action invoke and a JSON object.");
            var envelopeErrors = new List<string>();
            var allowed = new[] { "action", "command_id", "args", "schema_version", "paths_hint", "gate", "timeout_ms", "ignore_scene_dirty", "confirm_bypass" };
            if (JsonBody.GetObjectKeys(body).Any(key => !allowed.Contains(key))) envelopeErrors.Add("invoke accepts command_id, args, schema_version and transport options only.");
            BatchSchemaValidator.ValidateRequest(body, BridgeBatchSchemas.ByTool[ToolName], envelopeErrors);
            if (JsonBody.GetObjectKeys(body).Distinct(StringComparer.Ordinal).Count() != JsonBody.GetObjectKeys(body).Count)
                envelopeErrors.Add("Duplicate transport keys are not allowed.");
            if (envelopeErrors.Count > 0) return ToolDispatchResult.Fail("invalid_arguments", string.Join("; ", envelopeErrors));
            if (!Resolve(body, out entry))
                return ToolDispatchResult.Fail("command_unavailable", "Unknown, disabled or conflicting command id: " + Field(body, "command_id"));
            if (BridgeToolTogglePolicy.IsDisabled(entry.Id))
                return ToolDispatchResult.Fail("tool_disabled", "Project command is disabled by bridge settings.");
            var version = Field(body, "schema_version");
            if (version != null && version != entry.SchemaVersion)
                return ToolDispatchResult.Fail("command_schema_changed", "Describe the command again before invoking its changed contract.");
            var args = JsonBody.GetTopLevelRawValue(body, "args") ?? "{}";
            var errors = new List<string>();
            if (!BridgeJson.IsValidJsonObject(args)) errors.Add("args must be a JSON object");
            else
            {
                var keys = JsonBody.GetObjectKeys(args);
                if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) errors.Add("Duplicate argument keys are not allowed.");
                BatchSchemaValidator.Validate(args, entry.Schema, "args", errors);
            }
            if (errors.Count > 0) return ToolDispatchResult.Fail("invalid_arguments", string.Join("; ", errors));
            try
            {
                values = entry.Method.GetParameters().Select(p =>
                {
                    if (p.ParameterType == typeof(ProjectCommandContext)) return context;
                    var raw = JsonBody.GetTopLevelRawValue(args, p.Name);
                    return raw == null ? p.DefaultValue : Bind(raw, p.ParameterType);
                }).ToArray();
            }
            catch (Exception ex) { return ToolDispatchResult.Fail("invalid_arguments", "Argument cannot be represented by its CLR type: " + ex.Message); }
            if (entry.Attribute.Async && context == null)
                return ToolDispatchResult.Fail("async_not_supported", "This command requires job orchestration; it was not started.");
            if (entry.Attribute.Lifecycle == LifecyclePolicy.CustomConfirmation)
                return ToolDispatchResult.Fail("lifecycle_not_supported", "Custom confirmation requires job orchestration; the command was not started.");
            var paths = ScopedPaths(body);
            if (paths.Any(p => !ValidScope(p)))
                return ToolDispatchResult.Fail("invalid_paths", "Use scoped Assets/ or Packages/ paths without traversal, empty segments or project-root fallback.");
            if (entry.Attribute.IsMutating && paths.Length == 0)
                return ToolDispatchResult.Fail("paths_hint_required", "Explicit mutation scope is required when the command declares no PathsHint.");
            var deny = BridgeDenyList.EvaluateProjectCommand(entry.Id, BridgeProjectSettings.ProjectCommandDenyPatterns, Bypass(body));
            if (!deny.Allowed)
                return ToolDispatchResult.Fail("denied_by_policy", deny.Reason + " Matched pattern: " + deny.MatchedPattern + ". " + deny.Suggestion);
            return null;
        }

        private static object Bind(string raw, Type type)
        {
            raw = raw.Trim();
            if (raw == "null") return null;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray)
            {
                var items = JsonBody.GetArrayRawValues(raw);
                var array = Array.CreateInstance(type.GetElementType(), items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(Bind(items[i], type.GetElementType()), i);
                return array;
            }
            var text = raw.StartsWith("\"") ? Field("{\"v\":" + raw + "}", "v") : raw;
            if (type == typeof(string)) return text;
            if (type == typeof(bool)) return bool.Parse(text);
            if (type == typeof(long)) return long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            if (type.IsEnum) return Enum.Parse(type, text, false);
            var number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Non-finite number.");
            if (type == typeof(int)) return checked((int)number);
            if (type == typeof(float))
            {
                var value = (float)number;
                if (float.IsInfinity(value)) throw new ArgumentException("Number exceeds Single range.");
                return value;
            }
            return number;
        }

        internal static ToolDispatchResult Execute(string body)
        {
            var refusal = Preflight(body, out var entry, out var values);
            if (refusal != null) return refusal;
            try
            {
                RecordStarted(body, entry);
                var raw = (string)entry.Method.Invoke(null, values);
                var output = OutputSerializer.SerializeJson(raw);
                return ToolDispatchResult.Ok("{\"result\":" + output + "}");
            }
            catch (Exception ex)
            {
                // An arbitrary command may have changed state before throwing or returning
                // invalid output. Preserve that uncertainty so validation/settle still run.
                var cause = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                return entry.Attribute.IsMutating
                    ? ToolDispatchResult.PartialFailure("execution_error", cause.Message, null)
                    : ToolDispatchResult.Fail("execution_error", cause.Message);
            }
        }

        internal static void RecordStarted(string body, ProjectCommandCatalog.Entry entry, string jobId = null)
        {
            try
            {
                // Persist acceptance before user code can reload the domain. A missing
                // terminal record means an unknown outcome, never evidence of failure.
                if (BridgeAuditLog.Enabled)
                {
                    var started = GatePolicy.Skipped(ToolDispatchResult.Ok(), "in_progress");
                    Decorate(started, body, 0);
                    if (jobId != null) started.ProjectCommandJson = started.ProjectCommandJson.Replace("\"jobId\":null", "\"jobId\":" + BridgeJson.EscapeString(jobId));
                    BridgeAuditLog.Record(new BridgeAuditRecord {
                        Timestamp = DateTime.UtcNow, ProjectHash = BridgeAuditRecorder.ResolveAuditProjectHash(),
                        Tool = ToolName, ProjectCommandJson = started.ProjectCommandJson,
                        GateMode = BridgeRequestBody.ExtractGateMode(body), PathsHint = ScopedPaths(body),
                        Outcome = "started", EffectiveReadOnly = !entry.Attribute.IsMutating,
                        BypassedDenyList = Bypass(body)
                    });
                }
            }
            catch { /* Audit must never prevent command execution. */ }
        }

        private static bool Bypass(string body) => BridgeRequestBody.ExtractGateMode(body) == "off"
            && JsonBody.GetBool(JsonBody.TopLevelField(body, "confirm_bypass"), "confirm_bypass");

        internal static void Decorate(GateDispatchResult result, string body, long durationMs)
        {
            result.CommandDurationMs = durationMs;
            result.CommandBypassedDenyList = Bypass(body);
            var identity = "{\"id\":" + BridgeJson.EscapeString(Field(body, "command_id"));
            if (Resolve(body, out var entry))
                identity += ",\"schemaVersion\":" + BridgeJson.EscapeString(entry.SchemaVersion)
                    + ",\"declaringAssembly\":" + BridgeJson.EscapeString(entry.Method.DeclaringType.Assembly.GetName().Name)
                    + ",\"declaringType\":" + BridgeJson.EscapeString(entry.Method.DeclaringType.FullName)
                    + ",\"isMutating\":" + (entry.Attribute.IsMutating ? "true" : "false")
                    + ",\"lifecycle\":" + BridgeJson.EscapeString(entry.Attribute.Lifecycle.ToWireString());
            result.ProjectCommandJson = identity + ",\"gate\":" + BridgeJson.EscapeString(BridgeRequestBody.ExtractGateMode(body))
                + ",\"pathsHint\":" + ProjectCommandSchema.Strings(ScopedPaths(body)) + ",\"jobId\":null}";
        }
    }
}
