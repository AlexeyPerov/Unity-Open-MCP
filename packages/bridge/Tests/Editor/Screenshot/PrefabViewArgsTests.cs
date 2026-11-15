using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Screenshot;

namespace UnityOpenMcpBridge.Tests
{
    // unity_senses_screenshot view "prefab": argument rules, the canvas_scaler
    // object and the order the CanvasScaler base is chosen in. The render and
    // the preview-scene lifecycle are covered by PrefabViewTests (uGUI tests).
    public class PrefabViewArgsTests
    {
        private static string Validate(PrefabViewRequest r, out string code) => PrefabView.ValidateArgs(r, out code);

        [Test]
        public void ValidateArgs_NeedsExactlyOneSource()
        {
            Assert.IsNotNull(Validate(new PrefabViewRequest(), out var code));
            Assert.AreEqual("missing_parameter", code);
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", GameObjectPath = "Canvas" }, out code));
            Assert.AreEqual("validation_error", code);
            Assert.IsNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab" }, out _));
            Assert.IsNull(Validate(new PrefabViewRequest { GameObjectPath = "Canvas/Panel" }, out _));
        }

        [Test]
        public void ValidateArgs_OneScreenSize()
        {
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Width = 100 }, out _), "width without height");
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Width = 100, Height = 200, Device = "Phone" }, out _));
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Device = "Phone", SizeLabel = "Full HD" }, out _));
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Width = 8, Height = 200 }, out _), "below the minimum");
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Orientation = "portrait" }, out _), "orientation needs a device");
            Assert.IsNotNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Device = "Phone", Orientation = "sideways" }, out _));
            Assert.IsNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Device = "Phone", Orientation = "landscape_left" }, out _));
            Assert.IsNull(Validate(new PrefabViewRequest { AssetPath = "Assets/A.prefab", Width = 1242, Height = 2688 }, out _));
        }

        [Test]
        public void ValidateArgs_RejectsABadCanvasScaler()
        {
            var r = new PrefabViewRequest { AssetPath = "Assets/A.prefab", CanvasScaler = "{\"match\":0.5}" };
            StringAssert.Contains("Unknown canvas_scaler key 'match'", Validate(r, out _));
            r.CanvasScaler = "{\"match_width_or_height\":2}";
            StringAssert.Contains("0 (width) to 1 (height)", Validate(r, out _));
            r.CanvasScaler = "{\"ui_scale_mode\":\"stretch\"}";
            StringAssert.Contains("ui_scale_mode must be one of", Validate(r, out _));
            r.CanvasScaler = "[1,2]";
            StringAssert.Contains("must be an object", Validate(r, out _));
        }

        [Test]
        public void CanvasScaler_AppliesOnlyTheGivenKeys_OverTheBase()
        {
            var s = new CanvasScalerSettings
            {
                UiScaleMode = "scale_with_screen_size",
                ReferenceResolution = new Vector2(1080, 1920),
                MatchWidthOrHeight = 0.5f,
            };
            var applied = new List<string>();
            Assert.IsNull(s.ApplyJson("{\"match_width_or_height\":1,\"reference_resolution\":[750,1334]}", applied));
            CollectionAssert.AreEqual(new[] { "match_width_or_height", "reference_resolution" }, applied);
            Assert.AreEqual("scale_with_screen_size", s.UiScaleMode);
            Assert.AreEqual(1f, s.MatchWidthOrHeight);
            Assert.AreEqual(new Vector2(750, 1334), s.ReferenceResolution);
        }

        [Test]
        public void CanvasScaler_AnInvalidValueLeavesTheSettingsUnchanged()
        {
            var s = new CanvasScalerSettings();
            Assert.IsNotNull(s.ApplyJson("{\"scale_factor\":2,\"reference_resolution\":[0,10]}", new List<string>()));
            Assert.AreEqual(1f, s.ScaleFactor, "nothing is applied when one value is invalid");
        }

        [Test]
        public void CanvasScaler_JsonNamesTheModeFields()
        {
            var s = new CanvasScalerSettings { UiScaleMode = "scale_with_screen_size", ReferenceResolution = new Vector2(1080, 1920), MatchWidthOrHeight = 0.5f };
            var sb = new System.Text.StringBuilder();
            s.AppendJson(sb);
            Assert.AreEqual("\"ui_scale_mode\":\"scale_with_screen_size\",\"reference_resolution\":[1080,1920]," +
                            "\"screen_match_mode\":\"match_width_or_height\",\"match_width_or_height\":0.5,\"reference_pixels_per_unit\":100",
                sb.ToString());
        }

        // ---- the base scaler ----

        private static readonly CanvasScalerSettings Arg = new CanvasScalerSettings { ScaleFactor = 2 };
        private static readonly CanvasScalerSettings Own = new CanvasScalerSettings { ScaleFactor = 3 };
        private static readonly CanvasScalerSettings Parent = new CanvasScalerSettings { ScaleFactor = 4 };
        private static readonly CanvasScalerSettings Scene = new CanvasScalerSettings { ScaleFactor = 5 };

        [Test]
        public void ChooseScaler_ScalerFromWinsOverEverything()
        {
            var c = PrefabView.ChooseScaler(Arg, "Assets/Hud.prefab", Own, false, Parent, "p", Scene, "s");
            Assert.AreEqual("scaler_from", c.From);
            Assert.AreEqual("Assets/Hud.prefab", c.FromPath);
            Assert.AreEqual(2f, c.Settings.ScaleFactor);
            Assert.IsTrue(c.Changed);
            Assert.AreNotSame(Arg, c.Settings, "the base is copied, never edited in place");
        }

        [Test]
        public void ChooseScaler_OwnCanvasKeepsItsScaler()
        {
            var c = PrefabView.ChooseScaler(null, null, Own, false, Parent, "p", Scene, "s");
            Assert.AreEqual("own", c.From);
            Assert.AreEqual(3f, c.Settings.ScaleFactor);
            Assert.IsFalse(c.Changed, "the canvas is rendered as authored");
        }

        [Test]
        public void ChooseScaler_OwnCanvasWithoutScaler_TakesTheParentCanvas_ElseNone()
        {
            var c = PrefabView.ChooseScaler(null, null, null, false, Parent, "Main.unity:Canvas", null, null);
            Assert.AreEqual("parent_canvas", c.From);
            Assert.AreEqual(4f, c.Settings.ScaleFactor);
            c = PrefabView.ChooseScaler(null, null, null, false, null, null, Scene, "s");
            Assert.AreEqual("none", c.From, "a prefab with its own canvas never borrows a scene's scaler");
            Assert.IsNull(c.Settings);
        }

        [Test]
        public void ChooseScaler_PanelTakesParent_ThenScene_ThenDefaults()
        {
            Assert.AreEqual("parent_canvas", PrefabView.ChooseScaler(null, null, null, true, Parent, "p", Scene, "s").From);
            var scene = PrefabView.ChooseScaler(null, null, null, true, null, null, Scene, "Assets/Boot.unity:Canvas");
            Assert.AreEqual("scene", scene.From);
            Assert.AreEqual("Assets/Boot.unity:Canvas", scene.FromPath);
            var fallback = PrefabView.ChooseScaler(null, null, null, true, null, null, null, null);
            Assert.AreEqual("default", fallback.From);
            Assert.AreEqual("constant_pixel_size", fallback.Settings.UiScaleMode);
            Assert.AreEqual(1f, fallback.Settings.ScaleFactor);
        }

        // ---- the tool's argument routing ----

        [Test]
        public void ValidateFrameArgs_PrefabTakesNativeSizeAndTheSafeAreaOverlay()
        {
            Assert.IsNull(ScreenshotService.ValidateFrameArgs("prefab", null, null, "native", "safe_area"));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("prefab", 100, null, null, null));
            Assert.IsNotNull(ScreenshotService.ValidateFrameArgs("scene", null, null, null, "safe_area"));
        }

        [Test]
        public void Screenshot_PrefabArgumentsOnAnotherView_AreRefused()
        {
            var json = new Tool_Screenshot().Screenshot(view: "scene", asset_path: "Assets/A.prefab");
            StringAssert.Contains("\"code\":\"validation_error\"", json);
            StringAssert.Contains("view: \\\"prefab\\\"", json);
        }

        [Test]
        public void Screenshot_PrefabView_MissingAndNonPrefabAssets()
        {
            if (PrefabView.Ugui == null) Assert.Ignore("uGUI is not installed: the prefab view answers ugui_unavailable.");
            StringAssert.Contains("\"code\":\"asset_not_found\"",
                new Tool_Screenshot().Screenshot(view: "prefab", width: 64, height: 64, asset_path: "Assets/__no_such_prefab__.prefab"));
            StringAssert.Contains("\"code\":\"not_a_prefab\"",
                new Tool_Screenshot().Screenshot(view: "prefab", width: 64, height: 64, asset_path: "Packages/com.alexeyperov.unity-open-mcp-bridge/package.json"));
        }
    }
}
