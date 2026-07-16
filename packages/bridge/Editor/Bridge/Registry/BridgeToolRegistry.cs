using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge
{
    public static class BridgeToolRegistry
    {
        private static readonly Dictionary<string, BridgeToolEntry> _tools = new();

        // M18 Plan 6 / T18.6.2 — the tool ids that were rejected during the
        // last Scan() because an earlier assembly had already registered them
        // (e.g. a legacy extension pack + the embedded bridge copy both define
        // `unity_open_mcp_navigation_surface_add`). Each colliding name is
        // recorded once regardless of how many duplicates were seen. Exposed
        // via DuplicateCount / DuplicateToolNames so the duplicate-registration
        // guard is observable to EditMode tests + diagnostics without relying
        // on Unity's log capture. The first-wins LogWarning below stays.
        private static readonly List<string> _duplicateToolNames = new();

        public static int Count => _tools.Count;

        /// <summary>Number of distinct tool ids that collided across assemblies
        /// during the last <see cref="Scan"/>. Non-zero means a duplicate
        /// registration was detected and silently kept first-registered.</summary>
        public static int DuplicateCount => _duplicateToolNames.Count;

        // Production scan entry point. Excludes test assemblies (anything
        // referencing nunit.framework) so the [BridgeTool] fixtures that drive
        // the bridge's own EditMode tests never leak into the Tools tab,
        // GET /tools, or the group→tools capability map. See Scan(bool).
        public static void Scan()
        {
            Scan(includeTestAssemblies: false);
        }

        // Tests opt into scanning their own nunit assembly via
        // includeTestAssemblies: true (the AttributeScannerTests fixtures live
        // in com.alexeyperov.unity-open-mcp-bridge.Editor.Tests, which
        // references nunit.framework and is therefore excluded by the default
        // Scan()). Production callers always use the parameterless Scan().
        internal static void Scan(bool includeTestAssemblies)
        {
            _tools.Clear();
            _duplicateToolNames.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!includeTestAssemblies && IsTestAssembly(assembly)) continue;
                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BridgeToolRegistry] Error scanning assembly {assembly.GetName().Name}: {e.Message}");
                }
            }

            if (_duplicateToolNames.Count > 0)
            {
                Debug.LogWarning(
                    $"[BridgeToolRegistry] {_duplicateToolNames.Count} duplicate tool id(s) detected across assemblies " +
                    $"(kept first registered): {string.Join(", ", _duplicateToolNames)}");
            }

            Debug.Log($"[BridgeToolRegistry] Registered {_tools.Count} typed tool(s)");
        }

        /// <summary>Enumerate the tool ids that collided across assemblies
        /// during the last <see cref="Scan"/> (empty when none). Returns a
        /// snapshot; mutating it does not affect the registry.</summary>
        public static IEnumerable<string> DuplicateToolNames()
        {
            return _duplicateToolNames.AsReadOnly();
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
                        // M18 Plan 6 / T18.6.2 — record each colliding id once so
                        // the duplicate-registration guard (EditMode test + CI) can
                        // observe it without log capture.
                        if (!_duplicateToolNames.Contains(attr.Name))
                            _duplicateToolNames.Add(attr.Name);
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

            // Unwrap Nullable<T> so a nullable value-type parameter (e.g.
            // `float?`, `int?`) converts through its underlying type. Without
            // this the typed checks below miss Nullable<float> entirely and the
            // raw string leaks through as an unassignable value. M20 Plan 2
            // (light_set) is the first registry tool to use nullable value-type
            // parameters; the unwrapped value boxes back into Nullable<T>.
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                targetType = underlying;

            if (targetType == typeof(string))
            {
                if (raw.StartsWith("\"") && raw.EndsWith("\""))
                    return JsonBody.GetString("{\"v\":" + raw + "}", "v");
                return raw;
            }

            if (targetType == typeof(int))
            {
                var s = StripJsonQuotes(raw);
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
                return 0;
            }

            // Unity 6000.5+ EntityIds exceed JS Number.MAX_SAFE_INTEGER, so the
            // agent-facing wire form is a JSON string. Parse flexibly (quoted
            // string or bare number) via InstanceId — do NOT fall through to the
            // string-passthrough below or Method.Invoke throws
            // "String cannot be converted to Int64".
            if (targetType == typeof(long))
            {
                return InstanceId.Parse(StripJsonQuotes(raw));
            }

            if (targetType == typeof(float))
            {
                var s = StripJsonQuotes(raw);
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
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
                var cleaned = StripJsonQuotes(raw);
                if (Enum.IsDefined(targetType, cleaned))
                    return Enum.Parse(targetType, cleaned);
                if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                    return Enum.ToObject(targetType, intVal);
                return Enum.GetValues(targetType).GetValue(0);
            }

            if (raw.StartsWith("\"") && raw.EndsWith("\""))
                return raw.Substring(1, raw.Length - 2);

            return raw;
        }

        private static string StripJsonQuotes(string raw)
        {
            if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        public static IEnumerable<BridgeToolEntry> All()
        {
            return _tools.Values;
        }

        // A test assembly is identified by a reference to nunit.framework — the
        // test framework every EditMode test asmdef pulls in. This catches all
        // current test assemblies plus any future one regardless of its name,
        // and never matches production assemblies (the TestRunner tool lives in
        // com.alexeyperov.unity-open-mcp-bridge.TestRunner.Editor, which does
        // NOT reference nunit.framework). GetReferencedAssemblies can throw on
        // dynamic/error assemblies; treat those as non-test (the ScanAssembly
        // try/catch reports the real error).
        private static bool IsTestAssembly(Assembly assembly)
        {
            AssemblyName[] refs;
            try
            {
                refs = assembly.GetReferencedAssemblies();
            }
            catch
            {
                return false;
            }
            if (refs == null) return false;
            foreach (var r in refs)
            {
                if (r != null && r.Name == "nunit.framework") return true;
            }
            return false;
        }
    }
}
