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
        private static readonly List<Diagnostic> Errors = new List<Diagnostic>();

        // A tool call answered with the gate envelope that sends this header as
        // "1" gets BuildHeader() back under the same name, snapshotted on the
        // main thread its dispatch already holds, so qualifying the result costs
        // the client no /compile-state round trip or second main-thread hop.
        internal const string Header = "X-Unity-Open-MCP-Compile-State";

        // Compile inputs are the files the C# pipeline reads. Project settings
        // and the package manifest are deliberately NOT part of the set: an
        // edit there that affects compilation (defines, API level, a package
        // add) starts a compile by itself, which re-records the inputs, while
        // the far more common non-code edits (company name, icons, a UI
        // package) must not flip every live result to "assembly_stale".
        private static readonly string[] SourceExtensions = { ".cs", ".asmdef", ".asmref" };

        // The compile state qualifies live tool results (/compile-state, or the
        // Header snapshot taken inside the tool's own dispatch), so nothing on
        // that path may walk the project. A background pass (enumeration,
        // stat and hashing need no Unity API) refreshes the cache at most every
        // RevalidateIntervalSeconds; the main thread only captures the root
        // list and reads the last completed value. Started() computes
        // synchronously so a compile generation records its exact inputs;
        // Hashes keeps that main-thread pass to a stat of every input plus a
        // read of the files that changed since the previous pass.
        private const double RevalidateIntervalSeconds = 2.0;
        private static readonly object CacheLock = new object();
        private static readonly SourceHashes Hashes = new SourceHashes(File.ReadAllBytes);
        private static string _cachedFingerprint;
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

        // Content hash per input file, keyed by the length + mtime it was read
        // under: any content write changes mtime, so a pass rereads only the
        // files whose stat moved and still yields the value a fresh hash of
        // every file would. The stat is taken before the read, so a write that
        // races the read leaves a stale key and the next pass rereads the file.
        // In memory only: a reload empties it, and the warm pass scheduled by
        // the static constructor refills it off the main thread, well before
        // the next edit starts a compile.
        internal sealed class SourceHashes
        {
            private readonly Func<string, byte[]> _read;
            private readonly object _lock = new object();
            private Dictionary<string, (long Length, long Ticks, string Hash)> _byPath =
                new Dictionary<string, (long Length, long Ticks, string Hash)>(StringComparer.Ordinal);

            internal SourceHashes(Func<string, byte[]> read)
            {
                _read = read;
            }

            // Throws when an input cannot be read (a missing file included);
            // unreadable inputs can never certify a clean compile.
            internal string Fingerprint(string[] paths)
            {
                Dictionary<string, (long Length, long Ticks, string Hash)> previous;
                lock (_lock) previous = _byPath;
                var next = new Dictionary<string, (long Length, long Ticks, string Hash)>(StringComparer.Ordinal);
                using var hash = SHA256.Create();
                var text = new StringBuilder();
                foreach (var path in paths.Distinct().OrderBy(p => p, StringComparer.Ordinal))
                {
                    var info = new FileInfo(path);
                    var length = info.Exists ? info.Length : -1;
                    var ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
                    if (!previous.TryGetValue(path, out var entry) || entry.Length != length || entry.Ticks != ticks)
                        entry = (length, ticks, Convert.ToBase64String(hash.ComputeHash(_read(path))));
                    next[path] = entry;
                    text.Append(path).Append(':').Append(entry.Hash).Append('\n');
                }
                // Started() and a background pass may overlap; each publishes a
                // complete map, and either one is a valid base for the next pass.
                lock (_lock) _byPath = next;
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
            }
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

        private static string Compute(Roots roots) => Hashes.Fingerprint(SourceFiles(roots));

        private static void Store(string fingerprint, double capturedAt)
        {
            lock (CacheLock)
            {
                // A synchronous Started() capture taken after this pass began
                // is newer; never let the background result overwrite it.
                if (capturedAt < _cachedAtEditorTime) return;
                _cachedFingerprint = fingerprint;
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
                    var fingerprint = Compute(CaptureRoots());
                    Store(fingerprint, now);
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
                        var fingerprint = Compute(roots);
                        Store(fingerprint, now);
                    }
                    catch
                    {
                        Store(null, now); // unreadable inputs can never certify a clean compile
                    }
                });
                // Until a pass completes, nothing certifies a clean compile —
                // and nothing proves a stale one either.
                return cached;
            }
            catch
            {
                Store(null, EditorApplication.timeSinceStartup);
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

        // ---- Compile handshake ------------------------------------------------
        //
        // A refresh / script write / recompile request leaves Unity with a
        // compile it has not STARTED yet: EditorApplication.isCompiling may read
        // true for a moment and then false again for seconds before
        // compilationStarted fires (an unfocused Editor defers it), so "not
        // compiling" right after such a call is not "idle". The dispatch that
        // left a compile requested records the generation it saw; Started()
        // clears the record when the next compile begins. The instance lock and
        // /compile-state publish it as `pending`, and the server-side wait holds
        // until it clears (bounded by its own grace period, since Unity can drop
        // a request that turns out to need no compile). SessionState: the record
        // survives the domain reload it announces.
        private const string RequestedKey = Prefix + "requestedAtGeneration";
        private const string RequestedAtKey = Prefix + "requestedAtUtc";
        private const string RunningKey = Prefix + "running";

        internal static int Generation => SessionState.GetInt(Prefix + "generation", 0);

        /// <summary>A compile was requested and has not started since.</summary>
        internal static bool Pending => SessionState.GetInt(RequestedKey, -1) >= 0;

        // Test seam: restore the handshake record a test changed (main thread).
        internal static void RestoreRequestForTest(int requestedAtGeneration, string requestedAtUtc)
        {
            SessionState.SetInt(RequestedKey, requestedAtGeneration);
            SessionState.SetString(RequestedAtKey, requestedAtUtc ?? "");
        }

        internal static int RequestedAtGenerationForTest => SessionState.GetInt(RequestedKey, -1);
        internal static string RequestedAtUtcForTest => SessionState.GetString(RequestedAtKey, "");

        /// <summary>Record that a compile is due (main thread).</summary>
        internal static void NoteRequested()
        {
            SessionState.SetInt(RequestedKey, Generation);
            SessionState.SetString(RequestedAtKey, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// End of a tool's main-thread dispatch: a compile Unity reports as due
        /// (isCompiling) while no pipeline run is in progress was requested by
        /// this call (or just before it) and has not started yet.
        /// </summary>
        internal static void NoteDispatchEnd()
        {
            try
            {
                if (EditorApplication.isCompiling && !SessionState.GetBool(RunningKey, false)) NoteRequested();
            }
            catch { /* the handshake is advisory; a dispatch never fails on it */ }
        }

        // What the last recompile_scripts imported before it requested its
        // compile (ScriptImportSync), keyed by the generation the call started
        // at. A successful compile ends in a domain reload that usually closes
        // the call's response before it is written; /compile-state publishes
        // this record so the server can still report the import with the
        // compile it waited for. SessionState: it survives that reload.
        private const string ScriptImportKey = Prefix + "lastScriptImport";

        /// <summary>Record a recompile_scripts call's import (main thread).</summary>
        internal static void NoteScriptImport(int generationAtStart, string scriptImportJson)
        {
            try
            {
                SessionState.SetString(ScriptImportKey,
                    "{\"generationAtStart\":" + generationAtStart + ",\"scriptImport\":" + scriptImportJson + "}");
            }
            catch { /* advisory: the call's own response carries it when it is written */ }
        }

        // The compile part of the instance lock (main thread: SessionState).
        // Readable while the bridge is down, so a status built from the lock
        // during a reload still knows the last generation and whether a compile
        // is due.
        internal static string LockJson()
        {
            var requestedAt = Pending ? SessionState.GetString(RequestedAtKey, "") : "";
            return "{\"generation\":" + Generation
                + ",\"running\":" + (SessionState.GetBool(RunningKey, false) ? "true" : "false")
                + ",\"completed\":" + (SessionState.GetBool(Prefix + "completed", false) ? "true" : "false")
                + ",\"failed\":" + (SessionState.GetBool(Prefix + "failed", false) ? "true" : "false")
                + ",\"errorCount\":" + SessionState.GetInt(Prefix + "errorCount", 0)
                + ",\"pending\":" + (Pending ? "true" : "false")
                + ",\"requestedAt\":" + (requestedAt.Length > 0 ? BridgeJson.EscapeString(requestedAt) : "null") + "}";
        }

        // The `compile` block of a gate envelope: the generation the call
        // started at (a compile the call triggered gets a higher one), the
        // generation now, and whether a compile is still due.
        internal static string EnvelopeJson(int generationAtStart)
            => "{\"generationAtStart\":" + generationAtStart
                + ",\"generation\":" + Generation
                + ",\"pending\":" + (Pending ? "true" : "false")
                + ",\"running\":" + (SessionState.GetBool(RunningKey, false) ? "true" : "false") + "}";

        private static void Started(object context)
        {
            SessionState.SetInt(Prefix + "generation", SessionState.GetInt(Prefix + "generation", 0) + 1);
            SessionState.SetInt(RequestedKey, -1);
            SessionState.SetBool(RunningKey, true);
            SessionState.SetBool(Prefix + "completed", false);
            SessionState.SetString(Prefix + "inputs", Sources(force: true) ?? "");
            SessionState.SetString(Prefix + "before", AssemblyMtime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Errors.Clear();
        }

        private static void AssemblyFinished(string path, CompilerMessage[] messages)
        {
            foreach (var error in messages.Where(m => m.type == CompilerMessageType.Error))
            {
                Errors.Add(new Diagnostic
                {
                    File = error.file ?? "",
                    Line = error.line,
                    Column = error.column,
                    Code = System.Text.RegularExpressions.Regex.Match(error.message ?? "", @"\bCS\d+\b").Value,
                    Message = error.message ?? "",
                });
            }
        }

        private static void Finished(object context)
        {
            var errors = Dedupe(Errors);
            var json = new StringBuilder();
            foreach (var error in errors)
            {
                if (json.Length > 0) json.Append(',');
                json.Append("{\"file\":").Append(BridgeJson.EscapeString(error.File))
                    .Append(",\"line\":").Append(error.Line)
                    .Append(",\"column\":").Append(error.Column)
                    .Append(",\"code\":").Append(BridgeJson.EscapeString(error.Code))
                    .Append(",\"message\":").Append(BridgeJson.EscapeString(error.Message)).Append('}');
            }
            SessionState.SetString(Prefix + "errors", "[" + json + "]");
            SessionState.SetInt(Prefix + "errorCount", errors.Count);
            SessionState.SetBool(Prefix + "failed", errors.Count > 0);
            SessionState.SetBool(Prefix + "completed", true);
            SessionState.SetBool(RunningKey, false);
            SessionState.SetString(Prefix + "after", AssemblyMtime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        internal sealed class Diagnostic
        {
            internal string File;
            internal int Line;
            internal int Column;
            internal string Code;
            internal string Message;
        }

        // A compiler command-line fragment: a quoted argument (-r:"Library/…,
        // /additionalfile:"…) or a Bee build-artifact path.
        private static readonly System.Text.RegularExpressions.Regex CommandLineFragment =
            new System.Text.RegularExpressions.Regex("\"|^[-/][A-Za-z]+:|Library/Bee/");

        /// <summary>
        /// One record per diagnostic. When compiler invocations interleave their
        /// output, Unity can parse a diagnostic a second time with the start of a
        /// command line glued before its path or a quoted argument after its
        /// message. That copy has the same line, column and code, a path ending
        /// with the clean path and a message containing the clean message:
        /// it is dropped and the clean record kept. Pure.
        /// </summary>
        internal static List<Diagnostic> Dedupe(IEnumerable<Diagnostic> diagnostics)
        {
            var kept = new List<Diagnostic>();
            foreach (var d in diagnostics)
            {
                var twin = kept.FindIndex(k => SameDiagnostic(k, d));
                if (twin < 0) kept.Add(d);
                else if (Cleanliness(d) > Cleanliness(kept[twin])) kept[twin] = d;
            }
            return kept;
        }

        private static bool SameDiagnostic(Diagnostic a, Diagnostic b)
        {
            if (a.Line != b.Line || a.Column != b.Column || a.Code != b.Code) return false;
            if (a.File.Length == 0 || b.File.Length == 0) return a.File == b.File && a.Message == b.Message;
            if (!CommandLineFragment.IsMatch(a.File + a.Message) && !CommandLineFragment.IsMatch(b.File + b.Message))
                return a.File == b.File && a.Message == b.Message;
            var files = a.File.EndsWith(b.File, StringComparison.Ordinal) || b.File.EndsWith(a.File, StringComparison.Ordinal);
            // CompilerMessage.message is the whole diagnostic line, so a fragment
            // glued before the path is a prefix of the message and one glued
            // after it a suffix: the clean message lies inside the garbled one.
            var messages = a.Message.IndexOf(b.Message, StringComparison.Ordinal) >= 0
                || b.Message.IndexOf(a.Message, StringComparison.Ordinal) >= 0;
            return files && messages;
        }

        private static double Cleanliness(Diagnostic d)
            => (CommandLineFragment.IsMatch(d.File) ? 0 : 2) + (CommandLineFragment.IsMatch(d.Message) ? 0 : 1)
               - (d.File.Length + d.Message.Length) / 1e6;

        internal static string BuildJson() => Build(summary: false);

        // The Header form of BuildJson: errors[] folds to errorCount so the
        // value stays small whatever the compile reported, and the JSON is
        // base64'd UTF-8 because a header value must be ASCII and the project
        // path need not be. Null when the snapshot cannot be taken: the response
        // then goes without it and the client reads /compile-state.
        internal static string BuildHeader()
        {
            try { return Convert.ToBase64String(Encoding.UTF8.GetBytes(Build(summary: true))); }
            catch { return null; }
        }

        private static string Build(bool summary)
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
            // Why it is indeterminate: no compile has completed in this Editor
            // session (it started on assemblies it did not build, and waiting
            // never changes that: only a compile does), or the current source
            // fingerprint is not known yet (a background pass after a reload).
            var indeterminateReason = status != "indeterminate" ? null
                : !completed ? "no_compile_this_session" : "fingerprint_pending";
            // Same identity the instance lock and /ping publish, so the server's
            // projectPath comparison cannot drift on cwd casing or symlink form.
            var projectPath = BridgeSession.ProjectPath ?? Path.GetFullPath(".");
            return "{\"status\":" + BridgeJson.EscapeString(status)
                + (indeterminateReason != null ? ",\"indeterminateReason\":" + BridgeJson.EscapeString(indeterminateReason) : "")
                + ",\"projectPath\":" + BridgeJson.EscapeString(projectPath)
                + ",\"generation\":" + SessionState.GetInt(Prefix + "generation", 0)
                + ",\"sourceMatches\":" + (matches ? "true" : "false")
                + ",\"pending\":" + (Pending ? "true" : "false")
                + ",\"beforeAssemblyMtimeMs\":" + SessionState.GetString(Prefix + "before", "0")
                + ",\"afterAssemblyMtimeMs\":" + SessionState.GetString(Prefix + "after", "0")
                + (summary
                    ? ",\"errorCount\":" + SessionState.GetInt(Prefix + "errorCount", 0)
                    : ",\"errors\":" + SessionState.GetString(Prefix + "errors", "[]") + LastScriptImportMember()) + "}";
        }

        private static string LastScriptImportMember()
        {
            var record = SessionState.GetString(ScriptImportKey, "");
            return record.Length > 0 ? ",\"lastScriptImport\":" + record : "";
        }
    }
}
