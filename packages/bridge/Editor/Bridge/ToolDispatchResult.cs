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
