using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.UI.Controls;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge
{
    public partial class UnityOpenMcpBridgeWindow
    {
        // ---------- Tools tab (M4.5-4/5/6) ----------

        // Token-estimate tooltip — shared across the header total, the per-group
        // summary, and the per-tool chip so the figure reads consistently. The
        // estimate is regenerated from the MCP-server tool schemas by
        // scripts/generate-token-estimates.mjs (no hand-maintained list).
        private const string TooltipTokenEstimate =
            "Estimated tokens this tool contributes to the AI context window. " +
            "Computed from the tool's MCP wire JSON (name + description + input schema) " +
            "via a chars/4 heuristic — the value is an estimate for relative cost, not an exact count. " +
            "Disable a tool (or its group) to drop its tokens from the active total.";
        private const string TooltipTokenTotal =
            "Estimated tokens contributed by all ENABLED tools combined — the headline context-window cost " +
            "of the active tool set an agent will see when it connects. Recomputed live as you toggle tools/groups.";

        // Per-group token summary foldout (collapsed by default — the headline
        // number lives in the filters header; this is the breakdown).
        [NonSerialized] private bool _toolGroupTokensFoldout = false;
        [NonSerialized] private Vector2 _toolGroupTokensScroll;

        private void DrawToolsTab()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tools catalog", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unified list of dispatchable tools in this Editor session. " +
                "Untoggle a tool to block its HTTP dispatch path with an explicit `tool_disabled` error. " +
                "Disable state persists in `.unity-open-mcp/settings.json` and survives domain reload. " +
                "Each row shows an estimated token cost (chars/4 over the tool's MCP schema); " +
                "the header reports the active-set total.",
                MessageType.None);

            var items = BridgeToolCatalog.Build();
            var filtered = BuildFilteredToolList(items);
            DrawToolFilters(items, filtered);
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawToolGroupTokenSummary(items);
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawToolList(filtered);
        }

        // Per-group token breakdown. Collapsed by default (the headline active
        // total lives in DrawToolFilters); expanding shows one row per group
        // with its active vs total token cost so the operator can see which
        // groups dominate the context budget.
        private void DrawToolGroupTokenSummary(List<BridgeToolCatalogItem> items)
        {
            var summaries = BridgeToolCatalog.GroupTokenSummaries(items);
            if (summaries == null || summaries.Count == 0) return;

            _toolGroupTokensFoldout = EditorGUILayout.Foldout(
                _toolGroupTokensFoldout,
                $"Per-group token estimate ({summaries.Count} groups)",
                true);
            if (!_toolGroupTokensFoldout) return;

            _toolGroupTokensScroll = EditorGUILayout.BeginScrollView(
                _toolGroupTokensScroll, GUILayout.MaxHeight(160));
            foreach (var s in summaries)
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel(s.Group, null, 150);
                var activeFormatted = BridgeToolTokenEstimates.Format(s.ActiveTokens);
                var totalFormatted = BridgeToolTokenEstimates.Format(s.TotalTokens);
                GUILayout.Label(
                    new GUIContent(
                        $"~{activeFormatted} active  /  ~{totalFormatted} total  ({s.ActiveToolCount}/{s.ToolCount} tools)",
                        TooltipTokenEstimate),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        // Apply the current filter + search once per frame and return the
        // already-narrowed list. DrawToolList then paginates this result so the
        // page count reflects exactly what the operator sees.
        private List<BridgeToolCatalogItem> BuildFilteredToolList(List<BridgeToolCatalogItem> items)
        {
            var result = new List<BridgeToolCatalogItem>();
            if (items == null) return result;

            var search = (_toolSearch ?? "").Trim();
            var hasSearch = !string.IsNullOrEmpty(search);
            foreach (var item in items)
            {
                if (item == null) continue;
                if (!PassesFilter(item)) continue;
                if (hasSearch && !MatchesSearch(item, search)) continue;
                result.Add(item);
            }
            return result;
        }

        private void DrawToolFilters(List<BridgeToolCatalogItem> allItems, List<BridgeToolCatalogItem> filtered)
        {
            int total = allItems?.Count ?? 0;
            int enabled = BridgeToolCatalog.CountEnabled(allItems);
            int disabled = total - enabled;
            int activeTokens = BridgeToolCatalog.SumEnabledTokens(allItems);
            var activeTokensLabel = BridgeToolTokenEstimates.Format(activeTokens);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"Total: {total}    Enabled: {enabled}    Disabled: {disabled}",
                EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            // Headline active-token total — the number an operator acts on. It
            // is recomputed every frame from the live toggle policy, so toggling
            // a tool or group updates it immediately.
            EditorGUILayout.LabelField(
                new GUIContent($"Active tokens: ~{activeTokensLabel}", TooltipTokenTotal),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            var prev = GUI.color;
            _toolFilter = DrawFilterButton(ToolFilterMode.All, "All", _toolFilter);
            _toolFilter = DrawFilterButton(ToolFilterMode.Enabled, $"Enabled ({enabled})", _toolFilter);
            _toolFilter = DrawFilterButton(ToolFilterMode.Disabled, $"Disabled ({disabled})", _toolFilter);
            GUI.color = prev;

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField(new GUIContent("Search", "Filter the list by tool name, title, declaring type, or parameter name/type."), GUILayout.Width(50));
            var newSearch = EditorGUILayout.TextField(_toolSearch ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(180));
            if (newSearch != _toolSearch)
            {
                _toolSearch = newSearch ?? "";
                _toolPageToShow = null;
            }
            if (GUILayout.Button(new GUIContent("Clear", "Clear the search box."), EditorStyles.miniButton, GUILayout.Width(48)))
            {
                _toolSearch = "";
                _toolPageToShow = null;
            }

            if (GUILayout.Button(new GUIContent("Enable all", "Re-enable every tool (clears the disabled-tool list in settings.json)."), EditorStyles.miniButton, GUILayout.Width(78)) && disabled > 0)
            {
                BridgeToolTogglePolicy.Clear();
            }

            EditorGUILayout.EndHorizontal();
        }

        private ToolFilterMode DrawFilterButton(ToolFilterMode mode, string label, ToolFilterMode current)
        {
            var isCurrent = mode == current;
            var prev = GUI.color;
            if (isCurrent) GUI.color = new Color(0.7f, 0.85f, 1f);
            if (GUILayout.Toggle(isCurrent, label, EditorStyles.miniButton, GUILayout.Width(110)) != isCurrent)
            {
                GUI.color = prev;
                return mode;
            }
            GUI.color = prev;
            return current;
        }

        private void DrawToolList(List<BridgeToolCatalogItem> filtered)
        {
            if (filtered == null || filtered.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally("No dispatchable tools discovered in this Editor session.", new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            DrawToolPagination(filtered.Count);

            // Bounded list scroll (MaxHeight) nested inside the shell's page
            // scroll. Pagination caps a page at ToolsPageSize rows, so this
            // shows the current page without competing with the shell scroll
            // for the whole tab surface.
            _toolListScroll = EditorGUILayout.BeginScrollView(
                _toolListScroll, GUILayout.MaxHeight(420));

            int pagesCount = filtered.Count / ToolsPageSize + (filtered.Count % ToolsPageSize > 0 ? 1 : 0);
            bool paginated = pagesCount > 1 && _toolPageToShow.HasValue;
            int page = _toolPageToShow ?? 0;
            int start = paginated ? page * ToolsPageSize : 0;
            int end = paginated ? Mathf.Min((page + 1) * ToolsPageSize, filtered.Count) : filtered.Count;

            int shown = 0;
            for (int i = start; i < end; i++)
            {
                var item = filtered[i];
                if (item == null) continue;
                DrawToolRow(item);
                shown++;
            }
            EditorGUILayout.EndScrollView();

            if (shown == 0)
            {
                EditorGUILayout.Space(4);
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally("No tools match the current filter / search.", new Color(0.7f, 0.7f, 0.7f));
            }
        }

        // Page selector modelled on Unity-Dependencies-Hunter: numbered page
        // buttons, plus an "All" affordance when the filtered set is small.
        // Selecting "All" sets _toolPageToShow to null (show everything).
        private void DrawToolPagination(int totalCount)
        {
            int pagesCount = totalCount / ToolsPageSize + (totalCount % ToolsPageSize > 0 ? 1 : 0);
            if (pagesCount <= 1)
            {
                // No pagination needed — but clamp stale state so re-expanding a
                // filtered set doesn't leave a phantom page selection.
                if (_toolPageToShow.HasValue && _toolPageToShow.Value > 0) _toolPageToShow = null;
                EditorGUILayout.Space(2);
                return;
            }

            EditorGUILayout.Space(4);
            _toolPagesScroll = EditorGUILayout.BeginScrollView(_toolPagesScroll, GUILayout.Height(EditorGUIUtility.singleLineHeight + 4));
            EditorGUILayout.BeginHorizontal();

            var showAllButton = totalCount <= ToolsShowAllThreshold;
            if (showAllButton)
            {
                var prevAll = GUI.backgroundColor;
                GUI.backgroundColor = !_toolPageToShow.HasValue ? new Color(1f, 0.95f, 0.4f) : Color.white;
                if (GUILayout.Button("All", GUILayout.Width(34f)))
                {
                    _toolPageToShow = null;
                }
                GUI.backgroundColor = prevAll;
            }

            // If "All" is no longer available but state was null, land on page 0.
            if (!showAllButton && !_toolPageToShow.HasValue)
            {
                _toolPageToShow = 0;
            }

            for (var i = 0; i < pagesCount; i++)
            {
                var prevPage = GUI.backgroundColor;
                GUI.backgroundColor = _toolPageToShow == i ? new Color(1f, 0.95f, 0.4f) : Color.white;
                if (GUILayout.Button((i + 1).ToString(), GUILayout.Width(30f)))
                {
                    _toolPageToShow = i;
                }
                GUI.backgroundColor = prevPage;
            }

            // Clamp the selected page if the filtered set shrank (e.g. search
            // narrowed the list while page 5 was selected).
            if (_toolPageToShow.HasValue && _toolPageToShow.Value > pagesCount - 1)
            {
                _toolPageToShow = pagesCount - 1;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(2);
        }

        private bool PassesFilter(BridgeToolCatalogItem item)
        {
            bool disabled = BridgeToolTogglePolicy.IsDisabled(item.Name);
            return _toolFilter switch
            {
                ToolFilterMode.Enabled => !disabled,
                ToolFilterMode.Disabled => disabled,
                _ => true
            };
        }

        private bool MatchesSearch(BridgeToolCatalogItem item, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            if (Contains(item.Name, search)) return true;
            if (!string.IsNullOrEmpty(item.Title) && Contains(item.Title, search)) return true;
            if (!string.IsNullOrEmpty(item.DeclaringTypeName) && Contains(item.DeclaringTypeName, search)) return true;
            if (item.Parameters != null)
            {
                foreach (var p in item.Parameters)
                {
                    if (p == null) continue;
                    if (Contains(p.Name, search)) return true;
                    if (Contains(p.TypeName, search)) return true;
                    if (!string.IsNullOrEmpty(p.Description) && Contains(p.Description, search)) return true;
                }
            }
            return false;
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawToolRow(BridgeToolCatalogItem item)
        {
            bool enabled = !BridgeToolTogglePolicy.IsDisabled(item.Name);
            bool expanded = _toolFoldoutExpanded.Contains(item.Name);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            // ToggleLeft doesn't expose a tooltip directly; bind one via a label
            // rect so hovering "Enabled" explains what disabling does.
            var toggleRect = GUILayoutUtility.GetRect(new GUIContent("Enabled", TooltipEnabledToggle), EditorStyles.label, GUILayout.Width(70));
            var newEnabled = GUI.Toggle(toggleRect, enabled, new GUIContent("Enabled", TooltipEnabledToggle));
            if (newEnabled != enabled)
            {
                BridgeToolTogglePolicy.SetDisabled(item.Name, !newEnabled);
                enabled = newEnabled;
            }

            var labelStyle = EditorStyles.boldLabel;
            var nameContent = new GUIContent(item.Name,
                $"MCP tool id. Agents call this via POST /tools/{item.Name}.");
            if (!enabled)
            {
                var prev = GUI.color;
                GUI.color = new Color(0.85f, 0.55f, 0.55f);
                GUILayout.Label(nameContent, labelStyle);
                GUI.color = prev;
            }
            else
            {
                GUILayout.Label(nameContent, labelStyle);
            }

            GUILayout.FlexibleSpace();

            var mutColor = item.Mutability == BridgeToolMutability.Mutating
                ? new Color(1f, 0.75f, 0.45f)
                : new Color(0.6f, 0.85f, 0.6f);
            var mutTooltip = item.Mutability == BridgeToolMutability.Mutating ? TooltipMutating : TooltipReadOnly;
            BridgeGUIUtilities.DrawColoredLabel(
                item.Mutability == BridgeToolMutability.Mutating ? "mutating" : "read-only",
                mutColor, 70, mutTooltip);

            var gateColor = !enabled
                ? new Color(0.7f, 0.7f, 0.7f)
                : (item.GateMode == "enforce" ? new Color(1f, 0.75f, 0.45f)
                : item.GateMode == "warn" ? new Color(1f, 0.9f, 0.4f)
                : item.GateMode == "off" ? new Color(0.6f, 0.85f, 0.6f)
                : new Color(0.7f, 0.7f, 0.7f));
            BridgeGUIUtilities.DrawColoredLabel(
                $"gate: {item.GateMode}", gateColor, 110, GateModeTooltip(item.GateMode));

            var sourceLabel = item.Source == BridgeToolSource.Registry ? "registry" : "hardcoded";
            var sourceTooltip = item.Source == BridgeToolSource.Registry ? TooltipSourceRegistry : TooltipSourceHardcoded;
            BridgeGUIUtilities.DrawColoredLabel(sourceLabel, new Color(0.7f, 0.85f, 1f), 70, sourceTooltip);

            // Per-tool token estimate chip. Null estimate (a tool the codegen
            // table did not cover) renders "~? tokens" so the gap is visible
            // rather than silently absent; every catalog tool should resolve.
            var tokenText = item.TokenEstimate.HasValue
                ? $"~{BridgeToolTokenEstimates.Format(item.TokenEstimate.Value)} tokens"
                : "~? tokens";
            BridgeGUIUtilities.DrawColoredLabel(
                tokenText, new Color(0.8f, 0.8f, 0.85f), 110, TooltipTokenEstimate);

            var expandLabel = expanded ? "Hide" : "Details";
            var expandTooltip = expanded
                ? "Collapse the parameter / metadata panel for this tool."
                : "Expand to see the human title, mutability, hints, and parameter list for this tool.";
            if (GUILayout.Button(new GUIContent(expandLabel, expandTooltip), EditorStyles.miniButton, GUILayout.Width(60)))
            {
                if (expanded) _toolFoldoutExpanded.Remove(item.Name);
                else _toolFoldoutExpanded.Add(item.Name);
            }

            EditorGUILayout.EndHorizontal();

            if (!enabled)
            {
                EditorGUILayout.HelpBox(
                    $"Disabled — `POST /tools/{item.Name}` returns a `tool_disabled` error with the tool name. " +
                    "Re-enable to resume dispatch.",
                    MessageType.Warning);
            }

            if (expanded)
            {
                EditorGUILayout.Space(2);
                if (!string.IsNullOrEmpty(item.Title))
                    BridgeGUIUtilities.RowLabel("Title", "Human-readable name shown to MCP clients.", item.Title);
                BridgeGUIUtilities.RowLabel("Mutability",
                    item.Mutability == BridgeToolMutability.Mutating ? TooltipMutating : TooltipReadOnly,
                    item.Mutability == BridgeToolMutability.Mutating ? "mutating (gate-routed)" : "read-only");
                if (item.Source == BridgeToolSource.Registry && !string.IsNullOrEmpty(item.DeclaringTypeName))
                    BridgeGUIUtilities.RowLabel("Declaring type", TooltipSourceRegistry, item.DeclaringTypeName);
                if (!string.IsNullOrEmpty(item.Group))
                    BridgeGUIUtilities.RowLabel("Group",
                        "Tool-group id from the canonical MCP catalog (mcp-server/src/capabilities/tool-groups.ts). " +
                        "Hidden from ListTools until the session activates the group via manage_tools.",
                        item.Group);
                if (item.TokenEstimate.HasValue)
                    BridgeGUIUtilities.RowLabel("Token estimate", TooltipTokenEstimate,
                        $"~{BridgeToolTokenEstimates.Format(item.TokenEstimate.Value)} tokens");

                var hints = BuildHintSummary(item);
                if (!string.IsNullOrEmpty(hints))
                    BridgeGUIUtilities.RowLabel("Hints",
                        "MCP annotations advertised to the client: read-only, idempotent, destructive, default gate, and lifecycle policy.",
                        hints);

                EditorGUILayout.LabelField(
                    new GUIContent("Parameters",
                        "Input schema for this tool (name: type, with defaults). For built-in tools this is mirrored from the MCP server definitions."),
                    new GUIContent(BridgeToolCatalog.FormatParameterList(item)));
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        // Pick the gate-mode tooltip that matches the cell value.
        private static string GateModeTooltip(string gateMode)
        {
            return gateMode switch
            {
                "enforce" => TooltipGateEnforce,
                "warn"    => TooltipGateWarn,
                "off"     => TooltipGateOff,
                "n/a"     => TooltipGateNa,
                _ => null
            };
        }

        private static string BuildHintSummary(BridgeToolCatalogItem item)
        {
            var parts = new List<string>(4);
            if (item.ReadOnlyHint) parts.Add("read-only");
            if (item.IdempotentHint) parts.Add("idempotent");
            if (item.DestructiveHint) parts.Add("destructive");
            if (item.Mutability == BridgeToolMutability.Mutating) parts.Add($"gate default: {item.GateMode}");
            // M13 T4.1 — surface the lifecycle policy so operators can see which
            // tools settle-wait or survive a domain reload without reading code.
            if (item.Lifecycle != LifecyclePolicy.None)
                parts.Add($"lifecycle: {item.Lifecycle.ToWireString()}");
            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }

        // ---------- Gate tab (M4.5-7/8/9) ----------
    }
}
