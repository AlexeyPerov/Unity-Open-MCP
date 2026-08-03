using System;
using System.IO;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge.MetaTools.RoslynFallback
{
    /// <summary>
    /// Static, hash-pinned description of the IL-only Roslyn fallback the
    /// bridge can download on demand for editors that ship no Mono-loadable
    /// Roslyn (Unity 6000.x ships only ReadyToRun images — see
    /// RoslynHost.IsReadyToRunImage). The full netstandard2.0 dependency
    /// closure of Microsoft.CodeAnalysis.CSharp 4.8.0 is pinned here so no
    /// nuspec resolution happens at runtime and every downloaded byte is
    /// verified against a SHA-256 recorded at review time.
    ///
    /// Pin computation procedure (record of 2026-08-03): each nupkg was
    /// downloaded twice from the flat-container URL below and hashed with
    /// `shasum -a 256`; both passes matched. NuGet flat-container content is
    /// immutable per (id, version), so a mismatch at install time means a
    /// corrupted or tampered download and MUST fail the install.
    /// </summary>
    internal static class RoslynFallbackConfig
    {
        internal readonly struct PinnedPackage
        {
            public readonly string Id;
            public readonly string Version;
            public readonly string Sha256;

            public PinnedPackage(string id, string version, string sha256)
            {
                Id = id;
                Version = version;
                Sha256 = sha256;
            }

            public string FileName => $"{Id}.{Version}.nupkg";
        }

        /// <summary>Roslyn version the install directory is keyed by.</summary>
        internal const string RoslynVersion = "4.8.0";

        internal const string FlatContainerBaseUrl = "https://api.nuget.org/v3-flatcontainer";

        /// <summary>
        /// Microsoft.CodeAnalysis.CSharp 4.8.0 + its complete netstandard2.0
        /// closure. Everything ships; nothing is assumed to be provided by the
        /// editor's Mono (Unity bundles far older assembly versions of these).
        /// </summary>
        internal static readonly PinnedPackage[] Packages =
        {
            new PinnedPackage("microsoft.codeanalysis.csharp", "4.8.0",
                "3263a75c9bddfdecece543dcab218b9db673e66f9579da517c1fa415979eaa45"),
            new PinnedPackage("microsoft.codeanalysis.common", "4.8.0",
                "dc81229d54d9abafda6a331503c5d344edcb4c812dd0fba11b22131892edab3e"),
            new PinnedPackage("system.collections.immutable", "7.0.0",
                "f5a9f6c1bc6e7b6aabb6e818112f5ac2c85083e29f26a6a386786ce3991021d9"),
            new PinnedPackage("system.reflection.metadata", "7.0.0",
                "1b000a4219213c1613aa645d1bd73db5aaab292283c325203848562cac5634f2"),
            new PinnedPackage("system.text.encoding.codepages", "7.0.0",
                "782293570ba60f4e7564472825c0d54469c8180b04bcaa5f1f7c9d2a5b87c66a"),
            new PinnedPackage("system.runtime.compilerservices.unsafe", "6.0.0",
                "6c41b53e70e9eee298cff3a02ce5acdd15b04125589be0273f0566026720a762"),
            new PinnedPackage("system.memory", "4.5.5",
                "10f43da352a29fb2b3188e4edd4dcf5100194c8b526e4f61fe2e2b5623775a22"),
            new PinnedPackage("system.buffers", "4.5.1",
                "c30b3dd2c7e2f4cee4b823d692fd42118309b42ab1f5007f923d329a5b0d6b12"),
            new PinnedPackage("system.numerics.vectors", "4.4.0",
                "6ae5d02b67e52ff2699c1feb11c01c526e2f60c09830432258e0809486aabb65"),
            new PinnedPackage("system.threading.tasks.extensions", "4.5.4",
                "a304a963cc0796c5179f9c6b7d8022bbce3b2fa7c029eb6196f631f7b462d678"),
        };

        /// <summary>
        /// TFM lib folders accepted from a nupkg, in preference order. Every
        /// pinned package ships netstandard2.0; the tail entries are a safety
        /// net should a future pin lack it.
        /// </summary>
        internal static readonly string[] TfmPriority =
        {
            "netstandard2.0", "netstandard1.3", "netstandard1.1",
        };

        internal static string DownloadUrl(PinnedPackage p) =>
            $"{FlatContainerBaseUrl}/{p.Id}/{p.Version}/{p.Id}.{p.Version}.nupkg";

        /// <summary>
        /// Test-only override for the install dir (mirrors
        /// InstancePortResolver.InstancesDirOverride). Production callers leave
        /// it null. When set, it is used verbatim as the install dir.
        /// </summary>
        public static string RoslynDirOverride;

        /// <summary>~/.unity-open-mcp/roslyn — shared across projects/editors.</summary>
        internal static string RoslynRootDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BridgeConstants.SettingsDirName,
            "roslyn");

        /// <summary>Final install dir the RoslynHost candidate points at.</summary>
        internal static string InstallDir =>
            !string.IsNullOrEmpty(RoslynDirOverride)
                ? RoslynDirOverride
                : Path.Combine(RoslynRootDir, RoslynVersion);

        /// <summary>Staging dir swapped into place atomically on success.</summary>
        internal static string StagingDir => InstallDir + ".installing";

        /// <summary>Downloaded nupkg cache (hash-verified; safe to delete).</summary>
        internal static string CacheDir => Path.Combine(RoslynRootDir, "cache");
    }
}
