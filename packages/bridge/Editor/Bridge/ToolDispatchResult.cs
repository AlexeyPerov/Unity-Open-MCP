namespace UnityAgentBridge
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
    }
}
