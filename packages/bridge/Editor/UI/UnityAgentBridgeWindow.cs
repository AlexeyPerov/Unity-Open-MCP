// Bridge runtime dashboard window (M4.5-1 shell, M4.5-2 status panel, M4.5-3 helper baseline,
// M4.5-4 tools catalog, M4.5-5/6 runtime toggles + filter UX).
// Tab navigation pattern adapted from
//   /Users/alexeyperov/Projects/Unity-Scanner/Editor/UI/Window/UnityScannerWindow.cs
// (copy/adapt only; no scanner orchestrator / categories / MCP glue imported).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityAgentBridge.UI.Controls;

namespace UnityAgentBridge
{
    public enum BridgeWindowTab
    {
        Status,
        Tools,
        Gate,
        Activity,
        Settings
    }

    public class UnityAgentBridgeWindow : EditorWindow
    {
        const string MenuPath = "Tools/Unity Agent Bridge";
        const string SelectedTabPref = "UAB_SelectedTab";
        const string BindAddress = "127.0.0.1";

        static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        [MenuItem(MenuPath)]
        public static void Launch()
        {
            var window = GetWindow<UnityAgentBridgeWindow>("Unity Agent Bridge");
            window.minSize = new Vector2(520, 360);
        }

        BridgeWindowTab _currentTab;
        Vector2 _contentScroll;

        string _lastPingResult = "";
        MessageType _lastPingMessageType = MessageType.None;
        bool _pingInFlight;

        [NonSerialized] bool _stopConfirmPending;
        [NonSerialized] double _stopConfirmDeadline;

        // Tools tab state (M4.5-4/5/6)
        enum ToolFilterMode { All, Enabled, Disabled }
        [NonSerialized] ToolFilterMode _toolFilter = ToolFilterMode.All;
        [NonSerialized] string _toolSearch = "";
        [NonSerialized] Vector2 _toolListScroll;
        [NonSerialized] readonly HashSet<string> _toolFoldoutExpanded = new HashSet<string>();

        void OnEnable()
        {
            _currentTab = (BridgeWindowTab)EditorPrefs.GetInt(SelectedTabPref, (int)BridgeWindowTab.Status);
            EditorApplication.update -= RepaintTick;
            EditorApplication.update += RepaintTick;
            BridgeToolTogglePolicy.Changed -= RepaintTick;
            BridgeToolTogglePolicy.Changed += RepaintTick;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintTick;
            BridgeToolTogglePolicy.Changed -= RepaintTick;
            EditorPrefs.SetInt(SelectedTabPref, (int)_currentTab);
        }

        void RepaintTick()
        {
            if (_stopConfirmPending && EditorApplication.timeSinceStartup >= _stopConfirmDeadline)
            {
                _stopConfirmPending = false;
                Repaint();
            }
            Repaint();
        }

        void OnGUI()
        {
            DrawToolbar();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawContent();
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var tabs = (BridgeWindowTab[])Enum.GetValues(typeof(BridgeWindowTab));
            foreach (var tab in tabs)
            {
                var isCurrent = tab == _currentTab;
                var label = new GUIContent(TabLabel(tab));
                var prev = GUI.backgroundColor;
                if (isCurrent) GUI.backgroundColor = new Color(0.7f, 0.85f, 1f);
                if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    if (!isCurrent)
                    {
                        _currentTab = tab;
                        EditorPrefs.SetInt(SelectedTabPref, (int)_currentTab);
                        GUIUtility.keyboardControl = 0;
                    }
                }
                GUI.backgroundColor = prev;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Bridge {BridgeSession.BridgeVersion}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        static string TabLabel(BridgeWindowTab tab)
        {
            return tab switch
            {
                BridgeWindowTab.Status => "Status",
                BridgeWindowTab.Tools => "Tools",
                BridgeWindowTab.Gate => "Gate",
                BridgeWindowTab.Activity => "Activity",
                BridgeWindowTab.Settings => "Settings",
                _ => tab.ToString()
            };
        }

        void DrawContent()
        {
            _contentScroll = EditorGUILayout.BeginScrollView(_contentScroll);
            switch (_currentTab)
            {
                case BridgeWindowTab.Status:
                    DrawStatusTab();
                    break;
                case BridgeWindowTab.Tools:
                    DrawToolsTab();
                    break;
                case BridgeWindowTab.Gate:
                    DrawPlaceholderTab("Gate default policy and manual verify land in M4.5 Plan 3.");
                    break;
                case BridgeWindowTab.Activity:
                    DrawPlaceholderTab("Activity log lands in M4.5 Plan 4.");
                    break;
                case BridgeWindowTab.Settings:
                    DrawPlaceholderTab("Settings persistence lands in M4.5 Plan 4 (.unity-agent/settings.json).");
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        static void DrawPlaceholderTab(string message)
        {
            EditorGUILayout.Space(20);
            BridgeGUIUtilities.DrawLabelAtCenterHorizontally(message, new Color(0.7f, 0.7f, 0.7f));
        }

        void DrawStatusTab()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bridge runtime", EditorStyles.boldLabel);

            var running = BridgeHttpServer.IsRunning;
            var statusColor = BridgeGUIUtilities.GetStateColor(running, BridgeSession.IsCompiling);
            var statusText = running
                ? (BridgeSession.IsCompiling ? "Running (compiling)" : "Running")
                : "Stopped";

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Listener", GUILayout.Width(120));
            var prev = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusText, EditorStyles.boldLabel);
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Bind URL", GUILayout.Width(120));
            EditorGUILayout.SelectableLabel($"http://{BindAddress}:{BridgeHttpServer.Port}/", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mode", GUILayout.Width(120));
            EditorGUILayout.LabelField(BridgeSession.Mode);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Editor state", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Compiling", GUILayout.Width(120));
            var compileColor = BridgeSession.IsCompiling ? Color.yellow : new Color(0.6f, 0.9f, 0.6f);
            var prevC = GUI.color;
            GUI.color = compileColor;
            GUILayout.Label(BridgeSession.IsCompiling ? "Yes" : "No", EditorStyles.boldLabel);
            GUI.color = prevC;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Play mode", GUILayout.Width(120));
            var playColor = BridgeSession.IsPlaying ? new Color(0.5f, 0.8f, 1f) : new Color(0.7f, 0.7f, 0.7f);
            prevC = GUI.color;
            GUI.color = playColor;
            GUILayout.Label(BridgeSession.IsPlaying ? "Playing" : "Edit", EditorStyles.boldLabel);
            GUI.color = prevC;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Project path", GUILayout.Width(120));
            EditorGUILayout.SelectableLabel(BridgeSession.ProjectPath ?? "(unknown)", EditorStyles.textField);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Unity version", GUILayout.Width(120));
            EditorGUILayout.LabelField(BridgeSession.UnityVersion ?? "(unknown)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Bridge version", GUILayout.Width(120));
            EditorGUILayout.LabelField(BridgeSession.BridgeVersion);
            EditorGUILayout.EndHorizontal();

            BridgeGUIUtilities.HorizontalLine(8, 6);
            EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);
            DrawRuntimeControls();
            DrawLocalPing();

            if (!string.IsNullOrEmpty(_lastPingResult))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_lastPingResult, _lastPingMessageType);
            }
        }

        void DrawRuntimeControls()
        {
            var running = BridgeHttpServer.IsRunning;
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(running);
            if (GUILayout.Button("Start", GUILayout.Width(110)))
            {
                try
                {
                    BridgeHttpServer.Start();
                }
                catch (Exception e)
                {
                    _lastPingResult = $"Start failed: {e.Message}";
                    _lastPingMessageType = MessageType.Error;
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!running);
            var buttonLabel = _stopConfirmPending ? "Confirm Stop" : "Stop";
            if (GUILayout.Button(buttonLabel, GUILayout.Width(110)))
            {
                if (!_stopConfirmPending)
                {
                    _stopConfirmPending = true;
                    _stopConfirmDeadline = EditorApplication.timeSinceStartup + 5.0;
                    _lastPingResult = "Press 'Confirm Stop' within 5 seconds to stop the bridge listener.\n" +
                                      "Warning: stopping the listener will drop MCP connectivity for any active agent.";
                    _lastPingMessageType = MessageType.Warning;
                    Repaint();
                }
                else
                {
                    try
                    {
                        BridgeHttpServer.Stop();
                        _lastPingResult = "Bridge listener stopped. MCP clients will no longer reach the bridge until you Start again.";
                        _lastPingMessageType = MessageType.Info;
                    }
                    catch (Exception e)
                    {
                        _lastPingResult = $"Stop failed: {e.Message}";
                        _lastPingMessageType = MessageType.Error;
                    }
                    finally
                    {
                        _stopConfirmPending = false;
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            if (_stopConfirmPending)
            {
                var prev = GUI.color;
                GUI.color = Color.yellow;
                GUILayout.Label($"Confirm within {Mathf.Max(0f, (float)(_stopConfirmDeadline - EditorApplication.timeSinceStartup)):0.0}s");
                GUI.color = prev;
            }

            EditorGUILayout.EndHorizontal();
        }

        void DrawLocalPing()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_pingInFlight || !BridgeHttpServer.IsRunning);
            if (GUILayout.Button("Ping", GUILayout.Width(110)))
            {
                _ = RunLocalPingAsync();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.Label("Local ping verifies the listener responds on the configured bind URL.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        async Task RunLocalPingAsync()
        {
            _pingInFlight = true;
            try
            {
                var url = $"http://{BindAddress}:{BridgeHttpServer.Port}/ping";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response = await SharedHttpClient.GetAsync(url, cts.Token).ConfigureAwait(true);
                sw.Stop();
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                _lastPingResult = $"HTTP {(int)response.StatusCode} in {sw.ElapsedMilliseconds} ms\n{body}";
                _lastPingMessageType = response.IsSuccessStatusCode ? MessageType.Info : MessageType.Warning;
            }
            catch (Exception e)
            {
                _lastPingResult = $"Ping failed: {e.Message}";
                _lastPingMessageType = MessageType.Error;
            }
            finally
            {
                _pingInFlight = false;
                Repaint();
            }
        }

        // ---------- Tools tab (M4.5-4/5/6) ----------

        const string TokenEstimateNote = "Token estimate deferred in v1 (no fake heuristic surfaced).";

        void DrawToolsTab()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tools catalog", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unified list of dispatchable tools in this Editor session. " +
                "Toggle a tool off to block its HTTP dispatch path with an explicit `tool_disabled` error. " +
                "Disable state persists in `.unity-agent/settings.json` and survives domain reload.\n" +
                TokenEstimateNote,
                MessageType.None);

            var items = BridgeToolCatalog.Build();
            DrawToolFilters(items);
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawToolList(items);
        }

        void DrawToolFilters(List<BridgeToolCatalogItem> items)
        {
            int total = items?.Count ?? 0;
            int enabled = BridgeToolCatalog.CountEnabled(items);
            int disabled = total - enabled;

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Total: {total}    Enabled: {enabled}    Disabled: {disabled}", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            var prev = GUI.color;
            _toolFilter = DrawFilterButton(ToolFilterMode.All, "All", _toolFilter);
            _toolFilter = DrawFilterButton(ToolFilterMode.Enabled, $"Enabled ({enabled})", _toolFilter);
            _toolFilter = DrawFilterButton(ToolFilterMode.Disabled, $"Disabled ({disabled})", _toolFilter);
            GUI.color = prev;

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Search", GUILayout.Width(50));
            var newSearch = EditorGUILayout.TextField(_toolSearch ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(180));
            if (newSearch != _toolSearch)
            {
                _toolSearch = newSearch ?? "";
                GUIUtility.keyboardControl = 0;
            }
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48)))
            {
                _toolSearch = "";
                GUIUtility.keyboardControl = 0;
            }

            if (GUILayout.Button("Enable all", EditorStyles.miniButton, GUILayout.Width(78)) && disabled > 0)
            {
                BridgeToolTogglePolicy.Clear();
            }

            EditorGUILayout.EndHorizontal();
        }

        ToolFilterMode DrawFilterButton(ToolFilterMode mode, string label, ToolFilterMode current)
        {
            var isCurrent = mode == current;
            var prev = GUI.color;
            if (isCurrent) GUI.color = new Color(0.7f, 0.85f, 1f);
            if (GUILayout.Toggle(isCurrent, label, EditorStyles.miniButton, GUILayout.Width(isCurrent ? 110 : 80)) != isCurrent)
            {
                GUI.color = prev;
                return mode;
            }
            GUI.color = prev;
            return current;
        }

        void DrawToolList(List<BridgeToolCatalogItem> items)
        {
            if (items == null || items.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally("No dispatchable tools discovered in this Editor session.", new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            var search = (_toolSearch ?? "").Trim();
            var hasSearch = !string.IsNullOrEmpty(search);

            _toolListScroll = EditorGUILayout.BeginScrollView(_toolListScroll, GUILayout.MinHeight(280));
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!PassesFilter(item)) continue;
                if (hasSearch && !MatchesSearch(item, search)) continue;

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

        bool PassesFilter(BridgeToolCatalogItem item)
        {
            bool disabled = BridgeToolTogglePolicy.IsDisabled(item.Name);
            return _toolFilter switch
            {
                ToolFilterMode.Enabled => !disabled,
                ToolFilterMode.Disabled => disabled,
                _ => true
            };
        }

        bool MatchesSearch(BridgeToolCatalogItem item, string search)
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

        static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void DrawToolRow(BridgeToolCatalogItem item)
        {
            bool disabled = BridgeToolTogglePolicy.IsDisabled(item.Name);
            bool expanded = _toolFoldoutExpanded.Contains(item.Name);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            var newDisabled = EditorGUILayout.ToggleLeft("Disable", disabled, GUILayout.Width(70));
            if (newDisabled != disabled)
            {
                BridgeToolTogglePolicy.SetDisabled(item.Name, newDisabled);
                disabled = newDisabled;
            }

            var labelStyle = EditorStyles.boldLabel;
            if (disabled)
            {
                var prev = GUI.color;
                GUI.color = new Color(0.85f, 0.55f, 0.55f);
                GUILayout.Label(item.Name, labelStyle);
                GUI.color = prev;
            }
            else
            {
                GUILayout.Label(item.Name, labelStyle);
            }

            GUILayout.FlexibleSpace();

            var mutColor = item.Mutability == BridgeToolMutability.Mutating
                ? new Color(1f, 0.75f, 0.45f)
                : new Color(0.6f, 0.85f, 0.6f);
            BridgeGUIUtilities.DrawColoredLabel(
                item.Mutability == BridgeToolMutability.Mutating ? "mutating" : "read-only",
                mutColor, 70);

            var gateColor = disabled
                ? new Color(0.7f, 0.7f, 0.7f)
                : (item.GateMode == "enforce" ? new Color(1f, 0.75f, 0.45f)
                : item.GateMode == "warn" ? new Color(1f, 0.9f, 0.4f)
                : item.GateMode == "off" ? new Color(0.6f, 0.85f, 0.6f)
                : new Color(0.7f, 0.7f, 0.7f));
            BridgeGUIUtilities.DrawColoredLabel($"gate: {item.GateMode}", gateColor, 110);

            var sourceLabel = item.Source == BridgeToolSource.Registry ? "registry" : "hardcoded";
            BridgeGUIUtilities.DrawColoredLabel(sourceLabel, new Color(0.7f, 0.85f, 1f), 70);

            var expandLabel = expanded ? "Hide" : "Details";
            if (GUILayout.Button(expandLabel, EditorStyles.miniButton, GUILayout.Width(60)))
            {
                if (expanded) _toolFoldoutExpanded.Remove(item.Name);
                else _toolFoldoutExpanded.Add(item.Name);
            }

            EditorGUILayout.EndHorizontal();

            if (disabled)
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
                    EditorGUILayout.LabelField("Title", item.Title);
                EditorGUILayout.LabelField("Mutability",
                    item.Mutability == BridgeToolMutability.Mutating ? "mutating (gate-routed)" : "read-only");
                if (item.Source == BridgeToolSource.Registry && !string.IsNullOrEmpty(item.DeclaringTypeName))
                    EditorGUILayout.LabelField("Declaring type", item.DeclaringTypeName);

                var hints = BuildHintSummary(item);
                if (!string.IsNullOrEmpty(hints))
                    EditorGUILayout.LabelField("Hints", hints);

                EditorGUILayout.LabelField("Parameters", BridgeToolCatalog.FormatParameterList(item));
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        static string BuildHintSummary(BridgeToolCatalogItem item)
        {
            var parts = new List<string>(4);
            if (item.ReadOnlyHint) parts.Add("read-only");
            if (item.IdempotentHint) parts.Add("idempotent");
            if (item.DestructiveHint) parts.Add("destructive");
            if (item.Mutability == BridgeToolMutability.Mutating) parts.Add($"gate default: {item.GateMode}");
            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }
    }
}
