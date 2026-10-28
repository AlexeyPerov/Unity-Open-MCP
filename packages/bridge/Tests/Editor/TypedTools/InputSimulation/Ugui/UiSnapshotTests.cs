// ui_snapshot + label targeting (target_text) — EditMode tests over a canvas
// built per test: roles, labels folded into controls, visibility (on_screen /
// clipped / offscreen / transparent / zero_size), profiles, paging, filters,
// diagnostics, text normalisation and the ambiguity / hidden-match refusals.
//
// The overlay canvas is sized to the Game view by Unity; elements sit near its
// top-left corner (on screen for any reasonable view) and the offscreen one is
// thousands of pixels outside. Every query is scoped with `under` to this
// canvas so other open scenes do not leak in. Taps through the real input
// module are covered by the live replay on the demo UI fixture (play mode).
#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class UiSnapshotTests
    {
        private GameObject canvasGo;
        private string canvasName;
        private Font font;

        [SetUp]
        public void SetUp()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasName = "UiSnapTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            canvasGo = new GameObject(canvasName, typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            var size = ((RectTransform)canvasGo.transform).rect.size;
            if (size.x < 400 || size.y < 300)
                Assert.Ignore("Overlay canvas has no usable screen size in this Editor (" + size + ").");

            var root = canvasGo.transform;
            var buy = Button("Btn_A", root, 20, 20, 200, 60);
            Label(buy, "  <b>BUY</b>\n now ");
            var offerA = Rect("OfferA", root, 20, 100, 200, 60);
            Label(Button("Btn", offerA, 0, 0, 200, 60), "Play");
            var offerB = Rect("OfferB", root, 240, 100, 200, 60);
            Label(Button("Btn", offerB, 0, 0, 200, 60), "Play");
            Text("Coins", root, 240, 20, 200, 40, "Coins: 5");

            var panel = Rect("HiddenPanel", root, 460, 20, 200, 120);
            panel.gameObject.AddComponent<CanvasGroup>().alpha = 0f;
            Label(Button("HiddenClose", panel, 0, 0, 200, 60), "Close");

            var viewport = Rect("Viewport", root, 20, 200, 200, 100);
            viewport.gameObject.AddComponent<RectMask2D>();
            Label(Button("Item1", viewport, 0, 10, 200, 40), "Item 1");
            Label(Button("Item2", viewport, 0, 150, 200, 40), "Item 2");

            Text("Offscreen", root, -5000, 20, 200, 40, "Far away");
            Button("Ghost", root, 300, 330, 0, 0);
            Label(Button("Tiny", root, 20, 168, 20, 20), "i").fontSize = 10;

            var longText = Text("LongText", root, 240, 200, 100, 20,
                "This sentence is far too long to fit in a twenty pixel tall box.");
            longText.verticalOverflow = VerticalWrapMode.Truncate;
            Text("Missing", root, 240, 240, 320, 30, "{shop.missing_key}");
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasGo != null) Object.DestroyImmediate(canvasGo);
        }

        // --- helpers ------------------------------------------------------

        private static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        private Text Text(string name, Transform parent, float x, float y, float w, float h, string value)
        {
            var t = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = 18;
            t.text = value;
            return t;
        }

        private static Button Button(string name, Transform parent, float x, float y, float w, float h)
        {
            var rt = Rect(name, parent, x, y, w, h);
            rt.gameObject.AddComponent<Image>();
            return rt.gameObject.AddComponent<Button>();
        }

        private Text Label(Button b, string value)
        {
            var t = Text("Label", b.transform, 0, 0, 10, 10, value);
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            return t;
        }

        private List<UiNode> Nodes()
        {
            var all = UiScreenModel.Collect(GameViewWindows.MainGameViewSize());
            var scope = UiScope.Build(all, canvasName);
            return all.FindAll(scope.Contains);
        }

        private UiNode Node(string relativePath)
        {
            var path = canvasName + "/" + relativePath;
            var n = Nodes().Find(x => x.Path == path);
            Assert.IsNotNull(n, "node " + path);
            return n;
        }

        private bool Resolve(string text, string match, int? index, out UiTextResolution res, out string error)
            => UiTextTargets.TryResolve(text, match, canvasName, index, null, out res, out error);

        // --- registry -----------------------------------------------------

        [Test]
        public void Registry_SnapshotIsReadOnlyGateFreePlaytest()
        {
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(UiSnapshotTools.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.IsTrue(info.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("playtest", info.Group);
        }

        [Test]
        public void LabelResolverIsRegisteredForTheDeviceTools()
        {
            Assert.IsNotNull(InputBackends.UiText);
            CollectionAssert.Contains(UiTextReaders.Sources(), "ugui");
        }

        // --- model ------------------------------------------------------------

        [Test]
        public void RolesAndLabelsFoldIntoTheirControl()
        {
            var buy = Node("Btn_A");
            Assert.AreEqual("button", buy.Role);
            Assert.AreEqual("BUY now", buy.Text, "tags stripped, whitespace collapsed, case kept for display");
            Assert.IsTrue(buy.Interactable);
            var label = Node("Btn_A/Label");
            Assert.AreEqual("text", label.Role);
            Assert.IsTrue(label.FoldedIntoOwner);
            Assert.AreEqual(buy.Path, label.Owner.Path);
            Assert.AreEqual("text", Node("Coins").Role);
            Assert.AreEqual("container", Node("OfferA").Role);
        }

        [Test]
        public void VisibilityCoversEveryState()
        {
            Assert.AreEqual("on_screen", Node("Btn_A").Visibility);
            Assert.AreEqual("transparent", Node("HiddenPanel/HiddenClose").Visibility);
            Assert.AreEqual("Close", Node("HiddenPanel/HiddenClose").Text, "a hidden control keeps its label");
            Assert.AreEqual("on_screen", Node("Viewport/Item1").Visibility);
            Assert.AreEqual("clipped", Node("Viewport/Item2").Visibility);
            Assert.AreEqual("offscreen", Node("Offscreen").Visibility);
            Assert.AreEqual("zero_size", Node("Ghost").Visibility);
            Assert.IsFalse(Node("Viewport/Item2").Visible);
        }

        [Test]
        public void IgnoreParentGroupsRestartsTheAlphaChain()
        {
            var child = Node("HiddenPanel/HiddenClose").Go;
            child.AddComponent<CanvasGroup>().ignoreParentGroups = true;
            Assert.AreEqual("on_screen", Node("HiddenPanel/HiddenClose").Visibility);
        }

        [Test]
        public void DisabledCanvasHidesItsSubtree()
        {
            canvasGo.GetComponent<Canvas>().enabled = false;
            Assert.AreEqual("transparent", Node("Btn_A").Visibility);
        }

        [Test]
        public void CanvasRendererFadeMakesAGraphicTransparent()
        {
            Node("Coins").Go.GetComponent<CanvasRenderer>().SetAlpha(0f);
            Assert.AreEqual("transparent", Node("Coins").Visibility, "Graphic.CrossFadeAlpha fades the CanvasRenderer");
        }

        [Test]
        public void CanvasCameraFollowsUguiDrawRules()
        {
            var canvas = canvasGo.GetComponent<Canvas>();
            Assert.IsNull(UiScreenModel.CanvasCamera(canvas, out var noCamera));
            Assert.IsFalse(noCamera);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            Assert.IsNull(UiScreenModel.CanvasCamera(canvas, out noCamera), "no camera: drawn as Overlay");
            Assert.IsFalse(noCamera);
            canvas.renderMode = RenderMode.WorldSpace;
            Assert.AreEqual(Camera.main, UiScreenModel.CanvasCamera(canvas, out noCamera));
            Assert.AreEqual(Camera.main == null, noCamera);
        }

        // --- text normalisation / matching ------------------------------------

        [Test]
        public void NormalisationStripsTagsCollapsesWhitespaceAndIgnoresCase()
        {
            Assert.AreEqual("Buy now", UiText.Display("  <b>Buy</b>\n\t now <color=#f00>  </color>"));
            Assert.AreEqual("a <3 b", UiText.Display("a <3 b"), "a lone '<' is not a tag");
            Assert.AreEqual("<b>x</b>", UiText.Display("<b>x</b>", richText: false));
            Assert.AreEqual("Claim x2 now", UiText.Display("Claim<br>x2<BR />now"), "a <br> line break reads as a space");
            var exact = UiText.Matcher.Create("  buy NOW ", "exact", out _);
            Assert.IsTrue(exact.IsMatch("Buy now"));
            Assert.IsFalse(exact.IsMatch("Buy now!"));
            Assert.IsTrue(UiText.Matcher.Create("NOW", "contains", out _).IsMatch("Buy now"));
            Assert.IsTrue(UiText.Matcher.Create("^buy\\s+n", "regex", out _).IsMatch("Buy now"));
            Assert.IsNull(UiText.Matcher.Create("(", "regex", out var regexError));
            StringAssert.Contains("Invalid regex", regexError);
            Assert.IsNull(UiText.Matcher.Create("x", "fuzzy", out _));
            Assert.IsNull(UiText.Matcher.Create(" <b></b> ", "exact", out _), "empty after normalisation");
        }

        [Test]
        public void RegexTimeoutStopsMatchingAndRefuses()
        {
            // Nested quantifiers backtrack exponentially on a non-matching tail.
            var matcher = UiText.Matcher.Create("^(a+)+$", "regex", out _);
            Assert.IsFalse(matcher.IsMatch(new string('a', 40) + "!"));
            Assert.IsTrue(matcher.TimedOut, "a timeout is not a plain no-match");
            Assert.IsFalse(matcher.IsMatch("aaa"), "a timed-out run matches nothing more");

            const string catastrophic = "^(\\w+\\s?)+$"; // the long sentence ends with '.'
            Assert.IsFalse(Resolve(catastrophic, "regex", null, out _, out var error));
            StringAssert.Contains("\"regex_timeout\"", error);
            StringAssert.Contains("\"regex_timeout\"",
                UiSnapshotTools.Snapshot(under: canvasName, placeholder_pattern: catastrophic));
        }

        // --- label targeting -------------------------------------------------

        [Test]
        public void TargetText_ExactMatchIgnoresCaseTagsAndWhitespace()
        {
            Assert.IsTrue(Resolve("buy now", null, null, out var res, out var error), error);
            Assert.AreEqual(canvasName + "/Btn_A", res.Node.Path);
            Assert.Less(Vector2.Distance(res.Node.VisibleRect.center, res.Point), 0.01f, "the label fills its button");
            StringAssert.Contains("\"match\":\"exact\"", res.Json());
        }

        [Test]
        public void TargetText_LabelMatchTapsThatLabelInsideItsControl()
        {
            // Free band between the offers (y 100..160) and the viewport (y 200..).
            var row = Button("Row", canvasGo.transform, 60, 165, 160, 30);
            Text("Title", row.transform, 0, 0, 80, 30, "Daily deal");
            Text("Price", row.transform, 80, 0, 80, 30, "250 gems");
            var price = Node("Row/Price");

            Assert.IsTrue(Resolve("250 gems", null, null, out var res, out var error), error);
            Assert.AreEqual(canvasName + "/Row", res.Node.Path, "the control stays the target");
            Assert.IsTrue(price.VisibleRect.Contains(res.Point), "tapped where the matched text shows");
            Assert.Greater(Vector2.Distance(res.Node.TapPoint, res.Point), 1f);

            // A whole-label match is refined by the first label text that also matches.
            Assert.IsTrue(Resolve("gems", "contains", null, out res, out error), error);
            Assert.IsTrue(price.VisibleRect.Contains(res.Point));
        }

        [Test]
        public void TargetText_PartialLabelNeedsContains()
        {
            // Exact compares the whole (normalised) text; a partial label needs contains.
            Assert.IsFalse(Resolve("buy", "exact", null, out _, out var error));
            StringAssert.Contains("\"target_not_found\"", error);
            Assert.IsTrue(Resolve("buy", "contains", null, out var res, out error), error);
            Assert.AreEqual(canvasName + "/Btn_A", res.Node.Path);
        }

        [Test]
        public void TargetText_EqualLabelsAreAmbiguousUntilIndexed()
        {
            Assert.IsFalse(Resolve("Play", null, null, out _, out var error));
            StringAssert.Contains("\"ambiguous_target\"", error);
            StringAssert.Contains(canvasName + "/OfferA/Btn", error);
            StringAssert.Contains(canvasName + "/OfferB/Btn", error);
            StringAssert.Contains("\"matches\":[{\"index\":0", error);

            Assert.IsTrue(Resolve("Play", null, 1, out var res, out error), error);
            Assert.AreEqual(canvasName + "/OfferB/Btn", res.Node.Path);
            Assert.AreEqual(2, res.Matches);

            Assert.IsFalse(Resolve("Play", null, 2, out _, out error));
            StringAssert.Contains("\"index_out_of_range\"", error);
        }

        [Test]
        public void TargetText_HiddenMatchesAreReportedNotTapped()
        {
            Assert.IsFalse(Resolve("Close", null, null, out _, out var error));
            StringAssert.Contains("\"target_not_found\"", error);
            StringAssert.Contains("\"hidden_matches\"", error);
            StringAssert.Contains("\"visibility\":\"transparent\"", error);
            Assert.IsFalse(Resolve("Item 2", null, null, out _, out error));
            StringAssert.Contains("\"visibility\":\"clipped\"", error);
            Assert.IsTrue(Resolve("item 1", null, null, out _, out error), error);
        }

        [Test]
        public void TargetText_UnderScopesAndRejectsUnknownScopes()
        {
            Assert.IsTrue(UiTextTargets.TryResolve("Play", null, canvasName + "/OfferA", null, null, out var res, out var error), error);
            Assert.AreEqual(canvasName + "/OfferA/Btn", res.Node.Path);
            Assert.IsFalse(UiTextTargets.TryResolve("Play", null, "NoSuchScope_" + canvasName, null, null, out _, out error));
            StringAssert.Contains("\"under_not_found\"", error);
        }

        [Test]
        public void TargetText_InvalidArgumentsRefuse()
        {
            Assert.IsFalse(Resolve("x", "fuzzy", null, out _, out var error));
            StringAssert.Contains("\"invalid_arguments\"", error);
            Assert.IsFalse(Resolve("x", null, -1, out _, out error));
            StringAssert.Contains("\"invalid_arguments\"", error);
        }

        [Test]
        public void DeviceAdapterReturnsPointAndTargetJson()
        {
            Assert.IsTrue(InputBackends.UiText.TryResolve("Coins: 5", null, canvasName, null, null,
                out var point, out var json, out var error), error);
            Assert.AreEqual(Node("Coins").TapPoint, point);
            StringAssert.Contains("\"role\":\"text\"", json);
        }

        // --- tool response ----------------------------------------------------

        [Test]
        public void Snapshot_CompactListsVisibleTextAndControlsOnly()
        {
            var json = UiSnapshotTools.Snapshot(under: canvasName, page_size: 200);
            StringAssert.Contains("\"profile\":\"compact\"", json);
            StringAssert.Contains("\"path\":\"" + canvasName + "/Btn_A\"", json);
            StringAssert.Contains("\"text\":\"BUY now\"", json);
            StringAssert.Contains("\"path\":\"" + canvasName + "/Coins\"", json);
            StringAssert.Contains("\"path\":\"" + canvasName + "/Viewport/Item1\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/Btn_A/Label\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/Viewport/Item2\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/Offscreen\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/HiddenPanel/HiddenClose\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/OfferA\"", json);
            StringAssert.DoesNotContain("\"sprite\"", json);
            StringAssert.Contains("\"occlusion\":\"unavailable\"", json);
        }

        [Test]
        public void Snapshot_FullListsHiddenNodesWithEveryKey()
        {
            var json = UiSnapshotTools.Snapshot(profile: "full", under: canvasName, page_size: 200);
            StringAssert.Contains("\"path\":\"" + canvasName + "/Viewport/Item2\"", json);
            StringAssert.Contains("\"visibility\":\"clipped\"", json);
            StringAssert.Contains("\"visibility\":\"offscreen\"", json);
            StringAssert.Contains("\"visibility\":\"transparent\"", json);
            StringAssert.Contains("\"visibility\":\"zero_size\"", json);
            StringAssert.Contains("\"owner\":\"" + canvasName + "/Btn_A\"", json);
            StringAssert.Contains("\"occluded_by\":null", json);
            StringAssert.Contains("\"depth\":", json);
            // visible_only narrows full back to on-screen nodes.
            var visible = UiSnapshotTools.Snapshot(profile: "full", under: canvasName, page_size: 200, visible_only: true);
            StringAssert.DoesNotContain("\"visibility\":\"clipped\"", visible);
        }

        [Test]
        public void Snapshot_BalancedAddsImagesAndContainersButNotFoldedLabels()
        {
            var json = UiSnapshotTools.Snapshot(profile: "balanced", under: canvasName, page_size: 200);
            StringAssert.Contains("\"path\":\"" + canvasName + "/OfferA\"", json);
            StringAssert.DoesNotContain("\"path\":\"" + canvasName + "/Btn_A/Label\"", json);
            StringAssert.Contains("\"depth\":", json);
        }

        [Test]
        public void Snapshot_PagesWithCursorAndDiagnosticsOnFirstPageOnly()
        {
            var first = UiSnapshotTools.Snapshot(under: canvasName, page_size: 2);
            StringAssert.Contains("\"next_cursor\":\"ui_snapshot:2\"", first);
            StringAssert.Contains("\"diagnostics\":", first);
            var second = UiSnapshotTools.Snapshot(under: canvasName, page_size: 2, cursor: "ui_snapshot:2");
            StringAssert.Contains("\"cursor\":\"ui_snapshot:2\"", second);
            StringAssert.DoesNotContain("\"diagnostics\":", second);
        }

        [Test]
        public void Snapshot_FiltersByRoleAndText()
        {
            var buttons = UiSnapshotTools.Snapshot(under: canvasName, role: "button", page_size: 200);
            StringAssert.DoesNotContain("\"role\":\"text\"", buttons);
            var coins = UiSnapshotTools.Snapshot(under: canvasName, text_contains: "  COINS ", page_size: 200);
            StringAssert.Contains("\"total\":1", coins);
            StringAssert.Contains("\"error\"", UiSnapshotTools.Snapshot(role: "slidr"));
            StringAssert.Contains("\"under_not_found\"", UiSnapshotTools.Snapshot(under: "NoSuchScope_" + canvasName));
            StringAssert.Contains("\"invalid_arguments\"", UiSnapshotTools.Snapshot(profile: "verbose"));
        }

        [Test]
        public void Diagnostics_OverflowAndZeroSizeAlways_OptInChecksOnlyWhenPassed()
        {
            var plain = UiSnapshotTools.Snapshot(under: canvasName, page_size: 1);
            StringAssert.Contains("\"overflow\":{\"count\":1", plain);
            StringAssert.Contains(canvasName + "/LongText", plain);
            StringAssert.Contains("\"zero_size_interactables\":{\"count\":1", plain);
            StringAssert.Contains(canvasName + "/Ghost", plain);
            StringAssert.DoesNotContain("\"placeholder\"", plain);
            StringAssert.DoesNotContain("\"small_touch_targets\"", plain);

            var opted = UiSnapshotTools.Snapshot(under: canvasName, page_size: 1,
                placeholder_pattern: "^[*#{].*[*#}]$", min_touch_size: 30f);
            StringAssert.Contains("\"placeholder\":{\"count\":1,\"pattern\":", opted);
            StringAssert.Contains("{shop.missing_key}", opted);
            StringAssert.Contains("\"small_touch_targets\":{\"count\":1,\"min_touch_size\":30", opted);
            StringAssert.Contains(canvasName + "/Tiny", opted);
            StringAssert.Contains("\"dpi\":", opted);

            StringAssert.Contains("\"invalid_arguments\"", UiSnapshotTools.Snapshot(placeholder_pattern: "("));
            StringAssert.Contains("\"invalid_arguments\"", UiSnapshotTools.Snapshot(min_touch_size: 0f));
        }

        // --- playtest_wait `ui` condition probe (same node model) ---------------

        [Test]
        public void UiConditionProbe_RegisteredAndUsesTheSnapshotModel()
        {
            Assert.IsNotNull(UnityOpenMcpBridge.Playtest.PlaytestProbes.Ui, "registered by the uGUI assembly");
            var probe = UnityOpenMcpBridge.Playtest.PlaytestProbes.Ui;
            Assert.AreEqual(1, probe.Count("play", canvasName + "/OfferA/Btn", true, out var first));
            StringAssert.Contains("\"role\":\"button\"", first);
            StringAssert.Contains("\"text\":\"Play\"", first);
            Assert.AreEqual(0, probe.Count("close", canvasName + "/HiddenPanel/HiddenClose", true, out _), "transparent is not visible");
            Assert.AreEqual(1, probe.Count("close", canvasName + "/HiddenPanel/HiddenClose", false, out first));
            StringAssert.Contains("\"visibility\":\"transparent\"", first);
            Assert.AreEqual(1, probe.Count(null, "Viewport/Item2", false, out first), "path suffix; clipped");
            StringAssert.Contains("\"visibility\":\"clipped\"", first);
            // Without a path a label counts once, as its control.
            Assert.AreEqual(1, probe.Count("buy now", null, true, out first));
            StringAssert.Contains(canvasName + "/Btn_A", first);
        }

        // --- input tools: argument contract (play-mode guard comes first) -------

        [Test]
        public void PointerGuardRunsBeforeLabelResolution()
        {
            StringAssert.Contains("\"play_mode_required\"", PointerTools.Pointer(action: "click", target_text: "Play"));
        }
    }
}
#endif
