using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityOpenMcpBridge.MetaTools;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityOpenMcpBridge.Batch
{
    // Headless compile-check state machine.
    //
    // compile_check cannot use the synchronous BridgeBatchEntry.Execute() path:
    // it must wait for project compilation to settle, and RequestScriptCompilation
    // triggers a domain reload that wipes in-memory subscribers. This class mirrors
    // the TestRunner PlayMode pattern — a [InitializeOnLoad] survivor that persists
    // collected CompilerMessages to disk so the check survives the reload, finalizes
    // once Unity stops compiling, then hands the JSON back to BridgeBatchEntry for
    // emission between the output markers.
    //
    // In normal (non-batch) Editor operation the static constructor still wires the
    // events, but every entry point early-returns when no pending file is present,
    // so this is inert outside an active compile-check run.
    [InitializeOnLoad]
    public static class CompileCheckState
    {
        internal static readonly string StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unity-open-mcp");

        internal static string PendingFilePath => Path.Combine(StatusDir, "compile-check-pending.json");

        // Bound the payload for catastrophically broken projects. The first N
        // errors are enough for an agent to diagnose; beyond that, fix forward
        // and re-check.
        internal const int MaxErrors = 200;

        private const long DefaultTimeoutMs = 300_000;

        static CompileCheckState()
        {
            // Re-subscribe after a domain reload so a compile-check that was
            // mid-flight before the reload keeps collecting messages.
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        /// <summary>
        /// Begins a headless compile-check. Writes the pending marker, subscribes
        /// to compilation events in this domain, and requests a fresh script
        /// compile so messages are captured deterministically. Returns immediately;
        /// the <see cref="Update"/> loop finalizes across the domain reload.
        /// </summary>
        public static void Start(long timeoutMs)
        {
            // 0 = unspecified (no --timeout-ms flag). Apply the default before
            // clamping, so an explicit out-of-range value is still clamped but
            // an omitted flag yields the documented 5-minute default.
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;
            if (timeoutMs < 30_000) timeoutMs = 30_000;
            if (timeoutMs > 600_000) timeoutMs = 600_000;

            Directory.CreateDirectory(StatusDir);

            var pending = new PendingState
            {
                startedAtMs = NowMs(),
                timeoutMs = timeoutMs,
            };
            WritePending(pending);

            // The static constructor only fires after a reload — subscribe in
            // this domain too so the pre-reload compile is captured.
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.update -= Update;
            EditorApplication.update += Update;

            try
            {
                CompilationPipeline.RequestScriptCompilation();
            }
            catch (Exception e)
            {
                // If RequestScriptCompilation is unavailable (very old Unity) or
                // rejected, the Update loop still finalizes using whatever the
                // startup compile produced — surface the failure rather than hang.
                Debug.LogWarning($"[CompileCheck] RequestScriptCompilation failed: {e.Message}");
            }
        }

        private static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            if (!File.Exists(PendingFilePath)) return;

            var pending = ReadPending();
            if (pending == null) return;

            CollectErrors(pending, assembly, messages);
            WritePending(pending);
        }

        /// <summary>
        /// Pure collection step (testable without file IO). Appends error-severity
        /// compiler messages to the pending state, capped at <see cref="MaxErrors"/>.
        /// </summary>
        internal static void CollectErrors(PendingState pending, string assembly, CompilerMessage[] messages)
        {
            if (pending.assembliesSeen == null)
                pending.assembliesSeen = new List<string>();
            if (!pending.assembliesSeen.Contains(assembly))
                pending.assembliesSeen.Add(assembly);

            if (messages == null) return;

            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].type != CompilerMessageType.Error) continue;
                if (pending.errors.Count >= MaxErrors) break;

                pending.errors.Add(new CompileError
                {
                    assembly = assembly,
                    code = ExtractCode(messages[i].message),
                    message = messages[i].message ?? "",
                    file = messages[i].file ?? "",
                    line = messages[i].line,
                });
            }
        }

        private static void Update()
        {
            if (!File.Exists(PendingFilePath)) return;

            var pending = ReadPending();
            if (pending == null) return;

            bool timedOut = NowMs() - pending.startedAtMs > pending.timeoutMs;
            // Wait for Unity to stop compiling before finalizing — the
            // startup / RequestScriptCompilation compile must finish first so we
            // capture every assembly's messages.
            if (EditorApplication.isCompiling && !timedOut) return;

            Finalize(pending, timedOut);
        }

        private static void Finalize(PendingState pending, bool timedOut)
        {
            EditorApplication.update -= Update;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;

            var resultJson = BuildResultJson(pending, timedOut);

            try { File.Delete(PendingFilePath); }
            catch { /* best-effort cleanup */ }

            int exit = pending.errors.Count > 0
                ? BridgeBatchEntry.ExitFail
                : BridgeBatchEntry.ExitPass;

            BridgeBatchEntry.EmitCompileResult(resultJson, exit);
        }

        /// <summary>
        /// Builds the compile-check result body: status, errorCount, errors[].
        /// Pure (testable). The caller wraps this in the mutation success
        /// envelope — a completed check is a successful tool call regardless of
        /// whether compilation passed, so agents read <c>status</c>.
        /// </summary>
        internal static string BuildResultJson(PendingState pending, bool timedOut)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"status\":").Append(JsonString(pending.errors.Count > 0 ? "compile_failed" : "compile_passed")).Append(',');
            sb.Append("\"errorCount\":").Append(pending.errors.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"assembliesChecked\":").Append((pending.assembliesSeen?.Count ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',');
            if (timedOut)
                sb.Append("\"timedOut\":true,");

            sb.Append("\"errors\":[");
            for (int i = 0; i < pending.errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = pending.errors[i];
                sb.Append('{');
                sb.Append("\"assembly\":").Append(JsonString(e.assembly)).Append(',');
                sb.Append("\"code\":").Append(JsonString(e.code)).Append(',');
                sb.Append("\"message\":").Append(JsonString(e.message)).Append(',');
                sb.Append("\"file\":").Append(JsonString(e.file)).Append(',');
                sb.Append("\"line\":").Append(e.line.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Unity compiler messages look like "error CS0246: The type or namespace
        // name 'Foo' could not be found". Pull the CSxxxx token for structured
        // filtering; the full text stays in `message`.
        internal static string ExtractCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int idx = message.IndexOf("CS", StringComparison.Ordinal);
            // Require at least one digit after "CS" so incidental "CS" substrings
            // (e.g. a type named "FooCS") don't produce a bogus code.
            while (idx >= 0 && (idx + 2 >= message.Length || !char.IsDigit(message[idx + 2])))
            {
                idx = message.IndexOf("CS", idx + 1, StringComparison.Ordinal);
            }
            if (idx < 0) return "";
            int end = idx + 2;
            while (end < message.Length && char.IsDigit(message[end])) end++;
            return message.Substring(idx, end - idx);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + OutputSerializer.EscapeJsonString(s) + "\"";
        }

        #region Pending-file persistence (survives domain reload)

        private static void WritePending(PendingState pending)
        {
            try
            {
                Directory.CreateDirectory(StatusDir);
                File.WriteAllText(PendingFilePath, SerializePending(pending));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CompileCheck] Failed to write pending state: {e.Message}");
            }
        }

        private static PendingState ReadPending()
        {
            try
            {
                if (!File.Exists(PendingFilePath)) return null;
                return ParsePending(File.ReadAllText(PendingFilePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CompileCheck] Failed to read pending state: {e.Message}");
                return null;
            }
        }

        // Hand-rolled JSON for the pending file — the bridge has no JSON serializer
        // dependency (see packages/bridge/AGENTS.md §Transport) and these structs
        // are simple enough that pulling one in is not warranted.
        private static string SerializePending(PendingState pending)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"startedAtMs\":").Append(pending.startedAtMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"timeoutMs\":").Append(pending.timeoutMs.ToString(CultureInfo.InvariantCulture)).Append(',');

            sb.Append("\"assembliesSeen\":[");
            if (pending.assembliesSeen != null)
            {
                for (int i = 0; i < pending.assembliesSeen.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(pending.assembliesSeen[i]));
                }
            }
            sb.Append("],");

            sb.Append("\"errors\":[");
            for (int i = 0; i < pending.errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = pending.errors[i];
                sb.Append('{');
                sb.Append("\"assembly\":").Append(JsonString(e.assembly)).Append(',');
                sb.Append("\"code\":").Append(JsonString(e.code)).Append(',');
                sb.Append("\"message\":").Append(JsonString(e.message)).Append(',');
                sb.Append("\"file\":").Append(JsonString(e.file)).Append(',');
                sb.Append("\"line\":").Append(e.line.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static PendingState ParsePending(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var p = new PendingState();

            p.startedAtMs = JsonBody.GetLong(json, "startedAtMs", 0);
            p.timeoutMs = JsonBody.GetLong(json, "timeoutMs", DefaultTimeoutMs);

            var asm = JsonBody.GetStringArray(json, "assembliesSeen");
            if (asm != null)
            {
                p.assembliesSeen = new List<string>(asm);
            }

            // Errors are written in the same shape CompileError serializes to;
            // rehydrate via the raw object array and pull fields by key.
            var rawErrors = JsonBody.GetObjectArray(json, "errors");
            if (rawErrors != null)
            {
                foreach (var raw in rawErrors)
                {
                    p.errors.Add(new CompileError
                    {
                        assembly = JsonBody.GetString(raw, "assembly") ?? "",
                        code = JsonBody.GetString(raw, "code") ?? "",
                        message = JsonBody.GetString(raw, "message") ?? "",
                        file = JsonBody.GetString(raw, "file") ?? "",
                        line = JsonBody.GetInt(raw, "line", 0),
                    });
                }
            }

            return p;
        }

        #endregion
    }

    internal class PendingState
    {
        // Mirrors CompileCheckState.DefaultTimeoutMs; the field initializer
        // cannot reference the outer const cleanly, so the value is duplicated
        // and asserted once by the CompileCheckState.DefaultTimeoutMs tests.
        public long startedAtMs;
        public long timeoutMs = 300_000;
        public List<string> assembliesSeen;
        public List<CompileError> errors = new List<CompileError>();
    }

    internal struct CompileError
    {
        public string assembly;
        public string code;
        public string message;
        public string file;
        public int line;
    }
}
