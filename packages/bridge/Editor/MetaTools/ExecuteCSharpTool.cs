using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.MetaTools
{
    public static class ExecuteCSharpTool
    {
        static readonly string[] DefaultUsings =
        {
            "System",
            "System.IO",
            "System.Linq",
            "System.Collections",
            "System.Collections.Generic",
            "UnityEngine",
            "UnityEditor"
        };

        public static ToolDispatchResult Execute(string body)
        {
            var code = JsonBody.GetString(body, "code");
            if (string.IsNullOrEmpty(code))
                return ToolDispatchResult.Fail("validation_error",
                    "Field 'code' is required and must be non-empty");

            var extraUsings = JsonBody.GetStringArray(body, "usings");
            var allUsings = DefaultUsings
                .Concat(extraUsings ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            if (!RoslynHost.Initialize())
                return ToolDispatchResult.Fail("roslyn_unavailable",
                    "Could not load Roslyn compiler assemblies from Unity installation. " +
                    (RoslynHost.LastInitError ??
                     "Expected Mono-compatible Roslyn under {Unity.app}/Contents/Resources/Scripting/MonoBleedingEdge/.../Roslyn/"));

            var source = BuildSource(code, allUsings);

            var (pe, errors) = RoslynHost.Compile(source);
            if (pe == null)
                return ToolDispatchResult.Fail("compilation_error", errors ?? "Unknown compilation error");

            try
            {
                var assembly = Assembly.Load(pe);
                var type = assembly.GetType("UnityAgentSnippet.Snippet");
                if (type == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet type not found");

                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet entry point not found");

                var result = method.Invoke(null, null);
                var output = OutputSerializer.Serialize(result);
                return ToolDispatchResult.Ok(output);
            }
            catch (TargetInvocationException tie)
            {
                return ToolDispatchResult.Fail("execution_error", tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        static string BuildSource(string code, string[] usings)
        {
            var sb = new StringBuilder(code.Length + usings.Length * 30 + 120);
            foreach (var u in usings)
                sb.AppendLine($"using {u};");
            sb.AppendLine();
            sb.AppendLine("namespace UnityAgentSnippet {");
            sb.AppendLine("  public static class Snippet {");
            sb.AppendLine("    public static object Run() {");
            sb.AppendLine($"      {code}");
            sb.AppendLine("      return null;");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
