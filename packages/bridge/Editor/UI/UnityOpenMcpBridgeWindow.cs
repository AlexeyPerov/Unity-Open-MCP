using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.UI.Controls;

namespace UnityOpenMcpBridge
{
    public enum BridgeWindowTab
    {
        Status,
        Tools,
        Gate,
        Activity,
        Settings,
        Extensions
    }

    public class UnityOpenMcpBridgeWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Unity Open MCP Bridge";
        private const string SelectedTabPref = "UOMCB_SelectedTab";
        private const string BindAddress = "127.0.0.1";

        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        [MenuItem(MenuPath)]
        public static void Launch()
        {
            var window = GetWindow<UnityOpenMcpBridgeWindow>("Unity Open MCP Bridge");
            window.minSize = new Vector2(520, 360);
        }

        private BridgeWindowTab _currentTab;
        private Vector2 _contentScroll;

        private string _lastPingResult = "";
        private MessageType _lastPingMessageType = MessageType.None;
        private bool _pingInFlight;

        [NonSerialized] private bool _stopConfirmPending;
        [NonSerialized] private double _stopConfirmDeadline;

        // Tools tab state (M4.5-4/5/6)
        enum ToolFilterMode { All, Enabled, Disabled }
        [NonSerialized] private ToolFilterMode _toolFilter = ToolFilterMode.All;
        [NonSerialized] private string _toolSearch = "";
        [NonSerialized] private Vector2 _toolListScroll;
        [NonSerialized] private readonly HashSet<string> _toolFoldoutExpanded = new HashSet<string>();

        // Gate tab state (M4.5-7/8/9)
        [NonSerialized] private string _manualValidateInput = "";
        [NonSerialized] private Vector2 _gateTabScroll;
        [NonSerialized] private Vector2 _gateLatestScroll;
        [NonSerialized] private Vector2 _gateCheckpointScroll;
        [NonSerialized] private Vector2 _gateManualResultScroll;
        [NonSerialized] private bool _validateInFlight;
        [NonSerialized] private BridgeManualValidateResult _lastManualResult;
        [NonSerialized] private readonly HashSet<string> _manualAssetFoldoutExpanded = new HashSet<string>();
        [NonSerialized] private bool _gateLatestFoldout = true;
        [NonSerialized] private bool _gateCheckpointFoldout = true;
        [NonSerialized] private bool _gateManualFoldout = true;

        // Activity tab state (M4.5-10)
        [NonSerialized] private Vector2 _activityTabScroll;
        [NonSerialized] private bool _activityVerboseFoldout = false;
        [NonSerialized] private bool _activityFilterToolRequests = true;
        [NonSerialized] private bool _activityFilterDisabled = true;
        [NonSerialized] private bool _activityFilterErrors = true;
        [NonSerialized] private bool _activityFilterPing = false;
        [NonSerialized] private bool _activityFilterResources = false;
        [NonSerialized] private string _activitySearch = "";

        // Settings tab state (M4.5-11)
        [NonSerialized] private Vector2 _settingsTabScroll;

        private void OnEnable()
        {
            _currentTab = (BridgeWindowTab)EditorPrefs.GetInt(SelectedTabPref, (int)BridgeWindowTab.Status);
            EditorApplication.update -= RepaintTick;
            EditorApplication.update += RepaintTick;
            BridgeToolTogglePolicy.Changed -= RepaintTick;
            BridgeToolTogglePolicy.Changed += RepaintTick;
            BridgeGateDefaultPolicy.Changed -= RepaintTick;
            BridgeGateDefaultPolicy.Changed += RepaintTick;
            BridgeGateRunHistory.Changed -= RepaintTick;
            BridgeGateRunHistory.Changed += RepaintTick;
            BridgeActivityLog.Changed -= RepaintTick;
            BridgeActivityLog.Changed += RepaintTick;
            BridgeProjectSettings.Changed -= RepaintTick;
            BridgeProjectSettings.Changed += RepaintTick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintTick;
            BridgeToolTogglePolicy.Changed -= RepaintTick;
            BridgeGateDefaultPolicy.Changed -= RepaintTick;
            BridgeGateRunHistory.Changed -= RepaintTick;
            BridgeActivityLog.Changed -= RepaintTick;
            BridgeProjectSettings.Changed -= RepaintTick;
            EditorPrefs.SetInt(SelectedTabPref, (int)_currentTab);
        }

        private void RepaintTick()
        {
            if (_stopConfirmPending && EditorApplication.timeSinceStartup >= _stopConfirmDeadline)
            {
                _stopConfirmPending = false;
                Repaint();
            }
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawContent();
        }

        private void DrawToolbar()
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
                    }
                }
                GUI.backgroundColor = prev;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Bridge {BridgeSession.BridgeVersion}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static string TabLabel(BridgeWindowTab tab)
        {
            return tab switch
            {
                BridgeWindowTab.Status => "Status",
                BridgeWindowTab.Tools => "Tools",
                BridgeWindowTab.Gate => "Gate",
                BridgeWindowTab.Activity => "Activity",
                BridgeWindowTab.Settings => "Settings",
                BridgeWindowTab.Extensions => "Extensions",
                _ => tab.ToString()
            };
        }

        private void DrawContent()
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
                    DrawGateTab();
                    break;
                case BridgeWindowTab.Activity:
                    DrawActivityTab();
                    break;
                case BridgeWindowTab.Settings:
                    DrawSettingsTab();
                    break;
                case BridgeWindowTab.Extensions:
                    DrawExtensionsTab();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawStatusTab()
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

        private void DrawRuntimeControls()
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

        private void DrawLocalPing()
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

        private async Task RunLocalPingAsync()
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

        private const string TokenEstimateNote = "Token estimate deferred in v1 (no fake heuristic surfaced).";

        private void DrawToolsTab()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tools catalog", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unified list of dispatchable tools in this Editor session. " +
                "Untoggle a tool to block its HTTP dispatch path with an explicit `tool_disabled` error. " +
                "Disable state persists in `.unity-open-mcp/settings.json` and survives domain reload.\n" +
                TokenEstimateNote,
                MessageType.None);

            var items = BridgeToolCatalog.Build();
            DrawToolFilters(items);
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawToolList(items);
        }

        private void DrawToolFilters(List<BridgeToolCatalogItem> items)
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
            }
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48)))
            {
                _toolSearch = "";
            }

            if (GUILayout.Button("Enable all", EditorStyles.miniButton, GUILayout.Width(78)) && disabled > 0)
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
            if (GUILayout.Toggle(isCurrent, label, EditorStyles.miniButton, GUILayout.Width(isCurrent ? 110 : 80)) != isCurrent)
            {
                GUI.color = prev;
                return mode;
            }
            GUI.color = prev;
            return current;
        }

        private void DrawToolList(List<BridgeToolCatalogItem> items)
        {
            if (items == null || items.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally("No dispatchable tools discovered in this Editor session.", new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            var search = (_toolSearch ?? "").Trim();
            var hasSearch = !string.IsNullOrEmpty(search);

            _toolListScroll = EditorGUILayout.BeginScrollView(_toolListScroll);
            var shown = 0;
            foreach (var item in items)
            {
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

            var newEnabled = EditorGUILayout.ToggleLeft("Enabled", enabled, GUILayout.Width(70));
            if (newEnabled != enabled)
            {
                BridgeToolTogglePolicy.SetDisabled(item.Name, !newEnabled);
                enabled = newEnabled;
            }

            var labelStyle = EditorStyles.boldLabel;
            if (!enabled)
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

            var gateColor = !enabled
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

        private void DrawGateTab()
        {
            _gateTabScroll = EditorGUILayout.BeginScrollView(_gateTabScroll);
            DrawGateDefaultPolicySection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateLatestResultSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateCheckpointHistorySection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateManualValidateSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawGateDefaultPolicySection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Project default gate mode", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Sets the project-wide default for the gate policy. " +
                "Applies to all mutating tool calls that do not supply an explicit request-level `gate`. " +
                "Persists in `.unity-open-mcp/settings.json`.",
                MessageType.None);

            var current = BridgeGateDefaultPolicy.GetDefault();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Default mode", GUILayout.Width(120));
            var newMode = EditorGUILayout.Popup(IndexOfMode(current), ModeLabels());
            EditorGUILayout.EndHorizontal();
            if (newMode != IndexOfMode(current))
            {
                BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.ValidModes[newMode]);
            }

            EditorGUILayout.LabelField("Effective policy", ModeDescriptor(current), EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Precedence", BridgeGateDefaultPolicy.DescribePrecedence(), EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Storage", BridgeProjectSettings.SettingsPath ?? "(no project root)", EditorStyles.miniLabel);
        }

        private static GUIContent[] ModeLabels()
        {
            var labels = new GUIContent[BridgeGateDefaultPolicy.ValidModes.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(ModeDescriptor(BridgeGateDefaultPolicy.ValidModes[i]));
            }
            return labels;
        }

        private static int IndexOfMode(string mode)
        {
            for (int i = 0; i < BridgeGateDefaultPolicy.ValidModes.Length; i++)
            {
                if (BridgeGateDefaultPolicy.ValidModes[i] == mode) return i;
            }
            return 0;
        }

        private static string ModeDescriptor(string mode)
        {
            return mode switch
            {
                "enforce" => "enforce  (default — MCP errors on new errors)",
                "warn"    => "warn  (new errors surface as warnings, not MCP errors)",
                "off"     => "off  (no checkpoint/validate — explicit opt-in only)",
                _ => mode ?? BridgeGateDefaultPolicy.Default
            };
        }

        private void DrawGateLatestResultSection()
        {
            EditorGUILayout.Space(6);
            _gateLatestFoldout = EditorGUILayout.Foldout(_gateLatestFoldout, "Latest gate result (session, in-memory)", true);
            if (!_gateLatestFoldout) return;

            var latest = BridgeGateRunHistory.Latest;
            if (latest == null)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No gate results captured in this session yet. Trigger a mutating tool call to populate.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            _gateLatestScroll = EditorGUILayout.BeginScrollView(_gateLatestScroll, GUILayout.MinHeight(80));

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Tool", latest.ToolName ?? "(unknown)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mode", GUILayout.Width(120));
            EditorGUILayout.LabelField(latest.EffectiveMode ?? "(unknown)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Outcome", GUILayout.Width(120));
            var outcomeColor = OutcomeColor(latest.Outcome);
            var prev = GUI.color;
            GUI.color = outcomeColor;
            EditorGUILayout.LabelField(OutcomeLabel(latest.Outcome), EditorStyles.boldLabel);
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(latest.MutationError))
            {
                EditorGUILayout.HelpBox($"Mutation error: {latest.MutationError}", MessageType.Error);
            }

            BridgeGUIUtilities.HorizontalLine(2, 4);

            EditorGUILayout.LabelField("Delta",
                $"new errors: {latest.NewErrors}    new warnings: {latest.NewWarnings}    resolved errors: {latest.ResolvedErrors}    resolved warnings: {latest.ResolvedWarnings}");

            EditorGUILayout.LabelField("Durations (ms)",
                $"checkpoint: {latest.CheckpointDurationMs}    validation: {latest.ValidationDurationMs}    total: {latest.TotalGateDurationMs}");

            if (latest.CategoriesRun != null && latest.CategoriesRun.Length > 0)
            {
                EditorGUILayout.LabelField("Categories", string.Join(", ", latest.CategoriesRun));
            }
            else
            {
                EditorGUILayout.LabelField("Categories", "(none — gate skipped or off)");
            }

            if (latest.AgentNextSteps != null && latest.AgentNextSteps.Length > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Next steps", EditorStyles.miniBoldLabel);
                foreach (var t in latest.AgentNextSteps)
                {
                    EditorGUILayout.LabelField($"  • {t}", EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.LabelField("Captured", latest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                BridgeGateRunHistory.Clear();
            }
            EditorGUILayout.LabelField("In-memory only — not persisted to disk in v1.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static string OutcomeLabel(GateOutcome outcome)
        {
            return outcome switch
            {
                GateOutcome.Passed => "Passed",
                GateOutcome.Failed => "Failed",
                GateOutcome.Warned => "Warned",
                GateOutcome.Skipped => "Skipped (no gate ran)",
                _ => outcome.ToString()
            };
        }

        private static Color OutcomeColor(GateOutcome outcome)
        {
            return outcome switch
            {
                GateOutcome.Passed => new Color(0.6f, 0.9f, 0.6f),
                GateOutcome.Warned => new Color(1f, 0.9f, 0.4f),
                GateOutcome.Failed => new Color(1f, 0.5f, 0.5f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        private void DrawGateCheckpointHistorySection()
        {
            EditorGUILayout.Space(6);
            _gateCheckpointFoldout = EditorGUILayout.Foldout(_gateCheckpointFoldout, "Checkpoint history (in-memory ring buffer)", true);
            if (!_gateCheckpointFoldout) return;

            EditorGUILayout.HelpBox(
                "Session-scoped ring buffer (capacity " + BridgeGateRunHistory.Capacity + "). " +
                "Populated by gate runs and by the `unity_open_mcp_checkpoint_create` tool. " +
                "In-memory only — no on-disk persistence in v1.",
                MessageType.None);

            var count = CheckpointStore.Count;
            if (count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No checkpoints captured in this session yet.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            _gateCheckpointScroll = EditorGUILayout.BeginScrollView(_gateCheckpointScroll, GUILayout.MinHeight(80));
            var recent = CheckpointStore.Recent;
            // Display most-recent first for readability.
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var entry = recent[i];
                if (entry == null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(entry.CheckpointId ?? "(no id)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Captured", entry.Timestamp ?? "(no timestamp)", EditorStyles.miniLabel);
                if (entry.Paths != null && entry.Paths.Length > 0)
                {
                    EditorGUILayout.LabelField("Paths", string.Join(", ", entry.Paths), EditorStyles.wordWrappedMiniLabel);
                }
                if (entry.Categories != null && entry.Categories.Length > 0)
                {
                    EditorGUILayout.LabelField("Categories", string.Join(", ", entry.Categories), EditorStyles.miniLabel);
                }
                if (entry.Fingerprint?.Fingerprints != null)
                {
                    int totalErrors = 0, totalWarnings = 0;
                    foreach (var fp in entry.Fingerprint.Fingerprints.Values)
                    {
                        if (fp == null) continue;
                        totalErrors += fp.Errors;
                        totalWarnings += fp.Warnings;
                    }
                    EditorGUILayout.LabelField("Fingerprint", $"errors: {totalErrors}    warnings: {totalWarnings}", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawGateManualValidateSection()
        {
            EditorGUILayout.Space(6);
            _gateManualFoldout = EditorGUILayout.Foldout(_gateManualFoldout, "Manual validate (scoped, non-mutating)", true);
            if (!_gateManualFoldout) return;

            EditorGUILayout.HelpBox(
                "Run a scoped verify pass without dispatching through the MCP server. " +
                "Default source is the current Project window selection; the text area accepts " +
                "comma/newline separated `Assets/...` paths. No mutation occurs, no checkpoint " +
                "is created, and gate defaults are not consulted.",
                MessageType.None);

            var selection = BridgeManualVerifyRunner.GetSelectionAssetPaths();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Selection paths", GUILayout.Width(110));
            EditorGUILayout.LabelField(selection.Length == 0 ? "(no assets selected)" : string.Join(", ", selection), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Custom paths (optional, one per line)");
            _manualValidateInput = EditorGUILayout.TextArea(_manualValidateInput ?? "", GUILayout.MinHeight(48));

            var customPaths = BridgeManualVerifyRunner.ParsePathList(_manualValidateInput);
            var effectivePaths = selection.Length > 0 ? selection : customPaths;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_validateInFlight);
            if (GUILayout.Button(_validateInFlight ? "Validating…" : "Validate selection / paths", GUILayout.Width(220)))
            {
                _lastManualResult = BridgeManualVerifyRunner.Run(effectivePaths);
                Repaint();
            }
            if (GUILayout.Button("Use selection", GUILayout.Width(110)))
            {
                _manualValidateInput = "";
            }
            if (GUILayout.Button("Clear result", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                _lastManualResult = null;
                _manualAssetFoldoutExpanded.Clear();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (effectivePaths.Length == 0)
            {
                EditorGUILayout.HelpBox("Select assets in the Project window or enter paths above to validate.", MessageType.None);
            }

            DrawGateManualResult();
        }

        private void DrawGateManualResult()
        {
            var result = _lastManualResult;
            if (result == null)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No manual validate run yet.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                EditorGUILayout.HelpBox($"Validate run failed: {result.ErrorMessage}", MessageType.Error);
                return;
            }

            if (!result.Ran)
            {
                EditorGUILayout.HelpBox("No paths supplied — provide selection or custom paths to validate.", MessageType.None);
                return;
            }

            var passColor = result.TotalErrors == 0
                ? new Color(0.6f, 0.9f, 0.6f)
                : new Color(1f, 0.5f, 0.5f);
            var prev = GUI.color;
            GUI.color = passColor;
            EditorGUILayout.LabelField(
                result.TotalErrors == 0 ? "PASS  " : "FAIL  ",
                EditorStyles.boldLabel);
            GUI.color = prev;

            EditorGUILayout.LabelField(
                $"paths: {result.InputPaths.Length}    assets with issues: {result.TotalAssets}    " +
                $"errors: {result.TotalErrors}    warnings: {result.TotalWarnings}    duration: {result.DurationMs} ms");

            if (result.CategoriesRun != null && result.CategoriesRun.Length > 0)
                EditorGUILayout.LabelField("Categories run", string.Join(", ", result.CategoriesRun), EditorStyles.miniLabel);

            if (result.Groups == null || result.Groups.Count == 0)
            {
                EditorGUILayout.HelpBox("No issues detected for the supplied paths.", MessageType.Info);
                return;
            }

            _gateManualResultScroll = EditorGUILayout.BeginScrollView(_gateManualResultScroll, GUILayout.MinHeight(120));
            foreach (var group in result.Groups)
            {
                if (group == null) continue;
                DrawManualAssetGroup(group);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawManualAssetGroup(BridgeManualAssetGroup group)
        {
            var expanded = _manualAssetFoldoutExpanded.Contains(group.AssetPath);
            var header = $"{group.AssetPath}    errors: {group.ErrorCount}    warnings: {group.WarningCount}";

            Color headerColor;
            if (group.ErrorCount > 0) headerColor = new Color(1f, 0.55f, 0.55f);
            else if (group.WarningCount > 0) headerColor = new Color(1f, 0.9f, 0.4f);
            else headerColor = new Color(0.7f, 0.85f, 1f);

            var prev = GUI.color;
            GUI.color = headerColor;
            var nowExpanded = EditorGUILayout.Foldout(expanded, header, true);
            GUI.color = prev;

            if (nowExpanded != expanded)
            {
                if (nowExpanded) _manualAssetFoldoutExpanded.Add(group.AssetPath);
                else _manualAssetFoldoutExpanded.Remove(group.AssetPath);
            }

            if (nowExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var issue in group.Issues)
                {
                    if (issue == null) continue;
                    Color sevColor = issue.Severity == "error"
                        ? new Color(1f, 0.55f, 0.55f)
                        : new Color(1f, 0.9f, 0.4f);
                    var sprev = GUI.color;
                    GUI.color = sevColor;
                    EditorGUILayout.LabelField(
                        $"[{issue.Severity.ToUpperInvariant()}] {issue.RuleId} / {issue.IssueCode}",
                        EditorStyles.boldLabel);
                    GUI.color = sprev;
                    if (!string.IsNullOrEmpty(issue.Description))
                        EditorGUILayout.LabelField(issue.Description, EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUI.indentLevel--;
                BridgeGUIUtilities.HorizontalLine(1, 2);
            }
        }

        // ---------- Activity tab (M4.5-10) ----------

        private const string ActivityPrivacyNote =
            "Default capture is metadata only (no request/response bodies). " +
            "Verbose mode adds a truncated request snippet for debugging.";

        private void DrawActivityTab()
        {
            _activityTabScroll = EditorGUILayout.BeginScrollView(_activityTabScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Activity log (in-memory ring buffer)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Session-scoped ring buffer (capacity " + BridgeActivityLog.Capacity + ") of bridge HTTP events. " +
                "In-memory only — cleared on domain reload or Editor restart. " +
                ActivityPrivacyNote,
                MessageType.None);

            DrawActivityControls();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityFilters();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityList();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityPassiveBatchHint();

            EditorGUILayout.EndScrollView();
        }

        private void DrawActivityControls()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            var prevVerbose = BridgeActivityLog.Verbose;
            var newVerbose = EditorGUILayout.ToggleLeft(
                "Verbose mode (captures truncated request snippet, ≤ " + BridgeActivityLog.SnippetMaxChars + " chars)",
                prevVerbose, GUILayout.Width(420));
            if (newVerbose != prevVerbose)
            {
                BridgeActivityLog.Verbose = newVerbose;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                BridgeActivityLog.Clear();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                $"buffer: {BridgeActivityLog.Count} / {BridgeActivityLog.Capacity}    " +
                $"recorded: {BridgeActivityLog.TotalRecorded}    trimmed: {BridgeActivityLog.TotalDroppedTrim}",
                EditorStyles.miniLabel);
            
            _activityVerboseFoldout = newVerbose;
        }

        private void DrawActivityFilters()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Filters", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            _activityFilterToolRequests = EditorGUILayout.ToggleLeft("Tool requests", _activityFilterToolRequests, GUILayout.Width(120));
            _activityFilterDisabled = EditorGUILayout.ToggleLeft("Tool disabled", _activityFilterDisabled, GUILayout.Width(110));
            _activityFilterErrors = EditorGUILayout.ToggleLeft("Errors", _activityFilterErrors, GUILayout.Width(80));
            _activityFilterPing = EditorGUILayout.ToggleLeft("/ping", _activityFilterPing, GUILayout.Width(70));
            _activityFilterResources = EditorGUILayout.ToggleLeft("Resources", _activityFilterResources, GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Search", GUILayout.Width(50));
            var newSearch = EditorGUILayout.TextField(_activitySearch ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(160));
            if (newSearch != _activitySearch)
            {
                _activitySearch = newSearch ?? "";
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawActivityList()
        {
            var all = BridgeActivityLog.Events;
            if (all == null || all.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No activity captured in this session yet. Trigger a /ping or tool call to populate.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            // Display most-recent first.
            var search = (_activitySearch ?? "").Trim();
            int shown = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var evt = all[i];
                if (evt == null) continue;
                if (!PassesActivityFilter(evt)) continue;
                if (!string.IsNullOrEmpty(search) && !MatchesActivitySearch(evt, search)) continue;
                DrawActivityRow(evt);
                shown++;
            }

            if (shown == 0)
            {
                EditorGUILayout.Space(4);
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No events match the current filter / search.",
                    new Color(0.7f, 0.7f, 0.7f));
            }
        }

        private bool PassesActivityFilter(BridgeActivityEvent evt)
        {
            return evt.Kind switch
            {
                BridgeActivityKind.ToolRequest => _activityFilterToolRequests,
                BridgeActivityKind.ToolDisabled => _activityFilterDisabled,
                BridgeActivityKind.ToolError => _activityFilterErrors,
                BridgeActivityKind.Ping => _activityFilterPing,
                BridgeActivityKind.ResourceRequest => _activityFilterResources,
                BridgeActivityKind.ResourceError => _activityFilterResources || _activityFilterErrors,
                BridgeActivityKind.UnknownPath => _activityFilterErrors,
                _ => true
            };
        }

        private bool MatchesActivitySearch(BridgeActivityEvent evt, string search)
        {
            if (evt == null || string.IsNullOrEmpty(search)) return true;
            if (Contains(evt.ToolName, search)) return true;
            if (Contains(evt.ErrorCode, search)) return true;
            if (Contains(evt.ErrorMessage, search)) return true;
            if (Contains(evt.GateMode, search)) return true;
            return false;
        }

        private void DrawActivityRow(BridgeActivityEvent evt)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(evt.Timestamp.ToString("HH:mm:ss"), GUILayout.Width(70));
            BridgeGUIUtilities.DrawColoredLabel(ActivityKindLabel(evt.Kind), ActivityKindColor(evt.Kind), 110);
            if (!string.IsNullOrEmpty(evt.ToolName))
            {
                EditorGUILayout.LabelField(evt.ToolName, EditorStyles.boldLabel, GUILayout.Width(220));
            }
            else
            {
                EditorGUILayout.LabelField("-", GUILayout.Width(220));
            }
            if (!string.IsNullOrEmpty(evt.GateMode))
            {
                EditorGUILayout.LabelField($"gate: {evt.GateMode}", GUILayout.Width(110));
            }
            BridgeGUIUtilities.DrawColoredLabel(
                ActivityOutcomeLabel(evt.Outcome),
                ActivityOutcomeColor(evt.Outcome), 80);
            EditorGUILayout.LabelField($"HTTP {evt.HttpStatus}", GUILayout.Width(70));
            EditorGUILayout.LabelField($"{evt.DurationMs} ms", GUILayout.Width(80));
            if (evt.RequestBodyLength > 0)
            {
                EditorGUILayout.LabelField($"body: {evt.RequestBodyLength}b", GUILayout.Width(90));
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(evt.ErrorCode) || !string.IsNullOrEmpty(evt.ErrorMessage))
            {
                var msg = string.IsNullOrEmpty(evt.ErrorCode)
                    ? evt.ErrorMessage
                    : $"{evt.ErrorCode}: {evt.ErrorMessage}";
                EditorGUILayout.HelpBox(msg, evt.Outcome == BridgeActivityOutcome.Success ? MessageType.None : MessageType.Warning);
            }

            if (BridgeActivityLog.Verbose && !string.IsNullOrEmpty(evt.RequestSnippet))
            {
                EditorGUILayout.LabelField("Request snippet", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    evt.RequestSnippet, EditorStyles.textArea,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
            }

            EditorGUILayout.EndVertical();
        }

        private static string ActivityKindLabel(BridgeActivityKind kind)
        {
            return kind switch
            {
                BridgeActivityKind.Ping => "/ping",
                BridgeActivityKind.ToolRequest => "tool",
                BridgeActivityKind.ToolDisabled => "disabled",
                BridgeActivityKind.ToolError => "tool-err",
                BridgeActivityKind.ResourceRequest => "resource",
                BridgeActivityKind.ResourceError => "resource-err",
                BridgeActivityKind.UnknownPath => "404",
                _ => kind.ToString()
            };
        }

        private static Color ActivityKindColor(BridgeActivityKind kind)
        {
            return kind switch
            {
                BridgeActivityKind.Ping => new Color(0.7f, 0.85f, 1f),
                BridgeActivityKind.ToolRequest => new Color(0.7f, 0.85f, 1f),
                BridgeActivityKind.ToolDisabled => new Color(1f, 0.9f, 0.4f),
                BridgeActivityKind.ToolError => new Color(1f, 0.5f, 0.5f),
                BridgeActivityKind.ResourceRequest => new Color(0.7f, 0.7f, 0.7f),
                BridgeActivityKind.ResourceError => new Color(1f, 0.5f, 0.5f),
                BridgeActivityKind.UnknownPath => new Color(0.85f, 0.55f, 0.55f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        private static string ActivityOutcomeLabel(BridgeActivityOutcome outcome)
        {
            return outcome switch
            {
                BridgeActivityOutcome.Success => "ok",
                BridgeActivityOutcome.Failed => "fail",
                BridgeActivityOutcome.Timeout => "timeout",
                BridgeActivityOutcome.Skipped => "skip",
                _ => "-"
            };
        }

        private static Color ActivityOutcomeColor(BridgeActivityOutcome outcome)
        {
            return outcome switch
            {
                BridgeActivityOutcome.Success => new Color(0.6f, 0.9f, 0.6f),
                BridgeActivityOutcome.Failed => new Color(1f, 0.5f, 0.5f),
                BridgeActivityOutcome.Timeout => new Color(1f, 0.9f, 0.4f),
                BridgeActivityOutcome.Skipped => new Color(0.7f, 0.7f, 0.7f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        // ---------- Passive batch hint (M4.5-12) ----------

        private void DrawActivityPassiveBatchHint()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Batch workflows", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Batch scan / baseline / regression workflows run via `unity-open-mcp` " +
                "fallback (or headless Editor CLI). The full batch panel — entry points, filters, " +
                "regression threshold controls — is not part of v1 and will land in a future update. " +
                "Use the Gate tab's Manual validate for ad-hoc scoped scans in the meantime.",
                MessageType.None);
        }

        // ---------- Settings tab (M4.5-11) ----------

        private const string SettingsPersistenceNote =
            "Project-level runtime settings persist in `.unity-open-mcp/settings.json` at the project root. " +
            "Changes are saved immediately. v1 surface only — no Project Settings provider.";

        private void DrawSettingsTab()
        {
            _settingsTabScroll = EditorGUILayout.BeginScrollView(_settingsTabScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bridge runtime settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(SettingsPersistenceNote, MessageType.None);

            DrawAutoStartSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawDefaultGateModeSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawAuthSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawBindAddressSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawDenyListsSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawAuditLogSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityLogSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawSettingsStorageSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawSettingsPassiveBatchHint();

            EditorGUILayout.EndScrollView();
        }

        private void DrawAutoStartSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Auto-start bridge listener", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "When ON (default for new projects), the bridge listener auto-starts on Editor load. " +
                "Turn OFF to require a manual Start from the Status tab. Effective on the next " +
                "Editor domain reload / restart.",
                MessageType.None);

            var prev = BridgeProjectSettings.AutoStart;
            var next = EditorGUILayout.ToggleLeft(
                "Auto-start bridge HTTP listener on Editor load", prev);
            if (next != prev)
            {
                BridgeProjectSettings.SetAutoStart(next);
            }
        }

        private void DrawDefaultGateModeSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Project default gate mode", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Used when an incoming mutating tool request omits a `gate` value. " +
                "Per-request `gate` always wins (see Gate tab for full precedence).",
                MessageType.None);

            var current = BridgeGateDefaultPolicy.GetDefault();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Default mode", GUILayout.Width(120));
            var newIndex = EditorGUILayout.Popup(IndexOfMode(current), ModeLabels());
            EditorGUILayout.EndHorizontal();
            if (newIndex != IndexOfMode(current))
            {
                BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.ValidModes[newIndex]);
            }
        }

        private void DrawAuthSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Bridge auth", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Controls whether the bridge HTTP listener requires a bearer token. " +
                "A per-session token is always minted into the instance lock and auto-discovered " +
                "by the MCP server, so enabling `required` needs no client-side config change. " +
                "`required` is mandatory for remote bind (see Listener bind below) and recommended " +
                "on shared machines.",
                MessageType.None);

            var current = BridgeAuthPolicy.GetDefault();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Auth mode", GUILayout.Width(120));
            var newIndex = EditorGUILayout.Popup(IndexOfAuthMode(current), AuthModeLabels());
            EditorGUILayout.EndHorizontal();
            if (newIndex != IndexOfAuthMode(current))
            {
                BridgeAuthPolicy.SetDefault(BridgeAuthPolicy.ValidModes[newIndex]);
            }
        }

        private void DrawBindAddressSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Listener bind", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Address the HTTP listener binds. Loopback (127.0.0.1, default) is reachable " +
                "only from this machine. Remote (0.0.0.0) exposes the bridge to the network and " +
                "is refused at start unless Auth mode is `required` — remote access without token " +
                "auth is unsafe. Effective on the next listener start (bridge restart / domain reload).",
                MessageType.None);

            var current = BridgeProjectSettings.BindAddress;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Bind address", GUILayout.Width(120));
            var newIndex = EditorGUILayout.Popup(IndexOfBindAddress(current), BindAddressLabels());
            EditorGUILayout.EndHorizontal();
            if (newIndex != IndexOfBindAddress(current))
            {
                var next = BridgeBindAddress.ValidAddresses[newIndex];
                // Surface the remote-requires-auth rule immediately so the
                // operator knows why the next start may refuse.
                var decision = BridgeBindAddress.Decide(next, BridgeAuthPolicy.GetDefault());
                if (!decision.Allowed)
                {
                    EditorGUILayout.HelpBox(
                        "Remote bind will be refused at start: set Auth mode to `required` first. " +
                        decision.RefusalReason,
                        MessageType.Warning);
                }
                BridgeProjectSettings.SetBindAddress(next);
            }
        }

        private static GUIContent[] BindAddressLabels()
        {
            var labels = new GUIContent[BridgeBindAddress.ValidAddresses.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(BindAddressDescriptor(BridgeBindAddress.ValidAddresses[i]));
            }
            return labels;
        }

        private static int IndexOfBindAddress(string address)
        {
            for (int i = 0; i < BridgeBindAddress.ValidAddresses.Length; i++)
            {
                if (BridgeBindAddress.ValidAddresses[i] == address) return i;
            }
            return 0;
        }

        private static string BindAddressDescriptor(string address)
        {
            return address switch
            {
                BridgeBindAddress.Loopback => "127.0.0.1  (loopback only — default)",
                BridgeBindAddress.Remote   => "0.0.0.0  (remote — requires auth)",
                _ => address ?? BridgeBindAddress.Default
            };
        }

        private void DrawDenyListsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Power-tool deny lists", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Regex patterns that block destructive execute_csharp snippets and execute_menu " +
                "paths BEFORE they run. Built-in defaults block editor exit, bulk asset deletion, " +
                "and unbounded builds. Override with your own list, or set the field to empty to " +
                "disable. Bypass per-request via gate: \"off\" + confirm_bypass: true (audited). " +
                "Edit the patterns directly in the settings file shown under Storage.",
                MessageType.None);

            var csharpDefaults = BridgeDenyList.DefaultCSharpDenyPatterns;
            var menuDefaults = BridgeDenyList.DefaultMenuDenyPatterns;
            var csharpActive = BridgeProjectSettings.CSharpDenyPatterns;
            var menuActive = BridgeProjectSettings.MenuDenyPatterns;

            DrawDenyListSummary("execute_csharp", csharpActive, csharpDefaults);
            DrawDenyListSummary("execute_menu", menuActive, menuDefaults);
        }

        private static void DrawDenyListSummary(string tool, string[] active, string[] defaults)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(tool, GUILayout.Width(120));
            // null/empty both mean "built-in defaults" (JsonUtility serializes
            // null as [] on disk, so the distinction is lost across reload).
            var hasCustom = active != null && active.Length > 0;
            var summary = !hasCustom ? $"defaults ({defaults.Length} patterns)" : $"{active.Length} custom pattern(s)";
            EditorGUILayout.LabelField(summary);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAuditLogSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("On-disk audit log", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "When ON, every gate mutation (pass / fail / warn) and deny-list refusal is appended " +
                "to a rolling JSON-lines file under ~/.unity-open-mcp/audit/. Survives domain reload and " +
                "editor restart. Off by default — opt in for security-sensitive contexts.",
                MessageType.None);

            var prev = BridgeProjectSettings.AuditLogEnabled;
            var next = EditorGUILayout.ToggleLeft(
                "Persist gate-run audit records to disk", prev);
            if (next != prev)
            {
                BridgeProjectSettings.SetAuditLogEnabled(next);
            }

            if (prev || next)
            {
                EditorGUILayout.LabelField("Audit dir", BridgeAuditLog.AuditDir, EditorStyles.miniLabel);
            }
        }

        private static GUIContent[] AuthModeLabels()
        {
            var labels = new GUIContent[BridgeAuthPolicy.ValidModes.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(AuthModeDescriptor(BridgeAuthPolicy.ValidModes[i]));
            }
            return labels;
        }

        private static int IndexOfAuthMode(string mode)
        {
            for (int i = 0; i < BridgeAuthPolicy.ValidModes.Length; i++)
            {
                if (BridgeAuthPolicy.ValidModes[i] == mode) return i;
            }
            return 0;
        }

        private static string AuthModeDescriptor(string mode)
        {
            return mode switch
            {
                "none"     => "none  (default — accept any loopback request)",
                "required" => "required  (require Authorization: Bearer <token>)",
                _ => mode ?? BridgeAuthPolicy.Default
            };
        }

        private void DrawActivityLogSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Activity log", EditorStyles.miniBoldLabel);
            var prev = BridgeActivityLog.Verbose;
            var next = EditorGUILayout.ToggleLeft(
                "Verbose mode (truncated request snippet, ≤ " + BridgeActivityLog.SnippetMaxChars + " chars)",
                prev);
            if (next != prev)
            {
                BridgeActivityLog.Verbose = next;
            }
            EditorGUILayout.HelpBox(
                "Default mode captures metadata only (tool name, gate mode, outcome, duration, HTTP status, body byte count). " +
                "Verbose mode additionally stores a truncated request body snippet for debugging. " +
                "Response bodies are never captured in v1.",
                MessageType.None);
        }

        private void DrawSettingsStorageSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Storage", EditorStyles.miniBoldLabel);

            var path = BridgeProjectSettings.SettingsPath ?? "(no project root)";
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Settings file", GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(path, EditorStyles.textField);
            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                RevealSettingsFile();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "v1 schema (`.unity-open-mcp/settings.json`):\n" +
                "  - disabledTools: string[]\n" +
                "  - defaultGateMode: \"enforce\" | \"warn\" | \"off\"\n" +
                "  - autoStart: bool\n" +
                "  - verboseActivityLog: bool\n" +
                "  - authMode: \"none\" | \"required\"\n" +
                "  - bindAddress: \"127.0.0.1\" | \"0.0.0.0\"\n" +
                "  - csharpDenyPatterns: string[] (regex; non-empty overrides defaults)\n" +
                "  - menuDenyPatterns: string[] (regex; non-empty overrides defaults)\n" +
                "  - auditLogEnabled: bool\n" +
                "Future fields can extend this schema in place without breaking v1 readers.",
                MessageType.None);
        }

        private void RevealSettingsFile()
        {
            var path = BridgeProjectSettings.SettingsPath;
            if (string.IsNullOrEmpty(path)) return;
            if (!System.IO.File.Exists(path))
            {
                // Persist current defaults so the file is created, then reveal.
                BridgeProjectSettings.Save();
            }
            if (!System.IO.File.Exists(path)) return;
            EditorUtility.RevealInFinder(path);
        }

        // Passive batch hint shared between the Activity and Settings tabs (M4.5-12).
        private void DrawSettingsPassiveBatchHint()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Batch workflows", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Batch scan / baseline / regression workflows will land " +
                "in a dedicated batch panel in a future update. v1 ships a passive hint only " +
                "— no batch execution controls are exposed in the bridge window.",
                MessageType.None);
        }

        // ---------- Extensions tab (M16 Plan 10 / M18 Plan 4 T18.4.2) ----------

        [NonSerialized] private Vector2 _extensionsTabScroll;

        // The Extensions tab has two sections:
        //  1. Optional Unity dependencies (M18 T18.4.2) — the live install /
        //     status panel for the embedded domain tool groups. Owns the
        //     one-click UPM install/remove actions.
        //  2. Community / planned packs — the legacy ExtensionCatalog mirror.
        //     Shipped domains no longer need a separate pack (their tools are
        //     embedded), so this section now advertises only third-party and
        //     planned packs.
        private void DrawExtensionsTab()
        {
            _extensionsTabScroll = EditorGUILayout.BeginScrollView(_extensionsTabScroll);

            OptionalDependenciesPanel.Draw();

            BridgeGUIUtilities.HorizontalLine(2, 8);

            DrawCommunityPacksSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawCommunityPacksSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Community / planned packs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Shipped domain tools (NavMesh, Input System, ProBuilder, " +
                "Particle System, Animation) are embedded inside the bridge " +
                "and activate automatically when the matching Unity package is " +
                "present — see the Optional Unity dependencies panel above for " +
                "one-click install. The rows below cover the legacy catalog: " +
                "third-party / community packs still live under " +
                "packages/extensions/ as separate UPM packages, and planned " +
                "domains are coming-soon placeholders.",
                MessageType.None);

            BridgeGUIUtilities.HorizontalLine(2, 4);

            var installedCount = 0;
            foreach (var pack in ExtensionCatalog.Packs)
            {
                if (DrawExtensionPackRow(pack)) installedCount++;
            }

            BridgeGUIUtilities.HorizontalLine(2, 4);
            EditorGUILayout.LabelField(
                $"Installed: {installedCount} / {ExtensionCatalog.Packs.Length}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Catalog source: packages/bridge/Editor/UI/ExtensionCatalog.cs " +
                "(add a new pack here + mirror it in hub/src/lib/services/extensions.ts).",
                EditorStyles.miniLabel);
        }

        // Returns true when the pack is installed in this project.
        private bool DrawExtensionPackRow(ExtensionPack pack)
        {
            var installed = IsExtensionPackInstalled(pack);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            // Status dot + display name.
            var dotColor = !pack.Shipped
                ? new Color(0.7f, 0.7f, 0.7f)
                : installed
                    ? new Color(0.6f, 0.9f, 0.6f)
                    : new Color(1f, 0.85f, 0.4f);
            var prev = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label("●", EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            EditorGUILayout.LabelField(pack.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            BridgeGUIUtilities.DrawColoredLabel(
                !pack.Shipped ? "planned" : (installed ? "installed" : "available"),
                dotColor, 90);

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(pack.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Package", GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(pack.Id, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(pack.UpmDependency))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Unity dep", GUILayout.Width(70));
                EditorGUILayout.LabelField(pack.UpmDependency, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            if (pack.ToolIds != null && pack.ToolIds.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Tools", GUILayout.Width(70));
                EditorGUILayout.LabelField(
                    $"{pack.ToolIds.Length} tool(s) — {pack.ToolIds[0]}…",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Install", GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(
                $"\"{pack.Id}\": \"file:../../{pack.LocalPath}\"",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            return installed;
        }

        // A pack is installed when at least one of its tool ids is registered
        // (the extension assembly is loaded → BridgeToolRegistry picked it up).
        // Planned packs (shipped:false) report as not-installed by definition.
        private static bool IsExtensionPackInstalled(ExtensionPack pack)
        {
            if (!pack.Shipped || pack.ToolIds == null || pack.ToolIds.Length == 0)
                return false;
            return BridgeToolRegistry.Contains(pack.ToolIds[0]);
        }
    }
}
