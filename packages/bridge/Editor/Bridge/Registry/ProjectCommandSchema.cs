using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UnityOpenMcpBridge
{
    internal static class ProjectCommandSchema
    {
        internal static string Strings(System.Collections.Generic.IEnumerable<string> values)
            => "[" + string.Join(",", values.Select(BridgeJson.EscapeString)) + "]";

        internal static string Build(MethodInfo method)
        {
            if (!method.IsPublic || !method.IsStatic || method.ContainsGenericParameters || method.ReturnType != typeof(string))
                throw new ArgumentException("Commands must be public static non-generic methods returning JSON as string.");
            var parameters = method.GetParameters();
            var names = new System.Collections.Generic.HashSet<string>(parameters.Select(p => p.Name), StringComparer.Ordinal);
            var sb = new StringBuilder("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"x-project-command\":true,\"additionalProperties\":false,\"properties\":{");
            foreach (var p in parameters)
            {
                if (p != parameters[0]) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(p.Name)).Append(':');
                var schema = TypeSchema(p.ParameterType);
                sb.Append("{\"allOf\":[").Append(schema).Append(']');
                var attr = p.GetCustomAttribute<ProjectCommandParameterAttribute>();
                var description = attr?.Description ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (description != null) sb.Append(",\"description\":").Append(BridgeJson.EscapeString(description));
                if (p.HasDefaultValue)
                {
                    var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                    if (p.DefaultValue != null && t.IsEnum && !Enum.IsDefined(t, p.DefaultValue))
                        throw new ArgumentException("Enum default must name a declared value: " + p.Name);
                    sb.Append(",\"default\":").Append(Value(p.DefaultValue));
                }
                if (attr != null)
                {
                    var numeric = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                    if ((!double.IsNaN(attr.Minimum) || !double.IsNaN(attr.Maximum)) && numeric != typeof(int) && numeric != typeof(float) && numeric != typeof(double))
                        throw new ArgumentException("Numeric ranges require int, float or double: " + p.Name);
                    if (double.IsInfinity(attr.Minimum) || double.IsInfinity(attr.Maximum) || attr.Minimum > attr.Maximum)
                        throw new ArgumentException("Invalid numeric range: " + p.Name);
                    if (!double.IsNaN(attr.Minimum)) sb.Append(",\"minimum\":").Append(Value(attr.Minimum));
                    if (!double.IsNaN(attr.Maximum)) sb.Append(",\"maximum\":").Append(Value(attr.Maximum));
                    if (p.HasDefaultValue && p.DefaultValue != null && (numeric == typeof(int) || numeric == typeof(float) || numeric == typeof(double)))
                    {
                        var value = Convert.ToDouble(p.DefaultValue, CultureInfo.InvariantCulture);
                        if (value < attr.Minimum || value > attr.Maximum) throw new ArgumentException("Default is outside the numeric range: " + p.Name);
                    }
                    if (attr.Examples == null || attr.DeprecatedAliases == null) throw new ArgumentException("Examples and DeprecatedAliases must be arrays: " + p.Name);
                    if (attr.Examples.Length > 0)
                    {
                        foreach (var example in attr.Examples)
                            if (!BridgeJson.IsCompleteJson(example)) throw new ArgumentException("Example must be a JSON value: " + p.Name);
                        sb.Append(",\"examples\":[").Append(string.Join(",", attr.Examples)).Append(']');
                    }
                    foreach (var alias in attr.DeprecatedAliases)
                        if (string.IsNullOrWhiteSpace(alias) || !names.Add(alias)) throw new ArgumentException("Duplicate or empty parameter alias: " + alias);
                    sb.Append(",\"x-deprecatedAliases\":").Append(Strings(attr.DeprecatedAliases.OrderBy(x => x, StringComparer.Ordinal)));
                }
                sb.Append('}');
            }
            sb.Append("},\"required\":").Append(Strings(parameters.Where(p => !p.HasDefaultValue).Select(p => p.Name))).Append('}');
            return sb.ToString();
        }

        internal static string TypeSchema(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return "{\"anyOf\":[" + TypeSchema(underlying) + ",{\"type\":\"null\"}]}";
            if (type == typeof(string)) return "{\"type\":[\"string\",\"null\"]}";
            if (type == typeof(bool)) return "{\"type\":\"boolean\"}";
            if (type == typeof(int)) return "{\"type\":\"integer\",\"minimum\":-2147483648,\"maximum\":2147483647}";
            // Match the bridge's lossless Int64 / Unity instance-id wire convention.
            if (type == typeof(long)) return "{\"type\":\"string\",\"pattern\":\"^-?[0-9]+$\",\"x-clrType\":\"Int64\"}";
            if (type == typeof(float) || type == typeof(double)) return "{\"type\":\"number\"}";
            if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false))
                return "{\"type\":\"string\",\"enum\":" + Strings(Enum.GetNames(type).OrderBy(x => x, StringComparer.Ordinal)) + "}";
            if (type.IsArray && type.GetArrayRank() == 1 && !type.GetElementType().IsArray)
                return "{\"anyOf\":[{\"type\":\"array\",\"items\":" + TypeSchema(type.GetElementType()) + "},{\"type\":\"null\"}]}";
            throw new ArgumentException("Unsupported CLR parameter type: " + type);
        }

        private static string Value(object value)
        {
            if (value == null) return "null";
            if (value is string || value is Enum || value is long) return BridgeJson.EscapeString(Convert.ToString(value, CultureInfo.InvariantCulture));
            if (value is bool b) return b ? "true" : "false";
            var result = value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : value is float f ? f.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!BridgeJson.IsCompleteJson(result)) throw new ArgumentException("Default must be a finite JSON value.");
            return result;
        }
    }
}
