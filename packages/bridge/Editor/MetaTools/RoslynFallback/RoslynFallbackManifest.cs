using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityOpenMcpBridge.MetaTools.RoslynFallback
{
    /// <summary>
    /// install-manifest.json read/write for the downloaded Roslyn fallback.
    /// The manifest is written LAST during an install (after the staging dir
    /// is swapped into place), so its presence marks a complete install. An
    /// install is valid only when the manifest parses, lists every pinned
    /// package at the pinned version, and every DLL it names exists on disk —
    /// anything else reads as "not installed" (lenient: no exceptions
    /// escape).
    /// </summary>
    internal static class RoslynFallbackManifest
    {
        internal const string FileName = "install-manifest.json";

        internal sealed class InstalledPackage
        {
            public string Id;
            public string Version;
            public string Sha256;
            public List<string> Dlls = new List<string>();
        }

        internal static string PathFor(string installDir) =>
            Path.Combine(installDir, FileName);

        /// <summary>
        /// True when <paramref name="installDir"/> holds a complete install
        /// matching the current pin set in <see cref="RoslynFallbackConfig"/>.
        /// </summary>
        internal static bool IsInstallValid(string installDir)
        {
            try
            {
                var packages = Read(installDir);
                if (packages == null) return false;

                foreach (var pin in RoslynFallbackConfig.Packages)
                {
                    var installed = packages.FirstOrDefault(p =>
                        string.Equals(p.Id, pin.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.Version, pin.Version, StringComparison.Ordinal));
                    if (installed == null) return false;
                    if (installed.Dlls.Count == 0) return false;
                    foreach (var dll in installed.Dlls)
                    {
                        if (!File.Exists(Path.Combine(installDir, dll)))
                            return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Null when the manifest is missing or unparseable.</summary>
        internal static List<InstalledPackage> Read(string installDir)
        {
            try
            {
                var path = PathFor(installDir);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);

                // GetObjectArray is lenient — truncated/corrupt JSON yields an
                // empty array rather than null. A manifest with zero packages
                // is meaningless either way, so both read as "not installed".
                var entries = JsonBody.GetObjectArray(json, "packages");
                if (entries == null || entries.Length == 0) return null;

                var result = new List<InstalledPackage>();
                foreach (var entry in entries)
                {
                    var pkg = new InstalledPackage
                    {
                        Id = JsonBody.GetString(entry, "id"),
                        Version = JsonBody.GetString(entry, "version"),
                        Sha256 = JsonBody.GetString(entry, "sha256"),
                    };
                    var dlls = JsonBody.GetStringArray(entry, "dlls");
                    if (dlls != null) pkg.Dlls.AddRange(dlls);
                    if (string.IsNullOrEmpty(pkg.Id) || string.IsNullOrEmpty(pkg.Version))
                        return null;
                    result.Add(pkg);
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Atomic write (.tmp + move) so a crash can never leave a truncated
        /// manifest that IsInstallValid would misread.
        /// </summary>
        internal static void Write(string installDir, IEnumerable<InstalledPackage> packages)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{");
            sb.Append("\"bridgeVersion\":").Append(BridgeJson.EscapeString(BridgeVersionOrUnknown()));
            sb.Append(",\"roslynVersion\":").Append(BridgeJson.EscapeString(RoslynFallbackConfig.RoslynVersion));
            sb.Append(",\"installedAtUtc\":").Append(BridgeJson.EscapeString(
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            sb.Append(",\"packages\":[");
            var first = true;
            foreach (var p in packages)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":").Append(BridgeJson.EscapeString(p.Id));
                sb.Append(",\"version\":").Append(BridgeJson.EscapeString(p.Version));
                sb.Append(",\"sha256\":").Append(BridgeJson.EscapeString(p.Sha256 ?? ""));
                sb.Append(",\"dlls\":[");
                for (var i = 0; i < p.Dlls.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(BridgeJson.EscapeString(p.Dlls[i]));
                }
                sb.Append("]}");
            }
            sb.Append("]}");

            Directory.CreateDirectory(installDir);
            var path = PathFor(installDir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static string BridgeVersionOrUnknown()
        {
            // BridgeConstants.NpmPackage is "unity-open-mcp@X.Y.Z" — reuse the
            // pinned trio version rather than adding another sync point.
            var npm = Config.BridgeConstants.NpmPackage;
            var at = npm.LastIndexOf('@');
            return at >= 0 && at < npm.Length - 1 ? npm.Substring(at + 1) : "unknown";
        }
    }
}
