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

        // Hard cap on a single request body. Every mutating tool call flows
        // through ReadRequestBody; without a bound a caller (or a buggy MCP
        // server) could POST an arbitrarily large body and OOM the Editor. 64 MB
        // is far above any legitimate JSON tool payload (argument bodies are
        // kilobytes) while putting a ceiling on per-request heap allocation.
        // In the default `authMode: none` (local dev) any caller on the bind
        // address can reach this path, so the bound matters even on loopback.
        internal const long MaxRequestBodyBytes = 64L * 1024 * 1024;

        internal static string ReadRequestBody(HttpListenerRequest request)
        {
            using var stream = request.InputStream;

            // Fast pre-check when the client declared a length (most requests
            // do). ContentLength64 is -1 for chunked transfers, so this only
            // short-circuits the obvious case; the streaming copy below is the
            // real bound that also covers chunked encoding.
            if (request.ContentLength64 > MaxRequestBodyBytes)
                throw new BridgeRequestBodyTooLargeException(request.ContentLength64, MaxRequestBodyBytes);

            // Copy raw bytes with a hard cap, then decode once. Capping bytes
            // (not chars) bounds heap precisely regardless of UTF-8 width — a
            // char-based cap would admit up to 4x the limit for multibyte input.
            long declared = request.ContentLength64;
            int initialCap = (int)Math.Clamp(declared > 0 ? declared : 0, 0, MaxRequestBodyBytes);
            using var ms = new MemoryStream(initialCap);
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxRequestBodyBytes)
                    throw new BridgeRequestBodyTooLargeException(total, MaxRequestBodyBytes);
                ms.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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

            // Allow an optional leading sign so negative values parse and then
            // get clamped to the minimum, rather than falling through to the
            // default (which would silently ignore the caller's explicit value).
            var signEnd = start;
            if (signEnd < body.Length && (body[signEnd] == '-' || body[signEnd] == '+'))
                signEnd++;

            var end = signEnd;
            while (end < body.Length && char.IsDigit(body[end])) end++;

            if (end == signEnd || !int.TryParse(body.Substring(start, end - start), out var ms))
                return DefaultTimeoutMs;

            return Math.Clamp(ms, MinTimeoutMs, MaxTimeoutMs);
        }

        internal static string ExtractGateMode(string body)
        {
            // Precedence per docs/api/bridge-http.md#gate-policy:
            //   1. Request body `gate` value
            //   2. Project default from `.unity-open-mcp/settings.json`
            // The tool attribute is catalog/recommendation metadata and does
            // not override the project default at dispatch time.
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

    // Raised by BridgeRequestBody.ReadRequestBody when a request body exceeds
    // MaxRequestBodyBytes. Caught at the single dispatch call site to produce a
    // 413 response instead of letting an unbounded ReadToEnd OOM the Editor.
    internal sealed class BridgeRequestBodyTooLargeException : System.Exception
    {
        internal BridgeRequestBodyTooLargeException(long observedLength, long maxBytes)
            : base($"Request body of {observedLength} bytes exceeds the {maxBytes}-byte limit.") { }
    }
}
