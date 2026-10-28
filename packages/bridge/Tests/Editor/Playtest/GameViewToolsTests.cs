// Game view / Device Simulator control (unity_open_mcp_game_view) and the
// native-size composed capture: the shared Editor version table, the adapter's
// refusal on an unverified Editor, argument rules, the pure helpers (custom size
// label, aspect, insets, device matching), the play-mode settle step, the
// letterbox fit and the safe-area overlay. Changing the real Game view size or
// opening the Simulator is covered by the manual checklist (needs a GUI Editor).
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.Playtest;
using UnityOpenMcpBridge.Screenshot;

namespace UnityOpenMcpBridge.Tests.Playtest
{
    public class GameViewToolsTests
    {
        [TearDown]
        public void TearDown()
        {
            GameViewAdapter.UnityVersionOverride = null;
            EditorInputAdapter.UnityVersionOverride = null;
        }

        // ---- version table ------------------------------------------------

        [TestCase("6000.3.21f1", true)]
        [TestCase("6000.0.30f1", true)]
        [TestCase("6000.5.0a3", true)]
        [TestCase("2022.3.40f1", false)]
        [TestCase("2023.2.20f1", false)]
        [TestCase("7000.0.0f1", false)]
        [TestCase("garbage", false)]
        public void VersionTable_Unity6EnabledOthersRefused(string version, bool supported)
        {
            Assert.AreEqual(supported, GameViewAdapter.IsVersionSupported(version, out var detail), detail);
            if (!supported && version != "garbage")
            {
                StringAssert.Contains("Game view / Device Simulator control", detail);
                StringAssert.Contains("2022.3", detail);
            }
        }

        [Test]
        public void VersionTable_IsSharedWithTheLegacyInputAdapter()
        {
            Assert.AreEqual(GameViewAdapter.Versions.Rules.Length, EditorInputAdapter.Versions.Rules.Length);
            Assert.IsFalse(EditorInputAdapter.IsVersionSupported("2022.3.40f1", out var inputDetail));
            StringAssert.Contains("legacy input injection", inputDetail);
            Assert.IsTrue(EditorVersionTable.TryParse("6000.3.21f1", out var major, out var minor));
            Assert.AreEqual(6000, major);
            Assert.AreEqual(3, minor);
            Assert.IsFalse(EditorVersionTable.TryParse("6000", out _, out _));
        }

        [Test]
        public void UnverifiedEditor_EveryActionRefusesUnsupportedEditorVersion()
        {
            GameViewAdapter.UnityVersionOverride = "2022.3.40f1";
            foreach (var action in new[] { "get", "set_resolution", "set_device", "list_devices" })
            {
                var json = GameViewTools.GameView(action, 1080, 1920, null, "iOS Notch Device (1170x2532)");
                StringAssert.Contains("\"code\":\"unsupported_editor_version\"", json, action);
                StringAssert.Contains("\"unityVersion\":\"2022.3.40f1\"", json, action);
                StringAssert.Contains("agentNextSteps", json, action);
            }
        }

        [Test]
        public void UnverifiedEditor_CaptureContextFallsBackWithoutGuessing()
        {
            GameViewAdapter.UnityVersionOverride = "2022.3.40f1";
            var ctx = ScreenshotService.DescribeGameView();
            Assert.AreEqual("unknown", ctx.Mode);
            StringAssert.StartsWith("unsupported_editor_version", ctx.Note);
            var sb = new StringBuilder();
            ScreenshotService.AppendContext(sb, ctx);
            StringAssert.Contains("\"mode\":\"unknown\"", sb.ToString());
        }

        // ---- tool contract --------------------------------------------------

        [Test]
        public void Registry_GameViewIsGateFreePlaytest()
        {
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(GameViewTools.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("playtest", info.Group);
        }

        [Test]
        public void Arguments_InvalidActionAndTimeoutAreRefused()
        {
            StringAssert.Contains("\"code\":\"invalid_arguments\"", GameViewTools.GameView("zoom"));
            StringAssert.Contains("settle_timeout_ms", GameViewTools.GameView("get", settle_timeout_ms: 50));
        }

        [Test]
        public void Arguments_ResolutionRules()
        {
            Assert.IsNull(GameViewTools.ValidateResolutionArgs(1080, 1920, null));
            Assert.IsNull(GameViewTools.ValidateResolutionArgs(null, null, "Free Aspect"));
            StringAssert.Contains("not both", GameViewTools.ValidateResolutionArgs(1080, 1920, "Free Aspect"));
            StringAssert.Contains("width and height", GameViewTools.ValidateResolutionArgs(1080, null, null));
            StringAssert.Contains("16..8192", GameViewTools.ValidateResolutionArgs(8, 1920, null));
            StringAssert.Contains("16..8192", GameViewTools.ValidateResolutionArgs(1080, 9000, null));
        }

        [Test]
        public void Get_ReportsTheModeInThisEditor()
        {
            if (!GameViewAdapter.IsGameViewAvailable(out var code, out var message))
                Assert.Ignore(code + ": " + message);
            var json = GameViewTools.GameView("get");
            StringAssert.Contains("\"status\":\"ok\"", json);
            StringAssert.Contains("\"mode\":", json);
            StringAssert.Contains("\"screen\":", json);
        }

        // ---- pure helpers ---------------------------------------------------

        [Test]
        public void Helpers_LabelAspectOrientationAndInsets()
        {
            Assert.AreEqual("Open MCP 1080x1920", GameViewAdapter.CustomSizeLabel(1080, 1920));
            Assert.AreEqual("9:16", GameViewAdapter.AspectLabel(1080, 1920));
            Assert.AreEqual("16:9", GameViewAdapter.AspectLabel(1920, 1080));
            Assert.IsNull(GameViewAdapter.AspectLabel(0, 10));
            Assert.AreEqual("portrait", GameViewAdapter.ShapeOrientation(1080, 1920));
            Assert.AreEqual("landscape", GameViewAdapter.ShapeOrientation(1920, 1080));

            // A notched phone in portrait: safe area (0, 102, 1170, 2289) of 1170x2532.
            GameViewAdapter.Insets(new Rect(0, 102, 1170, 2289), 1170, 2532, out int top, out int bottom, out int left, out int right);
            Assert.AreEqual(141, top);
            Assert.AreEqual(102, bottom);
            Assert.AreEqual(0, left);
            Assert.AreEqual(0, right);

            Assert.IsTrue(GameViewAdapter.TryParseOrientation("Landscape_Left", out var o));
            Assert.AreEqual(ScreenOrientation.LandscapeLeft, o);
            Assert.AreEqual("portrait_upside_down", GameViewAdapter.OrientationName(ScreenOrientation.PortraitUpsideDown));
            Assert.IsFalse(GameViewAdapter.TryParseOrientation("sideways", out _));
        }

        [Test]
        public void Helpers_DeviceMatching()
        {
            var ids = new List<string> { "iOS Notch Device (1170x2532)", "iOS Notch Device Large (1284x2778)", "Tablet Small (1536x2048)" };
            Assert.AreEqual(0, GameViewAdapter.MatchDevice(ids, "ios notch device (1170x2532)", out _));
            Assert.AreEqual(2, GameViewAdapter.MatchDevice(ids, "tablet", out _));
            Assert.AreEqual(-2, GameViewAdapter.MatchDevice(ids, "notch", out var candidates));
            Assert.AreEqual(2, candidates.Count);
            Assert.AreEqual(-1, GameViewAdapter.MatchDevice(ids, "nokia", out _));
            Assert.AreEqual(-1, GameViewAdapter.MatchDevice(ids, "  ", out _));
        }

        [Test]
        public void ListDevices_EntryCarriesTheSafeAreaSummary()
        {
            var d = new DeviceEntry { Id = "iOS Notch Device (1170x2532)", Width = 1170, Height = 2532, Dpi = 460, Screens = 1 };
            d.Orientations.Add(new DeviceOrientationEntry { Orientation = ScreenOrientation.Portrait, SafeArea = new Rect(0, 102, 1170, 2289) });
            d.Orientations.Add(new DeviceOrientationEntry { Orientation = ScreenOrientation.LandscapeLeft, SafeArea = new Rect(141, 63, 2250, 1107) });
            var sb = new StringBuilder();
            GameViewTools.AppendDevice(sb, d);
            var json = sb.ToString();
            StringAssert.Contains("\"portrait_safe_area\":[0,102,1170,2289]", json);
            StringAssert.Contains("\"insets\":{\"top\":141,\"bottom\":102,\"left\":0,\"right\":0}", json);
            StringAssert.Contains("\"notch\":true", json);
            StringAssert.Contains("\"orientations\":[\"portrait\",\"landscape_left\"]", json);
        }

        // ---- settle ---------------------------------------------------------

        [Test]
        public void Settle_NeedsTwoMatchingFramesOfScreenAndRenderTarget()
        {
            var op = new GameViewSettleOperation(1080, 1920, 5000);
            Assert.IsFalse(op.Observe(2422, 1483, 2422, 1483, 0), "old size");
            Assert.IsFalse(op.Observe(1080, 1920, 2422, 1483, 16), "Screen changed, render target not yet");
            Assert.IsFalse(op.Observe(1080, 1920, 1080, 1920, 32), "first matching frame");
            Assert.IsTrue(op.Observe(1080, 1920, 1080, 1920, 48), "second matching frame");
            Assert.IsTrue(op.Settled);
            StringAssert.Contains("\"settled\":true", op.BuildResult(PlayModeEndReason.Completed, null));
        }

        [Test]
        public void Settle_MismatchResetsAndTheTimeLimitEndsUnsettled()
        {
            var op = new GameViewSettleOperation(1080, 1920, 100);
            Assert.IsFalse(op.Observe(1080, 1920, 1080, 1920, 0));
            Assert.IsFalse(op.Observe(1170, 2532, 1080, 1920, 16), "a mismatch resets the count");
            Assert.IsFalse(op.Observe(1080, 1920, 1080, 1920, 32));
            Assert.IsTrue(op.Observe(1170, 2532, 1170, 2532, 120), "out of time");
            Assert.IsFalse(op.Settled);
            var result = op.BuildResult(PlayModeEndReason.Completed, null);
            StringAssert.Contains("\"settled\":false", result);
            StringAssert.Contains("\"observed\":{\"screen\":[1170,2532],\"render_texture\":[1170,2532]", result);
            StringAssert.Contains("not visible", result);
        }

        [Test]
        public void Settle_AViewBehindAnotherTabNeverSettles()
        {
            var op = new GameViewSettleOperation(1080, 1920, 100);
            Assert.IsFalse(op.Observe(1080, 1920, 1080, 1920, 0, 1, viewVisible: false));
            Assert.IsFalse(op.Observe(1080, 1920, 1080, 1920, 16, 2, viewVisible: false), "stale render target of a hidden tab");
            Assert.IsTrue(op.Observe(1080, 1920, 1080, 1920, 120, 3, viewVisible: false), "out of time");
            Assert.IsFalse(op.Settled);
            StringAssert.Contains("\"visible\":false", op.BuildResult(PlayModeEndReason.Completed, null));
        }

        // ---- batch ----------------------------------------------------------

        [Test]
        public void Batch_SetActionsAreServerPolled_GetIsNot()
        {
            foreach (var action in new[] { "set_resolution", "set_device" })
            {
                var body = "{\"commands\":[{\"tool\":\"unity_open_mcp_game_view\",\"params\":{\"action\":\"" + action + "\",\"width\":1080,\"height\":1920}}]}";
                var result = BatchExecuteTool.Execute(body);
                Assert.AreEqual("batch_step_requires_server_poll", result.ErrorCode, action);
                StringAssert.Contains("top-level", result.ErrorMessage);
            }
            var get = BatchExecuteTool.Execute("{\"commands\":[{\"tool\":\"unity_open_mcp_game_view\",\"params\":{\"action\":\"get\"}}]}");
            Assert.AreNotEqual("batch_step_requires_server_poll", get.ErrorCode);
        }
    }

    public class ComposedCaptureTests
    {
        [Test]
        public void FitRect_PadsAMismatchedAspectAndKeepsAMatchingOne()
        {
            var tall = ScreenshotService.FitRect(1170, 2532, 1280, 720);
            Assert.AreEqual(new RectInt(473, 0, 333, 720), tall);
            var same = ScreenshotService.FitRect(1080, 1920, 540, 960);
            Assert.AreEqual(new RectInt(0, 0, 540, 960), same);
            var wide = ScreenshotService.FitRect(1920, 1080, 1080, 1920);
            Assert.AreEqual(0, wide.x);
            Assert.AreEqual(1080, wide.width);
            Assert.AreEqual(608, wide.height);
            Assert.AreEqual((1920 - 608) / 2, wide.y);
        }

        [Test]
        public void FrameArgs_Rules()
        {
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("composed", null, null, null, null));
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("composed", null, null, "native", "safe_area"));
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("composed", 1280, 720, null, null));
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("game", null, null, "native", null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("composed", 1280, null, null, null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("composed", 1280, 720, "native", null));
            // scene / game / isolated default a missing axis instead of refusing it.
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("scene", 1920, null, null, null));
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("isolated", null, 512, null, null));
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("game", 800, null, null, null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("game", null, 720, "native", null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("composed", null, null, "large", null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("scene", null, null, "native", null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("scene", null, null, null, "safe_area"));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("composed", null, null, null, "grid"));
        }

        [Test]
        public void SafeAreaOverlay_TintsTheUnsafeBandAndOutlinesTheSafeRect()
        {
            var tex = new Texture2D(100, 200, TextureFormat.RGBA32, false);
            try
            {
                var fill = new Color32[100 * 200];
                for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(0, 0, 255, 255);
                tex.SetPixels32(fill);
                tex.Apply();
                // Safe area: bottom 10 px and top 20 px are unsafe.
                ScreenshotService.DrawSafeAreaOverlay(tex, new Rect(0, 10, 100, 170));
                var band = (Color32)tex.GetPixel(50, 195);
                Assert.Greater(band.r, 0, "unsafe band is tinted");
                var bottomBand = (Color32)tex.GetPixel(50, 3);
                Assert.Greater(bottomBand.r, 0);
                var edge = (Color32)tex.GetPixel(50, 11);
                Assert.AreEqual(255, edge.g, "safe rect outline");
                var inside = (Color32)tex.GetPixel(50, 100);
                Assert.AreEqual(new Color32(0, 0, 255, 255), inside, "inside the safe area untouched");
            }
            finally { Object.DestroyImmediate(tex); }
        }

        [Test]
        public void ReadRenderTarget_FlipTurnsTheImageUpsideDown()
        {
            var src = new Texture2D(1, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var rt = new RenderTexture(1, 2, 0, RenderTextureFormat.ARGB32);
            Texture2D straight = null, flipped = null;
            try
            {
                src.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(0, 0, 255, 255) }); // bottom red, top blue
                src.Apply();
                Graphics.Blit(src, rt);
                straight = ScreenshotService.ReadRenderTarget(rt, false);
                flipped = ScreenshotService.ReadRenderTarget(rt, true);
                Assert.AreEqual(straight.GetPixel(0, 0), flipped.GetPixel(0, 1));
                Assert.AreEqual(straight.GetPixel(0, 1), flipped.GetPixel(0, 0));
                Assert.AreNotEqual(straight.GetPixel(0, 0), straight.GetPixel(0, 1));
            }
            finally
            {
                Object.DestroyImmediate(src);
                rt.Release();
                Object.DestroyImmediate(rt);
                if (straight != null) Object.DestroyImmediate(straight);
                if (flipped != null) Object.DestroyImmediate(flipped);
            }
        }
    }
}
