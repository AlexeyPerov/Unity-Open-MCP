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
    public enum BridgeWindowTab
    {
        Status,
        Tools,
        Gate,
        Activity,
        Batch,
        Settings,
        Extensions,
        Info
    }

    public partial class UnityOpenMcpBridgeWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Unity Open MCP Bridge";
        private const string SelectedTabPref = "UOMCB_SelectedTab";

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

        // Tools pagination (M20 UX pass). Page size 20 keeps a long tool catalog
        // navigable; "All" is offered only when the filtered set is small.
        private const int ToolsPageSize = 20;
        private const int ToolsShowAllThreshold = 150;
        [NonSerialized] private int? _toolPageToShow = 0;
        [NonSerialized] private Vector2 _toolPagesScroll;

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
        // Item C — two-click confirm state for the "Clear history" button on the
        // Gate tab's checkpoint section. Mirrors the listener stop pattern.
        [NonSerialized] private bool _checkpointClearPending;

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

        // Status tab foldouts — Editor state and Project are diagnostic/debug
        // fields, so they are collapsed by default to keep the runtime + control
        // surface at the top focused.
        [NonSerialized] private bool _statusEditorStateFoldout = false;
        [NonSerialized] private bool _statusProjectFoldout = false;

        // MCP connectivity panel (read-only diagnostics). The bridge has no
        // handle on the external Node MCP server process, so this surfaces the
        // signals the MCP server itself uses (instance lock + derived port) and
        // offers an opt-in probe via `npx unity-open-mcp status`.
        [NonSerialized] private bool _mcpConnectivityFoldout = false;
        [NonSerialized] private string _mcpProbeResult = "";
        [NonSerialized] private MessageType _mcpProbeMessageType = MessageType.None;
        [NonSerialized] private bool _mcpProbeInFlight;

        // "Configure AI client" panel (M27 Plan 5) — generates the MCP
        // client config snippet for the selected client against the
        // current project so an operator can copy it without leaving
        // Unity. The Hub wizard remains the full one-click writer; this
        // panel mirrors the envelope shapes so the bytes match.
        [NonSerialized] private bool _configureClientFoldout = false;
        [NonSerialized] private int _configureClientIndex = 0;
        [NonSerialized] private string _configureClientSnippet = "";
        [NonSerialized] private string _configureClientTargetPath = "";
        [NonSerialized] private bool _configureClientConfigured;

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
            BridgeBatchRunHistory.Changed -= RepaintTick;
            BridgeBatchRunHistory.Changed += RepaintTick;
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
            BridgeBatchRunHistory.Changed -= RepaintTick;
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
                var label = new GUIContent(TabLabel(tab), TabTooltip(tab));
                var prev = GUI.backgroundColor;
                if (isCurrent) GUI.backgroundColor = new Color(0.7f, 0.85f, 1f);
                if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(72)))
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
                BridgeWindowTab.Batch => "Batch",
                BridgeWindowTab.Settings => "Settings",
                BridgeWindowTab.Extensions => "Extensions",
                BridgeWindowTab.Info => "Info",
                _ => tab.ToString()
            };
        }

        // Short hover tooltip for each tab button — one line, no internal jargon.
        private static string TabTooltip(BridgeWindowTab tab)
        {
            return tab switch
            {
                BridgeWindowTab.Status => "Listener state and start/stop controls.",
                BridgeWindowTab.Tools => "Catalog of dispatchable tools; toggle or inspect each tool.",
                BridgeWindowTab.Gate => "Gate policy: project default, latest result, manual validate.",
                BridgeWindowTab.Activity => "Live log of HTTP events hitting the bridge.",
                BridgeWindowTab.Batch => "In-Editor batch-run progress and per-entry results (read-only).",
                BridgeWindowTab.Settings => "Project runtime settings persisted to settings.json.",
                BridgeWindowTab.Extensions => "Optional Unity domain tools and community packs.",
                BridgeWindowTab.Info => "Links to docs, repo, and quick references.",
                _ => null
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
                case BridgeWindowTab.Batch:
                    DrawBatchTab();
                    break;
                case BridgeWindowTab.Settings:
                    DrawSettingsTab();
                    break;
                case BridgeWindowTab.Extensions:
                    DrawExtensionsTab();
                    break;
                case BridgeWindowTab.Info:
                    DrawInfoTab();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }


    }
}
