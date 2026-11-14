using System;
using System.Collections.Generic;
using System.Text;

namespace UnityOpenMcpBridge
{
    public class ToolDispatchResult
    {
        public bool Success { get; }
        public string Output { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

        // B-N10 — true when a FAILED result still committed side effects the gate
        // must health-check. The motivating case is a partial batch_execute
        // (some steps succeeded before a later step failed): Success is false so
        // the gate marks the run failed, but the committed steps wrote assets and
        // the post-mutation validate/delta + settle wait must still run, or those
        // writes ship without a health check and the response can return while
        // the Editor is still importing them. Default false preserves the
        // existing "a failed mutation committed nothing" contract for every
        // other tool. Set via the partial-batch factory below.
        public bool PartialCommit { get; private set; }

        // Structured refusal context. ErrorDetailJson is a JSON object whose
        // members are serialized inside the `error` object after code/message
        // (gate envelope `mutation.error`, direct-response `error`); NextSteps
        // become the gate envelope's `agentNextSteps` or a top-level
        // `agentNextSteps` sibling of a direct-response `error`. Both stay null
        // for every other failure, so those shapes are unchanged.
        public string ErrorDetailJson { get; private set; }
        public string[] NextSteps { get; private set; }

        // The tool's own refusal body when this failure was read from one (see
        // FromRefusalBody). Gate-free tools answer with it verbatim, so their
        // flat `{ "error": {...}, ... }` response keeps every sibling in place.
        internal string RefusalBody { get; private set; }

        public ToolDispatchResult(bool success, string output, string errorCode, string errorMessage)
        {
            Success = success;
            Output = output;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public static ToolDispatchResult Ok(string output = null)
        {
            return new ToolDispatchResult(true, output, null, null);
        }

        public static ToolDispatchResult Fail(string code, string message)
        {
            return new ToolDispatchResult(false, null, code, message);
        }

        // A refusal that tells the caller what to do instead: structured error
        // members (a JSON object string) plus agent next steps.
        public static ToolDispatchResult FailWithDetail(string code, string message, string errorDetailJson, string[] nextSteps)
        {
            return new ToolDispatchResult(false, null, code, message) { ErrorDetailJson = errorDetailJson, NextSteps = nextSteps };
        }

        // A registry tool reports a refusal by returning the JSON object
        // `{"error":{"code":"…","message":"…",…},…}`. This reads that body into
        // a failed result so the refusal is a failure wherever the dispatch is
        // consumed (gate envelope, batch step, activity, audit): the `error`
        // object's other members and any other top-level member become
        // ErrorDetailJson, a top-level `agentNextSteps` string array becomes
        // NextSteps. Returns null for every other output.
        internal static ToolDispatchResult FromRefusalBody(string output)
        {
            if (output == null || output.IndexOf("\"error\"", StringComparison.Ordinal) < 0) return null;
            var members = JsonBody.GetTopLevelMembers(output);
            if (members == null) return null;

            string errorRaw = null;
            foreach (var member in members)
            {
                if (member.Key != "error") continue;
                errorRaw = member.Value;
                break;
            }
            if (string.IsNullOrEmpty(errorRaw) || errorRaw[0] != '{') return null;
            var errorMembers = JsonBody.GetTopLevelMembers(errorRaw);
            if (errorMembers == null) return null;

            string code = null, message = null;
            var detail = new StringBuilder();
            var detailKeys = new HashSet<string>(StringComparer.Ordinal) { "code", "message" };
            foreach (var member in errorMembers)
            {
                if (member.Key == "code") { code ??= ReadJsonString(member.Value); continue; }
                if (member.Key == "message") { message ??= ReadJsonString(member.Value); continue; }
                AppendDetail(detail, detailKeys, member);
            }
            if (string.IsNullOrEmpty(code)) return null;

            string[] nextSteps = null;
            foreach (var member in members)
            {
                if (member.Key == "error") continue;
                if (member.Key == "agentNextSteps" && nextSteps == null)
                {
                    nextSteps = ReadJsonStringArray(member.Value);
                    if (nextSteps != null) continue;
                }
                AppendDetail(detail, detailKeys, member);
            }

            return new ToolDispatchResult(false, null, code, message ?? "")
            {
                ErrorDetailJson = detail.Length > 0 ? "{" + detail + "}" : null,
                NextSteps = nextSteps != null && nextSteps.Length > 0 ? nextSteps : null,
                RefusalBody = output
            };
        }

        // First writer wins: an `error` member is never replaced by a sibling
        // of the same name.
        private static void AppendDetail(StringBuilder detail, HashSet<string> seen, KeyValuePair<string, string> member)
        {
            if (!seen.Add(member.Key)) return;
            if (detail.Length > 0) detail.Append(',');
            detail.Append(BridgeJson.EscapeString(member.Key)).Append(':').Append(member.Value);
        }

        private static string ReadJsonString(string raw)
            => raw.Length >= 2 && raw[0] == '"' ? JsonBody.GetString("{\"v\":" + raw + "}", "v") : null;

        // Null unless `raw` is an array of strings only.
        private static string[] ReadJsonStringArray(string raw)
        {
            if (raw.Length == 0 || raw[0] != '[') return null;
            foreach (var item in JsonBody.GetArrayRawValues(raw))
                if (item.Length == 0 || item[0] != '"') return null;
            return JsonBody.GetStringArray("{\"v\":" + raw + "}", "v");
        }

        // B25 — a failed mutation may still carry a structured output body the
        // caller needs (e.g. apply_fix's unknown_fix error lists available and
        // applicable fix ids). The plain Fail(code, message) factory drops the
        // output, losing that guidance; this variant keeps it so the gate
        // envelope reports `mutation.success: false` (so gate runners and
        // activity recording treat it as a real failure) while still surfacing
        // the structured JSON at `mutation.output`.
        public static ToolDispatchResult FailWithOutput(string code, string message, string output)
        {
            return new ToolDispatchResult(false, output, code, message);
        }

        // B-N10 — a partial-batch failure: Success is false (one or more steps
        // failed) but PartialCommit is true (at least one step committed), so the
        // gate must still run the post-mutation validate/delta and the settle
        // wait on the committed work. Mirrors FailWithOutput's shape (keeps the
        // per-step JSON) and stamps PartialCommit = true.
        public static ToolDispatchResult PartialFailure(string code, string message, string output)
        {
            return new ToolDispatchResult(false, output, code, message) { PartialCommit = true };
        }
    }
}
