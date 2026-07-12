using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

// Exposes bridge root internals (OutputSerializer, BridgeJson) to the first-party
// sub-assemblies that need the shared JSON helpers. BridgeJson stays internal per
// packages/bridge/AGENTS.md §Transport ("do not make it public just to reach an
// extension"); IVT is the sanctioned seam for same-package satellites.
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.Editor.Tests")]
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.TestRunner.Editor")]
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.Dependencies.Editor")]

namespace UnityOpenMcpBridge.MetaTools
{
    /// <summary>
    /// Options controlling the depth-limited reflective walker in <see cref="OutputSerializer"/>.
    /// Defaults mirror the unity-cli reference walker: depth 4, list truncation 100.
    /// </summary>
    public sealed class SerializeOptions
    {
        public int MaxDepth = 4;
        public int MaxListItems = 100;
        public bool IncludeFields = true;
        public bool IncludeProperties = true;
    }

    static class OutputSerializer
    {
        private const int EnumerableSafetyCap = 100_000;

        public static string Serialize(object value)
        {
            return Serialize(value, new SerializeOptions());
        }

        public static string Serialize(object value, SerializeOptions options)
        {
            if (options == null) options = new SerializeOptions();
            return SerializeInternal(value, 0, options, new HashSet<object>(ReferenceComparer.Instance));
        }

        private static string SerializeInternal(object value, int depth, SerializeOptions opts, HashSet<object> visited)
        {
            if (value == null) return null;

            // Unity "fake null": a destroyed UnityEngine.Object is not a real null
            // reference but the overloaded == operator reports it as null. Reading
            // any property on it throws, so short-circuit to JSON null first.
            if (value is UnityEngine.Object unityObj && unityObj == null)
                return null;

            var type = value.GetType();

            // --- Leaf types: serialize inline regardless of depth (no recursion risk) ---
            if (type == typeof(string))
                return "\"" + EscapeJsonString((string)value) + "\"";
            if (type == typeof(bool))
                return (bool)value ? "true" : "false";
            if (type == typeof(int))
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(uint))
                return ((uint)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(long))
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(ulong))
                return ((ulong)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(short))
                return ((short)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(ushort))
                return ((ushort)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(byte))
                return ((byte)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(sbyte))
                return ((sbyte)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(float))
                return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(double))
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(decimal))
                return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(char))
                return "\"" + EscapeJsonString(value.ToString()) + "\"";

            if (type.IsEnum)
                return "\"" + EscapeJsonString(value.ToString()) + "\"";

            // System.Type → emit the full name; reflecting into it reaches the type
            // system and produces enormous / throwing payloads.
            if (value is Type t)
                return "\"" + EscapeJsonString(t.FullName ?? t.Name) + "\"";

            // ECS FixedString types throw / recurse nastily; stringify like unity-cli.
            if (type.Name.StartsWith("FixedString"))
                return "\"" + EscapeJsonString(value.ToString()) + "\"";

            // UnityEngine.Object: never reflect (cyclic GameObject↔Component graphs,
            // property access can throw). Emit a compact descriptor instead.
            if (value is UnityEngine.Object liveUnityObj)
                return SerializeUnityObject(liveUnityObj);

            // --- Composite types: enforce depth limit to bound payload size ---
            if (depth > opts.MaxDepth)
                return "\"" + EscapeJsonString(SafeToString(value)) + "\"";

            // Cycle detection: only reference types can form true back-edges. Value
            // types are boxed copies and never cycle, so they are excluded to avoid
            // false positives when the same value (e.g. Vector3.zero) repeats.
            if (!type.IsValueType)
            {
                if (!visited.Add(value))
                    return "{\"$ref\":\"" + EscapeJsonString(type.Name) + "\"}";
                try
                {
                    return SerializeComposite(value, type, depth, opts, visited);
                }
                finally
                {
                    visited.Remove(value);
                }
            }

            return SerializeComposite(value, type, depth, opts, visited);
        }

        private static string SerializeComposite(object value, Type type, int depth, SerializeOptions opts, HashSet<object> visited)
        {
            if (value is IDictionary dict)
                return SerializeDictionary(dict, depth, opts, visited);

            if (value is IEnumerable enumerable && !(value is string))
                return SerializeEnumerable(enumerable, depth, opts, visited);

            return ReflectObject(value, type, depth, opts, visited);
        }

        private static string SerializeUnityObject(UnityEngine.Object obj)
        {
            return ObjectHandle.Serialize(obj);
        }

        private static string SerializeDictionary(IDictionary dict, int depth, SerializeOptions opts, HashSet<object> visited)
        {
            var items = new List<string>();
            foreach (DictionaryEntry entry in dict)
            {
                var val = SerializeInternal(entry.Value, depth + 1, opts, visited);
                // JSON object keys must be strings; coerce any key type to a quoted string.
                var keyStr = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "";
                items.Add("\"" + EscapeJsonString(keyStr) + "\":" + val);
            }
            return "{" + string.Join(",", items) + "}";
        }

        private static string SerializeEnumerable(IEnumerable enumerable, int depth, SerializeOptions opts, HashSet<object> visited)
        {
            var items = new List<string>();
            var taken = 0;
            var elided = 0;
            var capped = false;

            foreach (var item in enumerable)
            {
                if (taken < opts.MaxListItems)
                {
                    items.Add(SerializeInternal(item, depth + 1, opts, visited));
                    taken++;
                }
                else
                {
                    elided++;
                    if (elided >= EnumerableSafetyCap)
                    {
                        capped = true;
                        break;
                    }
                }
            }

            if (elided == 0)
                return "[" + string.Join(",", items) + "]";

            var truncated = capped
                ? "\"" + (elided + "+") + "\""
                : elided.ToString(CultureInfo.InvariantCulture);
            return "{\"items\":[" + string.Join(",", items) + "],\"truncated\":" + truncated + "}";
        }

        private static string ReflectObject(object value, Type type, int depth, SerializeOptions opts, HashSet<object> visited)
        {
            var members = new List<string>();
            members.Add("\"$type\":\"" + EscapeJsonString(type.Name) + "\"");

            if (opts.IncludeFields)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    string valJson;
                    try { valJson = SerializeInternal(f.GetValue(value), depth + 1, opts, visited); }
                    catch (Exception ex) { valJson = ErrorMarker(ex); }
                    members.Add("\"" + EscapeJsonString(f.Name) + "\":" + valJson);
                }
            }

            if (opts.IncludeProperties)
            {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var p in props)
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var getter = p.GetMethod;
                    if (getter == null || !getter.IsPublic) continue;
                    string valJson;
                    try { valJson = SerializeInternal(p.GetValue(value, null), depth + 1, opts, visited); }
                    catch (Exception ex) { valJson = ErrorMarker(ex); }
                    members.Add("\"" + EscapeJsonString(p.Name) + "\":" + valJson);
                }
            }

            return "{" + string.Join(",", members) + "}";
        }

        private static string ErrorMarker(Exception ex)
        {
            return "\"" + EscapeJsonString("<error: " + ex.GetType().Name + ">") + "\"";
        }

        private static string SafeToString(object value)
        {
            try { return value.ToString(); }
            catch { return value.GetType().Name; }
        }

        // Sanctioned reflection-serializer escape. This is the ONE place outside
        // BridgeJson that hand-rolls JSON escaping, because it has ~80 call sites
        // across the reflection/serializer tools and intentionally DIVERGES from
        // BridgeJson.EscapeStringContent: it emits `\b`/`\f` as the short escapes
        // `\\b`/`\\f`, whereas BridgeJson renders all C0 controls as `\uXXXX`.
        // Both are valid JSON (RFC 8259), so this divergence is not a bug — but
        // it means migrating these call sites to BridgeJson would be an
        // observable wire-shape change, which the M30 no-behavior-change guard
        // forbade. Do NOT introduce a third escape helper; new hand-rolled JSON
        // uses BridgeJson (see packages/bridge/AGENTS.md §Transport).
        public static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reference-identity comparer for cycle detection. The default object
        /// comparer uses overridden Equals/GetHashCode which breaks for types
        /// that implement value equality (strings, some structs boxed as object).
        /// </summary>
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            // 'new' is intentional: this implements IEqualityComparer<object>.Equals(object,object)
            // and deliberately hides the default object.Equals(object,object) with reference identity.
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
