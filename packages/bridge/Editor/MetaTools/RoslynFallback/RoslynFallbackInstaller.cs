using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools.RoslynFallback
{
    /// <summary>
    /// Download-on-demand installer for the IL-only Roslyn fallback described
    /// by <see cref="RoslynFallbackConfig"/>. Never runs at startup — only an
    /// explicit trigger (execute_csharp setup_roslyn flag, the editor menu, or
    /// the bridge window) starts it, and the caller owns the consent step.
    ///
    /// All network/disk work runs on a background thread; nothing there
    /// touches Unity APIs. On success the staging dir is atomically swapped
    /// into place, the manifest is written last, and RoslynHost.Reinitialize
    /// is scheduled on the main thread via EditorApplication.update.
    /// </summary>
    internal static class RoslynFallbackInstaller
    {
        internal enum InstallState { Idle, Installing, Installed, Failed }

        internal readonly struct InstallStatus
        {
            public readonly InstallState State;
            public readonly float Progress;   // 0..1 while Installing
            public readonly string Step;      // human-readable current step
            public readonly string Error;     // set when Failed

            public InstallStatus(InstallState state, float progress, string step, string error)
            {
                State = state;
                Progress = progress;
                Step = step;
                Error = error;
            }
        }

        private static readonly object Gate = new object();
        private static InstallState _state = InstallState.Idle;
        private static float _progress;
        private static string _step = "";
        private static string _error;

        internal static InstallStatus Status
        {
            get
            {
                lock (Gate)
                    return new InstallStatus(_state, _progress, _step, _error);
            }
        }

        /// <summary>True when a complete, pin-matching install exists.</summary>
        internal static bool IsInstalled =>
            RoslynFallbackManifest.IsInstallValid(RoslynFallbackConfig.InstallDir);

        /// <summary>
        /// Kick off the install on a background thread. No-op when already
        /// installing or installed. Returns true when a new install was
        /// started.
        /// </summary>
        internal static bool StartInstall()
        {
            lock (Gate)
            {
                if (_state == InstallState.Installing) return false;
                if (IsInstalled)
                {
                    _state = InstallState.Installed;
                    return false;
                }
                _state = InstallState.Installing;
                _progress = 0f;
                _step = "starting";
                _error = null;
            }

            Task.Run(() =>
            {
                try
                {
                    InstallCore();
                    lock (Gate)
                    {
                        _state = InstallState.Installed;
                        _progress = 1f;
                        _step = "installed";
                    }
                    ScheduleMainThread(() =>
                    {
                        RoslynHost.Reinitialize();
                        Debug.Log("[Unity Open MCP Bridge] Roslyn fallback installed at " +
                                  RoslynFallbackConfig.InstallDir +
                                  (RoslynHost.IsAvailable ? " (loaded)" : " (load pending)"));
                    });
                }
                catch (Exception e)
                {
                    lock (Gate)
                    {
                        _state = InstallState.Failed;
                        _error = e.Message;
                        _step = "failed";
                    }
                    ScheduleMainThread(() =>
                        Debug.LogWarning("[Unity Open MCP Bridge] Roslyn fallback install failed: " + e.Message));
                }
            });
            return true;
        }

        /// <summary>Allow a retry after a failure.</summary>
        internal static void ResetFailure()
        {
            lock (Gate)
            {
                if (_state == InstallState.Failed)
                {
                    _state = InstallState.Idle;
                    _error = null;
                }
            }
        }

        // ------------------------------------------------------------------
        // Core pipeline (background thread; no Unity APIs)
        // ------------------------------------------------------------------

        internal static void InstallCore()
        {
            var installDir = RoslynFallbackConfig.InstallDir;
            var stagingDir = RoslynFallbackConfig.StagingDir;
            var cacheDir = RoslynFallbackConfig.CacheDir;
            Directory.CreateDirectory(cacheDir);

            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            var packages = RoslynFallbackConfig.Packages;
            var installed = new List<RoslynFallbackManifest.InstalledPackage>();

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "unity-open-mcp-bridge/" + RoslynFallbackConfig.RoslynVersion);

                for (var i = 0; i < packages.Length; i++)
                {
                    var pkg = packages[i];
                    SetProgress((float)i / packages.Length, $"downloading {pkg.Id} {pkg.Version}");

                    var cachePath = Path.Combine(cacheDir, pkg.FileName);
                    if (!IsCacheValid(cachePath, pkg.Sha256))
                        DownloadWithRetry(http, pkg, cachePath);

                    SetProgress((i + 0.7f) / packages.Length, $"extracting {pkg.Id}");
                    var dlls = ExtractNetstandardDlls(cachePath, stagingDir);
                    if (dlls.Count == 0)
                        throw new InvalidOperationException(
                            $"no compatible lib/<tfm> DLLs found in {pkg.FileName}");

                    installed.Add(new RoslynFallbackManifest.InstalledPackage
                    {
                        Id = pkg.Id,
                        Version = pkg.Version,
                        Sha256 = pkg.Sha256,
                        Dlls = dlls,
                    });
                }
            }

            SetProgress(0.98f, "finalizing");

            // Atomic-ish swap: never leave a partially populated InstallDir.
            // The manifest is written AFTER the move so a crash between the
            // two leaves an install that IsInstallValid rejects.
            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            var parent = Path.GetDirectoryName(installDir);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            Directory.Move(stagingDir, installDir);
            RoslynFallbackManifest.Write(installDir, installed);
        }

        private static bool IsCacheValid(string cachePath, string expectedSha256)
        {
            return File.Exists(cachePath) &&
                   string.Equals(ComputeSha256(cachePath), expectedSha256,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void DownloadWithRetry(
            HttpClient http, RoslynFallbackConfig.PinnedPackage pkg, string cachePath)
        {
            var url = RoslynFallbackConfig.DownloadUrl(pkg);
            Exception last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var tmp = cachePath + ".tmp";
                    using (var response = http.GetAsync(url).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (var body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var file = File.Create(tmp))
                            body.CopyTo(file);
                    }

                    var actual = ComputeSha256(tmp);
                    if (!string.Equals(actual, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        // Hash mismatch is not retried: flat-container content
                        // is immutable, so a second download cannot legitimately
                        // differ. Fail closed.
                        throw new InvalidOperationException(
                            $"SHA-256 mismatch for {pkg.FileName} from {url}: " +
                            $"expected {pkg.Sha256}, got {actual}");
                    }

                    if (File.Exists(cachePath)) File.Delete(cachePath);
                    File.Move(tmp, cachePath);
                    return;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception e)
                {
                    last = e;
                }
            }
            throw new InvalidOperationException(
                $"download failed for {url}: {last?.Message}", last);
        }

        internal static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Extract the best lib/&lt;tfm&gt;/*.dll set from a nupkg (a plain zip)
        /// into <paramref name="destDir"/>. Satellite resource folders
        /// (lib/&lt;tfm&gt;/&lt;lang&gt;/…) are skipped. Returns the extracted file
        /// names (no paths).
        /// </summary>
        internal static List<string> ExtractNetstandardDlls(string nupkgPath, string destDir)
        {
            using (var zip = ZipFile.OpenRead(nupkgPath))
            {
                foreach (var tfm in RoslynFallbackConfig.TfmPriority)
                {
                    var prefix = "lib/" + tfm + "/";
                    var entries = zip.Entries.Where(e =>
                            e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            // no subfolders below the TFM dir (satellite langs)
                            e.FullName.IndexOf('/', prefix.Length) < 0)
                        .ToList();
                    if (entries.Count == 0) continue;

                    var names = new List<string>();
                    foreach (var entry in entries)
                    {
                        var name = Path.GetFileName(entry.FullName);
                        var dest = Path.Combine(destDir, name);
                        entry.ExtractToFile(dest, overwrite: true);
                        names.Add(name);
                    }
                    return names;
                }
            }
            return new List<string>();
        }

        private static void SetProgress(float progress, string step)
        {
            lock (Gate)
            {
                _progress = Mathf.Clamp01(progress);
                _step = step;
            }
        }

        private static void ScheduleMainThread(Action action)
        {
            // Thread-safe main-thread hop; the bridge's dispatcher drains on
            // EditorApplication.update.
            MainThreadDispatcher.Enqueue(action);
        }
    }
}
