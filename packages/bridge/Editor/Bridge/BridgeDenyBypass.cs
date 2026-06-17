// M14 T5.2 — Bypass resolver for the deny heuristic.
//
// The deny heuristic is bypassed only when BOTH conditions hold:
//   1. The request asks for gate mode "off" (the agent is explicitly opting
//      out of post-mutation verification).
//   2. The request sets confirm_bypass: true (an explicit acknowledgement).
//
// Requiring both keeps a confused agent from skirting the deny list by setting
// a single flag. The bypass is still audited — the request flows through the
// normal dispatch path and lands in BridgeActivityLog with gate.mode = off, so
// the operator can grep for bypasses in the activity tab.
//
// Split out from BridgeDenyList so it can read the gate value through the same
// precedence the dispatcher uses, without the deny module needing to know
// about request-body parsing.
namespace UnityOpenMcpBridge
{
    public static class BridgeDenyBypass
    {
        // gateMode is the effective mode the dispatcher already resolved for the
        // request (request body → project default → tool default). confirmBypass
        // is the raw flag from the request body.
        public static bool IsRequested(string gateMode, bool confirmBypass)
        {
            if (!confirmBypass) return false;
            return gateMode == BridgeGateDefaultPolicy.Off;
        }

        // Convenience for callers that have the raw body — resolves the gate
        // value inline so the deny evaluation can run before the dispatcher
        // has computed the effective mode. Mirrors BridgeHttpServer.ExtractGateMode
        // but never falls back to the project/tool default: a bypass requires an
        // EXPLICIT "off" on the request, not a default that happens to be off.
        public static bool IsRequestedFromBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            if (!JsonBody.GetBool(body, "confirm_bypass", false)) return false;
            return ExtractExplicitGateOff(body);
        }

        static bool ExtractExplicitGateOff(string body)
        {
            const string key = "\"gate\"";
            var idx = body.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return false;
            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return false;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '"') return false;
            start++;
            var end = start;
            while (end < body.Length && body[end] != '"') end++;
            if (end == start) return false;
            var value = body.Substring(start, end - start);
            return value == BridgeGateDefaultPolicy.Off;
        }
    }
}
