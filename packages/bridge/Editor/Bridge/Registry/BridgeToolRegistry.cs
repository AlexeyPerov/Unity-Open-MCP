using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeToolRegistry
    {
        private static readonly Dictionary<string, BridgeToolEntry> _tools = new();

        public static int Count => _tools.Count;

        public static void Scan()
        {
            _tools.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BridgeToolRegistry] Error scanning assembly {assembly.GetName().Name}: {e.Message}");
                }
            }

            Debug.Log($"[BridgeToolRegistry] Registered {_tools.Count} typed tool(s)");
        }

        private static void ScanAssembly(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass) continue;
                if (type.GetCustomAttribute<BridgeToolTypeAttribute>() == null) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    var attr = method.GetCustomAttribute<BridgeToolAttribute>();
                    if (attr == null) continue;
                    if (!attr.Enabled) continue;

                    if (_tools.ContainsKey(attr.Name))
                    {
                        Debug.LogWarning($"[BridgeToolRegistry] Duplicate tool name '{attr.Name}' — keeping first registered");
                        continue;
                    }

                    var entry = new BridgeToolEntry(
                        name: attr.Name,
                        title: attr.Title,
                        isMutating: attr.IsMutating,
                        gate: attr.Gate,
                        readOnlyHint: attr.ReadOnlyHint,
                        idempotentHint: attr.IdempotentHint,
                        destructiveHint: attr.DestructiveHint,
                        lifecycle: attr.Lifecycle,
                        enabled: attr.Enabled,
                        method: method,
                        group: attr.Group
                    );

                    _tools[attr.Name] = entry;
                }
            }
        }

        public static bool Contains(string toolName)
        {
            return _tools.ContainsKey(toolName);
        }

        public static bool TryGet(string toolName, out BridgeToolEntry entry)
        {
            return _tools.TryGetValue(toolName, out entry);
        }

        // M18 Plan 2 / T18.2 — group → tool-names mapping for the bridge's
        // capability surface. Tools with Group = null are always visible
        // (server meta-tools) and intentionally omitted — the capability
        // report only enumerates group-bound tools, which is what
        // manage_tools and the tool_groups resource need to know.
        //
        // Returns a fresh dictionary on every call so callers can mutate it
        // without affecting the registry; the registry itself never mutates
        // after Scan().
        public static IDictionary<string, System.Collections.Generic.List<string>> GroupToTools()
        {
            var map = new Dictionary<string, System.Collections.Generic.List<string>>();
            foreach (var entry in _tools.Values)
            {
                if (string.IsNullOrEmpty(entry.Group)) continue;
                if (!map.TryGetValue(entry.Group, out var list))
                {
                    list = new System.Collections.Generic.List<string>();
                    map[entry.Group] = list;
                }
                list.Add(entry.Name);
            }
            // Stable order — group iteration is deterministic across calls.
            foreach (var kv in map) kv.Value.Sort(StringComparer.Ordinal);
            return map;
        }

        public static ToolDispatchResult TryDispatch(string toolName, string body)
        {
            if (!_tools.TryGetValue(toolName, out var entry))
                return null;

            try
            {
                var args = ExtractArguments(entry, body);
                if (args == null)
                    return ToolDispatchResult.Fail("missing_parameter", $"Required parameter missing for tool '{toolName}'");

                var instance = entry.GetInstance();
                var result = entry.Method.Invoke(instance, args);

                var output = result?.ToString();
                if (result != null && output == null)
                    output = JsonBody.GetRawValue(result.ToString(), "");

                return ToolDispatchResult.Ok(output);
            }
            catch (TargetInvocationException tie)
            {
                var msg = tie.InnerException?.Message ?? tie.Message;
                return ToolDispatchResult.Fail("execution_error", msg);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        private static object[] ExtractArguments(BridgeToolEntry entry, string body)
        {
            if (entry.Parameters.Length == 0)
                return Array.Empty<object>();

            var args = new object[entry.Parameters.Length];

            for (int i = 0; i < entry.Parameters.Length; i++)
            {
                var param = entry.Parameters[i];
                var paramName = char.ToLowerInvariant(param.Name[0]) + param.Name.Substring(1);
                var paramType = param.ParameterType;
                var hasDefault = param.HasDefaultValue;
                var isNullable = !paramType.IsValueType || Nullable.GetUnderlyingType(paramType) != null;

                // JsonBody.GetRawValue wraps the key in quotes itself; pass the bare key.
                var rawValue = JsonBody.GetRawValue(body, paramName);
                if (rawValue == null)
                {
                    if (hasDefault)
                    {
                        args[i] = param.DefaultValue;
                        continue;
                    }

                    if (isNullable)
                    {
                        args[i] = null;
                        continue;
                    }

                    return null;
                }

                args[i] = ConvertValue(rawValue.Trim(), paramType);
            }

            return args;
        }

        internal static object ConvertValue(string raw, Type targetType)
        {
            if (raw == "null") return null;

            if (targetType == typeof(string))
            {
                if (raw.StartsWith("\"") && raw.EndsWith("\""))
                    return JsonBody.GetString("{\"v\":" + raw + "}", "v");
                return raw;
            }

            if (targetType == typeof(int))
            {
                if (int.TryParse(raw, out var v)) return v;
                return 0;
            }

            if (targetType == typeof(float))
            {
                if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
                return 0f;
            }

            if (targetType == typeof(bool))
            {
                if (raw == "true") return true;
                if (raw == "false") return false;
                return false;
            }

            if (targetType == typeof(string[]))
            {
                return JsonBody.GetStringArray("{\"v\":" + raw + "}", "v") ?? Array.Empty<string>();
            }

            if (targetType.IsEnum)
            {
                var cleaned = raw.Trim('"');
                if (Enum.IsDefined(targetType, cleaned))
                    return Enum.Parse(targetType, cleaned);
                if (int.TryParse(cleaned, out var intVal))
                    return Enum.ToObject(targetType, intVal);
                return Enum.GetValues(targetType).GetValue(0);
            }

            if (raw.StartsWith("\"") && raw.EndsWith("\""))
                return raw.Substring(1, raw.Length - 2);

            return raw;
        }

        public static IEnumerable<BridgeToolEntry> All()
        {
            return _tools.Values;
        }
    }
}
