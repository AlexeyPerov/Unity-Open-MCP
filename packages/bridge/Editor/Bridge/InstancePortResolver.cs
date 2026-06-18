using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityOpenMcpBridge
{
    // M13 T4.3 — Per-project deterministic port + instance discovery.
    //
    // Two Unity projects running bridges simultaneously can't share a fixed
    // port. We derive the port deterministically from the project path
    // (20000 + (sha256(path) % 10000)), so the bridge and the MCP server agree
    // without any shared config. An explicit override
    // (UNITY_OPEN_MCP_BRIDGE_PORT env var / -UNITY_OPEN_MCP_BRIDGE_PORT=<n>
    // CLI arg) always wins — it's the escape hatch for users who pin a port
    // and for CI flows that allocate ports externally.
    //
    // The port formula mirrors mcp-server/src/instance-discovery.ts byte for
    // byte: take the first 8 bytes of SHA256(path) as a big-endian unsigned
    // 64-bit integer, mod 10000, + 20000. The 8-byte prefix keeps the modulo
    // inside Int64 range so C# (UInt64) and TypeScript (BigInt) agree exactly;
    // a full 256-bit modulo would diverge across language BigInts. The cross-
    // side consistency is pinned by tests on both sides
    // (InstancePortResolverTests.cs / instance-discovery.test.ts).
    //
    // Path normalization (forward-slash, trailing-slash trim) is applied
    // BEFORE hashing so the same project resolves to the same port whether
    // its path was recorded with a trailing separator or not. We deliberately
    // do NOT lowercase: on macOS/Linux paths are case-sensitive, and
    // lowercasing would collide distinct projects. Windows is case-insensitive
    // but the Editor reports the canonical casing, so this is a non-issue in
    // practice.
    public static class InstancePortResolver
    {
        // Port range: [20000, 29999]. Matches the spec and the MCP server.
        public const int PortRangeStart = 20000;
        public const int PortRangeSize = 10000;
        public const int LegacyDefaultPort = 19120;

        // instances/ lives under the existing ~/.unity-open-mcp convention used
        // by TestRunnerService / ScreenshotService / CompileCheckState. One
        // lock file per running bridge instance, keyed by project hash.
        // Overridable via InstancesDirOverride for tests (so they don't write
        // into the real ~/.unity-open-mcp). Production callers leave it null.
        public static string InstancesDir
        {
            get
            {
                if (!string.IsNullOrEmpty(InstancesDirOverride))
                    return InstancesDirOverride;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".unity-open-mcp",
                    "instances");
            }
        }

        // Test-only override for the instances dir. Not for production use.
        // Set to an absolute temp dir in [SetUp] / [TearDown].
        public static string InstancesDirOverride;

        public static string LockPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentNullException(nameof(projectPath));
            return Path.Combine(InstancesDir, ProjectHash(projectPath) + ".json");
        }

        // SHA256 of the normalized path, lowercase hex. Used as the lock file
        // name and as the `projectHash` field written into the lock JSON so the
        // MCP server can verify it matched the project it expected.
        public static string ProjectHash(string projectPath)
        {
            var normalized = NormalizePath(projectPath);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // Deterministic port for a project path: 20000 + (sha256(path) % 10000).
        public static int ComputePort(string projectPath)
        {
            var hashHex = ProjectHash(projectPath);
            // First 8 bytes (16 hex chars) as a big-endian UInt64, mod 10000.
            // C#'s UInt64 and TypeScript's BigInt agree on this exact value.
            var prefix = hashHex.Substring(0, 16);
            var value = ulong.Parse(prefix, System.Globalization.NumberStyles.HexNumber);
            return PortRangeStart + (int)(value % (ulong)PortRangeSize);
        }

        // Resolve the bridge port with override precedence:
        //   1. UNITY_OPEN_MCP_BRIDGE_PORT env var (caller-provided, already parsed)
        //   2. -UNITY_OPEN_MCP_BRIDGE_PORT=<n> CLI arg (caller-provided, already parsed)
        //   3. deterministic hash of the project path
        //
        // Pass null for envPort/cliPort when the caller found no override;
        // IsValidPort is the caller's responsibility (parse + range check)
        // so the resolver only trusts values the caller already validated.
        public static int ResolvePort(string projectPath, int? envPort, int? cliPort)
        {
            if (envPort.HasValue && IsValidPort(envPort.Value)) return envPort.Value;
            if (cliPort.HasValue && IsValidPort(cliPort.Value)) return cliPort.Value;
            return ComputePort(projectPath);
        }

        public static bool IsValidPort(int port) => port >= 1 && port <= 65535;

        // Normalize before hashing: forward slashes, no trailing slash. Mirrors
        // the TS side (instance-discovery.ts normalizePath) byte for byte.
        public static string NormalizePath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return "";
            var norm = projectPath.Replace('\\', '/');
            while (norm.Length > 1 && norm.EndsWith('/'))
                norm = norm.Substring(0, norm.Length - 1);
            return norm;
        }
    }
}
