namespace UnityOpenMcpBridge
{
    public static class BridgeAuthCheck
    {
        // Returns true when the request should be allowed to proceed.
        public static bool IsAuthorized(string policy, string headerValue, string expectedToken)
        {
            // Only "none" is an explicit opt-out.
            if (policy == BridgeAuthPolicy.None) return true;

            // An unrecognized policy (null, corrupt settings, typo) must fail
            // closed — never silently fall back to "required", since that could
            // allow a valid token through under a policy the operator did not
            // intend. Only the explicit "required" policy proceeds to the token
            // check.
            if (policy != BridgeAuthPolicy.Required) return false;

            if (string.IsNullOrEmpty(expectedToken)) return false;

            var presented = BridgeAuthToken.ExtractBearer(headerValue);
            if (presented == null) return false;

            return BridgeAuthToken.EqualsConstantTime(presented, expectedToken);
        }
    }
}
