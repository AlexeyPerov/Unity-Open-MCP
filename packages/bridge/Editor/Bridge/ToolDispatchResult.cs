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
