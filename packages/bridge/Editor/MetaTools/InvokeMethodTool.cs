using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityOpenMcpBridge.ObjectRefs;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class InvokeMethodTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var typeName = JsonBody.GetString(body, "type_name");
            var methodName = JsonBody.GetString(body, "method_name");
            var isStatic = JsonBody.GetBool(body, "is_static", false);
            var assemblyName = JsonBody.GetString(body, "assembly_name");
            var objectId = JsonBody.GetInt(body, "object_id", 0);

            if (string.IsNullOrEmpty(typeName))
                return ToolDispatchResult.Fail("validation_error", "Field 'type_name' is required and must be non-empty");
            if (string.IsNullOrEmpty(methodName))
                return ToolDispatchResult.Fail("validation_error", "Field 'method_name' is required and must be non-empty");

            var type = FindType(typeName, assemblyName);
            if (type == null)
            {
                var hint = assemblyName != null ? $" in assembly '{assemblyName}'" : "";
                return ToolDispatchResult.Fail("type_not_found", $"Type '{typeName}' not found{hint}. " +
                    "Use fully qualified name including namespace. " +
                    "Use 'unity_open_mcp_find_members' to discover available types.");
            }

            var bindingFlags = BindingFlags.Public | BindingFlags.FlattenHierarchy;
            bindingFlags |= isStatic ? BindingFlags.Static : BindingFlags.Instance;

            // M16 Plan 6 — overload + generic-arg resolution. Legacy callers
            // pass neither generic_arg_types nor arg_type_names; the previous
            // single-GetMethod path then runs unchanged so existing calls keep
            // working. When arg_type_names is supplied we disambiguate by
            // parameter type names; when generic_arg_types is supplied we
            // MakeGenericMethod so GetComponent<Rigidbody>() style calls work.
            var genericArgTypeNames = JsonBody.GetStringArray(body, "generic_arg_types");
            var argTypeNames = JsonBody.GetStringArray(body, "arg_type_names");

            MethodInfo method;
            if (argTypeNames != null && argTypeNames.Length > 0)
            {
                method = ResolveOverload(type, methodName, bindingFlags, argTypeNames, genericArgTypeNames);
                if (method == null)
                    return ToolDispatchResult.Fail("method_not_found",
                        $"No overload of '{methodName}' on '{type.FullName}' matches arg_type_names " +
                        $"[{string.Join(", ", argTypeNames)}]. Use find_members with kind:method to list overloads.");
            }
            else
            {
                method = type.GetMethod(methodName, bindingFlags);
                if (method == null)
                    return ToolDispatchResult.Fail("method_not_found",
                        $"Method '{methodName}' not found on type '{type.FullName}'. " +
                        $"Available methods: {string.Join(", ", type.GetMethods(bindingFlags).Take(10).Select(m => m.Name))}" +
                        (type.GetMethods(bindingFlags).Length > 10 ? "..." : ""));

                // Generic method with explicit type args: bind them now so the
                // parameter types resolve correctly for arg conversion below.
                if (genericArgTypeNames != null && genericArgTypeNames.Length > 0)
                {
                    if (!method.IsGenericMethod)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{methodName}' is not generic, but generic_arg_types were supplied.");
                    if (method.GetGenericArguments().Length != genericArgTypeNames.Length)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{methodName}' has {method.GetGenericArguments().Length} generic parameter(s) " +
                            $"but {genericArgTypeNames.Length} generic_arg_types were supplied.");
                    method = BindGenericMethod(method, genericArgTypeNames);
                    if (method == null)
                        return ToolDispatchResult.Fail("generic_arg_not_found",
                            "One or more generic_arg_types could not be resolved. " +
                            "Use fully qualified type names.");
                }
            }

            var args = JsonBody.ParseArgsArray(body, "args");
            var parameters = method.GetParameters();
            var invokeArgs = ConvertArgs(args, parameters);

            object target = null;
            if (!isStatic)
            {
                if (objectId != 0)
                {
                    target = ObjectHandle.Resolve(objectId, type.FullName, null, null, null, null,
                        out var resolveError);
                    if (target == null)
                        return ToolDispatchResult.Fail("object_not_found",
                            $"Could not resolve object_id {objectId} as target for instance method: {resolveError}");
                    if (!type.IsInstanceOfType(target))
                        return ToolDispatchResult.Fail("type_mismatch",
                            $"Resolved object (type '{target.GetType().FullName}') is not assignable to '{type.FullName}'.");
                }
                else
                {
                    try
                    {
                        target = Activator.CreateInstance(type);
                    }
                    catch (Exception e)
                    {
                        return ToolDispatchResult.Fail("instantiation_error",
                            $"Cannot create instance of '{type.FullName}': {e.Message}. " +
                            "Use is_static: true for static methods, or pass object_id to target a live object.");
                    }
                }
            }

            try
            {
                var result = method.Invoke(target, invokeArgs);
                var output = OutputSerializer.Serialize(result, BuildSerializeOptions(body));
                return ToolDispatchResult.Ok(output);
            }
            catch (TargetInvocationException tie)
            {
                return ToolDispatchResult.Fail("invocation_error", tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        static SerializeOptions BuildSerializeOptions(string body)
        {
            var maxDepth = JsonBody.GetInt(body, "max_depth", 4);
            var maxItems = JsonBody.GetInt(body, "max_items", 100);
            return new SerializeOptions
            {
                MaxDepth = maxDepth <= 0 ? 4 : maxDepth,
                MaxListItems = maxItems <= 0 ? 100 : maxItems,
            };
        }

        static Type FindType(string typeName, string assemblyName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (!string.IsNullOrEmpty(assemblyName))
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName);
                return asm?.GetType(typeName);
            }

            foreach (var asm in assemblies)
            {
                var type = asm.GetType(typeName);
                if (type != null) return type;
            }

            foreach (var asm in assemblies)
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName)
                            return t;
                    }
                }
                catch { }
            }

            return null;
        }

        // M16 Plan 6 — pick the overload whose parameter types match
        // arg_type_names (full name or simple name), then optionally bind
        // generic type arguments when the method is generic. Returns null when
        // no overload matches.
        static MethodInfo ResolveOverload(Type type, string methodName, BindingFlags bindingFlags,
            string[] argTypeNames, string[] genericArgTypeNames)
        {
            MethodInfo[] candidates;
            try { candidates = type.GetMethods(bindingFlags); }
            catch { return null; }

            MethodInfo fallback = null;
            foreach (var m in candidates)
            {
                if (m.Name != methodName) continue;
                var parms = m.GetParameters();
                if (parms.Length != argTypeNames.Length) continue;

                bool match = true;
                for (int i = 0; i < parms.Length; i++)
                {
                    if (!TypeNameMatches(parms[i].ParameterType, argTypeNames[i]))
                    {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;

                // Prefer the non-generic match when generic args weren't
                // requested; otherwise look for the generic one we can bind.
                if (genericArgTypeNames == null || genericArgTypeNames.Length == 0)
                {
                    if (!m.IsGenericMethod) return m;
                    fallback ??= m;
                }
                else
                {
                    if (!m.IsGenericMethod) continue;
                    if (m.GetGenericArguments().Length != genericArgTypeNames.Length) continue;
                    var bound = BindGenericMethod(m, genericArgTypeNames);
                    if (bound != null) return bound;
                }
            }
            return fallback;
        }

        static bool TypeNameMatches(Type paramType, string requestedName)
        {
            if (string.IsNullOrEmpty(requestedName)) return false;
            // Full-name match first; fall back to simple name (matching FindType).
            if (!string.IsNullOrEmpty(paramType.FullName)
                && paramType.FullName == requestedName) return true;
            if (paramType.Name == requestedName) return true;
            // Accept common CLR aliases (int/Int32) so agents can use either form.
            if (ClrAliases.TryGetValue(requestedName, out var aliasName)
                && (paramType.Name == aliasName || paramType.FullName == aliasName)) return true;
            return false;
        }

        static MethodInfo BindGenericMethod(MethodInfo method, string[] genericArgTypeNames)
        {
            var typeArgs = new Type[genericArgTypeNames.Length];
            for (int i = 0; i < genericArgTypeNames.Length; i++)
            {
                typeArgs[i] = FindType(genericArgTypeNames[i], null);
                if (typeArgs[i] == null) return null;
            }
            try { return method.MakeGenericMethod(typeArgs); }
            catch (ArgumentException)
            {
                // Constraint violation — the type args don't satisfy the
                // method's generic constraints. Surface as not-found so the
                // caller gets a discoverable error.
                return null;
            }
        }

        static readonly Dictionary<string, string> ClrAliases = new()
        {
            { "int", "Int32" },
            { "uint", "UInt32" },
            { "long", "Int64" },
            { "ulong", "UInt64" },
            { "short", "Int16" },
            { "ushort", "UInt16" },
            { "byte", "Byte" },
            { "sbyte", "SByte" },
            { "float", "Single" },
            { "double", "Double" },
            { "decimal", "Decimal" },
            { "bool", "Boolean" },
            { "char", "Char" },
            { "string", "String" },
            { "object", "Object" },
        };

        static object[] ConvertArgs(List<object> args, ParameterInfo[] parameters)
        {
            if (args == null || args.Count == 0) return Array.Empty<object>();
            if (parameters == null || parameters.Length == 0) return Array.Empty<object>();

            var count = Math.Min(args.Count, parameters.Length);
            var result = new object[count];
            for (var i = 0; i < count; i++)
                result[i] = ConvertArg(args[i], parameters[i].ParameterType);
            return result;
        }

        static object ConvertArg(object value, Type targetType)
        {
            if (value == null)
            {
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);
                return null;
            }

            // Object handle resolution: when the target type is or derives from
            // UnityEngine.Object and the arg looks like a handle JSON, resolve it.
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType)
                && ObjectHandle.LooksLikeHandle(value))
            {
                var resolved = ObjectHandle.ResolveJson(value.ToString(), out var error);
                if (resolved != null)
                    return resolved;
            }

            if (targetType == typeof(string))
                return value is string s ? s : value.ToString();
            if (targetType == typeof(int))
                return value is long l ? (int)l : value is double d ? (int)d : Convert.ToInt32(value);
            if (targetType == typeof(float))
                return Convert.ToSingle(value);
            if (targetType == typeof(double))
                return Convert.ToDouble(value);
            if (targetType == typeof(bool))
                return value is bool b ? b : Convert.ToBoolean(value);
            if (targetType == typeof(long))
                return Convert.ToInt64(value);
            if (targetType == typeof(byte))
                return Convert.ToByte(value);
            if (targetType == typeof(short))
                return Convert.ToInt16(value);
            if (targetType == typeof(uint))
                return Convert.ToUInt32(value);
            if (targetType == typeof(ulong))
                return Convert.ToUInt64(value);
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value.ToString(), true);

            return value;
        }
    }
}
