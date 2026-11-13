using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityOpenMcpBridge
{
    // execute_csharp compile failures from guessed names. Most snippet compile
    // errors are a type in the wrong namespace (CS0234 / CS0246) or a member
    // that is named differently (CS1061 / CS0117). Both are a find_members
    // lookup away, so do that lookup for the caller: name the loaded types whose
    // simple name matches, and the closest member names on the type.
    internal static class SnippetDiagnosticHints
    {
        private const int MaxNames = 6;
        private const int MaxCandidates = 5;

        // CS0234: The type or namespace name 'X' does not exist in the namespace 'N' (…)
        // CS0246: The type or namespace name 'X' could not be found (…)
        private static readonly Regex MissingType = new Regex(
            @"error CS(?:0234|0246): The type or namespace name '(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:<[^']*>)?'",
            RegexOptions.CultureInvariant);

        // CS1061: 'T' does not contain a definition for 'M' and no accessible extension method …
        // CS0117: 'T' does not contain a definition for 'M'
        private static readonly Regex MissingMember = new Regex(
            @"error CS(?:1061|0117): '(?<type>[^']+)' does not contain a definition for '(?<member>[A-Za-z_][A-Za-z0-9_]*)'",
            RegexOptions.CultureInvariant);

        internal sealed class Hints
        {
            internal readonly List<KeyValuePair<string, List<string>>> Types = new List<KeyValuePair<string, List<string>>>();
            internal readonly List<(string Type, string Member, List<string> Closest)> Members = new List<(string, string, List<string>)>();
            internal bool Any => Types.Count > 0 || Members.Count > 0;
        }

        /// <summary>
        /// Hints for the diagnostics in <paramref name="errors"/>; empty when nothing matched.
        /// <paramref name="code"/> (the snippet) resolves a qualified guess the compiler
        /// reported only by its first segment: CS0246 'Game' for Game.Wrong.PlayerView.
        /// </summary>
        internal static Hints Build(string errors, IEnumerable<Type> loadedTypes = null, string code = null)
        {
            var hints = new Hints();
            if (string.IsNullOrEmpty(errors)) return hints;
            var typeNames = MissingType.Matches(errors).Cast<Match>().Select(m => m.Groups["name"].Value).Distinct().Take(MaxNames).ToList();
            var members = MissingMember.Matches(errors).Cast<Match>()
                .Select(m => (Type: m.Groups["type"].Value, Member: m.Groups["member"].Value)).Distinct().Take(MaxNames).ToList();
            if (typeNames.Count == 0 && members.Count == 0) return hints;

            var types = (loadedTypes ?? AllLoadedTypes()).ToList();
            foreach (var name in typeNames)
            {
                var candidates = Candidates(types, name);
                var label = name;
                if (candidates.Count == 0 && !string.IsNullOrEmpty(code))
                {
                    // The name may be the first segment of a qualified guess; try
                    // the segments after it, last first (the type is usually last).
                    var qualified = Regex.Match(code, @"\b" + Regex.Escape(name) + @"(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)+");
                    if (qualified.Success)
                    {
                        var segments = Regex.Split(qualified.Value, @"\s*\.\s*");
                        for (int i = segments.Length - 1; i > 0 && candidates.Count == 0; i--)
                            candidates = Candidates(types, segments[i]);
                        if (candidates.Count > 0) label = string.Join(".", segments);
                    }
                }
                hints.Types.Add(new KeyValuePair<string, List<string>>(label, candidates));
            }
            foreach (var (typeName, member) in members)
            {
                var owner = ResolveType(types, typeName);
                if (owner == null) continue;
                var closest = ClosestMembers(owner, member);
                if (closest.Count > 0) hints.Members.Add((DisplayName(owner), member, closest));
            }
            return hints;
        }

        /// <summary>The compile error text with a "did you mean" section appended (unchanged when there is no hint).</summary>
        internal static string AppendTo(string errors, Hints hints)
        {
            if (hints == null || !hints.Any) return errors;
            var sb = new StringBuilder(errors ?? "");
            sb.Append("\n\nDID YOU MEAN — ");
            foreach (var entry in hints.Types)
            {
                sb.Append("\n  '").Append(entry.Key).Append("': ");
                sb.Append(entry.Value.Count > 0
                    ? string.Join(", ", entry.Value) + " — use the full name (snippets get no project usings)."
                    : "no loaded type has this name; search with unity_open_mcp_find_members {\"query\":\"" + entry.Key + "\"}.");
            }
            foreach (var (type, member, closest) in hints.Members)
                sb.Append("\n  ").Append(type).Append('.').Append(member).Append(": closest members ").Append(string.Join(", ", closest)).Append('.');
            return sb.ToString();
        }

        /// <summary>The same hints as a JSON object for the error envelope's detail, or null.</summary>
        internal static string DetailJson(Hints hints)
        {
            if (hints == null || !hints.Any) return null;
            var sb = new StringBuilder(256);
            sb.Append("{\"didYouMean\":{\"types\":[");
            for (int i = 0; i < hints.Types.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(BridgeJson.EscapeString(hints.Types[i].Key)).Append(",\"candidates\":[");
                sb.Append(string.Join(",", hints.Types[i].Value.Select(BridgeJson.EscapeString))).Append("]}");
            }
            sb.Append("],\"members\":[");
            for (int i = 0; i < hints.Members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var (type, member, closest) = hints.Members[i];
                sb.Append("{\"type\":").Append(BridgeJson.EscapeString(type));
                sb.Append(",\"member\":").Append(BridgeJson.EscapeString(member));
                sb.Append(",\"closest\":[").Append(string.Join(",", closest.Select(BridgeJson.EscapeString))).Append("]}");
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        private static List<string> Candidates(List<Type> types, string simpleName) => types
            .Where(t => string.Equals(SimpleName(t), simpleName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => IsFrameworkType(t) ? 1 : 0).ThenBy(t => DisplayName(t), StringComparer.Ordinal)
            .Select(t => DisplayName(t) + " (" + t.Assembly.GetName().Name + ")")
            .Distinct().Take(MaxCandidates).ToList();

        private static IEnumerable<Type> AllLoadedTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var type in types)
                    if (type != null && !type.Name.StartsWith("<", StringComparison.Ordinal)) yield return type;
            }
        }

        private static string SimpleName(Type type)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        private static string DisplayName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

        private static bool IsFrameworkType(Type type)
        {
            var ns = type.Namespace ?? "";
            return ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Unity", StringComparison.Ordinal)
                || ns.StartsWith("Mono", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal);
        }

        // The compiler prints the type as written in the snippet, fully or partly
        // qualified; generic arguments are dropped for the lookup.
        private static Type ResolveType(List<Type> types, string printed)
        {
            var name = printed;
            var angle = name.IndexOf('<');
            if (angle >= 0) name = name.Substring(0, angle);
            var exact = types.FirstOrDefault(t => DisplayName(t) == name);
            if (exact != null) return exact;
            var simple = name.Substring(name.LastIndexOf('.') + 1);
            var bySimple = types.Where(t => SimpleName(t) == simple).ToList();
            return bySimple.Count == 1 ? bySimple[0] : bySimple.FirstOrDefault(t => DisplayName(t).EndsWith("." + name, StringComparison.Ordinal));
        }

        // Project types list non-public members too (marked; reached by
        // reflection or invoke_method); engine and BCL types only their public
        // surface — their internals (*_Injected bindings) are noise.
        private static List<string> ClosestMembers(Type type, string wanted)
        {
            var all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            if (!IsFrameworkType(type)) all |= BindingFlags.NonPublic;
            MemberInfo[] members;
            try { members = type.GetMembers(all); }
            catch { return new List<string>(); }
            var lower = wanted.ToLowerInvariant();
            return members
                .Where(m => !m.Name.StartsWith("<", StringComparison.Ordinal) && !m.Name.StartsWith("get_", StringComparison.Ordinal)
                    && !m.Name.StartsWith("set_", StringComparison.Ordinal) && !m.Name.StartsWith("add_", StringComparison.Ordinal)
                    && !m.Name.StartsWith("remove_", StringComparison.Ordinal) && m.MemberType != MemberTypes.Constructor)
                .Select(m => (Name: m.Name, Public: IsPublic(m)))
                .GroupBy(m => m.Name).Select(g => (Name: g.Key, Public: g.Any(x => x.Public)))
                .Select(m => (m.Name, m.Public, Score: Score(m.Name.ToLowerInvariant(), lower)))
                .Where(m => m.Score <= Math.Max(2, lower.Length / 3))
                .OrderBy(m => m.Score).ThenBy(m => m.Name, StringComparer.Ordinal)
                .Take(MaxCandidates)
                .Select(m => m.Public ? m.Name : m.Name + " (non-public)")
                .ToList();
        }

        private static bool IsPublic(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method: return method.IsPublic;
                case FieldInfo field: return field.IsPublic;
                case PropertyInfo property: return (property.GetGetMethod() ?? property.GetSetMethod()) != null;
                case EventInfo e: return e.GetAddMethod() != null;
                case Type nested: return nested.IsNestedPublic;
                default: return false;
            }
        }

        // Containment counts as distance 1 (Count vs CountAll); otherwise edit distance.
        private static int Score(string candidate, string wanted)
        {
            if (candidate == wanted) return 0;
            if (candidate.Contains(wanted) || wanted.Contains(candidate)) return 1;
            var previous = new int[wanted.Length + 1];
            for (int j = 0; j <= wanted.Length; j++) previous[j] = j;
            for (int i = 1; i <= candidate.Length; i++)
            {
                var current = new int[wanted.Length + 1];
                current[0] = i;
                for (int j = 1; j <= wanted.Length; j++)
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + (candidate[i - 1] == wanted[j - 1] ? 0 : 1));
                previous = current;
            }
            return previous[wanted.Length];
        }
    }
}
