// Unified tools catalog model for the bridge window Tools tab.
//
// Combines hardcoded meta-tools (the KnownTools set in BridgeHttpServer) with
// registry-discovered typed tools (BridgeToolRegistry) into a single read-only
// list that the UI can render. The catalog intentionally surfaces only the
// metadata an agent (or operator) needs to decide what to call:
//
//   - name, title, source (hardcoded vs registry)
//   - mutating / read-only
//   - gate hints (enforce / warn / off / n/a)
//   - read-only / idempotent / destructive hints
//   - parameter summaries (name : type) derived from attribute or
//     hand-authored metadata
//
// Token estimate is deliberately omitted in v1 per questions-9 Q11.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UnityAgentBridge
{
    public enum BridgeToolSource
    {
        Hardcoded,
        Registry
    }

    public enum BridgeToolMutability
    {
        ReadOnly,
        Mutating
    }

    public class BridgeToolParameterSummary
    {
        public string Name;
        public string TypeName;
        public string Description;
        public bool HasDefault;
        public object DefaultValue;

        public string Display
        {
            get
            {
                if (HasDefault)
                    return $"{Name}: {TypeName} = {FormatDefault(DefaultValue)}";
                return $"{Name}: {TypeName}";
            }
        }

        static string FormatDefault(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{s}\"";
            return value.ToString();
        }
    }

    public class BridgeToolCatalogItem
    {
        public string Name;
        public string Title;
        public BridgeToolSource Source;
        public BridgeToolMutability Mutability;
        public string GateMode;          // "enforce" / "warn" / "off" / "n/a"
        public bool ReadOnlyHint;
        public bool IdempotentHint;
        public bool DestructiveHint;
        public string DeclaringTypeName; // for registry tools
        public List<BridgeToolParameterSummary> Parameters = new List<BridgeToolParameterSummary>();
    }

    public static class BridgeToolCatalog
    {
        // Hardcoded meta-tools that BridgeHttpServer dispatches directly.
        // Mirrors the KnownTools set in BridgeHttpServer.cs so the catalog stays
        // honest about what is actually dispatchable in the current session.
        static readonly (string Name, string Title, bool IsMutating, string Gate)[] HardcodedTools =
        {
            ("unity_agent_execute_csharp", "Execute C#", true,  "enforce"),
            ("unity_agent_invoke_method",  "Invoke Method", true,  "enforce"),
            ("unity_agent_execute_menu",   "Execute Menu", true,   "enforce"),
            ("unity_agent_find_members",   "Find Members", false, "n/a"),
            ("unity_agent_validate_edit",  "Validate Edit", false, "n/a"),
            ("unity_agent_checkpoint_create", "Checkpoint Create", false, "n/a"),
            ("unity_agent_delta",          "Delta", false, "n/a"),
            ("unity_agent_find_references","Find References", false, "n/a"),
            ("unity_agent_scan_paths",     "Scan Paths", false, "n/a"),
            ("unity_agent_apply_fix",      "Apply Fix", true, "enforce"),
        };

        public static List<BridgeToolCatalogItem> Build()
        {
            var items = new List<BridgeToolCatalogItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var hc in HardcodedTools)
            {
                items.Add(new BridgeToolCatalogItem
                {
                    Name = hc.Name,
                    Title = hc.Title,
                    Source = BridgeToolSource.Hardcoded,
                    Mutability = hc.IsMutating ? BridgeToolMutability.Mutating : BridgeToolMutability.ReadOnly,
                    GateMode = hc.Gate,
                    ReadOnlyHint = !hc.IsMutating,
                    IdempotentHint = false,
                    DestructiveHint = false,
                    DeclaringTypeName = null,
                    Parameters = HardcodedParameterSummary(hc.Name)
                });
                seen.Add(hc.Name);
            }

            try
            {
                foreach (var entry in BridgeToolRegistry.All())
                {
                    if (entry == null) continue;
                    if (!seen.Add(entry.Name)) continue;

                    items.Add(new BridgeToolCatalogItem
                    {
                        Name = entry.Name,
                        Title = entry.Title,
                        Source = BridgeToolSource.Registry,
                        Mutability = entry.IsMutating ? BridgeToolMutability.Mutating : BridgeToolMutability.ReadOnly,
                        GateMode = entry.IsMutating ? GateModeToString(entry.Gate) : "n/a",
                        ReadOnlyHint = entry.ReadOnlyHint || !entry.IsMutating,
                        IdempotentHint = entry.IdempotentHint,
                        DestructiveHint = entry.DestructiveHint,
                        DeclaringTypeName = entry.DeclaringType != null ? entry.DeclaringType.FullName : null,
                        Parameters = RegistryParameterSummary(entry)
                    });
                }
            }
            catch (Exception)
            {
                // Registry may not be initialized yet (e.g. very early domain reload).
                // Skip registry enumeration; hardcoded list is still complete.
            }

            items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return items;
        }

        public static int CountEnabled(IReadOnlyList<BridgeToolCatalogItem> items)
        {
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!BridgeToolTogglePolicy.IsDisabled(items[i].Name)) n++;
            }
            return n;
        }

        public static int CountDisabled(IReadOnlyList<BridgeToolCatalogItem> items)
        {
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (BridgeToolTogglePolicy.IsDisabled(items[i].Name)) n++;
            }
            return n;
        }

        public static string FormatParameterList(BridgeToolCatalogItem item)
        {
            if (item == null || item.Parameters == null || item.Parameters.Count == 0)
                return "(no parameters)";
            return string.Join(", ", item.Parameters.ConvertAll(p => p.Display));
        }

        static string GateModeToString(GateMode mode)
        {
            return mode switch
            {
                GateMode.Enforce => "enforce",
                GateMode.Warn => "warn",
                GateMode.Off => "off",
                _ => "enforce"
            };
        }

        static List<BridgeToolParameterSummary> RegistryParameterSummary(BridgeToolEntry entry)
        {
            var list = new List<BridgeToolParameterSummary>();
            if (entry?.Parameters == null) return list;

            foreach (var p in entry.Parameters)
            {
                if (p == null) continue;
                list.Add(new BridgeToolParameterSummary
                {
                    Name = p.Name,
                    TypeName = FriendlyTypeName(p.ParameterType),
                    Description = entry.GetParameterDescription(p),
                    HasDefault = p.HasDefaultValue,
                    DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
                });
            }
            return list;
        }

        static List<BridgeToolParameterSummary> HardcodedParameterSummary(string toolName)
        {
            // Mirror the input schemas in specs/architecture/mcp-tools.md so the
            // Tools tab metadata matches what agents actually send. Keep this list
            // in sync with the schema when tools evolve.
            switch (toolName)
            {
                case "unity_agent_execute_csharp":
                    return P("code: string", "usings: string[]", "paths_hint: string[]", "gate: string = \"enforce\"", "timeout_ms: int = 30000");
                case "unity_agent_invoke_method":
                    return P("type_name: string", "method_name: string", "args: object[]", "is_static: bool = false", "assembly_name: string", "paths_hint: string[]", "gate: string = \"enforce\"", "timeout_ms: int = 30000");
                case "unity_agent_execute_menu":
                    return P("menu_path: string", "paths_hint: string[]", "gate: string = \"enforce\"");
                case "unity_agent_find_members":
                    return P("query: string", "kind: string = \"all\"", "assembly_filter: string", "include_unity_editor: bool = true", "include_project: bool = true", "max_results: int = 50");
                case "unity_agent_validate_edit":
                    return P("paths: string[]", "categories: string[]", "platform_profile: string = \"desktop\"", "detail: string = \"normal\"");
                case "unity_agent_checkpoint_create":
                    return P("paths: string[]", "label: string");
                case "unity_agent_delta":
                    return P("checkpoint_id: string", "paths: string[]");
                case "unity_agent_find_references":
                    return P("asset_path: string", "guid: string", "detail: string = \"normal\"", "max_results: int = 100");
                case "unity_agent_scan_paths":
                    return P("paths: string[]", "categories: string[]", "platform_profile: string", "fail_on_severity: string = \"never\"");
                case "unity_agent_apply_fix":
                    return P("fix_id: string", "issue_id: string", "dry_run: bool = true", "gate: string = \"enforce\"");
                default:
                    return new List<BridgeToolParameterSummary>();
            }
        }

        static List<BridgeToolParameterSummary> P(params string[] specs)
        {
            var list = new List<BridgeToolParameterSummary>(specs.Length);
            foreach (var s in specs)
            {
                var split = s.Split(new[] { ':' }, 2);
                var name = split[0].Trim();
                var rest = split.Length > 1 ? split[1].Trim() : "object";
                string typeName = rest;
                object defaultValue = null;
                bool hasDefault = false;
                var eqIdx = rest.IndexOf('=');
                if (eqIdx >= 0)
                {
                    typeName = rest.Substring(0, eqIdx).Trim();
                    var def = rest.Substring(eqIdx + 1).Trim();
                    hasDefault = true;
                    defaultValue = def;
                }
                list.Add(new BridgeToolParameterSummary
                {
                    Name = name,
                    TypeName = typeName,
                    HasDefault = hasDefault,
                    DefaultValue = defaultValue
                });
            }
            return list;
        }

        static string FriendlyTypeName(Type t)
        {
            if (t == null) return "object";
            if (t == typeof(string)) return "string";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(long)) return "long";
            if (t == typeof(double)) return "double";
            if (t.IsArray)
                return FriendlyTypeName(t.GetElementType()) + "[]";
            if (t.IsEnum) return t.Name;
            if (Nullable.GetUnderlyingType(t) is Type inner)
                return FriendlyTypeName(inner) + "?";
            return t.Name;
        }
    }
}
