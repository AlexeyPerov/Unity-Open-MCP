using UnityEditor;
using UnityEngine;

#if UNITY_6000_0_OR_NEWER
using UnityEditor.Toolbars;
#endif

#if !UNITY_6000_0_OR_NEWER
using UnityEngine.UIElements;
#endif

namespace UnityOpenMcpBridge
{
    // Toolbar MCP toggle. Visual states:
    //   - Green "MCP"  — listener running.
    //   - Gray "MCP"   — listener stopped.
    //   - Yellow "MCP" — Stop confirm armed (first click received; a second
    //                    click within 5s stops the listener). Mirrors the
    //                    Status tab's two-click confirm (M29 Plan 2).
    // Unity 6 uses the native MainToolbarElement API; legacy versions fall back
    // to injecting a UIElements button into the toolbar zone.
    [InitializeOnLoad]
    public static class BridgeToolbarToggle
    {
        private const string Label = "MCP";

#if UNITY_6000_0_OR_NEWER
        const string ToolbarId = "UnityOpenMCP/Bridge Toggle";
#else
        private const string ButtonName = "UnityOpenMCP_Toggle_Button";
        private static bool _installed;
        private static Button _legacyButton;
#endif

        private static bool _lastKnownRunning;
        private static bool _lastKnownArmed;

        // Tooltip copy reflects the Stop-confirm parity (M29 Plan 2): Start is
        // one-click, Stop requires a second click within 5s. No internal
        // jargon / milestone IDs.
        private const string Tooltip =
            "Unity Open MCP Bridge.\n" +
            "• Click while stopped: Start the listener (one click).\n" +
            "• Click while running: first click arms a Stop confirm (5s); " +
            "click again to stop. The Status tab mirrors the confirm.";

        static BridgeToolbarToggle()
        {
            _lastKnownRunning = BridgeHttpServer.IsRunning;
            _lastKnownArmed = BridgeStopConfirmCoordinator.IsArmed;

            EditorApplication.update -= PollState;
            EditorApplication.update += PollState;

#if !UNITY_6000_0_OR_NEWER
            EditorApplication.update -= TryInstallLegacyOnUpdate;
            EditorApplication.update += TryInstallLegacyOnUpdate;
#endif
        }

        private static void PollState()
        {
            // M29 Plan 2 — keep the shared Stop-confirm transient advancing
            // even when the bridge window is closed (the window's tick only
            // runs while it is open). Cheap: one bool + time comparison.
            BridgeStopConfirmCoordinator.Tick();

            var running = BridgeHttpServer.IsRunning;
            var armed = BridgeStopConfirmCoordinator.IsArmed;
            if (running != _lastKnownRunning || armed != _lastKnownArmed)
            {
                _lastKnownRunning = running;
                _lastKnownArmed = armed;
                RefreshVisual();
            }
        }

        // M29 Plan 2 — Stop parity with the Status tab.
        //
        // Start stays one-click (idempotent, non-destructive). Stop must NOT
        // silently drop agents: the same two-click / timed confirm that the
        // Status tab enforces applies here. The shared transient lives on
        // BridgeStopConfirmCoordinator.
        //
        //   - First toolbar Stop click: arm the confirm. If the bridge window
        //     is open, its "Confirm Stop" button lights up so the operator
        //     sees the second-click affordance. If the window is closed, open
        //     it (focused) so the confirm is never an invisible state — the
        //     operator always has a visible second-click surface.
        //   - Second toolbar Stop click within 5s: perform the stop.
        //   - No second click: the coordinator auto-expires the transient.
        public static void Toggle()
        {
            if (!BridgeHttpServer.IsRunning)
            {
                BridgeHttpServer.Start();
                _lastKnownRunning = true;
                RefreshVisual();
                return;
            }

            // Running → Stop path goes through the shared confirm.
            var stopped = BridgeStopConfirmCoordinator.RequestStop(BridgeHttpServer.Stop);
            // The armed/running flags may have flipped; sync the visual so the
            // toolbar turns yellow (armed) or gray (stopped) immediately.
            _lastKnownRunning = BridgeHttpServer.IsRunning;
            _lastKnownArmed = BridgeStopConfirmCoordinator.IsArmed;
            RefreshVisual();
            if (!stopped)
            {
                // First click — make sure the operator can see the confirm.
                EnsureWindowVisible();
            }
        }

        // Open (or focus) the bridge window so the armed confirm is visible.
        // The coordinator's transient is what the window reads; we just make
        // sure the window exists. No-op if it is already open.
        private static void EnsureWindowVisible()
        {
            try
            {
                var window = EditorWindow.GetWindow<UnityOpenMcpBridgeWindow>(
                    true, "Unity Open MCP Bridge", true);
                window.ShowTab();
            }
            catch
            {
                // Best-effort — never let a window-open failure swallow the
                // armed confirm. The coordinator state still advances.
            }
        }

        private static void RefreshVisual()
        {
#if UNITY_6000_0_OR_NEWER
            MainToolbar.Refresh(ToolbarId);
#else
            ApplyLegacyVisual();
#endif
        }

        private static string GetColoredLabel()
        {
            // Yellow while the Stop confirm is armed so the operator sees the
            // pending state on the toolbar itself, not only in the window.
            if (BridgeStopConfirmCoordinator.IsArmed)
                return $"<color=#CCCC33><b>{Label}</b></color>";
            if (BridgeHttpServer.IsRunning)
                return $"<color=#33AA33><b>{Label}</b></color>";
            return $"<color=#888888>{Label}</color>";
        }

#if UNITY_6000_0_OR_NEWER

        [MainToolbarElement(ToolbarId, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 200)]
        public static MainToolbarElement Create()
        {
            var content = new MainToolbarContent(GetColoredLabel(), Tooltip);
            return new MainToolbarButton(content, Toggle);
        }

#else

        private static void TryInstallLegacyOnUpdate()
        {
            if (_installed)
            {
                EditorApplication.update -= TryInstallLegacyOnUpdate;
                return;
            }

            if (TryInstallLegacy())
            {
                _installed = true;
                EditorApplication.update -= TryInstallLegacyOnUpdate;
            }
        }

        private static bool TryInstallLegacy()
        {
            var toolbar = GetToolbarWindow();
            if (toolbar == null) return false;

            var root = toolbar.rootVisualElement;
            if (root == null) return false;

            var existing = root.Q<Button>(ButtonName);
            if (existing != null)
            {
                _legacyButton = existing;
                _legacyButton.clicked -= Toggle;
                _legacyButton.clicked += Toggle;
                ApplyLegacyVisual();
                return true;
            }

            var zone = FindFirstZone(root);
            if (zone == null) return false;

            _legacyButton = new Button(Toggle)
            {
                name = ButtonName,
                text = Label,
                tooltip = Tooltip,
            };

            _legacyButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _legacyButton.style.marginLeft = 4;
            _legacyButton.style.marginRight = 4;
            _legacyButton.style.height = 18;

            ApplyLegacyVisual();
            zone.Insert(0, _legacyButton);
            return true;
        }

        private static VisualElement FindFirstZone(VisualElement root)
        {
            var names = new[]
            {
                "ToolbarZonePlayModes",
                "ToolbarZonePlayMode",
                "ToolbarZoneMiddleAlign",
                "ToolbarZoneCenterAlign",
                "ToolbarZoneLeftAlign"
            };

            for (int i = 0; i < names.Length; i++)
            {
                var z = root.Q<VisualElement>(names[i]);
                if (z != null) return z;
            }

            return null;
        }

        private static void ApplyLegacyVisual()
        {
            if (_legacyButton == null) return;

            // M29 Plan 2 — yellow background while the Stop confirm is armed
            // (mirrors the rich-text label on Unity 6).
            if (BridgeStopConfirmCoordinator.IsArmed)
            {
                _legacyButton.style.backgroundColor = new StyleColor(new Color(0.80f, 0.70f, 0.20f, 1f));
                _legacyButton.style.color = new StyleColor(Color.white);
            }
            else if (BridgeHttpServer.IsRunning)
            {
                _legacyButton.style.backgroundColor = new StyleColor(new Color(0.2f, 0.65f, 0.25f, 1f));
                _legacyButton.style.color = new StyleColor(Color.white);
            }
            else
            {
                _legacyButton.style.backgroundColor = new StyleColor(new Color(0.28f, 0.28f, 0.28f, 1f));
                _legacyButton.style.color = new StyleColor(Color.white);
            }
        }

        private static EditorWindow GetToolbarWindow()
        {
            var toolbarType = typeof(Editor).Assembly.GetType("UnityEditor.Toolbar");
            if (toolbarType == null) return null;

            var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
            if (toolbars == null || toolbars.Length == 0) return null;

            return toolbars[0] as EditorWindow;
        }

#endif
    }
}
