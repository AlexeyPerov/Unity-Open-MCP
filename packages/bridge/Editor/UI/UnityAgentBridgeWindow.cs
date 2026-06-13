// Bridge runtime dashboard window (M4.5-1 shell, M4.5-2 status panel, M4.5-3 helper baseline).
// Tab navigation pattern adapted from
//   /Users/alexeyperov/Projects/Unity-Scanner/Editor/UI/Window/UnityScannerWindow.cs
// (copy/adapt only; no scanner orchestrator / categories / MCP glue imported).
using System;
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

        void OnEnable()
        {
            _currentTab = (BridgeWindowTab)EditorPrefs.GetInt(SelectedTabPref, (int)BridgeWindowTab.Status);
            EditorApplication.update -= RepaintTick;
            EditorApplication.update += RepaintTick;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintTick;
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
                    DrawPlaceholderTab("Tools catalog and runtime toggles land in M4.5 Plan 2.");
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
            EditorGUILayout.SelectableLabel($"http://{BindAddress}:{BridgeHttpServer.Port}/", 120, EditorStyles.textField);
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
    }
}
