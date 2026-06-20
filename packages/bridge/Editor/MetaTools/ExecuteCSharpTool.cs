using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class ExecuteCSharpTool
    {
        private static readonly string[] DefaultUsings =
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

            // M14 T5.2 — deny heuristic runs before compile. The bypass contract
            // (gate: "off" + confirm_bypass: true) is evaluated from the request
            // body so the heuristic fires even before the dispatcher has resolved
            // the effective gate mode. The dispatcher also records the bypass in
            // the audit log via the gate envelope.
            var bypass = BridgeDenyBypass.IsRequestedFromBody(body);
            var deny = BridgeDenyList.EvaluateCSharp(
                code, BridgeProjectSettings.CSharpDenyPatterns, bypass);
            if (!deny.Allowed)
            {
                return ToolDispatchResult.Fail("denied_by_policy",
                    $"{deny.Reason} Suggestion: {deny.Suggestion} " +
                    $"Matched pattern: {deny.MatchedPattern}.");
            }

            var extraUsings = JsonBody.GetStringArray(body, "usings");
            var allUsings = DefaultUsings
                .Concat(extraUsings ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            // Resolve object_ids to live objects before compiling so the snippet
            // can access them via Refs[index] or Ref<T>(index).
            var objectIdStrings = JsonBody.GetStringArray(body, "object_ids");
            UnityEngine.Object[] resolvedRefs = null;
            if (objectIdStrings != null && objectIdStrings.Length > 0)
            {
                resolvedRefs = new UnityEngine.Object[objectIdStrings.Length];
                for (var i = 0; i < objectIdStrings.Length; i++)
                {
                    var idStr = objectIdStrings[i];
                    if (string.IsNullOrEmpty(idStr)) continue;

                    // Accept bare integers or full handle JSON.
                    var resolved = int.TryParse(idStr.Trim(), out var bareId)
                        ? ObjectHandle.Resolve(bareId, null, null, null, null, null, out _)
                        : ObjectHandle.ResolveJson(idStr, out _);
                    resolvedRefs[i] = resolved;
                }
            }

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
                var type = assembly.GetType("UnityOpenMcpSnippet.Snippet");
                if (type == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet type not found");

                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet entry point not found");

                // Inject resolved object references so the snippet can access live objects.
                if (resolvedRefs != null)
                {
                    var refsField = type.GetField("Refs", BindingFlags.Public | BindingFlags.Static);
                    if (refsField != null)
                        refsField.SetValue(null, resolvedRefs);
                }

                var result = method.Invoke(null, null);
                var output = OutputSerializer.Serialize(result, BuildSerializeOptions(body));
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

        private static SerializeOptions BuildSerializeOptions(string body)
        {
            var maxDepth = JsonBody.GetInt(body, "max_depth", 4);
            var maxItems = JsonBody.GetInt(body, "max_items", 100);
            return new SerializeOptions
            {
                MaxDepth = maxDepth <= 0 ? 4 : maxDepth,
                MaxListItems = maxItems <= 0 ? 100 : maxItems,
            };
        }

        private static string BuildSource(string code, string[] usings)
        {
            var sb = new StringBuilder(code.Length + usings.Length * 30 + 280);
            foreach (var u in usings)
                sb.AppendLine($"using {u};");
            sb.AppendLine();
            sb.AppendLine("namespace UnityOpenMcpSnippet {");
            sb.AppendLine("  public static class Snippet {");
            // Live object references injected from the object_ids parameter.
            // Access via Refs[i] or Ref<T>(i) in the snippet body.
            sb.AppendLine("    public static UnityEngine.Object[] Refs;");
            sb.AppendLine("    public static T Ref<T>(int index) where T : UnityEngine.Object {");
            sb.AppendLine("      if (Refs == null || index < 0 || index >= Refs.Length) return null;");
            sb.AppendLine("      return Refs[index] as T;");
            sb.AppendLine("    }");
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
