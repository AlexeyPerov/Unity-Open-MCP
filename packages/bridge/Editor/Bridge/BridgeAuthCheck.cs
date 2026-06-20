namespace UnityOpenMcpBridge
{
    public static class BridgeAuthCheck
    {
        // Returns true when the request should be allowed to proceed.
        public static bool IsAuthorized(string policy, string headerValue, string expectedToken)
        {
            // Only "none" is an explicit opt-out; anything else (null, unknown,
            // "required") fails closed.
            if (policy == BridgeAuthPolicy.None) return true;

            if (string.IsNullOrEmpty(expectedToken)) return false;

            var presented = BridgeAuthToken.ExtractBearer(headerValue);
            if (presented == null) return false;

            return BridgeAuthToken.EqualsConstantTime(presented, expectedToken);
        }
    }
}
