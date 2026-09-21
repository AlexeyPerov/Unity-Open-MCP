using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        // /compile-state is requested after every successful live tool call, so
        // the content fingerprint must not be recomputed per request. The cache
        // is keyed by a cheap stat signature (path + length + mtime) of the same
        // file set: content is only re-hashed when that signature changes, and
        // the stat pass itself is skipped when the last check is recent.
        // Started() bypasses the throttle so a compile generation always records
        // the exact inputs it was built from.
        private const double RevalidateIntervalSeconds = 2.0;
        private static string _cachedFingerprint;
        private static string _cachedSignature;
        private static double _cachedAtEditorTime = double.NegativeInfinity;

        static BridgeCompileState()
        {
            CompilationPipeline.compilationStarted += Started;
            CompilationPipeline.assemblyCompilationFinished += AssemblyFinished;
            CompilationPipeline.compilationFinished += Finished;
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

        private static string[] SourceFiles()
        {
            // Include unimported Assets files and assembly definitions as well as
            // pipeline-owned package sources. A newly written script must invalidate truth.
            return CompilationPipeline.GetAssemblies().SelectMany(a => a.sourceFiles)
                .Concat(Directory.GetFiles("Assets", "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".cs") || p.EndsWith(".asmdef") || p.EndsWith(".asmref")))
                .Concat(UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(p => p.source == UnityEditor.PackageManager.PackageSource.Local || p.source == UnityEditor.PackageManager.PackageSource.Embedded)
                    .SelectMany(p => Directory.GetFiles(p.resolvedPath, "*", SearchOption.AllDirectories))
                    .Where(p => p.EndsWith(".cs") || p.EndsWith(".asmdef") || p.EndsWith(".asmref")))
                .Concat(new[] { "Packages/manifest.json", "ProjectSettings/ProjectSettings.asset" })
                .ToArray();
        }

        private static string Sources(bool force)
        {
            try
            {
                var now = EditorApplication.timeSinceStartup;
                if (!force && _cachedFingerprint != null && now - _cachedAtEditorTime < RevalidateIntervalSeconds)
                    return _cachedFingerprint;
                var files = SourceFiles();
                var signature = StatSignature(files);
                if (_cachedFingerprint == null || signature != _cachedSignature)
                    _cachedFingerprint = Fingerprint(files);
                _cachedSignature = signature;
                _cachedAtEditorTime = now;
                return _cachedFingerprint;
            }
            catch
            {
                _cachedFingerprint = null;
                _cachedSignature = null;
                return ""; // unreadable inputs can never certify a clean compile
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
            SessionState.SetString(Prefix + "inputs", Sources(force: true));
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
            var matches = completed && inputs.Length > 0 && inputs == Sources(force: false);
            var failed = SessionState.GetBool(Prefix + "failed", false) || EditorUtility.scriptCompilationFailed;
            var status = EditorApplication.isCompiling ? "currently_compiling"
                : completed && !matches ? "assembly_stale" : failed ? "compile_failed"
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
