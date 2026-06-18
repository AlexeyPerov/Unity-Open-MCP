// M14 — Per-session bearer token minted when the bridge acquires its instance
// lock, plus a constant-time comparison helper for the HTTP auth check.
//
// The token is written into ~/.unity-open-mcp/instances/<projectHash>.json so the
// MCP server (and any other client that discovered the bridge via the lock
// file) can present it back. Enforcement is opt-in via `authMode` in
// .unity-open-mcp/settings.json (BridgeAuthPolicy): the token is always minted
// so flipping to "required" needs no restart, but the HTTP layer only checks
// it when the policy says so.
//
// No new dependencies: RandomNumberGenerator is BCL (already used in
// InstancePortResolver for the project hash). 32 random bytes hex-encoded give
// 256 bits of entropy and a stable 64-char ASCII payload that survives every
// transport the bridge already speaks.
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnityOpenMcpBridge
{
    public static class BridgeAuthToken
    {
        // 32 bytes → 256-bit token. Hex-encoded to stay ASCII-safe across the
        // lock file, HTTP headers, and the TS-side discovery parser.
        public const int ByteLength = 32;

        // Hex length = ByteLength * 2. Exposed so tests can pin the format
        // without hardcoding a magic number.
        public const int HexLength = ByteLength * 2;

        const string BearerPrefix = "Bearer ";

        // Mint a fresh token. Never returns null/empty.
        public static string Generate()
        {
            var bytes = new byte[ByteLength];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(HexLength);
            for (int i = 0; i < ByteLength; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Constant-time equality. Avoids early-exit timing leaks when comparing
        // the request's bearer value against the expected token. Different
        // lengths cannot match, but we still walk the full shorter length so a
        // timing profile can't reveal the expected length. Both inputs must be
        // hex strings in practice, but this helper is encoding-agnostic.
        public static bool EqualsConstantTime(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";

            var aLen = a.Length;
            var bLen = b.Length;
            var maxLen = Math.Max(aLen, bLen);

            var diff = (byte)(aLen ^ bLen);
            for (int i = 0; i < maxLen; i++)
            {
                var ca = (char)0;
                var cb = (char)0;
                if (i < aLen) ca = a[i];
                if (i < bLen) cb = b[i];
                diff |= (byte)(ca ^ cb);
            }
            return diff == 0;
        }

        // Parse an `Authorization` header value into the bare token, tolerating
        // surrounding whitespace and case differences in the scheme. Returns
        // null when the header is absent or not a Bearer value — callers treat
        // null as a 401.
        public static string ExtractBearer(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return null;
            var s = headerValue.Trim();
            if (s.Length <= BearerPrefix.Length) return null;
            // Case-insensitive scheme match.
            if (string.Compare(s, 0, BearerPrefix, 0, BearerPrefix.Length,
                    CultureInfo.InvariantCulture, CompareOptions.OrdinalIgnoreCase) != 0)
                return null;
            var token = s.Substring(BearerPrefix.Length).Trim();
            return token.Length == 0 ? null : token;
        }
    }
}
