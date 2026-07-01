using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Batch
{
    public static class BridgeBatchEntry
    {
        public const string OutputBegin = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
        public const string OutputEnd = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

        public const int ExitPass = 0;
        public const int ExitFail = 1;

        private static readonly string[] SupportedOperations =
        {
            "find_members",
            "compile_check",
            "execute_csharp",
            "invoke_method",
            "execute_menu",
        };

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();

            // compile_check is asynchronous: it must wait for project compilation
            // to settle across a domain reload, so it cannot follow the sync
            // Execute() -> print markers -> Exit() path. Hand off to the
            // CompileCheckState machine, which finalizes (and exits) from the
            // EditorApplication.update loop once compilation finishes. Early-out
            // here so the synchronous exit below does not run for it.
            var (isCompileCheck, timeoutMs) = DetectCompileCheck(args);
            if (isCompileCheck)
            {
                CompileCheckState.Start(timeoutMs);
                return;
            }

            int exitCode;
            string json;

            try
            {
                var result = Execute(args);
                exitCode = result.exitCode;
                json = result.json;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BridgeBatchEntry] Unhandled exception: {e}");
                exitCode = ExitFail;
                json = ErrorEnvelope("unhandled_exception", e.Message);
            }

            // System.Console (NOT UnityEngine.Debug) is intentional: this is a
            // -batchmode entry point whose stdout is parsed by the MCP server
            // (batch-spawn.ts extractJson reads the spawned process stdout) for
            // the JSON markers below. Debug.Log writes to Editor.log, not stdout,
            // so switching to it would break every batch invocation. The bare
            // 'Console' name is qualified with 'System.' because it would
            // otherwise resolve to the sibling UnityOpenMcpBridge.Console
            // namespace (Console/ReadConsoleTool.cs), causing CS0234.
            System.Console.WriteLine(OutputBegin);
            System.Console.WriteLine(json);
            System.Console.WriteLine(OutputEnd);

            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
        }

        // Inspects the post-`--` tool args for the compile_check operation and an
        // optional --timeout-ms flag. Returns timeoutMs=0 when the flag is absent;
        // CompileCheckState.Start applies its own default and clamps. Kept here
        // (not in CompileCheckState) so the decision to branch is local to the
        // entry point, matching how Execute() dispatches the synchronous ops.
        private static (bool isCompileCheck, long timeoutMs) DetectCompileCheck(string[] allArgs)
        {
            var toolArgs = ExtractToolArgs(allArgs);
            if (toolArgs.Length == 0 || toolArgs[0] != "compile_check")
                return (false, 0);

            long timeoutMs = 0;
            for (int i = 1; i < toolArgs.Length; i++)
            {
                if (toolArgs[i] == "--timeout-ms" && i + 1 < toolArgs.Length)
                {
                    long.TryParse(toolArgs[++i], out timeoutMs);
                }
            }
            return (true, timeoutMs);
        }

        internal static (int exitCode, string json) Execute(string[] allArgs)
        {
            var toolArgs = ExtractToolArgs(allArgs);

            if (toolArgs.Length == 0)
            {
                return Fail(
                    "No meta-tool arguments found after '--'. " +
                    "Usage: Unity -batchmode -executeMethod " +
                    "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run -- " +
                    "<operation> [--query <s>] [--kind <k>] ..."
                );
            }

            var operation = toolArgs[0];
            var flagArgs = SliceAfter(toolArgs, 0);

            switch (operation)
            {
                case "find_members":
                    return RunFindMembers(flagArgs);
                case "execute_csharp":
                    return RunExecuteCSharp(flagArgs);
                case "invoke_method":
                    return RunInvokeMethod(flagArgs);
                case "execute_menu":
                    return RunExecuteMenu(flagArgs);
                case "compile_check":
                    // Intercepted by Run() before reaching here (async handoff);
                    // arriving synchronously means the async branch misrouted.
                    return Fail(
                        "compile_check must be dispatched via the async " +
                        "CompileCheckState path. This is an internal error."
                    );
                default:
                    return Fail(
                        $"Unknown meta-tool operation '{operation}'. " +
                        $"Expected one of: {string.Join(", ", SupportedOperations)}."
                    );
            }
        }

        private static (int exitCode, string json) RunFindMembers(string[] args)
        {
            var parsed = ParseFindMembersFlags(args);
            if (parsed.error != null)
                return Fail(parsed.error);

            var body = BuildFindMembersBody(parsed);
            var result = FindMembersTool.Execute(body);

            if (result.Success)
            {
                return (ExitPass, BuildSuccessEnvelope(result.Output ?? "null"));
            }

            return (ExitFail, BuildFailureEnvelope(result.ErrorCode, result.ErrorMessage));
        }

        // M26 Plan 3 — full batch parity for the mutating meta-tools. All three
        // reuse the live tool implementations (ExecuteCSharpTool / InvokeMethodTool
        // / ExecuteMenuTool) by reconstructing the JSON body the live dispatcher
        // passes, so behavior is identical to the live path. The headless gate is
        // intentionally skipped (gate.mode:"off", gate.skipped:true in the
        // envelope) because the gate's checkpoint/validate/delta flow runs against
        // the live AssetDatabase in an interactive Editor and is unavailable
        // headless — this is the documented headless gate path (see
        // docs/api/mcp-tools.md §Batch support). Operators who want a guarded
        // mutation connect a live Editor; batch is the unguarded CI/script path.

        private static (int exitCode, string json) RunExecuteCSharp(string[] args)
        {
            var parsed = ParseExecuteCSharpFlags(args);
            if (parsed.error != null)
                return Fail(parsed.error);

            var body = BuildExecuteCSharpBody(parsed);
            var result = ExecuteCSharpTool.Execute(body);

            if (result.Success)
            {
                return (ExitPass, BuildSuccessEnvelope(result.Output ?? "null"));
            }

            return (ExitFail, BuildFailureEnvelope(result.ErrorCode, result.ErrorMessage));
        }

        private static (int exitCode, string json) RunInvokeMethod(string[] args)
        {
            var parsed = ParseInvokeMethodFlags(args);
            if (parsed.error != null)
                return Fail(parsed.error);

            var body = BuildInvokeMethodBody(parsed);
            var result = InvokeMethodTool.Execute(body);

            if (result.Success)
            {
                return (ExitPass, BuildSuccessEnvelope(result.Output ?? "null"));
            }

            return (ExitFail, BuildFailureEnvelope(result.ErrorCode, result.ErrorMessage));
        }

        private static (int exitCode, string json) RunExecuteMenu(string[] args)
        {
            var parsed = ParseExecuteMenuFlags(args);
            if (parsed.error != null)
                return Fail(parsed.error);

            if (!ExecuteMenuTool.IsBatchViable(parsed.menuPath))
            {
                return (ExitFail, BuildFailureEnvelope(
                    "menu_not_viable_in_batchmode",
                    $"Menu '{parsed.menuPath}' is not on the batch-viable allow-list. " +
                    "Most Editor menus open a window or dialog and fail under -batchmode " +
                    "(no UI). The allow-list covers pure AssetDatabase/project menus " +
                    "(e.g. Assets/Refresh, File/Save Project). For the full menu set, " +
                    "connect a live Editor."));
            }

            var body = BuildExecuteMenuBody(parsed);
            var result = ExecuteMenuTool.Execute(body);

            if (result.Success)
            {
                return (ExitPass, BuildSuccessEnvelope(result.Output ?? "null"));
            }

            return (ExitFail, BuildFailureEnvelope(result.ErrorCode, result.ErrorMessage));
        }

        #region Flag parsing

        class FindMembersFlags
        {
            public string query = "";
            public string kind = "all";
            public string assemblyFilter = null;
            public bool includeUnityEditor = true;
            public bool includeProject = true;
            public int maxResults = 50;
            public string error;
        }

        private static FindMembersFlags ParseFindMembersFlags(string[] args)
        {
            var p = new FindMembersFlags();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--query":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--query requires a value.";
                            return p;
                        }
                        p.query = args[++i];
                        break;

                    case "--kind":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--kind requires a value (type, method, property, or all).";
                            return p;
                        }
                        p.kind = args[++i];
                        var validKinds = new[] { "type", "method", "property", "all" };
                        if (Array.IndexOf(validKinds, p.kind) < 0)
                        {
                            p.error = $"Invalid kind '{p.kind}'. Expected one of: {string.Join(", ", validKinds)}.";
                            return p;
                        }
                        break;

                    case "--assembly-filter":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--assembly-filter requires a value.";
                            return p;
                        }
                        p.assemblyFilter = args[++i];
                        break;

                    case "--include-unity-editor":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--include-unity-editor requires a boolean (true or false).";
                            return p;
                        }
                        if (!TryParseBool(args[++i], out p.includeUnityEditor))
                        {
                            p.error = $"Invalid boolean '{args[i]}' for --include-unity-editor.";
                            return p;
                        }
                        break;

                    case "--include-project":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--include-project requires a boolean (true or false).";
                            return p;
                        }
                        if (!TryParseBool(args[++i], out p.includeProject))
                        {
                            p.error = $"Invalid boolean '{args[i]}' for --include-project.";
                            return p;
                        }
                        break;

                    case "--max-results":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--max-results requires an integer value.";
                            return p;
                        }
                        if (!int.TryParse(args[++i], out p.maxResults))
                        {
                            p.error = $"Invalid max-results '{args[i]}'. Expected an integer.";
                            return p;
                        }
                        break;

                    default:
                        if (args[i].StartsWith("--"))
                        {
                            p.error = $"Unknown argument '{args[i]}' for operation 'find_members'.";
                            return p;
                        }
                        break;
                }
            }

            return p;
        }

        private static bool TryParseBool(string s, out bool result)
        {
            if (s == "true") { result = true; return true; }
            if (s == "false") { result = false; return true; }
            result = false;
            return false;
        }

        // --- execute_csharp flags -------------------------------------------
        // The code payload is the only required field. Extra usings, object refs,
        // serialize-depth caps, and the deny-bypass contract (confirm_bypass +
        // gate:"off") are forwarded verbatim so a batch execute_csharp matches the
        // live dispatcher's behavior.
        class ExecuteCSharpFlags
        {
            public string code = "";
            public List<string> usings = new();
            public List<string> objectIds = new();
            public int maxDepth = 4;
            public int maxItems = 100;
            public bool confirmBypass = false;
            public string gate = null; // "off" when explicitly set
            public string error;
        }

        private static ExecuteCSharpFlags ParseExecuteCSharpFlags(string[] args)
        {
            var p = new ExecuteCSharpFlags();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--code":
                        p.code = ReadMultilineValue(args, ref i, "--code");
                        break;

                    case "--using":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--using requires a value.";
                            return p;
                        }
                        p.usings.Add(args[++i]);
                        break;

                    case "--object-id":
                        if (i + 1 >= args.Length)
                        {
                            p.error = "--object-id requires a value.";
                            return p;
                        }
                        p.objectIds.Add(args[++i]);
                        break;

                    case "--max-depth":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out p.maxDepth))
                        {
                            p.error = "--max-depth requires an integer value.";
                            return p;
                        }
                        break;

                    case "--max-items":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out p.maxItems))
                        {
                            p.error = "--max-items requires an integer value.";
                            return p;
                        }
                        break;

                    case "--confirm-bypass":
                        if (i + 1 >= args.Length || !TryParseBool(args[++i], out p.confirmBypass))
                        {
                            p.error = "--confirm-bypass requires a boolean (true or false).";
                            return p;
                        }
                        // Bypass also requires an explicit gate:"off".
                        p.gate = "off";
                        break;

                    default:
                        if (args[i].StartsWith("--"))
                        {
                            p.error = $"Unknown argument '{args[i]}' for operation 'execute_csharp'.";
                            return p;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(p.code))
                p.error = "--code is required for execute_csharp.";

            return p;
        }

        // --- invoke_method flags --------------------------------------------
        // type_name + method_name are required; the rest (static/instance, args,
        // overload/generic disambiguation, serialize caps) mirror the live schema.
        class InvokeMethodFlags
        {
            public string typeName = "";
            public string methodName = "";
            public bool isStatic = false;
            public string assemblyName = null;
            public string objectId = null;
            public List<string> args = new();
            public List<string> argTypeNames = new();
            public List<string> genericArgTypes = new();
            public int maxDepth = 4;
            public int maxItems = 100;
            public string error;
        }

        private static InvokeMethodFlags ParseInvokeMethodFlags(string[] args)
        {
            var p = new InvokeMethodFlags();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--type-name":
                        if (i + 1 >= args.Length) { p.error = "--type-name requires a value."; return p; }
                        p.typeName = args[++i];
                        break;

                    case "--method-name":
                        if (i + 1 >= args.Length) { p.error = "--method-name requires a value."; return p; }
                        p.methodName = args[++i];
                        break;

                    case "--is-static":
                        if (i + 1 >= args.Length || !TryParseBool(args[++i], out p.isStatic))
                        {
                            p.error = "--is-static requires a boolean (true or false).";
                            return p;
                        }
                        break;

                    case "--assembly-name":
                        if (i + 1 >= args.Length) { p.error = "--assembly-name requires a value."; return p; }
                        p.assemblyName = args[++i];
                        break;

                    case "--object-id":
                        if (i + 1 >= args.Length) { p.error = "--object-id requires a value."; return p; }
                        p.objectId = args[++i];
                        break;

                    case "--arg":
                        if (i + 1 >= args.Length) { p.error = "--arg requires a value."; return p; }
                        p.args.Add(args[++i]);
                        break;

                    case "--arg-type-name":
                        if (i + 1 >= args.Length) { p.error = "--arg-type-name requires a value."; return p; }
                        p.argTypeNames.Add(args[++i]);
                        break;

                    case "--generic-arg-type":
                        if (i + 1 >= args.Length) { p.error = "--generic-arg-type requires a value."; return p; }
                        p.genericArgTypes.Add(args[++i]);
                        break;

                    case "--max-depth":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out p.maxDepth))
                        {
                            p.error = "--max-depth requires an integer value.";
                            return p;
                        }
                        break;

                    case "--max-items":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out p.maxItems))
                        {
                            p.error = "--max-items requires an integer value.";
                            return p;
                        }
                        break;

                    default:
                        if (args[i].StartsWith("--"))
                        {
                            p.error = $"Unknown argument '{args[i]}' for operation 'invoke_method'.";
                            return p;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(p.typeName))
                p.error = "--type-name is required for invoke_method.";
            else if (string.IsNullOrEmpty(p.methodName))
                p.error = "--method-name is required for invoke_method.";

            return p;
        }

        // --- execute_menu flags ---------------------------------------------
        class ExecuteMenuFlags
        {
            public string menuPath = "";
            public string error;
        }

        private static ExecuteMenuFlags ParseExecuteMenuFlags(string[] args)
        {
            var p = new ExecuteMenuFlags();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--menu-path":
                        if (i + 1 >= args.Length) { p.error = "--menu-path requires a value."; return p; }
                        p.menuPath = args[++i];
                        break;

                    default:
                        if (args[i].StartsWith("--"))
                        {
                            p.error = $"Unknown argument '{args[i]}' for operation 'execute_menu'.";
                            return p;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(p.menuPath))
                p.error = "--menu-path is required for execute_menu.";

            return p;
        }

        // Reads a flag value that may itself contain spaces / semicolons (C# code).
        // The MCP server encodes the value with ASCII unit separators (\x1f)
        // between space-split argv tokens so the original spaces round-trip
        // exactly. Unknown future flags after --code are left for the main loop
        // because --code is always the last value flag in practice.
        private static string ReadMultilineValue(string[] args, ref int i, string flagName)
        {
            if (i + 1 >= args.Length)
                return null;
            var raw = args[++i];
            // Rejoin tokens that the MCP server split on spaces and rejoined with
            // ASCII unit separator (0x1f). Any literal \x1f becomes a space.
            return raw.Replace("\x1f", " ");
        }

        #endregion

        #region JSON body construction

        private static string BuildFindMembersBody(FindMembersFlags flags)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"query\":").Append(JsonString(flags.query));
            sb.Append(",\"kind\":").Append(JsonString(flags.kind));

            if (!string.IsNullOrEmpty(flags.assemblyFilter))
                sb.Append(",\"assembly_filter\":").Append(JsonString(flags.assemblyFilter));

            sb.Append(",\"include_unity_editor\":").Append(flags.includeUnityEditor ? "true" : "false");
            sb.Append(",\"include_project\":").Append(flags.includeProject ? "true" : "false");
            sb.Append(",\"max_results\":").Append(flags.maxResults);
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildExecuteCSharpBody(ExecuteCSharpFlags flags)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"code\":").Append(JsonString(flags.code));

            if (flags.usings.Count > 0)
            {
                sb.Append(",\"usings\":").Append(JsonStringArray(flags.usings));
            }

            if (flags.objectIds.Count > 0)
            {
                sb.Append(",\"object_ids\":").Append(JsonStringArray(flags.objectIds));
            }

            if (flags.gate != null)
            {
                sb.Append(",\"gate\":").Append(JsonString(flags.gate));
                sb.Append(",\"confirm_bypass\":").Append(flags.confirmBypass ? "true" : "false");
            }

            sb.Append(",\"max_depth\":").Append(flags.maxDepth);
            sb.Append(",\"max_items\":").Append(flags.maxItems);
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildInvokeMethodBody(InvokeMethodFlags flags)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"type_name\":").Append(JsonString(flags.typeName));
            sb.Append(",\"method_name\":").Append(JsonString(flags.methodName));
            sb.Append(",\"is_static\":").Append(flags.isStatic ? "true" : "false");

            if (!string.IsNullOrEmpty(flags.assemblyName))
                sb.Append(",\"assembly_name\":").Append(JsonString(flags.assemblyName));

            if (!string.IsNullOrEmpty(flags.objectId))
                sb.Append(",\"object_id\":").Append(int.TryParse(flags.objectId, out var id) ? id.ToString() : flags.objectId);

            if (flags.args.Count > 0)
                sb.Append(",\"args\":").Append(JsonRawArray(flags.args));

            if (flags.argTypeNames.Count > 0)
                sb.Append(",\"arg_type_names\":").Append(JsonStringArray(flags.argTypeNames));

            if (flags.genericArgTypes.Count > 0)
                sb.Append(",\"generic_arg_types\":").Append(JsonStringArray(flags.genericArgTypes));

            sb.Append(",\"max_depth\":").Append(flags.maxDepth);
            sb.Append(",\"max_items\":").Append(flags.maxItems);
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildExecuteMenuBody(ExecuteMenuFlags flags)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"menu_path\":").Append(JsonString(flags.menuPath));
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + OutputSerializer.EscapeJsonString(s) + "\"";
        }

        // Emits a JSON array of quoted strings: ["a","b"].
        private static string JsonStringArray(List<string> values)
        {
            var sb = new StringBuilder(values.Count * 16 + 4);
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(values[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Emits a JSON array of raw values for invoke_method args. Each value is
        // parsed as JSON if it looks like JSON (object/array/bool/null/number),
        // otherwise quoted as a string — matching how the live dispatcher treats
        // `args` as a list of typed JSON values.
        private static string JsonRawArray(List<string> values)
        {
            var sb = new StringBuilder(values.Count * 16 + 4);
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var v = values[i];
                if (LooksLikeJsonScalar(v))
                    sb.Append(v);
                else
                    sb.Append(JsonString(v));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static bool LooksLikeJsonScalar(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            var c = v[0];
            // true / false / null / number literals pass through raw; objects and
            // arrays (hand-rolled handle JSON, or nested structures) pass through
            // raw too. Everything else is treated as a JSON string.
            return c == '{' || c == '[' || c == 't' || c == 'f' || c == 'n'
                || char.IsDigit(c) || c == '-' || c == '+' || c == '.';
        }

        #endregion

        #region Envelope builders

        private static string BuildSuccessEnvelope(string outputJson)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":true,\"output\":");
            sb.Append(outputJson);
            sb.Append(",\"error\":null}");
            sb.Append(",\"gate\":{\"mode\":\"off\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[]}");
            return sb.ToString();
        }

        private static string BuildFailureEnvelope(string errorCode, string errorMessage)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"");
            sb.Append(OutputSerializer.EscapeJsonString(errorCode ?? "execution_error"));
            sb.Append("\",\"message\":\"");
            sb.Append(OutputSerializer.EscapeJsonString(errorMessage ?? ""));
            sb.Append("\"}}");
            sb.Append(",\"gate\":{\"mode\":\"off\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[]}");
            return sb.ToString();
        }

        #endregion

        #region CLI extraction helpers

        private static string[] ExtractToolArgs(string[] allArgs)
        {
            for (int i = 0; i < allArgs.Length; i++)
            {
                if (allArgs[i] == "--")
                    return SliceAfter(allArgs, i);
            }

            for (int i = 0; i < allArgs.Length - 1; i++)
            {
                if (allArgs[i] == "-executeMethod")
                    return SliceAfter(allArgs, i + 1);
            }

            return Array.Empty<string>();
        }

        private static string[] SliceAfter(string[] source, int index)
        {
            if (index + 1 >= source.Length)
                return Array.Empty<string>();

            var result = new string[source.Length - index - 1];
            Array.Copy(source, index + 1, result, 0, result.Length);
            return result;
        }

        #endregion

        #region Output helpers

        private static (int exitCode, string json) Fail(string message)
        {
            return (ExitFail, ErrorEnvelope("batch_error", message));
        }

        private static string ErrorEnvelope(string code, string message)
        {
            return BuildFailureEnvelope(code, message);
        }

        /// <summary>
        /// Terminal output/exit path for the async compile_check operation.
        /// Called from <see cref="CompileCheckState"/> once compilation settles.
        /// The compile result body is wrapped in the same success envelope the
        /// synchronous path uses — a completed check is a successful tool call
        /// regardless of whether compilation passed; agents read <c>status</c>.
        /// </summary>
        public static void EmitCompileResult(string compileResultJson, int exitCode)
        {
            var wrapped = BuildSuccessEnvelope(compileResultJson);

            System.Console.WriteLine(OutputBegin);
            System.Console.WriteLine(wrapped);
            System.Console.WriteLine(OutputEnd);

            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
        }

        #endregion
    }
}
