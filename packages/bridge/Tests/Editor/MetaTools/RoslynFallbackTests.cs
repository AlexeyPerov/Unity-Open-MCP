using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.MetaTools.RoslynFallback;

namespace UnityOpenMcpBridge.Tests
{
    // Roslyn fallback installer (Unity 6000.x ships only R2R Roslyn images —
    // feedback-01-08-glm §1 follow-up). Everything here is offline: hash
    // computation, nupkg-shaped zip extraction, manifest round-trip, and the
    // RoslynHost candidate ordering. The real download path is exercised
    // manually (see docs/troubleshooting.md and the changelog entry).
    public static class RoslynFallbackTests
    {
        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"roslyn-fallback-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // --- ComputeSha256 -------------------------------------------------

        [Test]
        public static void ComputeSha256_KnownVector_MatchesExpectedHash()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"sha-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("abc"));
                Assert.AreEqual(
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    RoslynFallbackInstaller.ComputeSha256(tmp),
                    "SHA-256 of 'abc' must match the NIST test vector");
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        // --- ExtractNetstandardDlls ---------------------------------------

        private static string BuildFakeNupkg(string dir, params string[] entryNames)
        {
            var path = Path.Combine(dir, "fake.nupkg");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var name in entryNames)
                {
                    var entry = zip.CreateEntry(name);
                    using (var s = entry.Open())
                    {
                        var bytes = Encoding.ASCII.GetBytes("stub");
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            return path;
        }

        [Test]
        public static void ExtractNetstandardDlls_PicksNetstandard20_SkipsOtherTfmsAndSatellites()
        {
            var dir = TempDir();
            try
            {
                var nupkg = BuildFakeNupkg(dir,
                    "lib/netstandard2.0/Foo.dll",
                    "lib/net472/Bar.dll",
                    "lib/netstandard2.0/de/Foo.resources.dll",
                    "foo.nuspec");
                var dest = Path.Combine(dir, "out");
                Directory.CreateDirectory(dest);

                var extracted = RoslynFallbackInstaller.ExtractNetstandardDlls(nupkg, dest);

                Assert.AreEqual(new List<string> { "Foo.dll" }, extracted,
                    "only the top-level netstandard2.0 DLL must be extracted");
                Assert.IsTrue(File.Exists(Path.Combine(dest, "Foo.dll")));
                Assert.IsFalse(File.Exists(Path.Combine(dest, "Bar.dll")),
                    "net472 DLLs must not be extracted");
                Assert.IsFalse(File.Exists(Path.Combine(dest, "Foo.resources.dll")),
                    "satellite resource DLLs must not be extracted");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public static void ExtractNetstandardDlls_NoCompatibleTfm_ReturnsEmpty()
        {
            var dir = TempDir();
            try
            {
                var nupkg = BuildFakeNupkg(dir, "lib/net6.0/Foo.dll", "foo.nuspec");
                var dest = Path.Combine(dir, "out");
                Directory.CreateDirectory(dest);

                var extracted = RoslynFallbackInstaller.ExtractNetstandardDlls(nupkg, dest);

                Assert.IsEmpty(extracted);
                Assert.IsEmpty(Directory.GetFiles(dest));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // --- Manifest -------------------------------------------------------

        private static List<RoslynFallbackManifest.InstalledPackage> FullPinnedSet(string installDir)
        {
            // One stub DLL per pinned package so IsInstallValid's on-disk
            // check passes.
            var list = new List<RoslynFallbackManifest.InstalledPackage>();
            foreach (var pin in RoslynFallbackConfig.Packages)
            {
                var dll = pin.Id + ".stub.dll";
                File.WriteAllBytes(Path.Combine(installDir, dll), new byte[] { 0x4d, 0x5a });
                list.Add(new RoslynFallbackManifest.InstalledPackage
                {
                    Id = pin.Id,
                    Version = pin.Version,
                    Sha256 = pin.Sha256,
                    Dlls = new List<string> { dll },
                });
            }
            return list;
        }

        [Test]
        public static void Manifest_RoundTrip_IsInstallValid()
        {
            var dir = TempDir();
            try
            {
                RoslynFallbackManifest.Write(dir, FullPinnedSet(dir));

                var read = RoslynFallbackManifest.Read(dir);
                Assert.IsNotNull(read);
                Assert.AreEqual(RoslynFallbackConfig.Packages.Length, read.Count);
                Assert.IsTrue(read.Any(p => p.Id == "microsoft.codeanalysis.csharp"));

                Assert.IsTrue(RoslynFallbackManifest.IsInstallValid(dir),
                    "a manifest listing every pin with existing DLLs must be valid");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public static void Manifest_MissingDllOnDisk_IsInvalid()
        {
            var dir = TempDir();
            try
            {
                var packages = FullPinnedSet(dir);
                RoslynFallbackManifest.Write(dir, packages);
                File.Delete(Path.Combine(dir, packages[0].Dlls[0]));

                Assert.IsFalse(RoslynFallbackManifest.IsInstallValid(dir),
                    "a manifest naming a missing DLL must read as not installed");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public static void Manifest_CorruptJson_IsInvalidWithoutThrowing()
        {
            var dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, RoslynFallbackManifest.FileName),
                    "{\"packages\": [ TRUNCATED");
                Assert.IsFalse(RoslynFallbackManifest.IsInstallValid(dir));
                Assert.IsNull(RoslynFallbackManifest.Read(dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public static void Manifest_Missing_IsInvalid()
        {
            var dir = TempDir();
            try
            {
                Assert.IsFalse(RoslynFallbackManifest.IsInstallValid(dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // --- RoslynHost candidate ordering ----------------------------------

        [Test]
        public static void CandidateOrder_FallbackDirIsLast_AfterThreeEditorPaths()
        {
            var dir = TempDir();
            var prevOverride = RoslynFallbackConfig.RoslynDirOverride;
            try
            {
                RoslynFallbackConfig.RoslynDirOverride = dir;

                var candidates = RoslynHost
                    .GetRoslynDirectoryCandidates("/contents")
                    .ToList();

                Assert.AreEqual(4, candidates.Count,
                    "three editor paths + the downloaded fallback");
                Assert.AreEqual(dir, candidates[3],
                    "the fallback dir must be probed LAST so editor-shipped " +
                    "Mono Roslyn keeps winning where it exists");
                Assert.AreEqual(1, candidates.Count(c => c == dir),
                    "the fallback dir must appear exactly once");
                StringAssert.Contains("DotNetSdkRoslyn", candidates[1]);
            }
            finally
            {
                RoslynFallbackConfig.RoslynDirOverride = prevOverride;
                Directory.Delete(dir, recursive: true);
            }
        }

        // --- setup_roslyn tool flow (no-network paths only) ------------------

        [Test]
        public static void SetupRoslyn_WhenRoslynAlreadyAvailable_ReturnsStatusWithoutValidationError()
        {
            // Only meaningful where the editor ships a Mono-loadable Roslyn
            // (2022.3): with Roslyn available, setup_roslyn must short-circuit
            // to already_available and never require 'code' or start a
            // download. On editors where Roslyn is unavailable this test would
            // hit the network path, so it is skipped there.
            Assume.That(RoslynHost.Initialize(),
                "requires an editor-shipped Mono-loadable Roslyn (skipped on Unity 6000.x)");

            var result = ExecuteCSharpTool.Execute("{\"setup_roslyn\": true}");

            Assert.IsTrue(result.Success, "setup_roslyn alone must be a valid request");
            StringAssert.Contains("already_available", result.Output ?? "");
        }
    }
}
