using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    static class OutputSerializer
    {
        public static string Serialize(object value)
        {
            if (value == null) return null;

            var type = value.GetType();

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

            if (value is UnityEngine.Object obj)
                return SerializeUnityObject(obj);

            if (value is IDictionary dict)
                return SerializeDictionary(dict);

            if (value is IEnumerable enumerable && !(value is string))
                return SerializeEnumerable(enumerable);

            return "\"" + EscapeJsonString(value.ToString()) + "\"";
        }

        static string SerializeUnityObject(UnityEngine.Object obj)
        {
            var name = EscapeJsonString(obj.name);
            var typeName = EscapeJsonString(obj.GetType().FullName);
            return $"{{\"name\":\"{name}\",\"type\":\"{typeName}\",\"entityId\":\"{obj.GetEntityId()}\"}}";
        }

        static string SerializeDictionary(IDictionary dict)
        {
            var items = new List<string>();
            foreach (DictionaryEntry entry in dict)
            {
                var key = Serialize(entry.Key);
                var val = Serialize(entry.Value);
                items.Add($"{key}:{val}");
            }
            return "{" + string.Join(",", items) + "}";
        }

        static string SerializeEnumerable(IEnumerable enumerable)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
                items.Add(Serialize(item));
            return "[" + string.Join(",", items) + "]";
        }

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
    }
}
