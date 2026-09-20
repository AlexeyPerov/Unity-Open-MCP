using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityOpenMcpBridge.Update
{
    /// <summary>Explicit-only release lookup used by the update panel and tool.</summary>
    internal static class LatestVersionCheck
    {
        internal const string RegistryUrl = "https://registry.npmjs.org/unity-open-mcp/latest";
        private const string TagUrlPrefix =
            "https://api.github.com/repos/AlexeyPerov/Unity-Open-MCP/git/matching-refs/tags/bridge-v";
        private const string VersionKey = "UnityOpenMcp.Upgrade.LatestVersion";
        private const string ErrorKey = "UnityOpenMcp.Upgrade.LatestError";
        private static readonly Regex VersionField = new Regex(
            "\\\"version\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.CultureInvariant);

        internal readonly struct Result
        {
            public readonly bool Success;
            public readonly string Version;
            public readonly string Error;

            public Result(bool success, string version, string error)
            {
                Success = success;
                Version = version;
                Error = error;
            }
        }

        internal static string CachedVersion => SessionState.GetString(VersionKey, "");
        internal static string CachedError => SessionState.GetString(ErrorKey, "");

        internal static Result ParseRegistryPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return new Result(false, null, "The npm registry returned an empty response.");
            var match = VersionField.Match(payload);
            if (!match.Success)
                return new Result(false, null, "The npm registry response did not contain a version.");
            var version = VersionPinRewriter.NormalizeVersion(match.Groups[1].Value);
            return VersionPinRewriter.IsVersion(version)
                ? new Result(true, version, null)
                : new Result(false, null, "The npm registry returned an invalid version.");
        }

        internal static async Task<Result> CheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var client = CreateClient();
                using var response = await client.GetAsync(RegistryUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new Result(false, null,
                        $"npm registry request failed with HTTP {(int)response.StatusCode}.");
                var parsed = ParseRegistryPayload(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return parsed;
            }
            catch (Exception e)
            {
                return new Result(false, null, "Latest-version check failed: " + e.Message);
            }
        }

        internal static async Task<Result> ConfirmBridgeTagAsync(
            string version, CancellationToken cancellationToken = default)
        {
            if (!VersionPinRewriter.IsVersion(version))
                return new Result(false, null, "Target version must be a plain X.Y.Z.");
            try
            {
                using var client = CreateClient();
                using var response = await client.GetAsync(TagUrlPrefix + version, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new Result(false, null,
                        $"Bridge tag check failed with HTTP {(int)response.StatusCode}.");
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return body.IndexOf("bridge-v" + version, StringComparison.Ordinal) >= 0
                    ? new Result(true, version, null)
                    : new Result(false, null, $"Release tag bridge-v{version} was not found.");
            }
            catch (Exception e)
            {
                return new Result(false, null, "Bridge tag check failed: " + e.Message);
            }
        }

        internal static void Remember(Result result)
        {
            SessionState.SetString(VersionKey, result.Success ? result.Version : "");
            SessionState.SetString(ErrorKey, result.Success ? "" : result.Error ?? "Unknown error.");
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Unity-Open-MCP-Bridge");
            return client;
        }
    }
}
