// uGUI screen model shared by `ui_snapshot` and label targeting (`target_text`
// on inputsim_pointer / inputsim_touch / inputsim_gesture).
//
// One walk over every active RectTransform under a root Canvas, in hierarchy
// order, produces a flat list of UiNodes: role, text (UI.Text built in; other
// text components — TextMeshPro — come from registered IUiTextReaders), screen
// rect, effective alpha through the CanvasGroup chain, mask clipping
// (RectMask2D / Mask, stopping at an override-sorting canvas like uGUI does),
// screen bounds, and the resulting `visibility`:
//   zero_size   — the screen rect has no area
//   transparent — effective alpha ≈ 0 (CanvasGroup chain, a disabled Canvas, or
//                 an invisible text/image graphic: color × CanvasRenderer alpha)
//   offscreen   — the rect does not intersect the screen (or is behind the camera,
//                 or a World Space canvas has no camera to project it:
//                 Reason "no_camera")
//   clipped     — on screen but fully outside its masks
//   on_screen   — at least partly visible (`visible: true`)
// The label of a control (button / toggle / slider / input) is the text of its
// descendants that are not inside a nested control; those text nodes record the
// control as their `Owner`.
//
// Screen space is Screen pixels, bottom-left origin — the space of
// Input.mousePosition, PointerEventData.position and inputsim screen_x/screen_y.
// The screen size comes from the main Game view (Screen.width/height read from
// Editor code report the current GUIView, not the game).
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    // Text read from one text component. Registered readers fill it for
    // components the uGUI assembly cannot reference (TextMeshPro).
    internal struct UiTextInfo
    {
        public string Raw;
        public bool RichText;
        public float Alpha;            // graphic color alpha × CanvasRenderer alpha (0..1)
        public bool Overflowing;       // content exceeds the text rect
        public Vector2 Content;        // size the content needs (local units)
        public string Source;          // "ugui" | "tmp" | reader-specific
    }

    internal interface IUiTextReader
    {
        string Source { get; }
        // True when `go` carries an enabled text component this reader handles.
        bool TryRead(GameObject go, out UiTextInfo info);
    }

    internal static class UiTextReaders
    {
        private static readonly List<IUiTextReader> readers = new List<IUiTextReader>();

        internal static void Register(IUiTextReader reader)
        {
            if (reader == null) return;
            for (int i = 0; i < readers.Count; i++)
                if (readers[i].Source == reader.Source) { readers[i] = reader; return; }
            readers.Add(reader);
        }

        internal static IReadOnlyList<IUiTextReader> All => readers;

        // Text sources compiled in: "ugui" always, plus every registered reader.
        internal static List<string> Sources()
        {
            var list = new List<string> { "ugui" };
            foreach (var r in readers) list.Add(r.Source);
            return list;
        }
    }

    internal sealed class UiNode
    {
        public GameObject Go;
        public RectTransform Rt;
        public Canvas RootCanvas;
        public string Path;
        public int Depth;              // hierarchy depth below the root canvas (canvas = 0)
        public int Order;              // hierarchy order (stable, deterministic)
        public string Role;            // button | toggle | slider | input | scroll | text | image | container
        public bool Control;           // button / toggle / slider / input — owns descendant labels
        public bool Interactable;      // controls and scroll views only
        public UiNode Owner;           // nearest control ancestor for text nodes
        public bool Folded;            // this text is part of Owner's label
        public UiTextInfo? OwnText;    // own text component, when any
        public string OwnTextDisplay;  // tags stripped, whitespace collapsed
        public readonly List<string> LabelParts = new List<string>();
        public string Sprite;
        public float Alpha = 1f;
        public Rect ScreenRect;
        public Rect VisibleRect;
        public string Visibility = "on_screen";
        public string Reason;          // set when geometry alone does not explain Visibility ("no_camera")
        public bool Visible => Visibility == "on_screen";
        public bool Interactive => Control || Role == "scroll";

        // Display text: a control's composed label, else its own text.
        public string Text
        {
            get
            {
                if (Control && LabelParts.Count > 0) return string.Join(" ", LabelParts);
                return string.IsNullOrEmpty(OwnTextDisplay) ? null : OwnTextDisplay;
            }
        }

        public bool HasText => !string.IsNullOrEmpty(Text);

        // Text nodes whose content is already part of a control's label.
        public bool FoldedIntoOwner => Folded;

        public Vector2 TapPoint => VisibleRect.width > 0 && VisibleRect.height > 0
            ? VisibleRect.center : ScreenRect.center;
    }

    internal static class UiScreenModel
    {
        internal const float AlphaEpsilon = 0.01f;
        internal const float SizeEpsilon = 0.5f;

        // Walk every root canvas. `screen` is the game's Screen size in pixels.
        internal static List<UiNode> Collect(Vector2 screen)
        {
            var nodes = new List<UiNode>(128);
            var roots = new List<Canvas>();
            foreach (var c in SceneQuery.FindAllOfType<Canvas>())
                if (c != null && c.isRootCanvas && c.gameObject.activeInHierarchy) roots.Add(c);
            roots.Sort((a, b) => CompareHierarchy(a.transform, b.transform));

            var screenRect = new Rect(0, 0, Mathf.Max(0, screen.x), Mathf.Max(0, screen.y));
            foreach (var canvas in roots)
            {
                var cam = CanvasCamera(canvas, out bool noCamera);
                var walk = new Walk { Nodes = nodes, Screen = screenRect, Camera = cam, NoCamera = noCamera, Root = canvas };
                walk.Visit(canvas.transform, ParentPath(canvas.transform), 0, 1f, false, new List<Rect>(), null);
            }
            return nodes;
        }

        // The camera uGUI draws a root canvas with (GraphicRaycaster.eventCamera
        // rules): none for Overlay and for Screen Space - Camera without a camera,
        // which Unity draws as Overlay (world corners are screen pixels). World
        // Space uses its camera, else Camera.main; with neither it cannot be
        // projected (`noCamera`).
        internal static Camera CanvasCamera(Canvas canvas, out bool noCamera)
        {
            noCamera = false;
            var own = canvas.worldCamera != null ? canvas.worldCamera : null;
            switch (canvas.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    return null;
                case RenderMode.ScreenSpaceCamera:
                    return own;
                default:
                    var cam = own != null ? own : Camera.main;
                    noCamera = cam == null;
                    return cam;
            }
        }

        // Total ordering of transforms by scene, then hierarchy (sibling indices).
        internal static int CompareHierarchy(Transform a, Transform b)
        {
            if (a == b) return 0;
            int sa = a.gameObject.scene.handle, sb = b.gameObject.scene.handle;
            if (sa != sb)
            {
                int ia = SceneOrder(a.gameObject.scene), ib = SceneOrder(b.gameObject.scene);
                if (ia != ib) return ia.CompareTo(ib);
                return sa.CompareTo(sb);
            }
            var pa = SiblingPath(a);
            var pb = SiblingPath(b);
            int n = Math.Min(pa.Count, pb.Count);
            for (int i = 0; i < n; i++)
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            return pa.Count.CompareTo(pb.Count);
        }

        private static int SceneOrder(UnityEngine.SceneManagement.Scene scene)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i) == scene) return i;
            return int.MaxValue; // DontDestroyOnLoad and other hidden scenes last
        }

        private static List<int> SiblingPath(Transform t)
        {
            var list = new List<int>();
            while (t != null) { list.Add(t.GetSiblingIndex()); t = t.parent; }
            list.Reverse();
            return list;
        }

        private static string ParentPath(Transform t)
        {
            var p = t.parent;
            return p == null ? "" : PointerTargets.BuildPath(p.gameObject);
        }

        private sealed class Walk
        {
            public List<UiNode> Nodes;
            public Rect Screen;
            public Camera Camera;
            public bool NoCamera;          // World Space canvas with no camera: nothing projects
            public Canvas Root;
            private readonly Vector3[] corners = new Vector3[4];

            public void Visit(Transform t, string parentPath, int depth, float parentAlpha, bool hidden,
                List<Rect> clips, UiNode owner)
            {
                var go = t.gameObject;
                if (!go.activeInHierarchy) return;
                var path = string.IsNullOrEmpty(parentPath) ? go.name : parentPath + "/" + go.name;

                // Effective alpha: CanvasGroup chain (ignoreParentGroups restarts it),
                // a disabled Canvas hides its whole subtree.
                float alpha = parentAlpha;
                var group = go.GetComponent<CanvasGroup>();
                if (group != null && group.enabled)
                    alpha = group.ignoreParentGroups ? group.alpha : parentAlpha * group.alpha;
                var canvas = go.GetComponent<Canvas>();
                if (canvas != null && !canvas.enabled) hidden = true;
                // A nested override-sorting canvas stops mask clipping from above.
                if (canvas != null && canvas != Root && canvas.overrideSorting) clips = new List<Rect>();

                var rt = t as RectTransform;
                UiNode node = null;
                if (rt != null)
                {
                    node = new UiNode
                    {
                        Go = go,
                        Rt = rt,
                        RootCanvas = Root,
                        Path = path,
                        Depth = depth,
                        Order = Nodes.Count,
                    };
                    Classify(node);
                    node.Alpha = hidden ? 0f : alpha;
                    if (node.OwnText.HasValue && (node.Role == "text" || !node.Control))
                        node.Alpha *= node.OwnText.Value.Alpha;
                    else if (node.Role == "image")
                    {
                        var graphic = go.GetComponent<Graphic>();
                        if (graphic != null) node.Alpha *= graphic.enabled ? graphic.color.a * RendererAlpha(go) : 0f;
                    }
                    ComputeGeometry(node, clips);
                    if (node.Role == "text" || (!node.Control && node.OwnText.HasValue)) node.Owner = owner;
                    // A text is part of its control's label unless it alone is
                    // invisible (a hidden sub-label); a hidden control keeps its label.
                    if (node.Owner != null && node.Role == "text" && !string.IsNullOrEmpty(node.OwnTextDisplay)
                        && (node.Alpha > AlphaEpsilon || owner.Alpha <= AlphaEpsilon))
                    {
                        node.Folded = true;
                        owner.LabelParts.Add(node.OwnTextDisplay);
                    }
                    Nodes.Add(node);

                    if (node.Control) owner = node;
                    else if (node.Role == "scroll") owner = null; // list items are not the label of an outer control

                    // Masks clip children, not the mask itself.
                    if (IsMask(go))
                    {
                        clips = new List<Rect>(clips) { node.ScreenRect };
                    }
                }

                for (int i = 0; i < t.childCount; i++)
                    Visit(t.GetChild(i), path, depth + 1, alpha, hidden, clips, owner);
            }

            private static bool IsMask(GameObject go)
            {
                var rm = go.GetComponent<RectMask2D>();
                if (rm != null && rm.isActiveAndEnabled) return true;
                var m = go.GetComponent<Mask>();
                return m != null && m.isActiveAndEnabled && m.graphic != null;
            }

            private void ComputeGeometry(UiNode node, List<Rect> clips)
            {
                if (NoCamera)
                {
                    node.ScreenRect = node.VisibleRect = Rect.zero;
                    node.Visibility = "offscreen";
                    node.Reason = "no_camera";
                    return;
                }
                node.Rt.GetWorldCorners(corners);
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                bool behind = false;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 sp;
                    if (Camera != null)
                    {
                        sp = Camera.WorldToScreenPoint(corners[i]);
                        if (sp.z < 0) behind = true;
                    }
                    else sp = corners[i];
                    if (sp.x < minX) minX = sp.x;
                    if (sp.y < minY) minY = sp.y;
                    if (sp.x > maxX) maxX = sp.x;
                    if (sp.y > maxY) maxY = sp.y;
                }
                node.ScreenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);

                var visible = Intersect(node.ScreenRect, Screen);
                bool onScreen = !behind && visible.width > 0 && visible.height > 0;
                bool unclipped = true;
                if (onScreen)
                {
                    foreach (var clip in clips)
                    {
                        visible = Intersect(visible, clip);
                        if (visible.width <= 0 || visible.height <= 0) { unclipped = false; break; }
                    }
                }
                node.VisibleRect = onScreen && unclipped ? visible : Rect.zero;

                if (node.ScreenRect.width < SizeEpsilon || node.ScreenRect.height < SizeEpsilon) node.Visibility = "zero_size";
                else if (node.Alpha <= AlphaEpsilon) node.Visibility = "transparent";
                else if (!onScreen) node.Visibility = "offscreen";
                else if (!unclipped) node.Visibility = "clipped";
                else node.Visibility = "on_screen";
            }
        }

        internal static Rect Intersect(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin), yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax), yMax = Mathf.Min(a.yMax, b.yMax);
            if (xMax <= xMin || yMax <= yMin) return Rect.zero;
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        // Role, own text, sprite, interactable.
        private static void Classify(UiNode node)
        {
            var go = node.Go;
            var selectable = go.GetComponent<Selectable>();
            if (selectable != null)
            {
                node.Role = RoleOf(selectable);
                node.Control = true;
                node.Interactable = PointerTargets.ComputeInteractable(go);
            }
            else if (go.GetComponent<ScrollRect>() != null)
            {
                node.Role = "scroll";
                node.Interactable = PointerTargets.ComputeInteractable(go);
            }
            else if (HasClickHandler(go))
            {
                node.Role = "button";
                node.Control = true;
                node.Interactable = PointerTargets.ComputeInteractable(go);
            }

            if (TryReadText(go, out var text))
            {
                node.OwnText = text;
                node.OwnTextDisplay = UiText.Display(text.Raw, text.RichText);
                if (node.Role == null) node.Role = "text";
                if (node.Control && !string.IsNullOrEmpty(node.OwnTextDisplay)) node.LabelParts.Add(node.OwnTextDisplay);
            }

            var image = go.GetComponent<Image>();
            if (image != null && image.sprite != null) node.Sprite = image.sprite.name;
            if (node.Role == null)
                node.Role = image != null || go.GetComponent<RawImage>() != null ? "image" : "container";
        }

        internal static string RoleOf(Selectable s)
        {
            if (s is Toggle) return "toggle";
            if (s is Slider || s is Scrollbar) return "slider";
            if (s is InputField || s.GetType().Name.Contains("InputField")) return "input";
            return "button";
        }

        private static bool HasClickHandler(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            foreach (var c in comps)
                if (c is IPointerClickHandler || c is IPointerDownHandler || c is ISubmitHandler) return true;
            return false;
        }

        internal static bool TryReadText(GameObject go, out UiTextInfo info)
        {
            var text = go.GetComponent<Text>();
            if (text != null && text.enabled)
            {
                info = ReadUguiText(text);
                return true;
            }
            foreach (var reader in UiTextReaders.All)
            {
                try
                {
                    if (reader.TryRead(go, out info)) return true;
                }
                catch (Exception)
                {
                    // A reader failure must not break the snapshot; the node keeps no text.
                }
            }
            info = default;
            return false;
        }

        // CanvasRenderer alpha (Graphic.CrossFadeAlpha / CrossFadeColor fades),
        // 1 without a renderer. Read through GetComponent: Graphic.canvasRenderer
        // adds a missing renderer, which would mutate the scene.
        internal static float RendererAlpha(GameObject go)
        {
            var renderer = go.GetComponent<CanvasRenderer>();
            return renderer != null ? renderer.GetAlpha() : 1f;
        }

        private static UiTextInfo ReadUguiText(Text text)
        {
            var info = new UiTextInfo
            {
                Raw = text.text,
                RichText = text.supportRichText,
                Alpha = text.color.a * RendererAlpha(text.gameObject),
                Source = "ugui",
            };
            var rect = text.rectTransform.rect;
            if (!text.resizeTextForBestFit && !string.IsNullOrEmpty(text.text))
            {
                float pw = text.preferredWidth, ph = text.preferredHeight;
                info.Content = new Vector2(pw, ph);
                bool vertical = ph > rect.height + SizeEpsilon;
                bool horizontal = text.horizontalOverflow == HorizontalWrapMode.Overflow && pw > rect.width + SizeEpsilon;
                info.Overflowing = vertical || horizontal;
            }
            else info.Content = rect.size;
            return info;
        }
    }

    // Text normalisation and matching (exact | contains | regex).
    internal static class UiText
    {
        private static readonly Regex Tags = new Regex(@"</?[A-Za-z#][^<>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex LineBreakTags = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

        // What the player reads: rich-text tags stripped (when the component
        // renders rich text; a <br> line break reads as a space, like a newline),
        // zero-width characters removed, whitespace collapsed.
        internal static string Display(string raw, bool richText = true)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var s = richText ? Tags.Replace(LineBreakTags.Replace(raw, " "), "") : raw;
            s = s.Replace("​", "").Replace("­", "");
            return Spaces.Replace(s, " ").Trim();
        }

        // Normalised query: same rules, tags always stripped.
        internal static string Normalize(string raw) => Display(raw, true);

        internal static bool IsValidMode(string mode)
            => string.IsNullOrEmpty(mode) || mode == "exact" || mode == "contains" || mode == "regex";

        // One matcher serves one screen walk: a regex is bounded per text and
        // over the whole walk (UiRegexRun); once it times out, TimedOut is set,
        // every later IsMatch is false and the caller refuses with regex_timeout.
        internal sealed class Matcher
        {
            private readonly string mode;
            private readonly string needle;
            private readonly UiRegexRun regex;

            private Matcher(string mode, string needle, UiRegexRun regex)
            {
                this.mode = mode;
                this.needle = needle;
                this.regex = regex;
            }

            // Returns null and an error message for an invalid mode/pattern.
            internal static Matcher Create(string query, string mode, out string error)
            {
                error = null;
                mode = string.IsNullOrEmpty(mode) ? "exact" : mode;
                if (!IsValidMode(mode))
                {
                    error = "text_match must be exact, contains or regex.";
                    return null;
                }
                if (mode == "regex")
                {
                    try
                    {
                        var rx = new Regex(query ?? "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            UiRegexRun.PerText);
                        return new Matcher(mode, null, new UiRegexRun(rx));
                    }
                    catch (ArgumentException ex)
                    {
                        error = "Invalid regex: " + ex.Message;
                        return null;
                    }
                }
                var n = Normalize(query).ToLowerInvariant();
                if (n.Length == 0)
                {
                    error = "target_text is empty after normalisation (trim, whitespace collapse, rich-text tags removed).";
                    return null;
                }
                return new Matcher(mode, n, null);
            }

            internal string Mode => mode;
            internal bool TimedOut => regex != null && regex.TimedOut;
            internal UiRegexRun RegexRun => regex;

            // `display` is already normalised (UiText.Display).
            internal bool IsMatch(string display)
            {
                if (string.IsNullOrEmpty(display)) return false;
                switch (mode)
                {
                    case "regex":
                        return regex.IsMatch(display);
                    case "contains":
                        return display.ToLowerInvariant().Contains(needle);
                    default:
                        return string.Equals(display.ToLowerInvariant(), needle, StringComparison.Ordinal);
                }
            }
        }

        // Truncate for payloads.
        internal static string Clip(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }

    // A caller-supplied regex run over the texts of one screen walk. Each text
    // is bounded by the Regex's own timeout (PerText) and the walk by Budget, so
    // a catastrophic pattern costs the main thread a couple of seconds at most.
    // The first text past either limit stops the run (TimedOutOn); it is never
    // read as "no match" — callers refuse with regex_timeout.
    internal sealed class UiRegexRun
    {
        internal static readonly TimeSpan PerText = TimeSpan.FromMilliseconds(100);
        internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(2000);

        private readonly Regex regex;
        private readonly System.Diagnostics.Stopwatch spent = new System.Diagnostics.Stopwatch();

        internal UiRegexRun(Regex regex) { this.regex = regex; }

        internal string TimedOutOn { get; private set; }
        internal bool TimedOut => TimedOutOn != null;

        internal bool IsMatch(string text)
        {
            if (TimedOut || text == null) return false;
            spent.Start();
            try { return regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { TimedOutOn = text; return false; }
            finally
            {
                spent.Stop();
                if (TimedOutOn == null && spent.Elapsed > Budget) TimedOutOn = text;
            }
        }

        // {"error":{"code":"regex_timeout",…}} naming the pattern and the text.
        internal string ErrorJson(string prefix, string parameter)
            => InputSimulationJson.Error("regex_timeout",
                prefix + $"{parameter} '{UiText.Clip(regex.ToString(), 120)}' exceeded its time limit " +
                $"({PerText.TotalMilliseconds:0} ms per text, {Budget.TotalMilliseconds:0} ms per screen) at text '" +
                UiText.Clip(TimedOutOn, 80) + "'. Simplify the pattern: avoid nested quantifiers such as (\\w+\\s?)+.");
    }

    // `under` scope: a node path, or a trailing path suffix ("Shop" matches
    // "Canvas/Shop"); every node in the subtree of a match is in scope.
    internal sealed class UiScope
    {
        private readonly List<string> roots = new List<string>();
        public bool Any => roots.Count > 0;
        public string Query { get; }

        private UiScope(string query) { Query = query; }

        internal static UiScope Build(List<UiNode> nodes, string under)
        {
            var scope = new UiScope(under);
            if (string.IsNullOrEmpty(under)) return scope;
            var q = under.Trim('/');
            foreach (var n in nodes)
                if (n.Path == q || n.Path.EndsWith("/" + q, StringComparison.Ordinal)) scope.roots.Add(n.Path);
            return scope;
        }

        internal bool Contains(UiNode node)
        {
            if (string.IsNullOrEmpty(Query)) return true;
            foreach (var r in roots)
                if (node.Path == r || node.Path.StartsWith(r + "/", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    // Occlusion: the front-most raycast hit at the node's tap point that is not
    // the node, its descendant or ancestor, and lies in front of the node's own
    // hit. Null when the node is on top, or when it is not a raycast target at
    // that point (unknown). Play mode with an EventSystem only.
    internal static class UiOcclusion
    {
        internal static bool Available => UnityEditor.EditorApplication.isPlaying && EventSystem.current != null;

        internal static string OccluderPath(UiNode node)
        {
            if (!Available || !node.Visible) return null;
            PointerTargets.RaycastTop(node.TapPoint, out var results);
            if (results == null) return null;
            GameObject front = null;
            foreach (var r in results)
            {
                var hit = r.gameObject;
                if (hit == null) continue;
                if (PointerTargets.IsSameOrDescendant(node.Go, hit)) return front == null ? null : PointerTargets.BuildPath(front);
                if (PointerTargets.IsSameOrDescendant(hit, node.Go)) continue; // an ancestor draws behind
                if (front == null) front = hit;
            }
            return null; // the node itself is not hit here: unknown, not reported
        }
    }

    // Label targeting shared by inputsim_pointer (direct) and inputsim_touch /
    // inputsim_gesture (through InputBackends.UiText).
    internal sealed class UiTextResolution
    {
        public UiNode Node;
        public Vector2 Point;
        public int Index;
        public int Matches;
        public string Query;
        public string Mode;

        public string Json()
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"query\":").Append(InputSimulationJson.Esc(Query));
            sb.Append(",\"match\":").Append(InputSimulationJson.Esc(Mode));
            sb.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(Node.Text, 120)));
            sb.Append(",\"path\":").Append(InputSimulationJson.Esc(Node.Path));
            sb.Append(",\"id\":").Append(InstanceId.ToJson(Node.Go));
            sb.Append(",\"role\":").Append(InputSimulationJson.Esc(Node.Role));
            sb.Append(",\"index\":").Append(Index);
            sb.Append(",\"matches\":").Append(Matches);
            sb.Append(",\"point\":[").Append(InputSimulationJson.Num(Point.x)).Append(',').Append(InputSimulationJson.Num(Point.y)).Append("]}");
            return sb.ToString();
        }
    }

    internal static class UiTextTargets
    {
        internal const int MaxListed = 12;

        // Resolve the visible element whose text matches. Controls match on
        // their label or any single label text; a matching text inside a control
        // resolves to the control, tapped where that text shows inside it
        // (LabelTapPoint). Several visible matches refuse with ambiguous_target
        // unless `index` picks one (hierarchy order). `nodes` reuses a walk taken
        // by the caller (null = walk now).
        internal static bool TryResolve(string query, string mode, string under, int? index, string context,
            out UiTextResolution resolution, out string errorJson, List<UiNode> nodes = null)
        {
            resolution = null;
            errorJson = null;
            var prefix = string.IsNullOrEmpty(context) ? "" : context + ": ";
            var matcher = UiText.Matcher.Create(query, mode, out var matchError);
            if (matcher == null)
            {
                errorJson = InputSimulationJson.Error("invalid_arguments", prefix + matchError);
                return false;
            }
            if (index.HasValue && index.Value < 0)
            {
                errorJson = InputSimulationJson.Error("invalid_arguments", prefix + "index must be >= 0.");
                return false;
            }

            if (nodes == null) nodes = UiScreenModel.Collect(GameViewWindows.MainGameViewSize());
            var scope = UiScope.Build(nodes, under);
            if (!string.IsNullOrEmpty(under) && !scope.Any)
            {
                errorJson = InputSimulationJson.Error("under_not_found",
                    prefix + $"No active uGUI element path matches under '{under}'.");
                return false;
            }

            var visible = new List<UiNode>();
            var hidden = new List<UiNode>();
            var points = new Dictionary<UiNode, Vector2>();
            var byLabelText = new HashSet<UiNode>();
            foreach (var n in nodes)
            {
                if (!scope.Contains(n)) continue;
                UiNode candidate = null;
                Vector2 point = default;
                bool labelText = false;
                if (n.Control && matcher.IsMatch(n.Text)) { candidate = n; point = n.TapPoint; }
                else if (n.OwnText.HasValue && matcher.IsMatch(n.OwnTextDisplay))
                {
                    labelText = n.Folded;
                    candidate = labelText ? n.Owner : n;
                    point = labelText ? LabelTapPoint(n, n.Owner) : n.TapPoint;
                }
                if (matcher.TimedOut)
                {
                    errorJson = matcher.RegexRun.ErrorJson(prefix, "target_text regex");
                    return false;
                }
                if (candidate == null) continue;
                if (points.ContainsKey(candidate))
                {
                    // A control matched on its whole label is tapped on the first
                    // single label text that also matches.
                    if (labelText && byLabelText.Add(candidate)) points[candidate] = point;
                    continue;
                }
                points[candidate] = point;
                if (labelText) byLabelText.Add(candidate);
                if (candidate.Visible) visible.Add(candidate);
                else hidden.Add(candidate);
            }

            if (visible.Count == 0)
            {
                var msg = prefix + $"No visible uGUI element with text {Describe(query, matcher.Mode)}" +
                          (string.IsNullOrEmpty(under) ? "" : $" under '{under}'") + ".";
                if (hidden.Count > 0)
                    msg += $" {hidden.Count} match(es) exist but are not visible (see hidden_matches).";
                errorJson = ErrorWithMatches("target_not_found", msg, null, hidden, "hidden_matches");
                return false;
            }
            if (index.HasValue && index.Value >= visible.Count)
            {
                errorJson = ErrorWithMatches("index_out_of_range",
                    prefix + $"index {index.Value} is out of range: {visible.Count} visible match(es) for {Describe(query, matcher.Mode)}.",
                    visible, null, null);
                return false;
            }
            if (!index.HasValue && visible.Count > 1)
            {
                errorJson = ErrorWithMatches("ambiguous_target",
                    prefix + $"Text {Describe(query, matcher.Mode)} matches {visible.Count} visible elements. " +
                    "Pass `index` (hierarchy order, see matches[].index), narrow with `under`, or address the element by path.",
                    visible, null, null);
                return false;
            }

            int pick = index ?? 0;
            var node = visible[pick];
            resolution = new UiTextResolution
            {
                Node = node,
                Point = points[node],
                Index = pick,
                Matches = visible.Count,
                Query = query,
                Mode = matcher.Mode,
            };
            return true;
        }

        // Where a label text shows inside its control: the centre of the text's
        // visible rect clipped to the control's visible rect; the control's own
        // tap point when the text shows nowhere inside it.
        internal static Vector2 LabelTapPoint(UiNode text, UiNode owner)
        {
            var area = UiScreenModel.Intersect(text.VisibleRect, owner.VisibleRect);
            return area.width > 0 && area.height > 0 ? area.center : owner.TapPoint;
        }

        private static string Describe(string query, string mode)
            => "'" + query + "'" + (mode == "exact" ? "" : " (" + mode + ")");

        // {"error":{code,message,candidates:[paths],matches:[{index,...}]}}
        private static string ErrorWithMatches(string code, string message, List<UiNode> matches,
            List<UiNode> hidden, string hiddenKey)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"error\":{\"code\":").Append(InputSimulationJson.Esc(code));
            sb.Append(",\"message\":").Append(InputSimulationJson.Esc(message));
            if (matches != null)
            {
                sb.Append(",\"candidates\":[");
                for (int i = 0; i < matches.Count && i < MaxListed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(InputSimulationJson.Esc(matches[i].Path));
                }
                sb.Append("],\"matches\":");
                AppendMatches(sb, matches, true);
            }
            if (hidden != null)
            {
                sb.Append(",\"").Append(hiddenKey).Append("\":");
                AppendMatches(sb, hidden, false);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // `index` is only listed for pickable (visible) matches.
        private static void AppendMatches(StringBuilder sb, List<UiNode> list, bool pickable)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count && i < MaxListed; i++)
            {
                var n = list[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                if (pickable) sb.Append("\"index\":").Append(i).Append(',');
                sb.Append("\"path\":").Append(InputSimulationJson.Esc(n.Path));
                sb.Append(",\"id\":").Append(InstanceId.ToJson(n.Go));
                sb.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
                sb.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(n.Text, 80)));
                sb.Append(",\"visibility\":").Append(InputSimulationJson.Esc(n.Visibility));
                sb.Append(",\"rect\":");
                UiSnapshotJson.AppendRect(sb, n.ScreenRect);
                sb.Append('}');
            }
            if (list.Count > MaxListed) sb.Append(",{\"more\":").Append(list.Count - MaxListed).Append('}');
            sb.Append(']');
        }
    }

    // Registers the label resolver for the device tools in the bridge root
    // assembly (inputsim_touch / inputsim_gesture), which cannot reference uGUI.
    [UnityEditor.InitializeOnLoad]
    internal static class UiTextTargetsRegistration
    {
        static UiTextTargetsRegistration()
        {
            InputBackends.UiText = new Adapter();
        }

        private sealed class Adapter : IUiTextTargets
        {
            public bool TryResolve(string text, string match, string under, int? index, string context,
                out Vector2 point, out string targetJson, out string errorJson)
            {
                point = default;
                targetJson = null;
                // Inside a UiTextWalkScope every lookup reads the same walk.
                List<UiNode> nodes = null;
                if (UiTextWalkScope.Active)
                {
                    nodes = UiTextWalkScope.Shared as List<UiNode>;
                    if (nodes == null)
                        UiTextWalkScope.Shared = nodes = UiScreenModel.Collect(GameViewWindows.MainGameViewSize());
                }
                if (!UiTextTargets.TryResolve(text, match, under, index, context, out var res, out errorJson, nodes))
                    return false;
                point = res.Point;
                targetJson = res.Json();
                return true;
            }
        }
    }
}
