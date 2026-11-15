// unity_senses_screenshot view "prefab" — the render and its preview-scene
// lifecycle: a UI prefab with its own Canvas + CanvasScaler and a panel without
// a Canvas are rendered in a preview scene; every object and the preview scene
// are gone afterwards, also when the render throws; no open scene gains an
// object or a dirty flag; the CanvasScaler base / overrides reach the canvas.
//
// Fixture prefabs are built in a preview scene and saved under a temp folder
// that the fixture deletes. Renders need a graphics device (skipped under
// -nographics). The uGUI half (PrefabView.Ugui) is registered by this
// package's uGUI sub-assembly.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityOpenMcpBridge.Extensions.InputSimulation;
using UnityOpenMcpBridge.Screenshot;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    [TestFixture]
    public class PrefabViewTests
    {
        private const string Root = "Assets/Tests/BridgeFixtures/PrefabView";
        private const string ScreenPath = Root + "/PV_Screen.prefab";
        private const string PanelPath = Root + "/PV_Panel.prefab";
        private const string PlainPath = Root + "/PV_Plain.prefab";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureFolder(Root);
            BuildFixtures();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            PrefabView.AfterBuildForTests = null;
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
        }

        [TearDown]
        public void TearDown() => PrefabView.AfterBuildForTests = null;

        private static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device (-nographics): the prefab view renders a camera.");
        }

        [Test]
        public void UguiSupport_IsRegistered_AndRoundTripsTheScaler()
        {
            Assert.IsInstanceOf<UguiPreviewSupport>(PrefabView.Ugui);
            var go = new GameObject("PV_ScalerRoundTrip", typeof(RectTransform), typeof(Canvas));
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var canvas = go.GetComponent<Canvas>();
                Assert.IsNull(PrefabView.Ugui.ReadScaler(canvas), "no CanvasScaler yet");
                var s = new CanvasScalerSettings
                {
                    UiScaleMode = "scale_with_screen_size", ReferenceResolution = new Vector2(750, 1334),
                    ScreenMatchMode = "expand", MatchWidthOrHeight = 0.25f, ReferencePixelsPerUnit = 64,
                };
                PrefabView.Ugui.ApplyScaler(canvas, s);
                var scaler = go.GetComponent<CanvasScaler>();
                Assert.IsNotNull(scaler, "ApplyScaler adds the CanvasScaler");
                Assert.AreEqual(CanvasScaler.ScreenMatchMode.Expand, scaler.screenMatchMode);
                var back = PrefabView.Ugui.ReadScaler(canvas);
                Assert.AreEqual("scale_with_screen_size", back.UiScaleMode);
                Assert.AreEqual(new Vector2(750, 1334), back.ReferenceResolution);
                Assert.AreEqual("expand", back.ScreenMatchMode);
                Assert.AreEqual(0.25f, back.MatchWidthOrHeight);
                Assert.AreEqual(64f, back.ReferencePixelsPerUnit);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void OwnCanvas_RendersWithItsScaler_AndLeavesNothingBehind()
        {
            RequireGraphics();
            var before = SceneState();
            var built = new List<GameObject>();
            Scene preview = default;
            PrefabView.AfterBuildForTests = scene => { preview = scene; built.AddRange(scene.GetRootGameObjects()); };

            var capture = PrefabView.Capture(new PrefabViewRequest { AssetPath = ScreenPath, Width = 540, Height = 960 });

            Assert.AreEqual(540, capture.Width);
            Assert.AreEqual(960, capture.Height);
            Assert.AreEqual(PrefabView.Source, capture.Source);
            StringAssert.Contains("\"canvas\":\"own\"", capture.DetailJson);
            StringAssert.Contains("\"render_mode\":\"screen_space_overlay\"", capture.DetailJson);
            StringAssert.Contains("\"from\":\"own\"", capture.DetailJson);
            // 1080x1920 reference, match 0.5, at 540x960: half size.
            StringAssert.Contains("\"scale_factor\":0.5", capture.DetailJson);

            // The red marker anchored top-left (120 px at scale 0.5 = 60 px) and
            // the blue background in the middle are on the first render.
            var top = Pixel(capture.Png, 10, 960 - 10);
            Assert.Greater(top.r, 0.8f, "top-left marker is red: " + top);
            Assert.Less(top.g, 0.2f, "top-left marker is red: " + top);
            var middle = Pixel(capture.Png, 270, 300);
            Assert.Greater(middle.b, middle.r, "background is blue: " + middle);

            AssertCleanedUp(before, built, preview);
        }

        [Test]
        public void CleanupRuns_WhenTheRenderThrows()
        {
            RequireGraphics();
            var before = SceneState();
            var built = new List<GameObject>();
            Scene preview = default;
            PrefabView.AfterBuildForTests = scene =>
            {
                preview = scene;
                built.AddRange(scene.GetRootGameObjects());
                throw new InvalidOperationException("boom");
            };

            var e = Assert.Throws<InvalidOperationException>(() =>
                PrefabView.Capture(new PrefabViewRequest { AssetPath = ScreenPath, Width = 256, Height = 256 }));
            Assert.AreEqual("boom", e.Message);
            Assert.IsNotEmpty(built, "the hook saw the preview scene");
            AssertCleanedUp(before, built, preview);
        }

        [Test]
        public void Panel_IsWrapped_WithTheExplicitScaler()
        {
            RequireGraphics();
            var before = SceneState();
            var capture = PrefabView.Capture(new PrefabViewRequest
            {
                AssetPath = PanelPath, Width = 1080, Height = 1920,
                CanvasScaler = "{\"ui_scale_mode\":\"scale_with_screen_size\",\"reference_resolution\":[540,960]," +
                               "\"screen_match_mode\":\"match_width_or_height\",\"match_width_or_height\":1}",
            });
            StringAssert.Contains("\"canvas\":\"wrapped\"", capture.DetailJson);
            StringAssert.Contains("\"scale_factor\":2", capture.DetailJson);
            StringAssert.Contains("\"overrides\":[\"ui_scale_mode\",\"reference_resolution\",\"screen_match_mode\",\"match_width_or_height\"]", capture.DetailJson);
            // The 300x200 panel is centred: at scale 2 it spans 600x400 px around the middle.
            var centre = Pixel(capture.Png, 540, 960);
            Assert.Greater(centre.g, 0.6f, "panel is green: " + centre);
            var outside = Pixel(capture.Png, 540, 960 + 250);
            Assert.Less(outside.g, 0.4f, "above the panel is the grey background: " + outside);
            AssertCleanedUp(before, null, default);
        }

        [Test]
        public void Panel_TakesTheScalerFromAPrefab()
        {
            RequireGraphics();
            var capture = PrefabView.Capture(new PrefabViewRequest
            {
                AssetPath = PanelPath, Width = 540, Height = 960, ScalerFrom = ScreenPath,
                CanvasScaler = "{\"match_width_or_height\":0}",
            });
            StringAssert.Contains("\"from\":\"scaler_from\"", capture.DetailJson);
            StringAssert.Contains("\"from_path\":\"" + ScreenPath + "\"", capture.DetailJson);
            StringAssert.Contains("\"reference_resolution\":[1080,1920]", capture.DetailJson);
            StringAssert.Contains("\"match_width_or_height\":0", capture.DetailJson);
            StringAssert.Contains("\"scale_factor\":0.5", capture.DetailJson);
        }

        [Test]
        public void SafeAreaOverlay_DrawsTheRequestedScreen()
        {
            RequireGraphics();
            var capture = PrefabView.Capture(new PrefabViewRequest { AssetPath = ScreenPath, Width = 300, Height = 600, SafeAreaOverlay = true });
            Assert.IsTrue(capture.SafeAreaOverlay);
            Assert.AreEqual(new Rect(0, 0, 300, 600), capture.SafeArea, "without a device the whole screen is safe");
            Assert.AreEqual("preview", capture.Context.Mode);
        }

        [Test]
        public void Refusals_HaveStableCodes()
        {
            Assert.AreEqual("not_a_ui_prefab", Code(new PrefabViewRequest { AssetPath = PlainPath, Width = 64, Height = 64 }));
            Assert.AreEqual("asset_not_found", Code(new PrefabViewRequest { AssetPath = Root + "/Missing.prefab", Width = 64, Height = 64 }));
            Assert.AreEqual("scaler_not_found", Code(new PrefabViewRequest { AssetPath = PanelPath, Width = 64, Height = 64, ScalerFrom = PlainPath }));
            Assert.AreEqual("scaler_not_found", Code(new PrefabViewRequest { AssetPath = PanelPath, Width = 64, Height = 64, ScalerFrom = "__NoSuchRoot__/Canvas" }));
        }

        // ---- helpers ----

        private static string Code(PrefabViewRequest r)
        {
            int previews = EditorSceneManager.previewSceneCount;
            try
            {
                PrefabView.Capture(r);
                return null;
            }
            catch (CaptureRefusedException e)
            {
                Assert.AreEqual(previews, EditorSceneManager.previewSceneCount, "a refusal opens no preview scene");
                return e.Code;
            }
        }

        private struct State
        {
            public int Previews;
            public string Scenes;
        }

        // Loaded scenes with their root count and dirty flag, and the preview-scene count.
        private static State SceneState()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.Append(s.path).Append('|').Append(s.name).Append('|').Append(s.rootCount).Append('|').Append(s.isDirty).Append(';');
            }
            sb.Append("active=").Append(SceneManager.GetActiveScene().name);
            return new State { Previews = EditorSceneManager.previewSceneCount, Scenes = sb.ToString() };
        }

        private static void AssertCleanedUp(State before, List<GameObject> built, Scene preview)
        {
            var after = SceneState();
            Assert.AreEqual(before.Previews, after.Previews, "the preview scene is closed");
            Assert.AreEqual(before.Scenes, after.Scenes, "open scenes keep their roots, dirty flags and active scene");
            if (built != null)
                foreach (var go in built)
                    Assert.IsTrue(go == null, "an object of the preview scene survived the capture");
            if (preview.handle != 0) Assert.IsFalse(preview.IsValid(), "the preview scene handle is no longer valid");
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                Assert.IsFalse(go.name == "Prefab View Camera" || go.name == "Canvas (Prefab View)" || go.name == "Prefab View",
                    "leaked helper: " + go.name);
        }

        private static Color Pixel(byte[] png, int x, int y)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(png));
                return tex.GetPixel(x, y);
            }
            finally { Object.DestroyImmediate(tex); }
        }

        // PV_Screen: Canvas (Overlay) + CanvasScaler 1080x1920 / match 0.5, a
        // full-screen blue Image and a 120 px red marker anchored top-left.
        // PV_Panel: a 300x200 green Image with a RectTransform root, no Canvas.
        // PV_Plain: a plain Transform.
        private static void BuildFixtures()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var screen = New("PV_Screen", scene, typeof(RectTransform));
                screen.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = screen.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight = 0.5f;
                var bg = New("Background", scene, typeof(RectTransform));
                bg.transform.SetParent(screen.transform, false);
                Place(bg, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero);
                bg.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.9f, 1f);
                var marker = New("Marker", scene, typeof(RectTransform));
                marker.transform.SetParent(screen.transform, false);
                Place(marker, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(120, 120));
                marker.AddComponent<Image>().color = Color.red;
                PrefabUtility.SaveAsPrefabAsset(screen, ScreenPath);

                var panel = New("PV_Panel", scene, typeof(RectTransform));
                Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(300, 200));
                panel.AddComponent<Image>().color = Color.green;
                PrefabUtility.SaveAsPrefabAsset(panel, PanelPath);

                PrefabUtility.SaveAsPrefabAsset(New("PV_Plain", scene), PlainPath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static GameObject New(string name, Scene scene, params Type[] components)
        {
            var go = new GameObject(name, components);
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private static void Place(GameObject go, Vector2 min, Vector2 max, Vector2 pivot, Vector2 size)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = pivot;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = size;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
#endif
