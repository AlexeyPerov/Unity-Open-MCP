using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class FindMembersTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var query = JsonBody.GetString(body, "query") ?? "";
            var kind = JsonBody.GetString(body, "kind") ?? "all";
            var assemblyFilter = JsonBody.GetString(body, "assembly_filter");
            var includeUnityEditor = JsonBody.GetBool(body, "include_unity_editor", true);
            var includeProject = JsonBody.GetBool(body, "include_project", true);
            var maxResults = JsonBody.GetInt(body, "max_results", 50);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 200) maxResults = 200;

            var validKinds = new HashSet<string> { "type", "method", "property", "all" };
            if (!validKinds.Contains(kind))
                kind = "all";

            var results = new List<string>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var asm in assemblies)
            {
                if (!ShouldIncludeAssembly(asm, assemblyFilter, includeUnityEditor, includeProject))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    try
                    {
                        if (kind is "type" or "all")
                        {
                            if (MatchesQuery(type.Name, query) || MatchesQuery(type.FullName, query))
                            {
                                results.Add(SerializeMember("type", type.Name, type.FullName, "", GetSummary(type)));
                                if (results.Count >= maxResults) goto Done;
                            }
                        }

                        if (kind is "method" or "all")
                        {
                            foreach (var method in GetMethodsSafe(type))
                            {
                                if (MatchesQuery(method.Name, query))
                                {
                                    results.Add(SerializeMember("method", method.Name, type.FullName,
                                        GetMethodSignature(method), ""));
                                    if (results.Count >= maxResults) goto Done;
                                }
                            }
                        }

                        if (kind is "property" or "all")
                        {
                            foreach (var prop in GetPropertiesSafe(type))
                            {
                                if (MatchesQuery(prop.Name, query))
                                {
                                    results.Add(SerializeMember("property", prop.Name, type.FullName,
                                        GetPropertySignature(prop), ""));
                                    if (results.Count >= maxResults) goto Done;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

        Done:
            var json = "{\"members\":[" + string.Join(",", results) + "]}";
            return ToolDispatchResult.Ok(json);
        }

        static bool ShouldIncludeAssembly(Assembly asm, string assemblyFilter, bool includeUnityEditor, bool includeProject)
        {
            var name = asm.GetName().Name;

            if (!string.IsNullOrEmpty(assemblyFilter))
                return name.IndexOf(assemblyFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            if (!includeUnityEditor)
            {
                if (name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) && name.Contains("Editor"))
                    return false;
            }

            if (!includeProject)
            {
                if (!name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("System", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
                    return true;
                return true;
            }

            return true;
        }

        static bool MatchesQuery(string name, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<MethodInfo> GetMethodsSafe(Type type)
        {
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch { return Array.Empty<MethodInfo>(); }
        }

        static IEnumerable<PropertyInfo> GetPropertiesSafe(Type type)
        {
            try
            {
                return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch { return Array.Empty<PropertyInfo>(); }
        }

        static string GetMethodSignature(MethodInfo method)
        {
            try
            {
                var sb = new StringBuilder(64);
                sb.Append(method.ReturnType.Name);
                sb.Append(' ');
                sb.Append(method.Name);
                sb.Append('(');
                var parms = method.GetParameters();
                for (int i = 0; i < parms.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parms[i].ParameterType.Name);
                    sb.Append(' ');
                    sb.Append(parms[i].Name);
                }
                sb.Append(')');
                return sb.ToString();
            }
            catch { return ""; }
        }

        static string GetPropertySignature(PropertyInfo prop)
        {
            try
            {
                var sb = new StringBuilder(32);
                sb.Append(prop.PropertyType.Name);
                sb.Append(' ');
                sb.Append(prop.Name);
                sb.Append(" { ");
                if (prop.CanRead) sb.Append("get; ");
                if (prop.CanWrite) sb.Append("set; ");
                sb.Append('}');
                return sb.ToString();
            }
            catch { return ""; }
        }

        static string GetSummary(Type type)
        {
            return type.IsClass ? "class" : type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "";
        }

        static string SerializeMember(string kind, string name, string declaringType, string signature, string summary)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"kind\":\"").Append(OutputSerializer.EscapeJsonString(kind));
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(name));
            sb.Append("\",\"declaring_type\":\"").Append(OutputSerializer.EscapeJsonString(declaringType));
            sb.Append("\",\"signature\":\"").Append(OutputSerializer.EscapeJsonString(signature));
            sb.Append("\",\"summary\":\"").Append(OutputSerializer.EscapeJsonString(summary));
            sb.Append("\"}");
            return sb.ToString();
        }
    }
}
