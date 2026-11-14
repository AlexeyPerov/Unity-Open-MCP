using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Screenshot;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests
{
    // Single-capture crop / downscale / save_to (unity_senses_screenshot and
    // capture_inline). Pixels are synthesized: the left half red, the right
    // half blue, so a crop can be checked by colour.
    public class ImagePostProcessTests
    {
        private static byte[] SplitPng(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        pixels[y * width + x] = x < width / 2 ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 255, 255);
                tex.SetPixels32(pixels);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(tex); }
        }

        private static Color Center(byte[] png, out int width, out int height)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(png));
                width = tex.width;
                height = tex.height;
                return tex.GetPixel(width / 2, height / 2);
            }
            finally { Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Parse_ValidatesEveryOption()
        {
            Assert.IsNull(ImagePostProcess.Parse("[0,0,10,10]", 0.5f, 256, "Temp/shots/", out var ok));
            Assert.IsTrue(ok.Region.HasValue);
            StringAssert.Contains("image pixels", ImagePostProcess.Parse("[1,2,3]", null, null, null, out _));
            StringAssert.Contains("scale", ImagePostProcess.Parse(null, 2f, null, null, out _));
            StringAssert.Contains("max_edge", ImagePostProcess.Parse(null, null, 4, null, out _));
            StringAssert.Contains("outside Assets/", ImagePostProcess.Parse(null, null, null, "Assets/Shots/a.png", out _));
            StringAssert.Contains(".png", ImagePostProcess.Parse(null, null, null, "/tmp/a.jpg", out _));
            Assert.IsNull(ImagePostProcess.Parse(null, null, null, null, out var none));
            Assert.IsFalse(none.Any);
        }

        [Test]
        public void Apply_CropsBottomLeftRegionThenScales()
        {
            var post = new ImagePostProcess { Region = new RectInt(50, 0, 50, 40), Scale = 0.5f };
            var png = post.Apply(SplitPng(100, 40));
            var color = Center(png, out var w, out var h);
            Assert.AreEqual(25, w);
            Assert.AreEqual(20, h);
            Assert.AreEqual(1f, color.b, 0.01f, "the right (blue) half was kept");
            Assert.AreEqual(100, post.SourceWidth);
            Assert.AreEqual(new RectInt(50, 0, 50, 40), post.AppliedRegion.Value);
        }

        [Test]
        public void Apply_MaxEdgeBoundsTheLongestSideAndClipsTheRegion()
        {
            var post = new ImagePostProcess { MaxEdge = 32, Region = new RectInt(0, 0, 500, 500) };
            var png = post.Apply(SplitPng(128, 64));
            Center(png, out var w, out var h);
            Assert.AreEqual(32, w);
            Assert.AreEqual(16, h);
            Assert.AreEqual(new RectInt(0, 0, 128, 64), post.AppliedRegion.Value, "clipped to the image");

            var outside = new ImagePostProcess { Region = new RectInt(500, 500, 10, 10) };
            Assert.Throws<CaptureRefusedException>(() => outside.Apply(SplitPng(64, 64)));
        }

        [Test]
        public void TargetSize_NeverUpscales()
        {
            ImagePostProcess.TargetSize(100, 50, 1f, 400, out var w, out var h);
            Assert.AreEqual(100, w);
            Assert.AreEqual(50, h);
        }

        // The refusal is about where the file lands, not how the path is spelled:
        // an absolute or dot-segment path into the project's asset roots would
        // be imported exactly like "Assets/…".
        [Test]
        public void CheckSaveTo_RefusesTheProjectAssetRootsHoweverSpelled()
        {
            var root = Path.Combine(Path.GetTempPath(), "ipp-project");
            var refused = new[]
            {
                "Assets/a.png", "./Assets/a.png", "Temp/../Assets/Shots/", "assets/a.png", "Assets", "Packages/pkg/",
                Path.Combine(root, "Assets", "Shots", "a.png"), Path.Combine(root, "Packages") + "/",
            };
            foreach (var path in refused)
                StringAssert.Contains("outside Assets/", ImagePostProcess.CheckSaveTo(path, root), path);

            var allowed = new[] { "Temp/a.png", "AssetsBackup/a.png", "../shots/", "shots", "~/ipp-shots/", Path.Combine(Path.GetTempPath(), "ipp-out", "a.png") };
            foreach (var path in allowed)
                Assert.IsNull(ImagePostProcess.CheckSaveTo(path, root), path);
        }

        [Test]
        public void ResolveTarget_ReadsAnExtensionlessPathAsADirectory()
        {
            var root = Path.Combine(Path.GetTempPath(), "ipp-project");
            Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "out", "shots", "shot.png")),
                ImagePostProcess.ResolveTarget("out/shots", root, "shot.png"));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "out", "named.png")),
                ImagePostProcess.ResolveTarget("out/named.png", root, "shot.png"));
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(home, "ipp-shots", "shot.png")),
                ImagePostProcess.ResolveTarget("~/ipp-shots/", root, "shot.png"));
        }

        [Test]
        public void Save_CopiesToAFileOrIntoADirectory()
        {
            var root = Path.Combine(Path.GetTempPath(), "ipp-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "shot.png");
                File.WriteAllBytes(source, SplitPng(8, 8));
                var intoDir = new ImagePostProcess { SaveTo = "out/" }.Save(source, root);
                Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "out", "shot.png")), intoDir);
                Assert.IsTrue(File.Exists(intoDir));
                var named = new ImagePostProcess { SaveTo = Path.Combine(root, "named.png") }.Save(source, root);
                Assert.IsTrue(File.Exists(named));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
