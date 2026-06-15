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

            var method = type.GetMethod(methodName, bindingFlags);
            if (method == null)
                return ToolDispatchResult.Fail("method_not_found",
                    $"Method '{methodName}' not found on type '{type.FullName}'. " +
                    $"Available methods: {string.Join(", ", type.GetMethods(bindingFlags).Take(10).Select(m => m.Name))}" +
                    (type.GetMethods(bindingFlags).Length > 10 ? "..." : ""));

            var args = JsonBody.ParseArgsArray(body, "args");
            var parameters = method.GetParameters();
            var invokeArgs = ConvertArgs(args, parameters);

            object target = null;
            if (!isStatic)
            {
                if (objectId != 0)
                {
                    target = ObjectHandle.Resolve(objectId, type.FullName, null, null, null, null,
                        null, 0, out var resolveError);
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
