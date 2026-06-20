namespace UnityOpenMcpBridge
{
    public static class BridgeBindAddress
    {
        public const string Loopback = "127.0.0.1";
        public const string Remote = "0.0.0.0";
        public const string Default = Loopback;

        public static readonly string[] ValidAddresses = { Loopback, Remote };

        // Decision for a start attempt. Returns Allow for loopback (any auth
        // mode) and for remote only when authMode is "required". Any other
        // combination is Refuse with an actionable message.
        public readonly struct BindDecision
        {
            public readonly bool Allowed;
            public readonly string ResolvedAddress;
            public readonly string RefusalReason;

            private BindDecision(bool allowed, string resolvedAddress, string refusalReason)
            {
                Allowed = allowed;
                ResolvedAddress = resolvedAddress;
                RefusalReason = refusalReason;
            }

            public static BindDecision Allow(string address) =>
                new BindDecision(true, address, null);

            public static BindDecision Refuse(string address, string reason) =>
                new BindDecision(false, address, reason);
        }

        public static bool IsValid(string address)
        {
            return address == Loopback || address == Remote;
        }

        // Returns true when the address would expose the bridge beyond loopback.
        public static bool IsRemote(string address) => address == Remote;

        // Pure decision over (bindAddress, authMode). authMode is the canonical
        // policy string (already validated through BridgeAuthPolicy).
        public static BindDecision Decide(string bindAddress, string authMode)
        {
            var resolved = IsValid(bindAddress) ? bindAddress : Default;
            if (!IsRemote(resolved)) return BindDecision.Allow(resolved);

            if (authMode != BridgeAuthPolicy.Required)
            {
                return BindDecision.Refuse(resolved,
                    "Remote bind (0.0.0.0) requires authMode \"required\". The bridge refuses to " +
                    "start on a non-loopback interface without token auth — set authMode to " +
                    "\"required\" in .unity-open-mcp/settings.json (Settings tab → Bridge auth) " +
                    "before enabling remote bind. See docs/api/bridge-http.md §Remote bind for " +
                    "the threat model.");
            }
            return BindDecision.Allow(resolved);
        }
    }
}
