namespace UnityOpenMcpBridge
{
    public class ToolDispatchResult
    {
        public bool Success { get; }
        public string Output { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

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
    }
}
