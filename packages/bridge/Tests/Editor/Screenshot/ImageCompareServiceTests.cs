using System;
using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Screenshot;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests
{
    // EditMode tests for the visual-regression diff engine (ImageCompareService)
    // and its tool wrapper (Tool_VisualCompare). The compare math is pure and
    // exercised directly against synthesized PNGs; the save/list/delete path
    // writes into the real references dir under a unique "T_visreg_" prefix and
    // is cleaned up in TearDown so it never touches user references.
    public class ImageCompareServiceTests
    {
        // Unique prefix so cleanup never removes a user-owned reference.
        private const string TestPrefix = "T_visreg_";

        [TearDown]
        public void TearDown()
        {
            foreach (var info in ImageCompareService.ListReferences())
            {
                if (info.Name != null && info.Name.StartsWith(TestPrefix, StringComparison.Ordinal))
                    ImageCompareService.DeleteReference(info.Name);
            }
        }

        // ============================ registry wiring ============================

        [Test]
        public static void VisualCompare_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_visual_compare"),
                "unity_senses_visual_compare should be discovered by the registry");
        }

        [Test]
        public static void VisualCompare_IsReadOnlyAndGateOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_visual_compare", out var entry));
            Assert.IsFalse(entry.IsMutating, "visual_compare should be non-mutating (read-only)");
            Assert.AreEqual(GateMode.Off, entry.Gate, "visual_compare should have gate off");
            Assert.IsTrue(entry.ReadOnlyHint, "visual_compare should have ReadOnlyHint = true");
        }

        [Test]
        public static void VisualCompare_GroupIsAgentSenses()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_visual_compare", out var entry));
            Assert.AreEqual("agent-senses", entry.Group,
                "visual_compare should map to the agent-senses group");
        }

        // ============================ compare math ============================

        [Test]
        public static void Compare_IdenticalImages_ZeroDiffAndMatch()
        {
            var png = MakeSolidPng(16, 16, new Color32(100, 150, 200, 255));
            var result = ImageCompareService.Compare(png, png, sensitivity: 0f, produceDiff: true);

            Assert.AreEqual(0.0, result.PixelDiffPercent, 0.0001, "identical images must have 0% diff");
            Assert.AreEqual(0, result.MismatchedPixels, "identical images must have 0 mismatched pixels");
            Assert.AreEqual(0, result.PerceptualDistance, "identical images must have 0 perceptual distance");
            Assert.IsTrue(result.Match, "identical images must match");
            Assert.IsNull(result.DiffImageBytes, "no diff image should be produced for an exact match");
        }

        [Test]
        public static void Compare_DifferingImages_NonZeroDiffAndNoMatch()
        {
            var a = MakeSolidPng(16, 16, new Color32(10, 10, 10, 255));
            var b = MakeSolidPng(16, 16, new Color32(250, 250, 250, 255));
            var result = ImageCompareService.Compare(a, b, sensitivity: 0f, produceDiff: true);

            Assert.AreEqual(100.0, result.PixelDiffPercent, 0.0001, "fully contrasting images must be 100% diff");
            Assert.AreEqual(16 * 16, result.MismatchedPixels, "every pixel must mismatch");
            Assert.IsFalse(result.Match, "fully differing images must not match at sensitivity 0");
            Assert.IsNotNull(result.DiffImageBytes, "a diff image should be produced on mismatch");
            Assert.Greater(result.DiffImageBytes.Length, 0, "diff image bytes should be non-empty");
        }

        [Test]
        public static void Compare_SensitivityThreshold_FlipsMatch()
        {
            // 1 of 100 pixels differs → 1% diff. sensitivity 0.01 (1%) → match;
            // sensitivity 0.009 (0.9%) → no match.
            var a = MakePngWithOneDifferentPixel(10, 10, new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255));
            var baseline = MakeSolidPng(10, 10, new Color32(0, 0, 0, 255));

            var matchAtOnePercent = ImageCompareService.Compare(baseline, a, sensitivity: 0.01f, produceDiff: false);
            var noMatchBelowOnePercent = ImageCompareService.Compare(baseline, a, sensitivity: 0.009f, produceDiff: false);

            Assert.AreEqual(1.0, matchAtOnePercent.PixelDiffPercent, 0.01, "1/100 pixels = 1% diff");
            Assert.IsTrue(matchAtOnePercent.Match, "1% diff at 1% sensitivity should match (<=)");
            Assert.IsFalse(noMatchBelowOnePercent.Match, "1% diff at 0.9% sensitivity should not match");
        }

        [Test]
        public static void Compare_DifferingDimensions_ResamplesToReference()
        {
            // Reference is 32x32; current is 16x16 of the same color. The compare
            // must resample current up to 32x32 and report 0% diff.
            var reference = MakeSolidPng(32, 32, new Color32(80, 120, 160, 255));
            var current = MakeSolidPng(16, 16, new Color32(80, 120, 160, 255));
            var result = ImageCompareService.Compare(reference, current, sensitivity: 0f, produceDiff: false);

            Assert.AreEqual(0.0, result.PixelDiffPercent, 0.01, "resampled identical color must be 0% diff");
            Assert.IsTrue(result.Match, "resampled identical color must match");
        }

        [Test]
        public static void Compare_DiffImageContainsRedHighlights()
        {
            var a = MakeSolidPng(8, 8, new Color32(0, 0, 0, 255));
            var b = MakeSolidPng(8, 8, new Color32(255, 255, 255, 255));
            var result = ImageCompareService.Compare(a, b, sensitivity: 0f, produceDiff: true);

            Assert.IsNotNull(result.DiffImageBytes);
            // Decode the diff image and confirm it is mostly red (the highlight).
            var diffTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(diffTex.LoadImage(result.DiffImageBytes), "diff image must be a decodable PNG");
            var pixels = diffTex.GetPixels32();
            int redCount = 0;
            foreach (var p in pixels)
                if (p.r > 200 && p.g < 60 && p.b < 60) redCount++;
            Object.DestroyImmediate(diffTex);

            Assert.Greater(redCount, pixels.Length / 2,
                "the diff image should be predominantly red-highlighted on full mismatch");
        }

        [Test]
        public static void Compare_DriftWithinEpsilon_CountsAsMatch()
        {
            // Channel delta of 4 is below the epsilon of 8 → treated as equal.
            var a = MakeSolidPng(16, 16, new Color32(100, 100, 100, 255));
            var b = MakeSolidPng(16, 16, new Color32(104, 104, 104, 255));
            var result = ImageCompareService.Compare(a, b, sensitivity: 0f, produceDiff: false);

            Assert.AreEqual(0.0, result.PixelDiffPercent, 0.0001, "sub-epsilon drift must count as equal");
            Assert.IsTrue(result.Match, "sub-epsilon drift must match even at sensitivity 0");
        }

        // ============================ save / list / delete ============================

        [Test]
        public void SaveReference_ThenList_ThenDelete_RoundTrip()
        {
            string name = TestPrefix + "roundtrip";
            Assume.That(ImageCompareService.ReferenceExists(name), Is.False,
                "test reference should not pre-exist");

            var png = MakeSolidPng(20, 12, new Color32(30, 60, 90, 255));
            var info = ImageCompareService.SaveReference(name, png);

            Assert.AreEqual(name, info.Name);
            Assert.AreEqual(20, info.Width);
            Assert.AreEqual(12, info.Height);
            Assert.IsTrue(ImageCompareService.ReferenceExists(name), "reference should exist after save");

            // Meta sidecar round-trips.
            var meta = ImageCompareService.LoadReferenceMeta(name);
            Assert.IsNotNull(meta, "sidecar meta should load");
            Assert.AreEqual(20, meta.Width);

            // List includes it.
            var list = ImageCompareService.ListReferences();
            CollectionAssert.Contains(Array.ConvertAll(list, x => x.Name), name);

            // Delete removes both png + meta.
            Assert.IsTrue(ImageCompareService.DeleteReference(name), "delete should report it existed");
            Assert.IsFalse(ImageCompareService.ReferenceExists(name), "reference should be gone after delete");
        }

        [Test]
        public void SaveReference_OverwriteReplacesExisting()
        {
            string name = TestPrefix + "overwrite";
            ImageCompareService.SaveReference(name, MakeSolidPng(8, 8, new Color32(0, 0, 0, 255)));
            ImageCompareService.SaveReference(name, MakeSolidPng(16, 16, new Color32(255, 255, 255, 255)));

            var meta = ImageCompareService.LoadReferenceMeta(name);
            Assert.IsNotNull(meta);
            Assert.AreEqual(16, meta.Width, "second save should overwrite dims");
            Assert.AreEqual(16, meta.Height);
        }

        [Test]
        public void DeleteReference_Nonexistent_ReturnsFalse()
        {
            Assert.IsFalse(ImageCompareService.DeleteReference(TestPrefix + "never_saved"));
        }

        [Test]
        public void LoadReferencePng_Nonexistent_ReturnsNull()
        {
            Assert.IsNull(ImageCompareService.LoadReferencePng(TestPrefix + "never_saved"));
        }

        // ============================ name validation ============================

        [Test]
        public void SaveReference_RejectsTraversalAndSeparators()
        {
            Assert.Throws<ArgumentException>(() =>
                ImageCompareService.SaveReference("../escape", MakeSolidPng(4, 4, new Color32(255, 255, 255, 255))));
            Assert.Throws<ArgumentException>(() =>
                ImageCompareService.SaveReference("a/b", MakeSolidPng(4, 4, new Color32(255, 255, 255, 255))));
            Assert.Throws<ArgumentException>(() =>
                ImageCompareService.SaveReference("  ", MakeSolidPng(4, 4, new Color32(255, 255, 255, 255))));
        }

        // ============================ tool wrapper dispatch ============================

        [Test]
        public void Tool_UnknownAction_ReturnsErrorJson()
        {
            var tool = new Tool_VisualCompare();
            string json = tool.VisualCompare(action: "bogus", name: "x");

            Assert.IsTrue(json.Contains("\"error\""), "unknown action must return an error envelope: " + json);
            Assert.IsTrue(json.Contains("unknown_action"), json);
        }

        [Test]
        public void Tool_Delete_MissingName_ReturnsErrorJson()
        {
            var tool = new Tool_VisualCompare();
            string json = tool.VisualCompare(action: "delete", name: null);

            Assert.IsTrue(json.Contains("\"error\""), "delete without name must error: " + json);
            Assert.IsTrue(json.Contains("missing_parameter"), json);
        }

        [Test]
        public void Tool_Delete_TestReference_ReturnsDeletedFlag()
        {
            string name = TestPrefix + "tool_delete";
            ImageCompareService.SaveReference(name, MakeSolidPng(8, 8, new Color32(255, 255, 255, 255)));

            var tool = new Tool_VisualCompare();
            string json = tool.VisualCompare(action: "delete", name: name);

            Assert.IsTrue(json.Contains("\"action\":\"delete\""), json);
            Assert.IsTrue(json.Contains("\"deleted\":true"), json);
            Assert.IsFalse(ImageCompareService.ReferenceExists(name), "tool delete should remove the reference");
        }

        // ============================ PNG synthesis helpers ============================

        private static byte[] MakeSolidPng(int w, int h, Color32 color)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            tex.SetPixels32(pixels);
            tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }

        private static byte[] MakePngWithOneDifferentPixel(int w, int h, Color32 baseColor, Color32 oddPixel)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = baseColor;
            pixels[0] = oddPixel; // top-left
            tex.SetPixels32(pixels);
            tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            Object.DestroyImmediate(tex);
            return png;
        }
    }
}
