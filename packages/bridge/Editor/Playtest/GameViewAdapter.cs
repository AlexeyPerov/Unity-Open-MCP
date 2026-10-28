// The one adapter over Editor internals used by unity_open_mcp_game_view and by
// native-size composed captures.
//
// Game view sizes (UnityEditor.GameView / GameViewSizes), the play-mode view
// that renders the game (UnityEditor.PlayModeView: the main view, its render
// target, swapping the Game view tab for the Simulator tab in place) and the
// Device Simulator (UnityEditor.DeviceSimulation.*) are internal Editor types.
// Every reflection access to them lives here, behind a version table: an Editor
// outside the table refuses with `unsupported_editor_version` (never guesses),
// and on a listed Editor every member is resolved and signature-checked once per
// domain — a missing member refuses with `editor_internals_unavailable` naming
// it. The Game view half and the Simulator half are checked separately, so a
// missing Simulator member does not take `get` / `set_resolution` down.
// Display-only members (HiDPI backing scale, zoom, the low-resolution toggle,
// the dock area's front tab) are optional: a missing one drops its field and
// never disables a half.
//
// Nothing here patches Unity. The only persistent effect is a custom Game view
// size entry (`Open MCP <w>x<h>`) added to the Editor's size list (Editor
// preferences, shared by projects on this machine): created once, reused by
// label, never deleted.
//
// fd hygiene (packages/bridge/AGENTS.md): no OS handles are held — reflection
// handles only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    internal enum PlayModeViewKind { None, Game, Simulator, Other }

    // One Game view size entry (index in the current size group).
    internal sealed class GameViewSizeEntry
    {
        public int Index;
        public string Label;        // display text, e.g. "Full HD (1920x1080)" / "Open MCP 1080x1920"
        public string BaseText;
        public string Type;         // free_aspect | aspect_ratio | fixed_resolution
        public int Width, Height;   // pixels for fixed_resolution, ratio terms for aspect_ratio
        public bool Custom;
    }

    // One Device Simulator orientation of a device's first screen.
    internal sealed class DeviceOrientationEntry
    {
        public ScreenOrientation Orientation;
        public Rect SafeArea;       // device pixels in that orientation, bottom-left origin
        public int Cutouts;
    }

    internal sealed class DeviceEntry
    {
        public int Index;
        public string Id;           // the Simulator's friendly name (its own device identity)
        public int Width, Height;   // portrait pixels of the first screen
        public float Dpi;
        public int Screens;
        public readonly List<DeviceOrientationEntry> Orientations = new List<DeviceOrientationEntry>();

        public DeviceOrientationEntry Get(ScreenOrientation orientation)
        {
            foreach (var o in Orientations) if (o.Orientation == orientation) return o;
            return null;
        }
    }

    // Live Simulator state read from the open SimulatorWindow.
    internal sealed class SimulatorSnapshot
    {
        public DeviceEntry Device;
        public int Width, Height;   // Screen.width/height the game sees (the screen shim)
        public Rect SafeArea;       // Screen.safeArea
        public Rect[] Cutouts = new Rect[0];
        public float Dpi;
        public ScreenOrientation Orientation;
        public int Rotation;        // device rotation in degrees (0/90/180/270)
        public float Scale = -1;    // preview scale (0..1], -1 when unknown
    }

    internal static class GameViewAdapter
    {
        internal const string CustomSizePrefix = "Open MCP ";
        internal const int MinResolution = 16;
        internal const int MaxResolution = 8192;

        // Unity 6 line: verified on 6000.3.21f1 (macOS). Add 2022.3 here only after
        // a walkthrough there; the member probe still guards every call.
        internal static readonly EditorVersionTable Versions = new EditorVersionTable(
            "Game view / Device Simulator control",
            new EditorVersionTable.Rule(6000, 0, 7000, "6000.3.21f1 (macOS)"));

        // Test seam: overrides Application.unityVersion.
        internal static string UnityVersionOverride;
        internal static string UnityVersion => UnityVersionOverride ?? Application.unityVersion;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        // ------------------------------------------------------------------
        // Pure helpers (unit-tested without Editor internals)
        // ------------------------------------------------------------------

        internal static string CustomSizeLabel(int width, int height) =>
            CustomSizePrefix + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);

        internal static string OrientationName(ScreenOrientation orientation)
        {
            switch (orientation)
            {
                case ScreenOrientation.Portrait: return "portrait";
                case ScreenOrientation.PortraitUpsideDown: return "portrait_upside_down";
                case ScreenOrientation.LandscapeLeft: return "landscape_left";
                case ScreenOrientation.LandscapeRight: return "landscape_right";
                default: return "auto_rotation";
            }
        }

        internal static bool TryParseOrientation(string value, out ScreenOrientation orientation)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "portrait": orientation = ScreenOrientation.Portrait; return true;
                case "portrait_upside_down": orientation = ScreenOrientation.PortraitUpsideDown; return true;
                case "landscape_left": orientation = ScreenOrientation.LandscapeLeft; return true;
                case "landscape_right": orientation = ScreenOrientation.LandscapeRight; return true;
                default: orientation = ScreenOrientation.Portrait; return false;
            }
        }

        // Portrait / landscape from a plain size (the Game view has no orientation).
        internal static string ShapeOrientation(int width, int height) =>
            width > height ? "landscape" : "portrait";

        // "9:16" style reduced ratio.
        internal static string AspectLabel(int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            int a = width, b = height;
            while (b != 0) { var t = a % b; a = b; b = t; }
            return (width / a).ToString(CultureInfo.InvariantCulture) + ":" + (height / a).ToString(CultureInfo.InvariantCulture);
        }

        // Unsafe band widths of a safe area inside a screen (bottom-left origin).
        internal static void Insets(Rect safe, int width, int height, out int top, out int bottom, out int left, out int right)
        {
            left = Mathf.Max(0, Mathf.RoundToInt(safe.xMin));
            bottom = Mathf.Max(0, Mathf.RoundToInt(safe.yMin));
            right = Mathf.Max(0, Mathf.RoundToInt(width - safe.xMax));
            top = Mathf.Max(0, Mathf.RoundToInt(height - safe.yMax));
        }

        // Exact (case-insensitive) id, else a unique case-insensitive substring.
        // Returns the index into `ids`, -1 when nothing matches, -2 when ambiguous
        // (then `candidates` lists the matches).
        internal static int MatchDevice(IList<string> ids, string query, out List<string> candidates)
        {
            candidates = new List<string>();
            var q = (query ?? "").Trim();
            if (q.Length == 0) return -1;
            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], q, StringComparison.OrdinalIgnoreCase)) return i;
            int found = -1;
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == null || ids[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                candidates.Add(ids[i]);
                found = i;
            }
            if (candidates.Count == 1) return found;
            return candidates.Count == 0 ? -1 : -2;
        }

        // ------------------------------------------------------------------
        // Availability: version table + member probe
        // ------------------------------------------------------------------

        internal static bool IsVersionSupported(string version, out string detail) => Versions.IsSupported(version, out detail);

        // Game view half (get / set_resolution / composed native capture).
        internal static bool IsGameViewAvailable(out string code, out string message)
        {
            if (!IsVersionSupported(UnityVersion, out var detail))
            {
                code = "unsupported_editor_version";
                message = detail;
                return false;
            }
            var m = Resolve();
            if (m.GameViewMissing.Count > 0)
            {
                code = "editor_internals_unavailable";
                message = "This Editor does not expose the Game view members the adapter needs: " + string.Join(", ", m.GameViewMissing) + ".";
                return false;
            }
            code = null;
            message = null;
            return true;
        }

        // Simulator half (set_device / list_devices / simulator state in get).
        internal static bool IsSimulatorAvailable(out string code, out string message)
        {
            if (!IsGameViewAvailable(out code, out message)) return false;
            var m = Resolve();
            if (m.SimulatorMissing.Count > 0)
            {
                code = "editor_internals_unavailable";
                message = "This Editor does not expose the Device Simulator members the adapter needs: " + string.Join(", ", m.SimulatorMissing) + ".";
                return false;
            }
            return true;
        }

        internal static List<string> MissingMembers()
        {
            var m = Resolve();
            var all = new List<string>(m.GameViewMissing);
            all.AddRange(m.SimulatorMissing);
            return all;
        }

        private sealed class Members
        {
            public readonly List<string> GameViewMissing = new List<string>();
            public readonly List<string> SimulatorMissing = new List<string>();

            public Type PlayModeView, GameView, GameViewSizes, GameViewSizeGroup, GameViewSize, GameViewSizeType;
            public MethodInfo GetMainPlayModeView, SwapMainWindow, SizeSelectionCallback, SaveToHdd, GetTotalCount, GetBuiltinCount, GetGameViewSize, AddCustomSize;
            public PropertyInfo TargetSize, SelectedSizeIndex, TargetRenderSize, BackingScale, ZoomAreaScale, LowResolution, SizesInstance, CurrentGroup;
            public PropertyInfo SizeBaseText, SizeDisplayText, SizeType, SizeWidth, SizeHeight, SizeIsFree;
            public ConstructorInfo SizeCtor;
            public FieldInfo TargetTexture;
            public FieldInfo WindowParent;     // optional: EditorWindow.m_Parent (its HostView)
            public PropertyInfo HostActualView; // optional: HostView.actualView (the dock area's front tab)

            public Type SimulatorWindow, DeviceSimulatorMain, DeviceInfoAsset, DeviceInfo, ScreenData, OrientationData, ScreenSimulation, UserInterfaceController, SimulatorUtilities, DeviceLoader;
            public MethodInfo ShowSimulator, LoadDevices, RotationToOrientation, ApplyChanges;
            public PropertyInfo SimMain, Devices, DeviceIndex, ScreenSim, UserInterface, UiRotation, UiScale;
            public PropertyInfo SimWidth, SimHeight, SimSafeArea, SimCutouts, SimDpi, SimOrientation;
            public FieldInfo AssetDeviceInfo, InfoFriendlyName, InfoScreens, ScreenWidth, ScreenHeight, ScreenDpi, ScreenOrientations, OrientOrientation, OrientSafeArea, OrientCutouts;
        }

        private static Members members;

        private static Members Resolve()
        {
            if (members != null) return members;
            var m = new Members();
            try { ResolveGameView(m); }
            catch (Exception ex) { m.GameViewMissing.Add("resolve failed: " + ex.Message); }
            try { ResolveSimulator(m); }
            catch (Exception ex) { m.SimulatorMissing.Add("resolve failed: " + ex.Message); }
            members = m;
            return m;
        }

        private static void ResolveGameView(Members m)
        {
            var miss = m.GameViewMissing;
            var editor = typeof(EditorWindow).Assembly;
            m.PlayModeView = T(editor, "UnityEditor.PlayModeView", miss);
            m.GameView = T(editor, "UnityEditor.GameView", miss);
            m.GameViewSizes = T(editor, "UnityEditor.GameViewSizes", miss);
            m.GameViewSizeGroup = T(editor, "UnityEditor.GameViewSizeGroup", miss);
            m.GameViewSize = T(editor, "UnityEditor.GameViewSize", miss);
            m.GameViewSizeType = T(editor, "UnityEditor.GameViewSizeType", miss);
            if (miss.Count > 0) return;

            m.GetMainPlayModeView = Meth(m.PlayModeView, "GetMainPlayModeView", Stat, Type.EmptyTypes, m.PlayModeView, miss);
            m.SwapMainWindow = Meth(m.PlayModeView, "SwapMainWindow", Inst, new[] { typeof(Type) }, typeof(void), miss);
            m.TargetSize = Prop(m.PlayModeView, "targetSize", Inst, typeof(Vector2), miss);
            m.TargetTexture = Field(m.PlayModeView, "m_TargetTexture", Inst, typeof(RenderTexture), miss);

            m.SizeSelectionCallback = Meth(m.GameView, "SizeSelectionCallback", Inst, new[] { typeof(int), typeof(object) }, typeof(void), miss);
            m.SelectedSizeIndex = Prop(m.GameView, "selectedSizeIndex", Inst, typeof(int), miss);
            m.TargetRenderSize = Prop(m.GameView, "targetRenderSize", Inst, typeof(Vector2), miss);
            m.BackingScale = OptionalProp(m.GameView, "backingScale", typeof(float));
            m.ZoomAreaScale = OptionalProp(m.GameView, "zoomAreaScale", typeof(Vector2));
            m.LowResolution = OptionalProp(m.GameView, "lowResolutionForAspectRatios", typeof(bool));
            var hostView = editor.GetType("UnityEditor.HostView");
            var parent = typeof(EditorWindow).GetField("m_Parent", Inst);
            if (hostView != null && parent != null && parent.FieldType.IsAssignableFrom(hostView))
            {
                m.WindowParent = parent;
                m.HostActualView = OptionalProp(hostView, "actualView", typeof(EditorWindow));
            }

            m.SizesInstance = m.GameViewSizes.GetProperty("instance", Stat);
            if (m.SizesInstance == null) miss.Add("GameViewSizes.instance");
            m.CurrentGroup = Prop(m.GameViewSizes, "currentGroup", Inst, m.GameViewSizeGroup, miss);
            m.SaveToHdd = Meth(m.GameViewSizes, "SaveToHDD", Inst, Type.EmptyTypes, typeof(void), miss);
            m.GetTotalCount = Meth(m.GameViewSizeGroup, "GetTotalCount", Inst, Type.EmptyTypes, typeof(int), miss);
            m.GetBuiltinCount = Meth(m.GameViewSizeGroup, "GetBuiltinCount", Inst, Type.EmptyTypes, typeof(int), miss);
            m.GetGameViewSize = Meth(m.GameViewSizeGroup, "GetGameViewSize", Inst, new[] { typeof(int) }, m.GameViewSize, miss);
            m.AddCustomSize = Meth(m.GameViewSizeGroup, "AddCustomSize", Inst, new[] { m.GameViewSize }, typeof(void), miss);

            m.SizeBaseText = Prop(m.GameViewSize, "baseText", Inst, typeof(string), miss);
            m.SizeDisplayText = Prop(m.GameViewSize, "displayText", Inst, typeof(string), miss);
            m.SizeType = Prop(m.GameViewSize, "sizeType", Inst, m.GameViewSizeType, miss);
            m.SizeWidth = Prop(m.GameViewSize, "width", Inst, typeof(int), miss);
            m.SizeHeight = Prop(m.GameViewSize, "height", Inst, typeof(int), miss);
            m.SizeIsFree = Prop(m.GameViewSize, "isFreeAspectRatio", Inst, typeof(bool), miss);
            m.SizeCtor = m.GameViewSize.GetConstructor(Inst, null, new[] { m.GameViewSizeType, typeof(int), typeof(int), typeof(string) }, null);
            if (m.SizeCtor == null) miss.Add("GameViewSize(GameViewSizeType, int, int, string)");
            if (!Enum.IsDefined(m.GameViewSizeType, "FixedResolution") || !Enum.IsDefined(m.GameViewSizeType, "AspectRatio"))
                miss.Add("GameViewSizeType.FixedResolution/AspectRatio");
        }

        private static void ResolveSimulator(Members m)
        {
            var miss = m.SimulatorMissing;
            Assembly sim = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "UnityEditor.DeviceSimulatorModule") { sim = a; break; }
            if (sim == null)
            {
                miss.Add("assembly UnityEditor.DeviceSimulatorModule");
                return;
            }
            const string ns = "UnityEditor.DeviceSimulation.";
            m.SimulatorWindow = T(sim, ns + "SimulatorWindow", miss);
            m.DeviceSimulatorMain = T(sim, ns + "DeviceSimulatorMain", miss);
            m.DeviceInfoAsset = T(sim, ns + "DeviceInfoAsset", miss);
            m.DeviceInfo = T(sim, ns + "DeviceInfo", miss);
            m.ScreenData = T(sim, ns + "ScreenData", miss);
            m.OrientationData = T(sim, ns + "OrientationData", miss);
            m.ScreenSimulation = T(sim, ns + "ScreenSimulation", miss);
            m.UserInterfaceController = T(sim, ns + "UserInterfaceController", miss);
            m.SimulatorUtilities = T(sim, ns + "SimulatorUtilities", miss);
            m.DeviceLoader = T(sim, ns + "DeviceLoader", miss);
            if (miss.Count > 0) return;

            m.ShowSimulator = Meth(m.SimulatorWindow, "ShowWindow", Stat, Type.EmptyTypes, typeof(void), miss);
            m.SimMain = Prop(m.SimulatorWindow, "main", Inst, m.DeviceSimulatorMain, miss);
            m.Devices = Prop(m.DeviceSimulatorMain, "devices", Inst, m.DeviceInfoAsset.MakeArrayType(), miss);
            m.DeviceIndex = Prop(m.DeviceSimulatorMain, "deviceIndex", Inst, typeof(int), miss, needSetter: true);
            m.ScreenSim = Prop(m.DeviceSimulatorMain, "ScreenSimulation", Inst, m.ScreenSimulation, miss);
            m.UserInterface = Prop(m.DeviceSimulatorMain, "userInterface", Inst, m.UserInterfaceController, miss);
            m.UiRotation = Prop(m.UserInterfaceController, "Rotation", Inst, typeof(int), miss, needSetter: true);
            m.UiScale = m.UserInterfaceController.GetProperty("Scale", Inst); // optional (preview scale only)

            m.LoadDevices = Meth(m.DeviceLoader, "LoadDevices", Stat, Type.EmptyTypes, m.DeviceInfoAsset.MakeArrayType(), miss);
            m.RotationToOrientation = Meth(m.SimulatorUtilities, "RotationToScreenOrientation", Stat, new[] { typeof(int) }, typeof(ScreenOrientation), miss);
            m.ApplyChanges = Meth(m.ScreenSimulation, "ApplyChanges", Inst, Type.EmptyTypes, typeof(void), miss);

            m.SimWidth = Prop(m.ScreenSimulation, "width", Inst, typeof(int), miss);
            m.SimHeight = Prop(m.ScreenSimulation, "height", Inst, typeof(int), miss);
            m.SimSafeArea = Prop(m.ScreenSimulation, "safeArea", Inst, typeof(Rect), miss);
            m.SimCutouts = Prop(m.ScreenSimulation, "cutouts", Inst, typeof(Rect[]), miss);
            m.SimDpi = Prop(m.ScreenSimulation, "dpi", Inst, typeof(float), miss);
            m.SimOrientation = Prop(m.ScreenSimulation, "orientation", Inst, typeof(ScreenOrientation), miss);

            m.AssetDeviceInfo = Field(m.DeviceInfoAsset, "deviceInfo", Inst, m.DeviceInfo, miss);
            m.InfoFriendlyName = Field(m.DeviceInfo, "friendlyName", Inst, typeof(string), miss);
            m.InfoScreens = Field(m.DeviceInfo, "screens", Inst, m.ScreenData.MakeArrayType(), miss);
            m.ScreenWidth = Field(m.ScreenData, "width", Inst, typeof(int), miss);
            m.ScreenHeight = Field(m.ScreenData, "height", Inst, typeof(int), miss);
            m.ScreenDpi = Field(m.ScreenData, "dpi", Inst, typeof(float), miss);
            m.ScreenOrientations = Field(m.ScreenData, "orientations", Inst, m.OrientationData.MakeArrayType(), miss);
            m.OrientOrientation = Field(m.OrientationData, "orientation", Inst, typeof(ScreenOrientation), miss);
            m.OrientSafeArea = Field(m.OrientationData, "safeArea", Inst, typeof(Rect), miss);
            m.OrientCutouts = Field(m.OrientationData, "cutouts", Inst, typeof(Rect[]), miss);
        }

        private static Type T(Assembly asm, string name, List<string> miss)
        {
            var t = asm.GetType(name);
            if (t == null) miss.Add(name);
            return t;
        }

        private static MethodInfo Meth(Type type, string name, BindingFlags flags, Type[] args, Type returns, List<string> miss)
        {
            MethodInfo method = null;
            try { method = type.GetMethod(name, flags, null, args, null); }
            catch (AmbiguousMatchException) { /* treated as missing */ }
            if (method == null || (returns != null && !returns.IsAssignableFrom(method.ReturnType) && method.ReturnType != returns))
            {
                miss.Add(type.Name + "." + name);
                return null;
            }
            return method;
        }

        private static PropertyInfo Prop(Type type, string name, BindingFlags flags, Type propertyType, List<string> miss, bool needSetter = false)
        {
            PropertyInfo p = null;
            try { p = type.GetProperty(name, flags); }
            catch (AmbiguousMatchException) { /* treated as missing */ }
            if (p == null || p.GetGetMethod(true) == null || (needSetter && p.GetSetMethod(true) == null) ||
                (propertyType != null && p.PropertyType != propertyType))
            {
                miss.Add(type.Name + "." + name);
                return null;
            }
            return p;
        }

        // A display-only member: null when absent or of another type, never recorded as missing.
        private static PropertyInfo OptionalProp(Type type, string name, Type propertyType)
        {
            PropertyInfo p = null;
            try { p = type.GetProperty(name, Inst); }
            catch (AmbiguousMatchException) { /* treated as absent */ }
            return p != null && p.GetGetMethod(true) != null && p.PropertyType == propertyType ? p : null;
        }

        private static FieldInfo Field(Type type, string name, BindingFlags flags, Type fieldType, List<string> miss)
        {
            var f = type.GetField(name, flags);
            if (f == null || (fieldType != null && f.FieldType != fieldType))
            {
                miss.Add(type.Name + "." + name);
                return null;
            }
            return f;
        }

        // ------------------------------------------------------------------
        // Play-mode views
        // ------------------------------------------------------------------

        // The view the game renders into (the last focused Game / Simulator tab).
        internal static EditorWindow MainView()
        {
            var m = Resolve();
            if (m.GetMainPlayModeView == null) return null;
            return m.GetMainPlayModeView.Invoke(null, null) as EditorWindow;
        }

        internal static PlayModeViewKind Kind(EditorWindow view)
        {
            if (view == null) return PlayModeViewKind.None;
            var m = Resolve();
            if (m.GameView != null && m.GameView.IsInstanceOfType(view)) return PlayModeViewKind.Game;
            if (m.SimulatorWindow != null && m.SimulatorWindow.IsInstanceOfType(view)) return PlayModeViewKind.Simulator;
            return PlayModeViewKind.Other;
        }

        internal static string KindName(PlayModeViewKind kind)
        {
            switch (kind)
            {
                case PlayModeViewKind.Game: return "game";
                case PlayModeViewKind.Simulator: return "simulator";
                case PlayModeViewKind.Other: return "other";
                default: return "none";
            }
        }

        // The size the view asks the game to render at (Screen size once settled).
        internal static Vector2 TargetSize(EditorWindow view)
        {
            var m = Resolve();
            if (view == null || m.TargetSize == null) return Vector2.zero;
            return (Vector2)m.TargetSize.GetValue(view);
        }

        // The view's render target: the game frame (all cameras + Screen Space
        // Overlay UI, no Editor chrome, no Simulator bezel). Null until the view
        // first renders.
        internal static RenderTexture TargetTexture(EditorWindow view)
        {
            var m = Resolve();
            if (view == null || m.TargetTexture == null) return null;
            var rt = m.TargetTexture.GetValue(view) as RenderTexture;
            return rt != null && rt.IsCreated() ? rt : null;
        }

        // Swap the view's tab for another play-mode view type in place (what the
        // Game / Simulator dropdown does). Returns the new main view.
        internal static EditorWindow SwapTo(EditorWindow view, PlayModeViewKind kind)
        {
            var m = Resolve();
            var type = kind == PlayModeViewKind.Simulator ? m.SimulatorWindow : m.GameView;
            m.SwapMainWindow.Invoke(view, new object[] { type });
            var main = MainView();
            if (Kind(main) == kind) return main;
            foreach (var w in Resources.FindObjectsOfTypeAll(type))
                if (w is EditorWindow ew && ew != null) return ew;
            return main;
        }

        // Open a play-mode view of that kind when none exists (focuses it inside
        // Unity; the Unity application itself is never raised).
        internal static EditorWindow Open(PlayModeViewKind kind)
        {
            var m = Resolve();
            if (kind == PlayModeViewKind.Simulator)
            {
                m.ShowSimulator.Invoke(null, null);
                foreach (var w in Resources.FindObjectsOfTypeAll(m.SimulatorWindow))
                    if (w is EditorWindow ew && ew != null) return ew;
                return null;
            }
            return EditorWindow.GetWindow(m.GameView);
        }

        // ------------------------------------------------------------------
        // Game view sizes
        // ------------------------------------------------------------------

        private static object SizeGroup()
        {
            var m = Resolve();
            var instance = m.SizesInstance.GetValue(null);
            return m.CurrentGroup.GetValue(instance);
        }

        internal static List<GameViewSizeEntry> Sizes()
        {
            var m = Resolve();
            var group = SizeGroup();
            int total = (int)m.GetTotalCount.Invoke(group, null);
            int builtin = (int)m.GetBuiltinCount.Invoke(group, null);
            var list = new List<GameViewSizeEntry>(total);
            for (int i = 0; i < total; i++)
                list.Add(ReadSize(m.GetGameViewSize.Invoke(group, new object[] { i }), i, i >= builtin));
            return list;
        }

        private static GameViewSizeEntry ReadSize(object size, int index, bool custom)
        {
            var m = Resolve();
            bool free = (bool)m.SizeIsFree.GetValue(size);
            string type = free ? "free_aspect"
                : m.SizeType.GetValue(size).ToString() == "FixedResolution" ? "fixed_resolution" : "aspect_ratio";
            return new GameViewSizeEntry
            {
                Index = index,
                Label = (string)m.SizeDisplayText.GetValue(size),
                BaseText = (string)m.SizeBaseText.GetValue(size),
                Type = type,
                Width = (int)m.SizeWidth.GetValue(size),
                Height = (int)m.SizeHeight.GetValue(size),
                Custom = custom,
            };
        }

        // Find the fixed-resolution entry named `Open MCP <w>x<h>`, or add it
        // (and save the size list). Entries are reused by label, never deleted.
        internal static GameViewSizeEntry EnsureCustomSize(int width, int height, out bool created)
        {
            created = false;
            var label = CustomSizeLabel(width, height);
            foreach (var entry in Sizes())
                if (entry.Custom && entry.BaseText == label && entry.Type == "fixed_resolution" && entry.Width == width && entry.Height == height)
                    return entry;
            var m = Resolve();
            var fixedType = Enum.Parse(m.GameViewSizeType, "FixedResolution");
            var size = m.SizeCtor.Invoke(new object[] { fixedType, width, height, label });
            m.AddCustomSize.Invoke(SizeGroup(), new[] { size });
            m.SaveToHdd.Invoke(m.SizesInstance.GetValue(null), null);
            created = true;
            var sizes = Sizes();
            for (int i = sizes.Count - 1; i >= 0; i--)
                if (sizes[i].BaseText == label && sizes[i].Width == width && sizes[i].Height == height) return sizes[i];
            return null;
        }

        internal static GameViewSizeEntry SelectedSize(EditorWindow gameView)
        {
            var m = Resolve();
            if (Kind(gameView) != PlayModeViewKind.Game) return null;
            int index = (int)m.SelectedSizeIndex.GetValue(gameView);
            var sizes = Sizes();
            return index >= 0 && index < sizes.Count ? sizes[index] : null;
        }

        internal static void SelectSize(EditorWindow gameView, int index)
        {
            Resolve().SizeSelectionCallback.Invoke(gameView, new object[] { index, null });
        }

        internal static Vector2 RenderSize(EditorWindow gameView)
        {
            var m = Resolve();
            return Kind(gameView) == PlayModeViewKind.Game ? (Vector2)m.TargetRenderSize.GetValue(gameView) : Vector2.zero;
        }

        internal static float BackingScale(EditorWindow view)
        {
            var m = Resolve();
            if (Kind(view) == PlayModeViewKind.Game && m.BackingScale != null)
            {
                try { return (float)m.BackingScale.GetValue(view); } catch { /* fall through */ }
            }
            return EditorGUIUtility.pixelsPerPoint;
        }

        // Game view zoom, or -1 when unknown (not a Game view, or no zoom member).
        internal static float Zoom(EditorWindow gameView)
        {
            var m = Resolve();
            return Kind(gameView) == PlayModeViewKind.Game && m.ZoomAreaScale != null ? ((Vector2)m.ZoomAreaScale.GetValue(gameView)).y : -1f;
        }

        // The Game view's "Low Resolution Aspect Ratios" toggle; null when unknown.
        internal static bool? LowResolution(EditorWindow gameView)
        {
            var m = Resolve();
            if (Kind(gameView) != PlayModeViewKind.Game || m.LowResolution == null) return null;
            return (bool)m.LowResolution.GetValue(gameView);
        }

        // Whether the view is the front tab of its dock area. Only the front tab
        // renders: a hidden view's render target keeps the last frame it drew
        // while the game runs on. Null when this Editor does not expose the dock
        // area (the caller then reports nothing rather than guess).
        internal static bool? IsFrontTab(EditorWindow view)
        {
            var m = Resolve();
            if (view == null || m.WindowParent == null || m.HostActualView == null) return null;
            try
            {
                var host = m.WindowParent.GetValue(view) as UnityEngine.Object;
                if (host == null) return false;
                if (!m.HostActualView.DeclaringType.IsInstanceOfType(host)) return null;
                return (m.HostActualView.GetValue(host) as EditorWindow) == view;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------
        // Device Simulator
        // ------------------------------------------------------------------

        private static object SimMain(EditorWindow simulator)
        {
            var m = Resolve();
            return Kind(simulator) == PlayModeViewKind.Simulator ? m.SimMain.GetValue(simulator) : null;
        }

        // Device catalog: the open Simulator's list, else the loader's.
        internal static List<DeviceEntry> Devices(EditorWindow simulator = null)
        {
            var m = Resolve();
            var main = SimMain(simulator);
            var assets = main != null ? (Array)m.Devices.GetValue(main) : (Array)m.LoadDevices.Invoke(null, null);
            var list = new List<DeviceEntry>(assets?.Length ?? 0);
            if (assets == null) return list;
            for (int i = 0; i < assets.Length; i++)
            {
                var asset = assets.GetValue(i);
                if (asset == null) continue;
                var info = m.AssetDeviceInfo.GetValue(asset);
                if (info == null) continue;
                var entry = new DeviceEntry { Index = i, Id = (string)m.InfoFriendlyName.GetValue(info) };
                var screens = (Array)m.InfoScreens.GetValue(info);
                entry.Screens = screens?.Length ?? 0;
                if (entry.Screens > 0)
                {
                    var screen = screens.GetValue(0);
                    entry.Width = (int)m.ScreenWidth.GetValue(screen);
                    entry.Height = (int)m.ScreenHeight.GetValue(screen);
                    entry.Dpi = (float)m.ScreenDpi.GetValue(screen);
                    var orientations = (Array)m.ScreenOrientations.GetValue(screen);
                    if (orientations != null)
                        foreach (var o in orientations)
                        {
                            if (o == null) continue;
                            var cutouts = (Rect[])m.OrientCutouts.GetValue(o);
                            entry.Orientations.Add(new DeviceOrientationEntry
                            {
                                Orientation = (ScreenOrientation)m.OrientOrientation.GetValue(o),
                                SafeArea = (Rect)m.OrientSafeArea.GetValue(o),
                                Cutouts = cutouts?.Length ?? 0,
                            });
                        }
                }
                list.Add(entry);
            }
            return list;
        }

        internal static SimulatorSnapshot Simulator(EditorWindow simulator)
        {
            var m = Resolve();
            var main = SimMain(simulator);
            if (main == null) return null;
            var state = new SimulatorSnapshot();
            var devices = Devices(simulator);
            int index = (int)m.DeviceIndex.GetValue(main);
            foreach (var d in devices) if (d.Index == index) state.Device = d;
            var screen = m.ScreenSim.GetValue(main);
            if (screen != null)
            {
                state.Width = (int)m.SimWidth.GetValue(screen);
                state.Height = (int)m.SimHeight.GetValue(screen);
                state.SafeArea = (Rect)m.SimSafeArea.GetValue(screen);
                state.Cutouts = (Rect[])m.SimCutouts.GetValue(screen) ?? new Rect[0];
                state.Dpi = (float)m.SimDpi.GetValue(screen);
                state.Orientation = (ScreenOrientation)m.SimOrientation.GetValue(screen);
            }
            var ui = m.UserInterface.GetValue(main);
            if (ui != null)
            {
                state.Rotation = (int)m.UiRotation.GetValue(ui);
                if (m.UiScale != null && m.UiScale.PropertyType == typeof(int))
                {
                    try { state.Scale = (int)m.UiScale.GetValue(ui) / 100f; } catch { state.Scale = -1; }
                }
            }
            return state;
        }

        // Select a device (re-initialises the simulation, as the device menu does).
        internal static void SelectDevice(EditorWindow simulator, int index)
        {
            var m = Resolve();
            var main = SimMain(simulator);
            if (main == null) throw new InvalidOperationException("The Device Simulator is not open.");
            if ((int)m.DeviceIndex.GetValue(main) != index) m.DeviceIndex.SetValue(main, index);
        }

        // Device rotation (degrees) that the Simulator maps to `orientation`, or -1.
        internal static int RotationFor(ScreenOrientation orientation)
        {
            var m = Resolve();
            foreach (var degrees in new[] { 0, 90, 180, 270 })
                if ((ScreenOrientation)m.RotationToOrientation.Invoke(null, new object[] { degrees }) == orientation) return degrees;
            return -1;
        }

        // Rotate the simulated device (the Simulator's rotate buttons) and apply
        // the screen change now instead of on the next repaint. Whether the game
        // follows depends on the project's orientation settings (auto-rotation and
        // the allowed orientations), as on a real device.
        internal static void Rotate(EditorWindow simulator, int degrees)
        {
            var m = Resolve();
            var main = SimMain(simulator);
            if (main == null) throw new InvalidOperationException("The Device Simulator is not open.");
            var ui = m.UserInterface.GetValue(main);
            if ((int)m.UiRotation.GetValue(ui) != degrees) m.UiRotation.SetValue(ui, degrees);
            var screen = m.ScreenSim.GetValue(main);
            if (screen != null) m.ApplyChanges.Invoke(screen, null);
        }

        // ------------------------------------------------------------------
        // What the game sees (Screen size / safe area) — shared by game_view and
        // the capture context
        // ------------------------------------------------------------------

        // Screen size of the game for the main view: the simulated screen in
        // Simulator mode, the view's render size in Game view mode. Editor code
        // reading Screen.width directly gets the size of whichever Editor panel is
        // current, so this is the only reliable source outside the player loop.
        internal static bool TryGameScreen(out EditorWindow view, out PlayModeViewKind kind, out int width, out int height, out Rect safeArea, out SimulatorSnapshot simulator)
        {
            view = null;
            kind = PlayModeViewKind.None;
            width = height = 0;
            safeArea = default(Rect);
            simulator = null;
            if (!IsGameViewAvailable(out _, out _)) return false;
            view = MainView();
            kind = Kind(view);
            if (kind == PlayModeViewKind.Simulator && IsSimulatorAvailable(out _, out _))
            {
                simulator = Simulator(view);
                if (simulator != null)
                {
                    width = simulator.Width;
                    height = simulator.Height;
                    safeArea = simulator.SafeArea;
                    return width > 0 && height > 0;
                }
            }
            if (kind == PlayModeViewKind.None) return false;
            var size = kind == PlayModeViewKind.Game ? RenderSize(view) : TargetSize(view);
            if (size.x <= 0 || size.y <= 0) size = TargetSize(view);
            width = (int)Math.Round(size.x, MidpointRounding.AwayFromZero);
            height = (int)Math.Round(size.y, MidpointRounding.AwayFromZero);
            safeArea = new Rect(0, 0, width, height);
            return width > 0 && height > 0;
        }

        // Test-only: forget resolved members (e.g. after a version override).
        internal static void ResetForTests() => members = null;
    }
}
