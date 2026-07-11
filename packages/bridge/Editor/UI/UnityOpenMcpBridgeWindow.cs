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
    // M29 Plan 3 — peer-tab IA cleanup. Batch and Info are no longer peer
    // tabs: Batch is a section under Activity (its panel still renders via
    // BridgeBatchPanel.Draw), and Info is a toolbar About foldout (DrawAboutFoldout).
    // The enum is the source of truth for the toolbar row; do not re-add
    // removed values without updating the prefs migration below.
    public enum BridgeWindowTab
    {
        Status,
        Tools,
        Gate,
        Activity,
        Settings,
        Extensions
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

        // M29 Plan 3 — About foldout state. Info is no longer a peer tab; the
        // toolbar "?" button toggles this foldout, which renders the same docs
        // / repo / quick-reference links the old Info tab carried.
        [NonSerialized] private bool _aboutFoldout;

        private string _lastPingResult = "";
        private MessageType _lastPingMessageType = MessageType.None;
        private bool _pingInFlight;

        // M29 Plan 2 — the two-click Stop-confirm transient is now owned by
        // BridgeStopConfirmCoordinator so the toolbar and this window share
        // the same confirm policy. The window no longer keeps its own copy.

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

        // Gate tab state (M4.5-7/8/9). The page-level scroll is owned by the
        // shell (DrawContent's _contentScroll); only the bounded result-snippet
        // scrolls below remain.
        [NonSerialized] private string _manualValidateInput = "";
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

        // Activity tab state (M4.5-10). No page scroll — the shell owns it.
        [NonSerialized] private bool _activityVerboseFoldout = false;
        [NonSerialized] private bool _activityFilterToolRequests = true;
        [NonSerialized] private bool _activityFilterDisabled = true;
        [NonSerialized] private bool _activityFilterErrors = true;
        [NonSerialized] private bool _activityFilterPing = false;
        [NonSerialized] private bool _activityFilterResources = false;
        [NonSerialized] private string _activitySearch = "";

        // Settings tab state (M4.5-11). No page scroll — the shell owns it.

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

        // M29 Plan 3 — prefs migration. Before this plan the enum had 8 values
        // (Status, Tools, Gate, Activity, Batch, Settings, Extensions, Info).
        // Batch and Info were removed; the surviving indices shifted down. An
        // old saved index therefore no longer names the same tab. We remap the
        // legacy indices once and persist the migrated value so future loads
        // skip the map. Anything out of range falls back to Status.
        //
        // Legacy → new map:
        //   0 Status      → 0 Status
        //   1 Tools       → 1 Tools
        //   2 Gate        → 2 Gate
        //   3 Activity    → 3 Activity
        //   4 Batch       → 3 Activity  (Batch is now an Activity section)
        //   5 Settings    → 4 Settings
        //   6 Extensions  → 5 Extensions
        //   7 Info        → 0 Status    (Info is now a toolbar About foldout)
        private static BridgeWindowTab MigrateSelectedTab(int oldIndex)
        {
            switch (oldIndex)
            {
                case 0: return BridgeWindowTab.Status;
                case 1: return BridgeWindowTab.Tools;
                case 2: return BridgeWindowTab.Gate;
                case 3:
                case 4: return BridgeWindowTab.Activity;
                case 5: return BridgeWindowTab.Settings;
                case 6: return BridgeWindowTab.Extensions;
                case 7: return BridgeWindowTab.Status;
                default: return BridgeWindowTab.Status;
            }
        }

        private BridgeWindowTab LoadSelectedTabWithMigration()
        {
            var raw = EditorPrefs.GetInt(SelectedTabPref, (int)BridgeWindowTab.Status);
            // Any value outside the current enum range is legacy or corrupt —
            // remap it and persist so the migration runs exactly once.
            var max = Enum.GetValues(typeof(BridgeWindowTab)).Length - 1;
            if (raw < 0 || raw > max)
            {
                var migrated = MigrateSelectedTab(raw);
                EditorPrefs.SetInt(SelectedTabPref, (int)migrated);
                return migrated;
            }
            return (BridgeWindowTab)raw;
        }

        private void OnEnable()
        {
            _currentTab = LoadSelectedTabWithMigration();
            // EditorApplication.update drives the transient Stop-confirm
            // countdown only — it does NOT repaint every frame (see
            // EditorUpdateTick). Data-change repaints come from the *.Changed
            // events below via OnDataChanged.
            EditorApplication.update -= EditorUpdateTick;
            EditorApplication.update += EditorUpdateTick;
            // *.Changed → repaint immediately. These fire when underlying state
            // changes (tool toggle, gate run, activity event, batch progress,
            // settings write), so a repaint is always warranted. This is the
            // event-driven path that keeps live tabs fresh without a tick.
            BridgeToolTogglePolicy.Changed -= OnDataChanged;
            BridgeToolTogglePolicy.Changed += OnDataChanged;
            BridgeGateDefaultPolicy.Changed -= OnDataChanged;
            BridgeGateDefaultPolicy.Changed += OnDataChanged;
            BridgeGateRunHistory.Changed -= OnDataChanged;
            BridgeGateRunHistory.Changed += OnDataChanged;
            BridgeActivityLog.Changed -= OnDataChanged;
            BridgeActivityLog.Changed += OnDataChanged;
            BridgeBatchRunHistory.Changed -= OnDataChanged;
            BridgeBatchRunHistory.Changed += OnDataChanged;
            BridgeProjectSettings.Changed -= OnDataChanged;
            BridgeProjectSettings.Changed += OnDataChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdateTick;
            BridgeToolTogglePolicy.Changed -= OnDataChanged;
            BridgeGateDefaultPolicy.Changed -= OnDataChanged;
            BridgeGateRunHistory.Changed -= OnDataChanged;
            BridgeActivityLog.Changed -= OnDataChanged;
            BridgeBatchRunHistory.Changed -= OnDataChanged;
            BridgeProjectSettings.Changed -= OnDataChanged;
            EditorPrefs.SetInt(SelectedTabPref, (int)_currentTab);
        }

        // EditorApplication.update handler. The ONLY periodic repaint need is
        // the two-click Stop-confirm countdown: its "Confirm within Xs" label
        // counts down, and the pending state must auto-expire after 5s. While
        // that transient is active we repaint each frame (≤ 5s, negligible).
        //
        // M29 Plan 2 — the transient now lives on BridgeStopConfirmCoordinator
        // (shared with the toolbar). The window still drives the countdown
        // repaint + auto-expire tick while it is open.
        //
        // Everything else is event-driven and needs no tick:
        //  - Activity / Gate-run / Batch-progress updates arrive via *.Changed
        //    → OnDataChanged → Repaint.
        //  - Async ping/probe completions call Repaint() in their finally block.
        //  - Optional-deps UPM install/remove uses a modal progress bar + its
        //    own EditorApplication.update poll; no window repaint needed.
        // So when no transient is active, this method is a no-op and an idle
        // window (e.g. Settings) burns zero repaints from the update loop.
        private void EditorUpdateTick()
        {
            BridgeStopConfirmCoordinator.Tick();
            if (!BridgeStopConfirmCoordinator.IsArmed) return;
            Repaint();
        }

        // *.Changed handler — always repaint. The event means "underlying data
        // changed, the window should show it". Distinct from EditorUpdateTick
        // (which is conditional on a transient) so data changes always refresh
        // regardless of which tab is visible.
        private void OnDataChanged() => Repaint();

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
                // M29 Plan 3 — flexible width (sized to label) replaces the old
                // fixed 72px so the surviving six tabs fit at the 520px minSize
                // without clipping. ExpandWidth(false) keeps each button to its
                // content width and lets FlexibleSpace absorb the remainder.
                if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
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

            // M29 Plan 3 — About button. Info is no longer a peer tab; this
            // button toggles the About foldout (docs / repo / quick-reference
            // links) that draws at the top of DrawContent regardless of tab.
            var aboutPrev = GUI.backgroundColor;
            if (_aboutFoldout) GUI.backgroundColor = new Color(0.7f, 0.85f, 1f);
            if (GUILayout.Button(
                    new GUIContent("?",
                        "Docs, repository, and quick-reference links (bind URL, settings file, instance lock, audit dir)."),
                    EditorStyles.toolbarButton,
                    GUILayout.Width(24)))
            {
                _aboutFoldout = !_aboutFoldout;
            }
            GUI.backgroundColor = aboutPrev;

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

        // Short hover tooltip for each tab button — one line, no internal jargon.
        private static string TabTooltip(BridgeWindowTab tab)
        {
            return tab switch
            {
                BridgeWindowTab.Status => "Listener state and start/stop controls.",
                BridgeWindowTab.Tools => "Catalog of dispatchable tools; toggle or inspect each tool.",
                BridgeWindowTab.Gate => "Gate policy: project default, latest result, manual validate.",
                BridgeWindowTab.Activity => "Live log of HTTP events and in-Editor batch runs.",
                BridgeWindowTab.Settings => "Project runtime settings persisted to settings.json.",
                BridgeWindowTab.Extensions => "Optional Unity domain tools and extensions.",
                _ => null
            };
        }

        private void DrawContent()
        {
            // Single scroll owner: the shell wraps every tab in ONE page scroll.
            // Tabs must NOT open a second full-page BeginScrollView (nested
            // IMGUI scrolls fight the mouse wheel). Bounded list scrolls
            // (MaxHeight/MinHeight on Gate snippets, Batch rows, Tools
            // token-summary/pagination) are allowed because they are nested
            // list regions, not competing full-page scrolls.
            _contentScroll = EditorGUILayout.BeginScrollView(_contentScroll);

            // M29 Plan 3 — About foldout renders at the top of the scroll
            // regardless of the active tab so the links are reachable from
            // anywhere without a dedicated tab. It scrolls with the content.
            if (_aboutFoldout)
            {
                DrawAboutFoldout();
                BridgeGUIUtilities.HorizontalLine(2, 4);
            }

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


    }
}
