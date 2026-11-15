// Playtest — what a frame probe samples (unity_open_mcp_playtest_probe).
//
// Two read-only sources, both bound once at `start` and called every sample:
//   member      a field / property path read by reflection, either on a
//               GameObject or one of its components (resolved by the shared
//               GameObject resolver, DontDestroyOnLoad included) or from a
//               static root (`Namespace.Type.Member…`). Segments may index a
//               list or array (`waves[0].enemies`). Instantiating getters
//               (Renderer.material(s), MeshFilter.mesh) are refused.
//   expression  a C# expression compiled once through the execute_csharp
//               compile path (ExecuteCSharpTool.CompileSnippetType) into a
//               delegate. With a target, `target` is the GameObject or its
//               component. SnippetClassifier.ClassifyExpression must find it
//               read-only; the project's C# deny patterns apply.
//
// ProbeValue turns a sample into bounded JSON: numbers, strings (clipped),
// enums by name, vectors / quaternions / colours / rects / bounds as small
// objects, Unity objects as handles, the first items of a collection, and the
// public fields of other values to a small depth; anything larger becomes a
// truncation marker with a preview.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.ObjectRefs;
using UnityOpenMcpBridge.TypedTools;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Playtest
{
    // A bound sample source.
    internal sealed class ProbeSource
    {
        internal string Kind;            // "member" | "expression"
        internal string Text;            // the member path or the expression
        internal Func<object> Read;      // one sample; throws on failure
        internal Object Target;          // the tracked GameObject / component; null for a static source
        internal string TargetJson;      // {"game_object_path","instance_id","component_type"} or null
        internal string StaticType;      // the static root's type (member form without a target)
        internal FieldInfo RefsField;    // expression form: the snippet's Refs field (cleared at the end)

        internal bool TargetLost => TargetJson != null && Target == null;

        // Drop the references the source holds (the snippet assembly stays loaded until reload).
        internal void Release()
        {
            try { RefsField?.SetValue(null, null); } catch (Exception) { /* best effort */ }
            Read = null;
            Target = null;
        }

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"kind\":").Append(BridgeJson.EscapeString(Kind));
            sb.Append(Kind == "member" ? ",\"member\":" : ",\"expression\":").Append(BridgeJson.EscapeString(Text));
            if (StaticType != null) sb.Append(",\"static_type\":").Append(BridgeJson.EscapeString(StaticType));
            sb.Append(",\"target\":").Append(TargetJson ?? "null");
            sb.Append('}');
        }
    }

    // A member path that names nothing on the value it reaches, or names a
    // getter the probe must not call.
    internal sealed class ProbeMemberException : Exception
    {
        internal readonly string Code;
        internal ProbeMemberException(string code, string message) : base(message) { Code = code; }
    }

    internal static class ProbeSourceBinder
    {
        // ---- target ------------------------------------------------------

        // The GameObject a probe reads (path or instance id), through the shared
        // resolver; a path that names nothing there is looked up in the
        // DontDestroyOnLoad scene too (the playtest_wait object lookup). Null
        // with `refusal` set when it does not resolve.
        internal static GameObject ResolveTarget(string path, long instanceId, out ToolDispatchResult refusal)
        {
            refusal = null;
            var resolution = TypedTargets.Resolve(instanceId, path, null, TargetRequest.Ambient);
            if (resolution.Ok) return resolution.GameObject;
            if (resolution.Failure != null && resolution.Failure.IsNotFound && !string.IsNullOrEmpty(path))
            {
                var ddol = PlaytestObjects.Find(path);
                if (ddol != null) return ddol;
            }
            refusal = resolution.Failure?.ToResult()
                      ?? ToolDispatchResult.Fail(ResolveFailure.GameObjectNotFound, "The probe target was not found.");
            return null;
        }

        internal static string TargetJson(GameObject go, Component component)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"game_object_path\":").Append(BridgeJson.EscapeString(PlaytestObjects.PathOf(go)));
            sb.Append(",\"instance_id\":").Append(InstanceId.ToJson(go));
            if (component != null)
            {
                sb.Append(",\"component_type\":").Append(BridgeJson.EscapeString(component.GetType().FullName));
                sb.Append(",\"component_instance_id\":").Append(InstanceId.ToJson(component));
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ---- member form -------------------------------------------------

        internal struct Step
        {
            public string Name;
            public int[] Indices;
        }

        // "a.b[2].c" → steps; null + error for anything else.
        internal static List<Step> ParsePath(string path, out string error)
        {
            error = null;
            var steps = new List<Step>();
            if (string.IsNullOrWhiteSpace(path)) { error = "member is empty."; return null; }
            foreach (var raw in path.Trim().Split('.'))
            {
                var segment = raw.Trim();
                int bracket = segment.IndexOf('[');
                var name = bracket < 0 ? segment : segment.Substring(0, bracket);
                if (!IsIdentifier(name)) { error = "member segment '" + segment + "' is not an identifier (a.b[0].c)."; return null; }
                var indices = new List<int>();
                var rest = bracket < 0 ? "" : segment.Substring(bracket);
                while (rest.Length > 0)
                {
                    int close = rest.IndexOf(']');
                    if (rest[0] != '[' || close < 2 ||
                        !int.TryParse(rest.Substring(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        error = "member segment '" + segment + "' has a malformed index; use name[0].";
                        return null;
                    }
                    indices.Add(index);
                    rest = rest.Substring(close + 1);
                }
                steps.Add(new Step { Name = name, Indices = indices.ToArray() });
            }
            return steps;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        // A static root: the longest dotted prefix of `path` that names a type
        // (full name, or a class name anywhere), followed by at least one member.
        internal static Type ResolveStaticRoot(string path, out string memberPath)
        {
            memberPath = null;
            var parts = path.Trim().Split('.');
            for (int take = parts.Length - 1; take >= 1; take--)
            {
                var typeName = string.Join(".", parts, 0, take);
                if (typeName.IndexOf('[') >= 0) continue;
                var type = ComponentsTools.ResolveComponentType(typeName);
                if (type == null) continue;
                memberPath = string.Join(".", parts, take, parts.Length - take);
                return type;
            }
            return null;
        }

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        // Bind a member path. `root` is the instance (null for a static path on
        // `staticType`). The returned reader resolves members against each
        // value's runtime type (cached per type), so a field declared as a base
        // type or interface still reaches the subclass member.
        internal static Func<object> BindMember(object root, Type staticType, List<Step> steps)
        {
            var cache = new Dictionary<(Type, string), MemberInfo>();
            return () =>
            {
                object current = root;
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    bool isStatic = i == 0 && staticType != null;
                    if (!isStatic)
                    {
                        if (current == null || (current is Object uo && uo == null))
                            throw new NullReferenceException("'" + PathUpTo(steps, i) + "' is null.");
                    }
                    var type = isStatic ? staticType : current.GetType();
                    var member = Lookup(cache, type, step.Name, isStatic);
                    current = member is FieldInfo f ? f.GetValue(isStatic ? null : current)
                        : ((PropertyInfo)member).GetValue(isStatic ? null : current, null);
                    foreach (var index in step.Indices)
                    {
                        if (current == null) throw new NullReferenceException("'" + PathUpTo(steps, i + 1) + "' is null.");
                        if (!(current is IList list))
                            throw new ProbeMemberException("member_not_readable", "'" + PathUpTo(steps, i + 1) + "' is a " + current.GetType().Name + ", not a list or array; it cannot be indexed.");
                        if (index >= list.Count)
                            throw new IndexOutOfRangeException("'" + PathUpTo(steps, i + 1) + "' has " + list.Count + " items; index " + index + " is out of range.");
                        current = list[index];
                    }
                }
                return current;
            };
        }

        private static string PathUpTo(List<Step> steps, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && i < steps.Count; i++)
            {
                if (i > 0) sb.Append('.');
                sb.Append(steps[i].Name);
            }
            return count == 0 ? "the target" : sb.ToString();
        }

        private static MemberInfo Lookup(Dictionary<(Type, string), MemberInfo> cache, Type type, string name, bool isStatic)
        {
            if (cache.TryGetValue((type, name), out var hit)) return hit;
            var member = Find(type, name, isStatic, StringComparison.Ordinal) ?? Find(type, name, isStatic, StringComparison.OrdinalIgnoreCase);
            if (member == null)
                throw new ProbeMemberException("member_not_found",
                    "'" + name + "' is not a readable " + (isStatic ? "static" : "instance") + " field or property of " + type.FullName +
                    ". Members: " + Candidates(type, isStatic) + ".");
            if (member is PropertyInfo p && OutputSerializer.IsInstantiatingProperty(type, p))
                throw new ProbeMemberException("member_not_readable",
                    type.Name + "." + p.Name + " instantiates a copy when read (use shared" + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1) + ").");
            cache[(type, name)] = member;
            return member;
        }

        // Instance members: the type and its bases, private ones included (a
        // [SerializeField] private field is the usual thing to watch). Static
        // members: the type's own and the public / protected ones it inherits.
        private static MemberInfo Find(Type type, string name, bool isStatic, StringComparison comparison)
        {
            for (var t = type; t != null; t = isStatic ? null : t.BaseType)
            {
                var flags = isStatic ? StaticFlags : InstanceFlags | BindingFlags.DeclaredOnly;
                foreach (var f in t.GetFields(flags))
                    if (string.Equals(f.Name, name, comparison) && !f.Name.StartsWith("<", StringComparison.Ordinal)) return f;
                foreach (var p in t.GetProperties(flags))
                    if (string.Equals(p.Name, name, comparison) && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                        return p;
            }
            return null;
        }

        private static string Candidates(Type type, bool isStatic)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                var flags = isStatic ? StaticFlags : InstanceFlags;
                foreach (var f in type.GetFields(flags))
                    if (!f.Name.StartsWith("<", StringComparison.Ordinal) && (f.IsPublic || f.IsDefined(typeof(SerializeField), true) || !IsFramework(f.DeclaringType)))
                        names.Add(f.Name);
                foreach (var p in type.GetProperties(flags))
                    if (p.GetIndexParameters().Length == 0 && p.GetGetMethod(false) != null && !p.IsDefined(typeof(ObsoleteAttribute), true))
                        names.Add(p.Name);
            }
            catch (Exception) { /* a type that cannot list its members lists none */ }
            if (names.Count == 0) return "(none)";
            var list = new List<string>(names);
            return list.Count > 40 ? string.Join(", ", list.GetRange(0, 40)) + ", …" : string.Join(", ", list);
        }

        private static bool IsFramework(Type t)
        {
            var ns = t?.Namespace ?? "";
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("UnityEditor", StringComparison.Ordinal) || ns.StartsWith("System", StringComparison.Ordinal);
        }

        // ---- expression form ---------------------------------------------

        // The generated snippet body. A comment names the probe so the compiled
        // assembly — and its Refs field, which holds `target` — is this probe's own.
        internal static string ExpressionBody(string expression, Type targetType, string probeId)
        {
            var sb = new StringBuilder(expression.Length + 160);
            if (targetType != null) sb.Append("var target = Ref<").Append(TypeSyntax(targetType)).Append(">(0); ");
            sb.Append("return (object)(\n").Append(expression).Append("\n);\n// playtest_probe ").Append(probeId);
            return sb.ToString();
        }

        // C# syntax for `type` as a snippet can name it; the nearest visible,
        // non-generic Unity base otherwise.
        internal static string TypeSyntax(Type type)
        {
            if (type == typeof(GameObject)) return "UnityEngine.GameObject";
            if (type.IsVisible && !type.IsGenericType && type.FullName != null)
                return "global::" + type.FullName.Replace('+', '.');
            return typeof(MonoBehaviour).IsAssignableFrom(type) ? "UnityEngine.MonoBehaviour" : "UnityEngine.Component";
        }

        // Compile an expression into a reader. Null with `refusal` set when the
        // compile path refuses it (roslyn_unavailable / compilation_error / …).
        internal static Func<object> CompileExpression(string body, string[] usings, Object target, out FieldInfo refsField,
            out ToolDispatchResult refusal)
        {
            refsField = null;
            var type = ExecuteCSharpTool.CompileSnippetType(body, usings, out refusal);
            if (type == null) return null;
            var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (run == null)
            {
                refusal = ToolDispatchResult.Fail("execution_error", "Compiled snippet entry point not found");
                return null;
            }
            refsField = type.GetField("Refs", BindingFlags.Public | BindingFlags.Static);
            refsField?.SetValue(null, target != null ? new[] { target } : null);
            return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), run);
        }
    }

    // Bounded JSON for one sampled value.
    internal static class ProbeValue
    {
        internal const int MaxChars = 512;
        internal const int MaxDepth = 3;
        internal const int MaxItems = 16;
        internal const int MaxStringChars = 256;
        internal const int CountCap = 1000;
        internal const int PreviewChars = 160;

        // `number` is set when the value is a plain number (for the summary).
        internal static string ToJson(object value, out double? number)
        {
            number = null;
            var sb = new StringBuilder(64);
            Append(sb, value, 0);
            if (IsNumber(value)) number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (sb.Length <= MaxChars) return sb.ToString();
            var full = sb.ToString();
            var marker = new StringBuilder(PreviewChars + 96);
            marker.Append("{\"$truncated\":true,\"$type\":").Append(BridgeJson.EscapeString(TypeName(value)));
            marker.Append(",\"$chars\":").Append(full.Length);
            marker.Append(",\"preview\":").Append(BridgeJson.EscapeString(full.Substring(0, PreviewChars) + "…")).Append('}');
            return marker.ToString();
        }

        internal static bool IsNumber(object v)
        {
            switch (v)
            {
                case float f: return !float.IsNaN(f) && !float.IsInfinity(f);
                case double d: return !double.IsNaN(d) && !double.IsInfinity(d);
                case int _: case long _: case short _: case byte _: case uint _:
                case ulong _: case ushort _: case sbyte _: case decimal _:
                    return true;
                default: return false;
            }
        }

        private static string TypeName(object v) => v == null ? "null" : v.GetType().Name;

        private static void Append(StringBuilder sb, object v, int depth)
        {
            if (v == null || (v is Object uo && uo == null)) { sb.Append("null"); return; }
            switch (v)
            {
                case string s: BridgeJson.AppendJsonString(sb, s.Length > MaxStringChars ? s.Substring(0, MaxStringChars) + "…" : s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char c: BridgeJson.AppendJsonString(sb, c.ToString()); return;
                case float f: Real(sb, f, f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: Real(sb, d, d.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: BridgeJson.AppendJsonString(sb, e.ToString()); return;
                case Type t: BridgeJson.AppendJsonString(sb, t.FullName ?? t.Name); return;
                case Vector2 v2: Floats(sb, "x", v2.x, "y", v2.y); return;
                case Vector3 v3: Floats(sb, "x", v3.x, "y", v3.y, "z", v3.z); return;
                case Vector4 v4: Floats(sb, "x", v4.x, "y", v4.y, "z", v4.z, "w", v4.w); return;
                case Quaternion q: Floats(sb, "x", q.x, "y", q.y, "z", q.z, "w", q.w); return;
                case Color col: Floats(sb, "r", col.r, "g", col.g, "b", col.b, "a", col.a); return;
                case Rect r: Floats(sb, "x", r.x, "y", r.y, "width", r.width, "height", r.height); return;
                case Vector2Int i2: sb.Append("{\"x\":").Append(i2.x).Append(",\"y\":").Append(i2.y).Append('}'); return;
                case Vector3Int i3: sb.Append("{\"x\":").Append(i3.x).Append(",\"y\":").Append(i3.y).Append(",\"z\":").Append(i3.z).Append('}'); return;
                case Color32 c32: sb.Append("{\"r\":").Append(c32.r).Append(",\"g\":").Append(c32.g).Append(",\"b\":").Append(c32.b).Append(",\"a\":").Append(c32.a).Append('}'); return;
                case Bounds bounds:
                    sb.Append("{\"center\":"); Append(sb, bounds.center, depth + 1);
                    sb.Append(",\"size\":"); Append(sb, bounds.size, depth + 1);
                    sb.Append('}');
                    return;
                case Object obj:
                    sb.Append("{\"objectId\":").Append(InstanceId.ToJson(obj));
                    sb.Append(",\"type\":").Append(BridgeJson.EscapeString(obj.GetType().Name));
                    sb.Append(",\"name\":").Append(BridgeJson.EscapeString(obj.name)).Append('}');
                    return;
            }
            if (v.GetType().IsPrimitive) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (depth >= MaxDepth) { sb.Append("{\"$type\":").Append(BridgeJson.EscapeString(TypeName(v))).Append(",\"$depth\":true}"); return; }
            var type = v.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                sb.Append("{\"key\":"); Append(sb, type.GetProperty("Key").GetValue(v, null), depth + 1);
                sb.Append(",\"value\":"); Append(sb, type.GetProperty("Value").GetValue(v, null), depth + 1);
                sb.Append('}');
                return;
            }
            if (v is IDictionary dict) { AppendDictionary(sb, dict, depth); return; }
            if (v is IEnumerable items) { AppendList(sb, items, depth); return; }
            AppendFields(sb, v, type, depth);
        }

        private static void Real(StringBuilder sb, double value, string text)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) BridgeJson.AppendJsonString(sb, text);
            else sb.Append(text);
        }

        private static void Floats(StringBuilder sb, params object[] pairs)
        {
            sb.Append('{');
            for (int i = 0; i < pairs.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append((string)pairs[i]).Append("\":");
                var f = (float)pairs[i + 1];
                Real(sb, f, f.ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        private static void AppendDictionary(StringBuilder sb, IDictionary dict, int depth)
        {
            sb.Append('{');
            int n = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (n == MaxItems) break;
                if (n++ > 0) sb.Append(',');
                BridgeJson.AppendJsonString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                sb.Append(':');
                Append(sb, entry.Value, depth + 1);
            }
            if (dict.Count > MaxItems) sb.Append(n > 0 ? "," : "").Append("\"$count\":").Append(dict.Count);
            sb.Append('}');
        }

        private static void AppendList(StringBuilder sb, IEnumerable items, int depth)
        {
            var head = new StringBuilder();
            int n = 0;
            bool more = false;
            foreach (var item in items)
            {
                if (n == MaxItems) { more = true; break; }
                if (n++ > 0) head.Append(',');
                Append(head, item, depth + 1);
            }
            if (!more) { sb.Append('[').Append(head).Append(']'); return; }
            sb.Append("{\"$items\":[").Append(head).Append("],\"$count\":");
            if (items is ICollection collection) sb.Append(collection.Count);
            else
            {
                int count = 0;
                foreach (var _ in items) if (++count > CountCap) break;
                if (count > CountCap) sb.Append('"').Append(CountCap).Append("+\""); else sb.Append(count);
            }
            sb.Append('}');
        }

        // Public instance fields, plus the backing fields of auto-properties
        // under the property's name — never a getter call.
        private static void AppendFields(StringBuilder sb, object v, Type type, int depth)
        {
            if (IsAnonymous(type)) { AppendAnonymous(sb, v, type, depth); return; }
            sb.Append("{\"$type\":").Append(BridgeJson.EscapeString(type.Name));
            int n = 0;
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string name;
                if (f.IsPublic) name = f.Name;
                else if (f.Name.StartsWith("<", StringComparison.Ordinal) && f.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                {
                    name = f.Name.Substring(1, f.Name.IndexOf('>') - 1);
                    var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (p == null || p.GetGetMethod() == null) continue;
                }
                else continue;
                if (f.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                if (n == MaxItems) { sb.Append(",\"$more\":true"); break; }
                n++;
                sb.Append(',');
                BridgeJson.AppendJsonString(sb, name);
                sb.Append(':');
                object value;
                try { value = f.GetValue(v); }
                catch (Exception ex) { BridgeJson.AppendJsonString(sb, "<error: " + ex.GetType().Name + ">"); continue; }
                Append(sb, value, depth + 1);
            }
            sb.Append('}');
        }

        // `new { hp = …, pos = … }`: several values in one sample. Its
        // properties are compiler-generated reads of its own fields.
        private static bool IsAnonymous(Type type) =>
            type.Name.Contains("AnonymousType") && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

        private static void AppendAnonymous(StringBuilder sb, object v, Type type, int depth)
        {
            sb.Append('{');
            int n = 0;
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (n == MaxItems) { sb.Append(",\"$more\":true"); break; }
                if (n++ > 0) sb.Append(',');
                BridgeJson.AppendJsonString(sb, p.Name);
                sb.Append(':');
                Append(sb, p.GetValue(v, null), depth + 1);
            }
            sb.Append('}');
        }
    }
}
