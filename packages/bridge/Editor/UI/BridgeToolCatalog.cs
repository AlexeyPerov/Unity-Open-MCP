using System;
using System.Collections.Generic;

namespace UnityOpenMcpBridge
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

        private static string FormatDefault(object value)
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
        public LifecyclePolicy Lifecycle; // M13 T4.1 — settle/retry policy
        public string DeclaringTypeName; // for registry tools
        public List<BridgeToolParameterSummary> Parameters = new List<BridgeToolParameterSummary>();
        // Per-tool token estimate (chars/4 over the tool's MCP wire JSON),
        // sourced from the generated BridgeToolTokenEstimates table. Null when
        // the catalog surfaced a tool the codegen did not see (defensive —
        // renders "~?" in the UI). See scripts/generate-token-estimates.mjs.
        public int? TokenEstimate;
        // The tool's group id from the canonical MCP catalog
        // (mcp-server/src/capabilities/tool-groups.ts). Null for always-visible
        // meta-tools (capabilities, ping, manage_tools, …). Mirrored into the
        // generated table so the Tools tab can render per-group token subtotals
        // without a second hand-maintained mapping.
        public string Group;
    }

    public static class BridgeToolCatalog
    {
        // Hardcoded meta-tools that BridgeHttpServer dispatches directly.
        // Mirrors the KnownTools set in BridgeHttpServer.cs so the catalog stays
        // honest about what is actually dispatchable in the current session.
        private static readonly (string Name, string Title, bool IsMutating, string Gate)[] HardcodedTools =
        {
            ("unity_open_mcp_execute_csharp", "Execute C#", true,  "enforce"),
            ("unity_open_mcp_invoke_method",  "Invoke Method", true,  "enforce"),
            ("unity_open_mcp_execute_menu",   "Execute Menu", true,   "enforce"),
            ("unity_open_mcp_find_members",   "Find Members", false, "n/a"),
            ("unity_open_mcp_validate_edit",  "Validate Edit", false, "n/a"),
            ("unity_open_mcp_checkpoint_create", "Checkpoint Create", false, "n/a"),
            ("unity_open_mcp_delta",          "Delta", false, "n/a"),
            ("unity_open_mcp_find_references","Find References", false, "n/a"),
            ("unity_open_mcp_scan_paths",     "Scan Paths", false, "n/a"),
            ("unity_open_mcp_apply_fix",      "Apply Fix", true, "enforce"),
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
                        Lifecycle = ToolLifecycle.Resolve(hc.Name),
                        DeclaringTypeName = null,
                        Parameters = HardcodedParameterSummary(hc.Name),
                        TokenEstimate = BridgeToolTokenEstimates.EstimateFor(hc.Name),
                        Group = BridgeToolTokenEstimates.GroupFor(hc.Name),
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
                        Lifecycle = entry.Lifecycle,
                        DeclaringTypeName = entry.DeclaringType?.FullName,
                        Parameters = RegistryParameterSummary(entry),
                        TokenEstimate = BridgeToolTokenEstimates.EstimateFor(entry.Name),
                        Group = BridgeToolTokenEstimates.GroupFor(entry.Name),
                    });
                }
            }
            catch (Exception)
            {
                // Registry may not be initialized yet (e.g. very early domain reload).
                // Skip registry enumeration; hardcoded list is still complete.
            }

            // The hardcoded meta-tools above cover only the 10 dispatcher
            // entry points that mirror their input schemas here. The remaining
            // ~90 typed tools (gameobject/scene/component/material/prefab/
            // package/build/settings/profiler/...) are dispatched by the
            // hardcoded switch in BridgeHttpServer.DispatchTool and carry no
            // [BridgeTool] attribute, so they never enter BridgeToolRegistry.
            // Without this pass the Tools tab silently hid them even though
            // GET /tools (HandleToolsList) reports them via KnownTools. This
            // unions KnownTools into the catalog so the tab matches what the
            // bridge actually dispatches — same source of truth the MCP client
            // sees. Parameter schemas live server-side (mcp-server/src/tools)
            // and are intentionally not mirrored in C#; the catalog shows the
            // tool name, title, mutability, and gate mode only.
            try
            {
                foreach (var name in BridgeToolClassification.KnownTools)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!seen.Add(name)) continue;

                    bool isMutating = BridgeToolClassification.MutatingTools.Contains(name);
                    items.Add(new BridgeToolCatalogItem
                    {
                        Name = name,
                        Title = SynthesizeTitle(name),
                        Source = BridgeToolSource.Hardcoded,
                        Mutability = isMutating ? BridgeToolMutability.Mutating : BridgeToolMutability.ReadOnly,
                        GateMode = isMutating ? "enforce" : "n/a",
                        ReadOnlyHint = !isMutating,
                        IdempotentHint = false,
                        DestructiveHint = false,
                        Lifecycle = ToolLifecycle.Resolve(name),
                        DeclaringTypeName = null,
                        Parameters = HardcodedParameterSummary(name),
                        TokenEstimate = BridgeToolTokenEstimates.EstimateFor(name),
                        Group = BridgeToolTokenEstimates.GroupFor(name),
                    });
                }
            }
            catch (Exception)
            {
                // BridgeToolClassification is a static table; this only fails
                // if the bridge assembly failed to load. The already-collected
                // hardcoded + registry entries remain valid.
            }

            items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return items;
        }

        public static int CountEnabled(IReadOnlyList<BridgeToolCatalogItem> items)
        {
            if (items == null) return 0;
            var n = 0;
            foreach (var t in items)
            {
                if (!BridgeToolTogglePolicy.IsDisabled(t.Name))
                    n++;
            }
            return n;
        }

        // Sum the token estimates of every ENABLED tool. This is the headline
        // "active tokens" number the Tools tab renders in its header — it is
        // recomputed each frame from the live toggle policy, so disabling a
        // tool or group drops its tokens from the total immediately. Tools with
        // no estimate (TokenEstimate == null — a tool the codegen table did not
        // cover) contribute 0 so the total never goes negative.
        public static int SumEnabledTokens(IReadOnlyList<BridgeToolCatalogItem> items)
        {
            if (items == null) return 0;
            var total = 0;
            foreach (var t in items)
            {
                if (BridgeToolTogglePolicy.IsDisabled(t.Name)) continue;
                if (t.TokenEstimate.HasValue) total += t.TokenEstimate.Value;
            }
            return total;
        }

        // One row of the per-group token summary. `ActiveTokens` counts only
        // enabled tools in the group; `TotalTokens` counts every tool in the
        // group regardless of toggle state (so the operator can see the full
        // group cost vs what is currently active).
        public struct GroupTokenSummary
        {
            public string Group;
            public int ToolCount;
            public int ActiveToolCount;
            public int ActiveTokens;
            public int TotalTokens;
        }

        // Build the per-group token summary, ordered by group id for a stable
        // render. Tools with a null Group (always-visible meta-tools) collapse
        // into a synthetic "(always visible)" bucket so they still surface in
        // the summary — their tokens are part of the catalog total.
        public static List<GroupTokenSummary> GroupTokenSummaries(IReadOnlyList<BridgeToolCatalogItem> items)
        {
            var byGroup = new Dictionary<string, GroupTokenSummary>(StringComparer.Ordinal);
            if (items != null)
            {
                foreach (var t in items)
                {
                    var g = t.Group ?? "(always visible)";
                    if (!byGroup.TryGetValue(g, out var s))
                    {
                        s = new GroupTokenSummary { Group = g };
                    }
                    s.ToolCount++;
                    var enabled = !BridgeToolTogglePolicy.IsDisabled(t.Name);
                    if (enabled) s.ActiveToolCount++;
                    var tokens = t.TokenEstimate ?? 0;
                    if (enabled) s.ActiveTokens += tokens;
                    s.TotalTokens += tokens;
                    byGroup[g] = s;
                }
            }
            var list = new List<GroupTokenSummary>(byGroup.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Group, b.Group));
            return list;
        }

        // Return the tool names that belong to a group id. The synthetic
        // "(always visible)" bucket (used by GroupTokenSummaries for tools with
        // a null Group) is matched when `group` is that exact string OR null/
        // empty — so callers passing the summary's Group field round-trip. Used
        // by the Tools tab's per-group bulk enable/disable (M29 Plan 5).
        public static List<string> ToolNamesForGroup(IReadOnlyList<BridgeToolCatalogItem> items, string group)
        {
            var result = new List<string>();
            if (items == null) return result;
            var matchAlwaysVisible = string.IsNullOrEmpty(group) || group == "(always visible)";
            foreach (var t in items)
            {
                if (t == null) continue;
                var g = t.Group ?? "(always visible)";
                if (matchAlwaysVisible && string.IsNullOrEmpty(t.Group))
                {
                    result.Add(t.Name);
                    continue;
                }
                if (g == group) result.Add(t.Name);
            }
            return result;
        }

        public static string FormatParameterList(BridgeToolCatalogItem item)
        {
            if (item == null || item.Parameters == null || item.Parameters.Count == 0)
                return "(no parameters)";
            return string.Join(", ", item.Parameters.ConvertAll(p => p.Display));
        }

        private static string GateModeToString(GateMode mode)
        {
            return mode switch
            {
                GateMode.Enforce => "enforce",
                GateMode.Warn => "warn",
                GateMode.Off => "off",
                _ => "enforce"
            };
        }

        // Display-only title derived from the tool id for the KnownTools pass.
        // Strips the unity_open_mcp_ / unity_senses_ prefix and Title-Cases the
        // remainder (e.g. unity_open_mcp_gameobject_create -> "Gameobject
        // Create"). The canonical human titles live in the MCP server tool
        // definitions; this is just a readable label for the Editor window.
        private static string SynthesizeTitle(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return toolName;
            var rest = toolName;
            if (rest.StartsWith("unity_open_mcp_")) rest = rest.Substring("unity_open_mcp_".Length);
            else if (rest.StartsWith("unity_senses_")) rest = rest.Substring("unity_senses_".Length);

            if (string.IsNullOrEmpty(rest)) return toolName;
            var sb = new System.Text.StringBuilder(rest.Length);
            bool capitalizeNext = true;
            foreach (var c in rest)
            {
                if (c == '_')
                {
                    sb.Append(' ');
                    capitalizeNext = true;
                }
                else if (capitalizeNext)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    capitalizeNext = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static List<BridgeToolParameterSummary> RegistryParameterSummary(BridgeToolEntry entry)
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

        private static List<BridgeToolParameterSummary> HardcodedParameterSummary(string toolName)
        {
            // Mirror the input schemas in specs/architecture/mcp-tools.md so the
            // Tools tab metadata matches what agents actually send. Keep this list
            // in sync with the schema when tools evolve.
            switch (toolName)
            {
                case "unity_open_mcp_execute_csharp":
                    return P("code: string", "usings: string[]", "paths_hint: string[]", "gate: string = \"enforce\"", "timeout_ms: int = 30000");
                case "unity_open_mcp_invoke_method":
                    return P("type_name: string", "method_name: string", "args: object[]", "is_static: bool = false", "assembly_name: string", "paths_hint: string[]", "gate: string = \"enforce\"", "timeout_ms: int = 30000");
                case "unity_open_mcp_execute_menu":
                    return P("menu_path: string", "paths_hint: string[]", "gate: string = \"enforce\"");
                case "unity_open_mcp_find_members":
                    return P("query: string", "kind: string = \"all\"", "assembly_filter: string", "include_unity_editor: bool = true", "include_project: bool = true", "max_results: int = 50");
                case "unity_open_mcp_validate_edit":
                    return P("paths: string[]", "categories: string[]", "platform_profile: string = \"desktop\"", "detail: string = \"normal\"");
                case "unity_open_mcp_checkpoint_create":
                    return P("paths: string[]", "label: string");
                case "unity_open_mcp_delta":
                    return P("checkpoint_id: string", "paths: string[]");
                case "unity_open_mcp_find_references":
                    return P("asset_path: string", "guid: string", "detail: string = \"normal\"", "max_results: int = 100");
                case "unity_open_mcp_scan_paths":
                    return P("paths: string[]", "categories: string[]", "platform_profile: string", "fail_on_severity: string = \"never\"");
                case "unity_open_mcp_apply_fix":
                    return P("fix_id: string", "issue_id: string", "dry_run: bool = true", "gate: string = \"enforce\"");
                default:
                    return new List<BridgeToolParameterSummary>();
            }
        }

        private static List<BridgeToolParameterSummary> P(params string[] specs)
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

        private static string FriendlyTypeName(Type t)
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
            if (Nullable.GetUnderlyingType(t) is { } inner)
                return FriendlyTypeName(inner) + "?";
            return t.Name;
        }
    }
}
