using System;
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

        static readonly string[] SupportedOperations = { "find_members" };

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
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

            Console.WriteLine(OutputBegin);
            Console.WriteLine(json);
            Console.WriteLine(OutputEnd);

            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
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
                default:
                    return Fail(
                        $"Unknown meta-tool operation '{operation}'. " +
                        $"Expected one of: {string.Join(", ", SupportedOperations)}."
                    );
            }
        }

        static (int exitCode, string json) RunFindMembers(string[] args)
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

        static FindMembersFlags ParseFindMembersFlags(string[] args)
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

        static bool TryParseBool(string s, out bool result)
        {
            if (s == "true") { result = true; return true; }
            if (s == "false") { result = false; return true; }
            result = false;
            return false;
        }

        #endregion

        #region JSON body construction

        static string BuildFindMembersBody(FindMembersFlags flags)
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

        static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + OutputSerializer.EscapeJsonString(s) + "\"";
        }

        #endregion

        #region Envelope builders

        static string BuildSuccessEnvelope(string outputJson)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":true,\"output\":");
            sb.Append(outputJson);
            sb.Append(",\"error\":null}");
            sb.Append(",\"gate\":{\"mode\":\"off\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[]}");
            return sb.ToString();
        }

        static string BuildFailureEnvelope(string errorCode, string errorMessage)
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

        static string[] ExtractToolArgs(string[] allArgs)
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

        static string[] SliceAfter(string[] source, int index)
        {
            if (index + 1 >= source.Length)
                return Array.Empty<string>();

            var result = new string[source.Length - index - 1];
            Array.Copy(source, index + 1, result, 0, result.Length);
            return result;
        }

        #endregion

        #region Output helpers

        static (int exitCode, string json) Fail(string message)
        {
            return (ExitFail, ErrorEnvelope("batch_error", message));
        }

        static string ErrorEnvelope(string code, string message)
        {
            return BuildFailureEnvelope(code, message);
        }

        #endregion
    }
}
