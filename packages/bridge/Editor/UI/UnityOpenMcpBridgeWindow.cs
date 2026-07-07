using System;
using System.Collections.Generic;
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

    public class UnityOpenMcpBridgeWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Unity Open MCP Bridge";
        private const string SelectedTabPref = "UOMCB_SelectedTab";
        private const string BindAddress = "127.0.0.1";

        // Canonical repo URL — single source for every Info-tab link and any
        // in-window reference to the project.
        private const string RepoUrl = "https://github.com/AlexeyPerov/Unity-Open-MCP";

        // Shared hover-tooltip text for cell values / labels that surface
        // internal bridge concepts. Centralised so wording stays consistent
        // across the Tools tab, details foldout, and Activity rows.
        private const string TooltipMutating =
            "Mutating tool: changes Unity state (scene, assets, settings). " +
            "Runs the gate safety flow (checkpoint → mutate → validate → delta) when the gate mode is enforce/warn.";
        private const string TooltipReadOnly =
            "Read-only tool: does not change Unity state. Skips the gate flow.";
        private const string TooltipGateEnforce =
            "Gate mode 'enforce': the tool runs the checkpoint → mutate → validate flow and the MCP call fails if the mutation introduces new compile errors.";
        private const string TooltipGateWarn =
            "Gate mode 'warn': the gate still runs, but new compile errors surface as warnings rather than failing the call.";
        private const string TooltipGateOff =
            "Gate mode 'off': no checkpoint/validate — the mutation runs without the safety flow. Opt-in only.";
        private const string TooltipGateNa =
            "Read-only tool — the gate does not apply.";
        private const string TooltipSourceRegistry =
            "Registry tool: discovered via the [BridgeTool] attribute on a method. Its declaring type and parameter schema are reflected at load time.";
        private const string TooltipSourceHardcoded =
            "Built-in tool: dispatched by a hardcoded path in the bridge (not attribute-discovered). The parameter schema is mirrored from the MCP server definitions.";
        private const string TooltipEnabledToggle =
            "When unchecked, POST /tools/<name> is blocked and returns a 'tool_disabled' error before dispatch. State persists in settings.json.";
        private const string TooltipListener =
            "The local HTTP listener that MCP clients connect to. Green = running, yellow = compiling, red = stopped.";
        private const string TooltipBindUrl =
            "The URL MCP clients use to reach this bridge. Copy this into your MCP client config (or let the MCP server auto-discover it via the instance lock).";
        private const string TooltipPing =
            "Send a GET /ping to the local listener to confirm it is responding. Useful after Start or when an agent reports a connection failure.";

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
            BridgeGUIUtilities.FieldLabel("Listener", TooltipListener, 120);
            var prev = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusText, EditorStyles.boldLabel);
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Bind URL", TooltipBindUrl, 120);
            EditorGUILayout.SelectableLabel($"http://{BindAddress}:{BridgeHttpServer.Port}/", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Mode", "Editor session mode reported to MCP clients (live = a real Editor process).", 120);
            EditorGUILayout.LabelField(BridgeSession.Mode);
            EditorGUILayout.EndHorizontal();

            BridgeGUIUtilities.HorizontalLine(8, 6);
            EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Start / Stop control the local HTTP listener that MCP clients connect to.\n" +
                "• Start — launch the listener so agents can call tools. Auto-starts on Editor load unless disabled in Settings.\n" +
                "• Stop — shut the listener down. Any connected agent loses MCP connectivity until you Start again (two-click confirm).",
                MessageType.None);
            DrawRuntimeControls();
            DrawLocalPing();

            if (!string.IsNullOrEmpty(_lastPingResult))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_lastPingResult, _lastPingMessageType);
            }

            DrawPortInUseRecoveryIfNeeded();

            BridgeGUIUtilities.HorizontalLine(8, 6);

            DrawMcpConnectivitySection();

            BridgeGUIUtilities.HorizontalLine(8, 6);

            // Editor state + Project are diagnostic fields — collapsed by default.
            _statusEditorStateFoldout = EditorGUILayout.Foldout(
                _statusEditorStateFoldout, "Editor state (diagnostics)", true);
            if (_statusEditorStateFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Compiling", "Whether Unity is currently recompiling scripts. During compile, tool dispatch is paused.", 120);
                var compileColor = BridgeSession.IsCompiling ? Color.yellow : new Color(0.6f, 0.9f, 0.6f);
                var prevC = GUI.color;
                GUI.color = compileColor;
                GUILayout.Label(BridgeSession.IsCompiling ? "Yes" : "No", EditorStyles.boldLabel);
                GUI.color = prevC;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Play mode", "Whether the Editor is in Play mode. Some tools behave differently (or are gated) while playing.", 120);
                var playColor = BridgeSession.IsPlaying ? new Color(0.5f, 0.8f, 1f) : new Color(0.7f, 0.7f, 0.7f);
                prevC = GUI.color;
                GUI.color = playColor;
                GUILayout.Label(BridgeSession.IsPlaying ? "Playing" : "Edit", EditorStyles.boldLabel);
                GUI.color = prevC;
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            _statusProjectFoldout = EditorGUILayout.Foldout(
                _statusProjectFoldout, "Project (diagnostics)", true);
            if (_statusProjectFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Project path", "Absolute path to the Unity project root (the folder containing Assets/).", 120);
                EditorGUILayout.SelectableLabel(BridgeSession.ProjectPath ?? "(unknown)", EditorStyles.textField);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Unity version", "Unity Editor version this bridge is running in.", 120);
                EditorGUILayout.LabelField(BridgeSession.UnityVersion ?? "(unknown)");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Bridge version", "Version of the bridge package loaded in this Editor.", 120);
                EditorGUILayout.LabelField(BridgeSession.BridgeVersion);
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
        }

        private void DrawRuntimeControls()
        {
            var running = BridgeHttpServer.IsRunning;
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(running);
            if (GUILayout.Button(new GUIContent("Start", "Launch the local HTTP listener so MCP clients (agents) can connect and call tools."), GUILayout.Width(110)))
            {
                try
                {
                    BridgeHttpServer.Start();
                    if (!BridgeHttpServer.IsRunning && !string.IsNullOrEmpty(BridgeHttpServer.LastStartError))
                    {
                        _lastPingResult = $"Start failed: {BridgeHttpServer.LastStartError}";
                        _lastPingMessageType = MessageType.Error;
                    }
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
            var buttonTooltip = _stopConfirmPending
                ? "Click again to confirm: stops the listener and drops MCP connectivity for any active agent."
                : "Shut the listener down. Requires a two-click confirm because it disconnects any active agent.";
            if (GUILayout.Button(new GUIContent(buttonLabel, buttonTooltip), GUILayout.Width(110)))
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

        private void DrawPortInUseRecoveryIfNeeded()
        {
            if (BridgeHttpServer.IsRunning)
                return;

            var startError = BridgeHttpServer.LastStartError;
            if (!BridgeStartRecovery.IsPortInUseError(startError))
                return;

            EditorGUILayout.Space(4);
            var projectPath = BridgeSession.ProjectPath ?? BridgeHttpServer.GetProjectPathForPort();
            EditorGUILayout.HelpBox(
                BridgeStartRecovery.FormatPortInUseRecovery(projectPath, BridgeHttpServer.Port),
                MessageType.Info);
        }

        private void DrawLocalPing()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_pingInFlight || !BridgeHttpServer.IsRunning);
            if (GUILayout.Button(new GUIContent("Ping", TooltipPing), GUILayout.Width(110)))
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

        // ---------- MCP connectivity panel (read-only diagnostics) ----------
        //
        // The bridge runs inside Unity and has no handle on the external Node MCP
        // server process (it is spawned by the MCP client via `npx`). This panel
        // surfaces the same signals the MCP server uses to discover this bridge
        // (derived port + instance lock), plus an opt-in probe that shells out to
        // `npx unity-open-mcp status` so the operator can self-diagnose end-to-end
        // connectivity without leaving Unity. It is strictly read-only — there is
        // no start/stop of the MCP server here (that's the MCP client's job).

        private void DrawMcpConnectivitySection()
        {
            _mcpConnectivityFoldout = EditorGUILayout.Foldout(
                _mcpConnectivityFoldout, "MCP connectivity", true);
            if (!_mcpConnectivityFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "The bridge runs inside Unity; the MCP server is a separate Node process the MCP client " +
                "(Cursor/Claude) spawns via `npx`. This panel shows the discovery signals the MCP server " +
                "uses to reach this bridge, and lets you probe end-to-end connectivity.",
                MessageType.None);

            var projectPath = BridgeSession.ProjectPath;

            // Derived port — what the MCP server computes for this project.
            var hasProject = !string.IsNullOrEmpty(projectPath);
            var derivedPort = hasProject ? InstancePortResolver.ComputePort(projectPath) : 0;
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Derived port",
                "Port the MCP server derives for this project (20000 + sha256(path) % 10000). " +
                "UNITY_OPEN_MCP_BRIDGE_PORT env / -UNITY_OPEN_MCP_BRIDGE_PORT CLI override this on the server side.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? derivedPort.ToString() : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Instance lock — the discovery/heartbeat file the MCP server reads.
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Instance lock",
                "Lock + heartbeat file at ~/.unity-open-mcp/instances/<hash>.json. The MCP server reads it " +
                "to find this bridge's port + auth token without an HTTP round-trip.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? InstancePortResolver.LockPath(projectPath) : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Live lock state parsed from the on-disk file.
            var lockAcquired = BridgeInstanceLock.IsAcquired;
            var lockJson = BridgeInstanceLock.ReadCurrentJson();
            var snap = BridgeInstanceLock.TryParseSnapshot(lockJson);
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Lock state",
                "Whether this bridge has acquired the instance lock and the last heartbeat written.",
                120);
            var prevColor = GUI.color;
            GUI.color = lockAcquired ? new Color(0.6f, 0.9f, 0.6f) : new Color(1f, 0.5f, 0.5f);
            GUILayout.Label(lockAcquired ? "acquired" : "not acquired", EditorStyles.boldLabel);
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            if (snap.Valid)
            {
                if (!string.IsNullOrEmpty(snap.State))
                {
                    EditorGUILayout.BeginHorizontal();
                    BridgeGUIUtilities.FieldLabel("Heartbeat state", "Last editor state written to the lock.", 120);
                    EditorGUILayout.LabelField(snap.State);
                    EditorGUILayout.EndHorizontal();
                }
                if (!string.IsNullOrEmpty(snap.HeartbeatAt))
                {
                    EditorGUILayout.BeginHorizontal();
                    BridgeGUIUtilities.FieldLabel("Heartbeat at", "Timestamp of the last heartbeat write (UTC).", 120);
                    EditorGUILayout.LabelField(snap.HeartbeatAt);
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Expected launch command for the MCP client — what the operator
            // (or their MCP client config) should run to connect.
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Client launch",
                "The command the MCP client runs to start the MCP server for this project. " +
                "The MCP server auto-discovers the bridge port via the instance lock.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? $"UNITY_PROJECT_PATH=\"{projectPath}\" npx -y unity-open-mcp@latest" : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Opt-in probe. Shells out to the MCP server's own status command.
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_mcpProbeInFlight || !hasProject);
            if (GUILayout.Button(
                new GUIContent(_mcpProbeInFlight ? "Probing…" : "Probe MCP server",
                    "Runs `npx -y unity-open-mcp@latest status` to check end-to-end discovery + bridge reachability. " +
                    "Requires Node/npx on PATH. Does not start a long-running server."),
                GUILayout.Width(160)))
            {
                _ = RunMcpProbeAsync(projectPath);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_mcpProbeResult))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_mcpProbeResult, _mcpProbeMessageType);
            }

            EditorGUI.indentLevel--;
        }

        // Probe the MCP server end-to-end by shelling out to its status command.
        // Runs the process on a background thread (npx can take several seconds
        // to resolve the package) and marshals the result back to the UI thread
        // for display. Failures (npx missing, non-zero exit, timeout) are
        // reported clearly and never fatal.
        private async Task RunMcpProbeAsync(string projectPath)
        {
            _mcpProbeInFlight = true;
            _mcpProbeResult = "Probing… (running `npx -y unity-open-mcp@latest status`)";
            _mcpProbeMessageType = MessageType.Info;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "npx",
                    Arguments = $"-y unity-open-mcp@latest status --project \"{projectPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var stdout = await ReadProcessAsync(psi, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                var trimmed = (stdout.Stdout ?? "").Trim();
                var trimmedErr = (stdout.Stderr ?? "").Trim();
                var exit = stdout.ExitCode;

                if (exit == 0)
                {
                    _mcpProbeResult = string.IsNullOrEmpty(trimmed)
                        ? "Probe completed (exit 0, no output)."
                        : trimmed;
                    _mcpProbeMessageType = MessageType.Info;
                }
                else
                {
                    var msg = $"Probe exited with code {exit}.";
                    if (!string.IsNullOrEmpty(trimmedErr)) msg += $"\n{trimmedErr}";
                    if (!string.IsNullOrEmpty(trimmed)) msg += $"\n--- stdout ---\n{trimmed}";
                    _mcpProbeResult = msg;
                    _mcpProbeMessageType = MessageType.Warning;
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                // Most common: `npx` not found on PATH.
                _mcpProbeResult =
                    $"Could not start `npx` ({e.Message}). Install Node.js (includes npx) or add it to PATH " +
                    "to use the probe. The bridge itself does not require Node.";
                _mcpProbeMessageType = MessageType.Warning;
            }
            catch (Exception e)
            {
                _mcpProbeResult = $"Probe failed: {e.Message}";
                _mcpProbeMessageType = MessageType.Error;
            }
            finally
            {
                _mcpProbeInFlight = false;
                Repaint();
            }
        }

        // Run a process to completion off the main thread, with a hard timeout.
        // Returns merged stdout/stderr + exit code. Captures-but-discards the
        // process on timeout so it can't outlive the probe.
        private static async Task<ProcessOutput> ReadProcessAsync(
            System.Diagnostics.ProcessStartInfo psi, TimeSpan timeout)
        {
            return await Task.Run(() =>
            {
                using var p = new System.Diagnostics.Process { StartInfo = psi };
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                if (!p.Start()) return new ProcessOutput("", "Failed to start process.", -1);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch { }
                    return new ProcessOutput(stdoutBuilder.ToString(), "(timed out)", -1);
                }
                // WaitForExit(int) can return before async streams flush; this
                // no-arg overload blocks until the readers close.
                p.WaitForExit();
                return new ProcessOutput(stdoutBuilder.ToString(), stderrBuilder.ToString(), p.ExitCode);
            }).ConfigureAwait(true);
        }

        private readonly struct ProcessOutput
        {
            public readonly string Stdout;
            public readonly string Stderr;
            public readonly int ExitCode;
            public ProcessOutput(string stdout, string stderr, int exitCode)
            {
                Stdout = stdout;
                Stderr = stderr;
                ExitCode = exitCode;
            }
        }

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

            _toolListScroll = EditorGUILayout.BeginScrollView(_toolListScroll);

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
            DrawGlobalGateModeControl(showStorageHint: true);
        }

        // Shared, highlighted rendering of the project-wide gate control, used by
        // both the Gate tab and the Settings tab so there is one visual source of
        // truth. This is the global gate on/off: setting it to `off` disables the
        // checkpoint→mutate→validate safety flow project-wide for any mutating
        // call that does not send an explicit request-level `gate`. The control is
        // tinted yellow to stand out, and the dangerous states (`off`, `warn`)
        // get explicit warning text so the operator can't miss them.
        private static void DrawGlobalGateModeControl(bool showStorageHint)
        {
            var current = BridgeGateDefaultPolicy.GetDefault();

            // Soft yellow tint over the whole control so the global safety knob is
            // visually distinct from the per-tool rows around it.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.96f, 0.7f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            try
            {
                EditorGUILayout.LabelField(
                    "This is the global gate on/off control for this project.",
                    EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Default mode", GUILayout.Width(120));
                var newMode = EditorGUILayout.Popup(IndexOfMode(current), ModeLabels());
                EditorGUILayout.EndHorizontal();
                if (newMode != IndexOfMode(current))
                {
                    BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.ValidModes[newMode]);
                    current = BridgeGateDefaultPolicy.GetDefault();
                }

                EditorGUILayout.LabelField("Effective policy", ModeDescriptor(current), EditorStyles.miniLabel);

                // State-specific callouts so the dangerous modes are unmissable.
                if (current == BridgeGateDefaultPolicy.Off)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is OFF project-wide. Mutating tools run WITHOUT the checkpoint → validate safety flow, " +
                        "so a mutation that introduces compile errors will not fail the MCP call. " +
                        "This is the global turn-off — re-enable with `enforce` or `warn`.",
                        MessageType.Error);
                }
                else if (current == BridgeGateDefaultPolicy.Warn)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is in `warn` mode project-wide: mutating tools still run the safety flow, but new " +
                        "compile errors surface as warnings instead of failing the MCP call.",
                        MessageType.Warning);
                }

                EditorGUILayout.LabelField("Precedence", BridgeGateDefaultPolicy.DescribePrecedence(), EditorStyles.miniLabel);
                if (showStorageHint)
                {
                    EditorGUILayout.LabelField(
                        "Storage",
                        BridgeProjectSettings.SettingsPath ?? "(no project root)",
                        EditorStyles.miniLabel);
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
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
            EditorGUILayout.LabelField(
                new GUIContent("Tool", "The mutating tool call that triggered this gate run."),
                new GUIContent(latest.ToolName ?? "(unknown)"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Mode",
                "The effective gate mode that ran for this call (request-level gate overrides the project default).",
                120);
            EditorGUILayout.LabelField(latest.EffectiveMode ?? "(unknown)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Outcome",
                "Result of the gate flow. Passed = no new errors. Warned = errors downgraded to warnings. Failed = new errors blocked the call. Skipped = gate off.",
                120);
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

            EditorGUILayout.LabelField(
                new GUIContent("Delta",
                    "Change in console errors/warnings between the pre-mutation checkpoint and the post-mutation validate. 'new' = introduced by the call; 'resolved' = fixed by it."),
                new GUIContent($"new errors: {latest.NewErrors}    new warnings: {latest.NewWarnings}    resolved errors: {latest.ResolvedErrors}    resolved warnings: {latest.ResolvedWarnings}"));

            EditorGUILayout.LabelField(
                new GUIContent("Durations (ms)",
                    "Time spent in each gate phase: snapshotting the error state (checkpoint), re-validating after the mutation (validation), and the total."),
                new GUIContent($"checkpoint: {latest.CheckpointDurationMs}    validation: {latest.ValidationDurationMs}    total: {latest.TotalGateDurationMs}"));

            if (latest.CategoriesRun != null && latest.CategoriesRun.Length > 0)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Categories", "Verify rule categories that ran during the post-mutation validate pass."),
                    new GUIContent(string.Join(", ", latest.CategoriesRun)));
            }
            else
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Categories", "Verify rule categories that ran during the post-mutation validate pass."),
                    new GUIContent("(none — gate skipped or off)"));
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

            // Item C — let the operator reclaim the session checkpoint memory
            // explicitly. Checkpoints are not persisted, so this touches nothing
            // on disk; gate-run history (BridgeGateRunHistory) is left intact.
            // Two-click confirm mirrors the listener stop pattern so an
            // accidental click can't drop a baseline an agent is still using.
            var count = CheckpointStore.Count;
            if (count > 0)
            {
                var pending = _checkpointClearPending;
                var label = pending ? "Confirm clear" : "Clear history";
                var tooltip = pending
                    ? "Click again to confirm: removes all session checkpoints from the in-memory ring buffer."
                    : "Removes all session checkpoints from the in-memory ring buffer. " +
                      "Checkpoints are not persisted to disk, so nothing on disk is affected. " +
                      "Gate-run history is left intact. Re-capture with the unity_open_mcp_checkpoint_create tool.";
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.miniButton, GUILayout.Width(120)))
                {
                    if (pending)
                    {
                        CheckpointStore.Clear();
                        _checkpointClearPending = false;
                        Repaint();
                    }
                    else
                    {
                        _checkpointClearPending = true;
                        Repaint();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                _checkpointClearPending = false;
            }
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

            if (GUILayout.Button(new GUIContent("Clear", "Empty the in-memory activity buffer. Not persisted, so this only affects the current session."), EditorStyles.miniButton, GUILayout.Width(80)))
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
            _activityFilterToolRequests = DrawActivityFilterToggle(
                _activityFilterToolRequests, "Tool requests", 120,
                "Show successful / in-flight tool dispatch calls (POST /tools/<name>).");
            _activityFilterDisabled = DrawActivityFilterToggle(
                _activityFilterDisabled, "Tool disabled", 110,
                "Show calls rejected because the tool is toggled off (tool_disabled error).");
            _activityFilterErrors = DrawActivityFilterToggle(
                _activityFilterErrors, "Errors", 80,
                "Show failed dispatches, unknown paths, and resource errors.");
            _activityFilterPing = DrawActivityFilterToggle(
                _activityFilterPing, "/ping", 70,
                "Show connectivity checks (GET /ping) the MCP server sends.");
            _activityFilterResources = DrawActivityFilterToggle(
                _activityFilterResources, "Resources", 90,
                "Show resource reads (GET /resources/...).");
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(new GUIContent("Search", "Filter events by tool name, error code/message, or gate mode."), GUILayout.Width(50));
            var newSearch = EditorGUILayout.TextField(_activitySearch ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(160));
            if (newSearch != _activitySearch)
            {
                _activitySearch = newSearch ?? "";
            }
            EditorGUILayout.EndHorizontal();
        }

        // Filter toggle with a hover tooltip. EditorGUILayout.ToggleLeft does
        // not take a GUIContent, so we reserve the rect and use GUI.Toggle.
        private static bool DrawActivityFilterToggle(bool value, string label, int width, string tooltip)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(label, tooltip), EditorStyles.label, GUILayout.Width(width));
            return GUI.Toggle(rect, value, new GUIContent(label, tooltip));
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
                EditorGUILayout.LabelField(
                    new GUIContent($"gate: {evt.GateMode}", GateModeTooltip(evt.GateMode)),
                    GUILayout.Width(110));
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
                "fallback (or headless Editor CLI). Live in-Editor batch-run progress and per-entry " +
                "results are shown in the Batch tab. Use the Gate tab's Manual validate for ad-hoc " +
                "scoped scans in the meantime.",
                MessageType.None);
        }

        // ---------- Batch tab (T20.7.5.1) ----------

        // The Batch tab is a thin host for BridgeBatchPanel — the panel owns its
        // own scroll/foldout state (mirrors OptionalDependenciesPanel). The tab
        // itself just scopes the content scroll and forwards to the panel.
        [NonSerialized] private Vector2 _batchTabScroll;

        private void DrawBatchTab()
        {
            _batchTabScroll = EditorGUILayout.BeginScrollView(_batchTabScroll);
            BridgeBatchPanel.Draw();
            EditorGUILayout.EndScrollView();
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
            DrawVerifyCacheSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawBatchLimitsSection();
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
            DrawGlobalGateModeControl(showStorageHint: false);
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

        private void DrawVerifyCacheSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Verify cache", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Time-to-live for the in-memory verify health snapshot. Drives how fresh the " +
                "`health/summary` MCP resource and the `gate_budget_estimate` \"cache\" mode are — " +
                "within the TTL they reuse the last scan/validate/gate result instead of re-running. " +
                "Shorter = fresher but more work; longer = faster but staler. Range " +
                $"{BridgeProjectSettings.MinVerifyCacheTtlSeconds}–{BridgeProjectSettings.MaxVerifyCacheTtlSeconds}s, " +
                $"default {BridgeProjectSettings.DefaultVerifyCacheTtlSeconds}s.",
                MessageType.None);

            var prev = BridgeProjectSettings.VerifyCacheTtlSeconds;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("TTL (seconds)", GUILayout.Width(120));
            var next = EditorGUILayout.IntField(prev);
            EditorGUILayout.EndHorizontal();
            // IntField returns 0 for empty input and allows arbitrary values;
            // the setter clamps, but we also surface the clamped delta visually.
            if (next != prev)
            {
                BridgeProjectSettings.SetVerifyCacheTtlSeconds(next);
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Effective", GUILayout.Width(120));
            EditorGUILayout.LabelField(
                $"{BridgeProjectSettings.VerifyCacheTtlSeconds}s  " +
                $"(snapshot {(VerifyCacheService.HasData ? "present" : "empty")}" +
                $"{(VerifyCacheService.HasData && VerifyCacheService.IsStale() ? ", stale" : "")})",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Invalidate now", EditorStyles.miniButton))
            {
                VerifyCacheService.Clear();
            }
        }

        // M27 Plan 4 — batch_execute nested-command cap. Exposes the
        // batchExecuteMaxCommands project setting (default 25, hard max 100)
        // so an operator can tune it from the Settings tab. Mirrors the
        // Coplay parity knob (configurable in the Editor UI).
        private void DrawBatchLimitsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Batch execute", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Maximum number of nested tool calls one `unity_open_mcp_batch_execute` invocation " +
                "may carry. One HTTP round trip runs the sequence sequentially inside the open Editor, " +
                "wrapped in a single batch-level gate + undo group. Range " +
                $"{BridgeProjectSettings.MinBatchExecuteMaxCommands}–" +
                $"{BridgeProjectSettings.MaxBatchExecuteMaxCommands}, " +
                $"default {BridgeProjectSettings.DefaultBatchExecuteMaxCommands}.",
                MessageType.None);

            var prev = BridgeProjectSettings.BatchExecuteMaxCommands;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Max commands per batch", GUILayout.Width(160));
            var next = EditorGUILayout.IntField(prev);
            EditorGUILayout.EndHorizontal();
            // IntField allows arbitrary values; the setter clamps.
            if (next != prev)
            {
                BridgeProjectSettings.BatchExecuteMaxCommands = next;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Effective", GUILayout.Width(160));
            EditorGUILayout.LabelField(
                $"{BridgeProjectSettings.BatchExecuteMaxCommands}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
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
                "  - verifyCacheTtlSeconds: int (15–3600; verify health snapshot TTL)\n" +
                "  - batchExecuteMaxCommands: int (1–100; nested-command cap for batch_execute)\n" +
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
                "Batch scan / baseline / regression workflows land their live progress and " +
                "per-entry results in the Batch tab (read-only). Batch execution itself is driven " +
                "from the MCP batch surface or the Hub — no batch execution controls are exposed " +
                "in this window.",
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
            GUILayout.Label(new GUIContent("●",
                "Pack status: green = installed in this project, amber = available but not installed, grey = planned (not yet shipped)."),
                EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            EditorGUILayout.LabelField(pack.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            var statusLabel = !pack.Shipped ? "planned" : (installed ? "installed" : "available");
            var statusTooltip = !pack.Shipped
                ? "Planned pack: not yet shipped. Listed as a preview of upcoming domains."
                : installed
                    ? "Installed: the pack's assembly is loaded and its tools are registered in this project."
                    : "Available: the pack is shipped but not installed in this project. Add the UPM dependency to enable it.";
            BridgeGUIUtilities.DrawColoredLabel(statusLabel, dotColor, 90, statusTooltip);

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(pack.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Package",
                "UPM package id for this extension pack.", 70);
            EditorGUILayout.SelectableLabel(pack.Id, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(pack.UpmDependency))
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Unity dep",
                    "Unity package this domain needs to compile (e.g. com.unity.ai.navigation). Install it to activate the embedded tools.",
                    70);
                EditorGUILayout.LabelField(pack.UpmDependency, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            if (pack.ToolIds != null && pack.ToolIds.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Tools",
                    "Tool ids this pack contributes. Once installed they appear in the Tools tab.", 70);
                EditorGUILayout.LabelField(
                    $"{pack.ToolIds.Length} tool(s) — {pack.ToolIds[0]}…",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Install",
                "Snippet to paste into your Packages/manifest.json dependencies to add this pack via local file reference.",
                70);
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

        // ---------- Info tab ----------

        private Vector2 _infoTabScroll;

        // Single source of truth for the doc links surfaced in the Info tab.
        // Each entry is (label, relative path under docs/, tooltip). The repo
        // URL + branch prefix is prepended so links open the rendered markdown
        // on GitHub.
        private static readonly (string Label, string Path, string Tooltip)[] DocLinks =
        {
            ("README", "README.md", "Project overview, feature set, and quick links."),
            ("Wizard setup", "docs/wizard-setup.md", "Recommended onboarding flow via Unity Hub Pro."),
            ("Manual setup", "docs/manual-setup.md", "Direct MCP setup and client config snippets."),
            ("Troubleshooting", "docs/troubleshooting.md", "Bridge start failures, zombie listeners, and connectivity recovery."),
            ("Development setup", "docs/development-setup.md", "Local checkout, contributor and maintainer workflows."),
            ("Architecture", "docs/architecture.md", "Repository structure and cross-package boundaries."),
            ("Bridge HTTP API", "docs/api/bridge-http.md", "Bridge endpoints, envelopes, /ping, and remote bind."),
            ("MCP tools API", "docs/api/mcp-tools.md", "Tool catalog, route policy, and gate behavior."),
            ("MCP resources API", "docs/api/resources.md", "Resource URIs and payloads."),
            ("Extensions", "docs/extensions.md", "Domain catalog and activation (embedded tools + community packs)."),
            ("Skills", "docs/skills.md", "Agent playbooks shipped into a project."),
            ("Code conventions", "docs/code-conventions.md", "Non-obvious C# decisions (instance IDs, namespaces)."),
        };

        private void DrawInfoTab()
        {
            _infoTabScroll = EditorGUILayout.BeginScrollView(_infoTabScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Unity Open MCP Bridge", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unity Open MCP is an open MCP server for Unity — it exposes the Editor to " +
                "MCP-compatible AI clients (Claude, Cursor, …) over a local HTTP bridge so an " +
                "agent can drive scene editing, asset work, builds, and validation.\n\n" +
                "The links below open the latest documentation on GitHub.",
                MessageType.None);

            DrawInfoSection("Documentation", DocLinks, RepoUrl);

            BridgeGUIUtilities.HorizontalLine(2, 8);

            DrawInfoSection("Project", new[]
            {
                ("GitHub repository", "", "Source code, issues, and releases."),
                ("Issues", "issues", "Bug reports and feature requests."),
                ("Releases", "releases", "Version history and release notes."),
            }, RepoUrl);

            BridgeGUIUtilities.HorizontalLine(2, 8);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Quick references", EditorStyles.miniBoldLabel);
            DrawInfoLinkRow("Local bind URL",
                $"http://{BindAddress}:{BridgeHttpServer.Port}/",
                "The listener address MCP clients connect to (see Status tab).");
            DrawInfoLinkRow("Settings file",
                BridgeProjectSettings.SettingsPath ?? "(no project root)",
                "Per-project runtime settings (.unity-open-mcp/settings.json). Persistent; edit by hand or via the Settings tab.");
            DrawInfoLinkRow("Instance lock",
                "~/.unity-open-mcp/instances/<project-hash>.json",
                "Per-project lock file used for discovery + heartbeat by the MCP server. Regenerated each session.");
            DrawInfoLinkRow("Audit log",
                "~/.unity-open-mcp/audit/",
                "Optional on-disk audit of gate runs and deny-list refusals (enable in Settings).");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                $"Bridge {BridgeSession.BridgeVersion}  •  Unity {BridgeSession.UnityVersion ?? "?"}  •  {BridgeSession.Mode} mode",
                EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();
        }

        // Renders a titled list of links. Each row: a label-style button that
        // opens the URL via Application.OpenURL, plus a tooltip. `urlPrefix`
        // is prepended to each entry's relative path (empty path ⇒ the prefix
        // itself, e.g. the repo root).
        private void DrawInfoSection(string title, IEnumerable<(string Label, string Path, string Tooltip)> links, string urlPrefix)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            foreach (var link in links)
            {
                var url = string.IsNullOrEmpty(link.Path) ? urlPrefix : $"{urlPrefix}/{link.Path}";
                DrawInfoLinkRow(link.Label, url, link.Tooltip);
            }
        }

        // Label column + selectable URL + an "Open" button that launches the
        // system browser. The URL is selectable so it can be copied.
        private void DrawInfoLinkRow(string label, string url, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(180));
            EditorGUILayout.SelectableLabel(url, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button(new GUIContent("Open", "Open this link in your default browser."), GUILayout.Width(64)))
            {
                Application.OpenURL(url);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
