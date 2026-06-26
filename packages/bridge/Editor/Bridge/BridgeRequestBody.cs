using System;
using System.IO;
using System.Net;
using System.Text;

namespace UnityOpenMcpBridge
{
    // Request-body parsing for the HTTP dispatcher. Extracts the three scalar
    // fields the dispatcher needs straight off the raw JSON body — timeout_ms,
    // gate, and the asset path encoded in an issue_id — plus the timeout clamping
    // bounds.
    //
    // These deliberately use hand-rolled IndexOf substring parsing rather than
    // the JsonBody typed accessors: they run on the hot dispatch path for every
    // mutating tool call and only need one or two scalars. JsonBody.GetString /
    // JsonBody.GetStringArray is used elsewhere in the dispatcher where multiple
    // typed fields are read.

    internal static class BridgeRequestBody
    {
        internal const int DefaultTimeoutMs = 30000;
        internal const int MinTimeoutMs = 1000;
        // Matches the documented maximum in the run-tests tool schema
        // (mcp-server/src/tools/run-tests.ts). Previously 300000, which silently
        // clamped a caller's explicit value below the advertised ceiling.
        internal const int MaxTimeoutMs = 600000;

        internal static string ReadRequestBody(HttpListenerRequest request)
        {
            using var stream = request.InputStream;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        internal static int ExtractTimeoutMs(string body)
        {
            if (string.IsNullOrEmpty(body)) return DefaultTimeoutMs;

            const string key = "\"timeout_ms\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return DefaultTimeoutMs;

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return DefaultTimeoutMs;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            var end = start;
            while (end < body.Length && char.IsDigit(body[end])) end++;

            if (end == start || !int.TryParse(body.Substring(start, end - start), out var ms))
                return DefaultTimeoutMs;

            return Math.Clamp(ms, MinTimeoutMs, MaxTimeoutMs);
        }

        internal static string ExtractGateMode(string body)
        {
            // Precedence per architecture/gate-policy.md:
            //   1. Request body `gate` value
            //   2. Project default from `.unity-open-mcp/settings.json`
            //   3. Tool-level default (caller-provided)
            if (string.IsNullOrEmpty(body)) return BridgeGateDefaultPolicy.GetDefault();

            const string key = "\"gate\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return BridgeGateDefaultPolicy.GetDefault();

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return BridgeGateDefaultPolicy.GetDefault();

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            if (start >= body.Length || body[start] != '"') return BridgeGateDefaultPolicy.GetDefault();
            start++;

            var end = start;
            while (end < body.Length && body[end] != '"') end++;

            if (end == start) return BridgeGateDefaultPolicy.GetDefault();

            var value = body.Substring(start, end - start);
            return BridgeGateDefaultPolicy.IsValid(value) ? value : BridgeGateDefaultPolicy.GetDefault();
        }

        internal static string[] PathsFromIssueId(string issueId)
        {
            if (string.IsNullOrEmpty(issueId)) return null;
            var parts = issueId.Split('|');
            if (parts.Length < 3) return null;
            var assetPath = parts[2];
            if (string.IsNullOrEmpty(assetPath)) return null;
            return new[] { assetPath };
        }
    }
}
