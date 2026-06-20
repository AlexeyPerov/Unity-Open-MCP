using System;
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
    // Toolbar toggle (T7.7): green "MCP" label when the bridge is listening,
    // gray when stopped. Clicking toggles the bridge listener.
    // Unity 6 uses the native MainToolbarElement API; legacy versions fall back
    // to injecting a UIElements button into the toolbar zone.
    // Reference: references/UnifiedUnityMCP-main/.../McpToolbarToggle.cs (adapted).
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

        static BridgeToolbarToggle()
        {
            _lastKnownRunning = BridgeHttpServer.IsRunning;

            EditorApplication.update -= PollState;
            EditorApplication.update += PollState;

#if !UNITY_6000_0_OR_NEWER
            EditorApplication.update -= TryInstallLegacyOnUpdate;
            EditorApplication.update += TryInstallLegacyOnUpdate;
#endif
        }

        private static void PollState()
        {
            var running = BridgeHttpServer.IsRunning;
            if (running != _lastKnownRunning)
            {
                _lastKnownRunning = running;
                RefreshVisual();
            }
        }

        public static void Toggle()
        {
            if (BridgeHttpServer.IsRunning)
                BridgeHttpServer.Stop();
            else
                BridgeHttpServer.Start();
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
            if (BridgeHttpServer.IsRunning)
                return $"<color=#33AA33><b>{Label}</b></color>";
            return $"<color=#888888>{Label}</color>";
        }

#if UNITY_6000_0_OR_NEWER

        [MainToolbarElement(ToolbarId, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 200)]
        public static MainToolbarElement Create()
        {
            var content = new MainToolbarContent(GetColoredLabel(), "Toggle Unity Open MCP Bridge (Start/Stop)");
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
                text = Label
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

            if (BridgeHttpServer.IsRunning)
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
