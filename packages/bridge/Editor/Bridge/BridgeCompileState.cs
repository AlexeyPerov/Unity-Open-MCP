using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityOpenMcpBridge
{
    // SessionState survives domain reload, but never survives an Editor restart.
    // Only a completed pipeline whose input content still matches can certify clean.
    [InitializeOnLoad]
    internal static class BridgeCompileState
    {
        private const string Prefix = "UnityOpenMcp.Compile.";
        private static readonly StringBuilder Errors = new StringBuilder();

        // Compile inputs are the files the C# pipeline reads. Project settings
        // and the package manifest are deliberately NOT part of the set: an
        // edit there that affects compilation (defines, API level, a package
        // add) starts a compile by itself, which re-records the inputs, while
        // the far more common non-code edits (company name, icons, a UI
        // package) must not flip every live result to "assembly_stale".
        private static readonly string[] SourceExtensions = { ".cs", ".asmdef", ".asmref" };

        // /compile-state is requested after live tool calls, so nothing on the
        // request path may walk the project. A background pass (enumeration,
        // stat and hashing need no Unity API) refreshes the cache at most every
        // RevalidateIntervalSeconds; the main thread only captures the root
        // list and reads the last completed value. Started() computes
        // synchronously so a compile generation records its exact inputs.
        private const double RevalidateIntervalSeconds = 2.0;
        private static readonly object CacheLock = new object();
        private static string _cachedFingerprint;
        private static string _cachedSignature;
        private static double _cachedAtEditorTime = double.NegativeInfinity;
        private static Task _refresh;

        static BridgeCompileState()
        {
            CompilationPipeline.compilationStarted += Started;
            CompilationPipeline.assemblyCompilationFinished += AssemblyFinished;
            CompilationPipeline.compilationFinished += Finished;
            // Every domain reload empties the static cache while SessionState
            // still says "completed". Warm the fingerprint on the first update
            // tick so the first /compile-state of the new domain can answer
            // instead of reporting "indeterminate" until a request happens to
            // trigger the background pass. Deferred, not inline: the pipeline
            // and package APIs are not reliable inside InitializeOnLoad.
            EditorUpdateOnce.Schedule(() => Sources(force: false));
        }

        internal static string Fingerprint(string[] paths)
        {
            using var hash = SHA256.Create();
            var text = new StringBuilder();
            foreach (var path in paths.Distinct().OrderBy(p => p, StringComparer.Ordinal))
            {
                text.Append(path).Append(':');
                text.Append(Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)))).Append('\n');
            }
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        // Membership + size + mtime of the input set. Cheap (no file reads);
        // any content write changes mtime, so a stable signature means the
        // cached content fingerprint is still valid.
        internal static string StatSignature(string[] paths)
        {
            using var hash = SHA256.Create();
            var text = new StringBuilder();
            foreach (var path in paths.Distinct().OrderBy(p => p, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                text.Append(path).Append(':')
                    .Append(info.Exists ? info.Length : -1).Append(':')
                    .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
            }
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        // Main-thread only (Unity APIs): the pipeline's own source list plus the
        // directories whose not-yet-imported scripts must still invalidate truth.
        private sealed class Roots
        {
            internal string[] PipelineFiles;
            internal string[] Directories;
        }

        private static Roots CaptureRoots()
        {
            return new Roots
            {
                PipelineFiles = CompilationPipeline.GetAssemblies().SelectMany(a => a.sourceFiles).ToArray(),
                Directories = new[] { "Assets" }
                    .Concat(UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                        .Where(p => p.source == UnityEditor.PackageManager.PackageSource.Local
                            || p.source == UnityEditor.PackageManager.PackageSource.Embedded)
                        .Select(p => p.resolvedPath))
                    .ToArray(),
            };
        }

        private static bool IsSource(string path)
            => SourceExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // File system only, safe off the main thread. Native extension patterns
        // keep the walk from materializing every texture/model/meta path under
        // Assets/; the IsSource filter guards against the Win32 "*.cs" matching
        // longer extensions through 8.3 names.
        private static string[] SourceFiles(Roots roots)
        {
            IEnumerable<string> files = roots.PipelineFiles;
            foreach (var directory in roots.Directories)
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;
                foreach (var ext in SourceExtensions)
                    files = files.Concat(Directory.EnumerateFiles(directory, "*" + ext, SearchOption.AllDirectories));
            }
            return files.Where(IsSource).ToArray();
        }

        private static string Compute(Roots roots, out string signature)
        {
            var files = SourceFiles(roots);
            signature = StatSignature(files);
            lock (CacheLock)
            {
                if (_cachedFingerprint != null && signature == _cachedSignature) return _cachedFingerprint;
            }
            return Fingerprint(files);
        }

        private static void Store(string fingerprint, string signature, double capturedAt)
        {
            lock (CacheLock)
            {
                // A synchronous Started() capture taken after this pass began
                // is newer; never let the background result overwrite it.
                if (capturedAt < _cachedAtEditorTime) return;
                _cachedFingerprint = fingerprint;
                _cachedSignature = signature;
                _cachedAtEditorTime = capturedAt;
            }
        }

        // Returns the current source fingerprint, or null when it is UNKNOWN:
        // no pass has completed in this domain yet (cold cache right after a
        // reload) or the inputs could not be read. Callers must treat null as
        // "cannot tell", never as "the sources changed" — a cold cache used to
        // read as "" here and made every first result after a reload report
        // assembly_stale until the background pass caught up.
        private static string Sources(bool force)
        {
            try
            {
                var now = EditorApplication.timeSinceStartup;
                if (force)
                {
                    var fingerprint = Compute(CaptureRoots(), out var signature);
                    Store(fingerprint, signature, now);
                    return fingerprint;
                }

                string cached;
                double cachedAt;
                bool refreshing;
                lock (CacheLock)
                {
                    cached = _cachedFingerprint;
                    cachedAt = _cachedAtEditorTime;
                    refreshing = _refresh != null && !_refresh.IsCompleted;
                }
                if (refreshing || now - cachedAt < RevalidateIntervalSeconds)
                    return cached;

                var roots = CaptureRoots();
                _refresh = Task.Run(() =>
                {
                    try
                    {
                        var fingerprint = Compute(roots, out var signature);
                        Store(fingerprint, signature, now);
                    }
                    catch
                    {
                        Store(null, null, now); // unreadable inputs can never certify a clean compile
                    }
                });
                // Until a pass completes, nothing certifies a clean compile —
                // and nothing proves a stale one either.
                return cached;
            }
            catch
            {
                Store(null, null, EditorApplication.timeSinceStartup);
                return null;
            }
        }

        private static double AssemblyMtime()
        {
            try
            {
                return Directory.GetFiles("Library/ScriptAssemblies", "*.dll")
                    .Select(p => (File.GetLastWriteTimeUtc(p) - new DateTime(1970, 1, 1)).TotalMilliseconds)
                    .DefaultIfEmpty(0).Max();
            }
            catch { return 0; }
        }

        private static void Started(object context)
        {
            SessionState.SetInt(Prefix + "generation", SessionState.GetInt(Prefix + "generation", 0) + 1);
            SessionState.SetBool(Prefix + "completed", false);
            SessionState.SetString(Prefix + "inputs", Sources(force: true) ?? "");
            SessionState.SetString(Prefix + "before", AssemblyMtime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Errors.Clear();
        }

        private static void AssemblyFinished(string path, CompilerMessage[] messages)
        {
            foreach (var error in messages.Where(m => m.type == CompilerMessageType.Error))
            {
                if (Errors.Length > 0) Errors.Append(',');
                Errors.Append("{\"file\":").Append(BridgeJson.EscapeString(error.file))
                    .Append(",\"line\":").Append(error.line)
                    .Append(",\"column\":").Append(error.column)
                    .Append(",\"code\":").Append(BridgeJson.EscapeString(System.Text.RegularExpressions.Regex.Match(error.message ?? "", @"\bCS\d+\b").Value))
                    .Append(",\"message\":").Append(BridgeJson.EscapeString(error.message)).Append('}');
            }
        }

        private static void Finished(object context)
        {
            SessionState.SetString(Prefix + "errors", "[" + Errors + "]");
            SessionState.SetBool(Prefix + "failed", Errors.Length > 0);
            SessionState.SetBool(Prefix + "completed", true);
            SessionState.SetString(Prefix + "after", AssemblyMtime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        internal static string BuildJson()
        {
            var inputs = SessionState.GetString(Prefix + "inputs", "");
            var completed = SessionState.GetBool(Prefix + "completed", false);
            // null = the current fingerprint is unknown (cold cache after a
            // reload, unreadable inputs). Unknown is "indeterminate", never
            // "assembly_stale": only a KNOWN fingerprint that differs from the
            // recorded inputs proves the sources moved on.
            var sources = Sources(force: false);
            var known = completed && inputs.Length > 0 && sources != null;
            var matches = known && inputs == sources;
            var failed = SessionState.GetBool(Prefix + "failed", false) || EditorUtility.scriptCompilationFailed;
            // A failed compile is current truth until the next compile finishes,
            // whatever happened to the sources since: reporting it as merely
            // "stale" would hide its diagnostics. sourceMatches still tells the
            // caller whether those diagnostics describe the code now on disk.
            var status = EditorApplication.isCompiling ? "currently_compiling"
                : failed ? "compile_failed"
                : known && !matches ? "assembly_stale"
                : matches ? "no_errors_found" : "indeterminate";
            // Same identity the instance lock and /ping publish, so the server's
            // projectPath comparison cannot drift on cwd casing or symlink form.
            var projectPath = BridgeSession.ProjectPath ?? Path.GetFullPath(".");
            return "{\"status\":" + BridgeJson.EscapeString(status)
                + ",\"projectPath\":" + BridgeJson.EscapeString(projectPath)
                + ",\"generation\":" + SessionState.GetInt(Prefix + "generation", 0)
                + ",\"sourceMatches\":" + (matches ? "true" : "false")
                + ",\"beforeAssemblyMtimeMs\":" + SessionState.GetString(Prefix + "before", "0")
                + ",\"afterAssemblyMtimeMs\":" + SessionState.GetString(Prefix + "after", "0")
                + ",\"errors\":" + SessionState.GetString(Prefix + "errors", "[]") + "}";
        }
    }
}
