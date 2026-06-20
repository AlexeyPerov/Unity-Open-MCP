// M14 — Pure decision function for the HTTP auth check, isolated from
// HttpListenerContext so it is unit-testable without the live-bridge harness.
// BridgeHttpServer.CheckAuth is the thin wrapper that reads the header off the
// context and feeds it here.
//
// Semantics:
//   - policy "none"     → always authorized (token may still be present)
//   - policy "required" → authorized iff the extracted bearer equals the
//                          expected token (constant-time compare)
//   - unknown policy    → treated as "required" (fail-closed) so a corrupt
//                          settings file can't silently disable auth
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
