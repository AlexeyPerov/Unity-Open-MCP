using System;
using System.Collections.Generic;
using System.Globalization;
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
            var includeSignatures = JsonBody.GetBool(body, "include_signatures", true);
            var maxResults = JsonBody.GetInt(body, "max_results", 50);
            if (maxResults < 1) maxResults = 1;
            if (maxResults > 200) maxResults = 200;

            var validKinds = new HashSet<string> { "type", "method", "property", "all" };
            if (!validKinds.Contains(kind))
                kind = "all";

            var results = new List<string>();
            // M13 T4.6 — total match count (pre-cap) so we can report `truncated`
            // accurately. The walk continues past the cap to count the rest of
            // the matches; the returned payload only carries `max_results`.
            int totalMatches = 0;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            bool capReached = false;

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
                                totalMatches++;
                                if (results.Count < maxResults)
                                {
                                    results.Add(SerializeType(type, includeSignatures));
                                }
                                else
                                {
                                    capReached = true;
                                }
                            }
                        }

                        if (kind is "method" or "all")
                        {
                            // M16 Plan 6 — enumerate every overload separately
                            // so an agent can pick the right one for invoke_method.
                            // GetMethods with DeclaredOnly already groups them by
                            // declaring type; we just don't deduplicate by name.
                            foreach (var method in GetMethodsSafe(type))
                            {
                                if (MatchesQuery(method.Name, query))
                                {
                                    totalMatches++;
                                    if (results.Count < maxResults)
                                    {
                                        results.Add(SerializeMethod(type, method, includeSignatures));
                                    }
                                    else
                                    {
                                        capReached = true;
                                    }
                                }
                            }
                        }

                        if (kind is "property" or "all")
                        {
                            foreach (var prop in GetPropertiesSafe(type))
                            {
                                if (MatchesQuery(prop.Name, query))
                                {
                                    totalMatches++;
                                    if (results.Count < maxResults)
                                    {
                                        results.Add(SerializeProperty(type, prop, includeSignatures));
                                    }
                                    else
                                    {
                                        capReached = true;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // M13 T4.6 — always report truncation. `truncated: 0` means the cap
            // was not hit; agents can trust the absence of further matches.
            int truncated = capReached ? totalMatches - results.Count : 0;
            var json = "{\"members\":[" + string.Join(",", results) + "]"
                + ",\"count\":" + results.Count
                + ",\"truncated\":" + truncated
                + "}";
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
                sb.Append(TypeDisplayName(method.ReturnType));
                sb.Append(' ');
                sb.Append(method.Name);
                if (method.IsGenericMethod)
                {
                    sb.Append('<');
                    sb.Append(string.Join(", ", method.GetGenericArguments().Select(t => t.Name)));
                    sb.Append('>');
                }
                sb.Append('(');
                var parms = method.GetParameters();
                for (int i = 0; i < parms.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(TypeDisplayName(parms[i].ParameterType));
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
                sb.Append(TypeDisplayName(prop.PropertyType));
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

        // M16 Plan 6 — TypeDisplayName mirrors the find_members signature
        // style (int, string, Vector3, List<T>) so method and type reads line
        // up across tools.
        static string TypeDisplayName(Type type)
        {
            if (type == null) return "null";
            var name = type.Name;
            if (type.IsGenericType)
            {
                var tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                var args = type.GetGenericArguments();
                name += "<" + string.Join(", ", args.Select(TypeDisplayName)) + ">";
            }
            return name;
        }

        // ---------------- M16 Plan 6 structured serializers ----------------

        static string SerializeType(Type type, bool includeSignatures)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"kind\":\"type");
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(type.Name));
            sb.Append("\",\"fullName\":\"").Append(OutputSerializer.EscapeJsonString(type.FullName ?? type.Name));
            sb.Append("\",\"namespace\":\"").Append(OutputSerializer.EscapeJsonString(type.Namespace ?? ""));
            sb.Append("\",\"assembly\":\"").Append(OutputSerializer.EscapeJsonString(type.Assembly.GetName().Name ?? ""));
            sb.Append("\",\"isEnum\":").Append(type.IsEnum ? "true" : "false");
            sb.Append(",\"isClass\":").Append(type.IsClass ? "true" : "false");
            sb.Append(",\"summary\":\"").Append(OutputSerializer.EscapeJsonString(GetSummary(type)));
            if (includeSignatures)
            {
                sb.Append("\",\"signature\":\"").Append(OutputSerializer.EscapeJsonString(type.FullName ?? type.Name));
            }
            sb.Append("\"}");
            return sb.ToString();
        }

        static string SerializeMethod(Type declaringType, MethodInfo method, bool includeSignatures)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"kind\":\"method");
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(method.Name));
            sb.Append("\",\"declaringType\":\"").Append(OutputSerializer.EscapeJsonString(declaringType.FullName ?? declaringType.Name));
            sb.Append("\",\"isStatic\":").Append(method.IsStatic ? "true" : "false");
            sb.Append(",\"isGeneric\":").Append(method.IsGenericMethod ? "true" : "false");
            sb.Append(",\"returnType\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(method.ReturnType)));

            var genericArgs = method.GetGenericArguments();
            sb.Append("\",\"genericParameters\":[");
            for (int i = 0; i < genericArgs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(genericArgs[i].Name)).Append('"');
                sb.Append(",\"constraints\":[");
                var constraints = genericArgs[i].GetGenericParameterConstraints();
                for (int c = 0; c < constraints.Length; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(TypeDisplayName(constraints[c]))).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append(']');

            var parms = method.GetParameters();
            sb.Append(",\"parameters\":[");
            for (int i = 0; i < parms.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(parms[i].Name ?? ""));
                sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(parms[i].ParameterType)));
                sb.Append("\",\"hasDefault\":").Append(parms[i].HasDefaultValue ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');
            if (includeSignatures)
            {
                sb.Append(",\"signature\":\"").Append(OutputSerializer.EscapeJsonString(GetMethodSignature(method)));
            }
            sb.Append("\"}");
            return sb.ToString();
        }

        static string SerializeProperty(Type declaringType, PropertyInfo prop, bool includeSignatures)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"kind\":\"property");
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(prop.Name));
            sb.Append("\",\"declaringType\":\"").Append(OutputSerializer.EscapeJsonString(declaringType.FullName ?? declaringType.Name));
            sb.Append("\",\"propertyType\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(prop.PropertyType)));
            sb.Append("\",\"canRead\":").Append(prop.CanRead ? "true" : "false");
            sb.Append(",\"canWrite\":").Append(prop.CanWrite ? "true" : "false");
            var getter = prop.GetMethod;
            sb.Append(",\"isStatic\":").Append(getter != null && getter.IsStatic ? "true" : "false");
            if (includeSignatures)
            {
                sb.Append(",\"signature\":\"").Append(OutputSerializer.EscapeJsonString(GetPropertySignature(prop)));
            }
            sb.Append("\"}");
            return sb.ToString();
        }
    }
}
