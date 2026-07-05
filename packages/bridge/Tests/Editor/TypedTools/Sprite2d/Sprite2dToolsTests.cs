// M20 Plan 9 / T20.9.1 — 2D art pipeline (SpriteAtlas + Texture import)
// EditMode round-trip + registry/classification tests. Both domains ship
// ungated (built-in 2D module) and share the `sprite2d` tool group.
#pragma warning disable CS0618
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.SpriteAtlasExt;
using UnityOpenMcpBridge.Extensions.TextureExt;

namespace UnityOpenMcpBridge.Tests.Extensions.Sprite2dExt
{
    public class Sprite2dToolsTests
    {
        private const string TestFolder = "Assets/__MCPTest_Sprite2D__";
        private const string AtlasPath = TestFolder + "/TestAtlas.spriteatlas";
        private const string SpritePath = TestFolder + "/TestSprite.png";
        private const string TexturePath = TestFolder + "/TestTexture.png";

        private static readonly string[] ExpectedSpriteAtlasTools =
        {
            "unity_open_mcp_spriteatlas_create",
            "unity_open_mcp_spriteatlas_get",
            "unity_open_mcp_spriteatlas_add_packable",
            "unity_open_mcp_spriteatlas_remove_packable",
            "unity_open_mcp_spriteatlas_modify",
            "unity_open_mcp_spriteatlas_delete",
            "unity_open_mcp_spriteatlas_list",
        };

        private static readonly string[] ExpectedTextureTools =
        {
            "unity_open_mcp_texture_get_importer",
            "unity_open_mcp_texture_set_import",
            "unity_open_mcp_texture_reimport",
            "unity_open_mcp_texture_get",
        };

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.DeleteAsset(TestFolder);
                AssetDatabase.Refresh();
            }
        }

        // ----------------------- Registry / classification ----------------

        [Test]
        public void Registry_AllElevenToolsDiscovered()
        {
            foreach (var id in ExpectedSpriteAtlasTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected sprite atlas tool '{id}' to be discovered.");
            }
            foreach (var id in ExpectedTextureTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected texture tool '{id}' to be discovered.");
            }
        }

        [Test]
        public void Registry_ReadToolsAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_spriteatlas_get", out var atlasGet));
            Assert.IsFalse(atlasGet.IsMutating);
            Assert.IsTrue(atlasGet.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, atlasGet.Gate);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_texture_get_importer", out var importerGet));
            Assert.IsFalse(importerGet.IsMutating);
            Assert.IsTrue(importerGet.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, importerGet.Gate);
        }

        [Test]
        public void Registry_MutatingToolsRequireEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_spriteatlas_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual(GateMode.Enforce, create.Gate);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_texture_set_import", out var setImport));
            Assert.IsTrue(setImport.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, setImport.Lifecycle);
            Assert.AreEqual(GateMode.Enforce, setImport.Gate);
        }

        [Test]
        public void Registry_AllToolsAssignedToSprite2dGroup()
        {
            foreach (var id in ExpectedSpriteAtlasTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("sprite2d", info.Group, $"Expected '{id}' in sprite2d group.");
            }
            foreach (var id in ExpectedTextureTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("sprite2d", info.Group, $"Expected '{id}' in sprite2d group.");
            }
        }

        [Test]
        public void Dispatch_SpriteAtlasCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_spriteatlas_create",
                "{\"asset_path\":\"" + AtlasPath + "\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_TextureSetImport_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_texture_set_import",
                "{\"asset_path\":\"" + TexturePath + "\",\"settings_json\":\"{}\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // ----------------------- SpriteAtlas round-trip -------------------

        [Test]
        public void SpriteAtlas_CreateAddModifyGetDelete_RoundTrips()
        {
            CreateSpriteAsset(SpritePath);

            var createOut = SpriteAtlasTools.Create(
                AtlasPath, include_in_build: true, paths_hint: new[] { AtlasPath });
            AssertJsonOk(createOut);
            StringAssert.Contains("\"path\":\"" + AtlasPath + "\"", createOut);

            var addOut = SpriteAtlasTools.AddPackable(
                AtlasPath,
                new[] { SpritePath },
                paths_hint: new[] { AtlasPath });
            AssertJsonOk(addOut);
            StringAssert.Contains("\"packableCount:after\":1", addOut);

            var getAfterAdd = SpriteAtlasTools.Get(AtlasPath);
            AssertJsonOk(getAfterAdd);
            StringAssert.Contains("\"packableCount\":1", getAfterAdd);
            StringAssert.Contains(SpritePath, getAfterAdd);

            var modifyOut = SpriteAtlasTools.Modify(
                AtlasPath,
                "{\"include_in_build\":false,\"packing\":{\"padding\":8}}",
                paths_hint: new[] { AtlasPath });
            AssertJsonOk(modifyOut);
            StringAssert.Contains("\"field\":\"include_in_build\"", modifyOut);
            StringAssert.Contains("\"field\":\"packing.padding\"", modifyOut);

            var getAfterModify = SpriteAtlasTools.Get(AtlasPath);
            AssertJsonOk(getAfterModify);
            StringAssert.Contains("\"includeInBuild\":false", getAfterModify);
            StringAssert.Contains("\"padding\":8", getAfterModify);

            var listOut = SpriteAtlasTools.List(TestFolder);
            AssertJsonOk(listOut);
            StringAssert.Contains(AtlasPath, listOut);

            var removeOut = SpriteAtlasTools.RemovePackable(
                AtlasPath,
                new[] { SpritePath },
                paths_hint: new[] { AtlasPath });
            AssertJsonOk(removeOut);
            StringAssert.Contains("\"packableCount:after\":0", removeOut);

            var deleteOut = SpriteAtlasTools.Delete(
                AtlasPath, paths_hint: new[] { AtlasPath });
            AssertJsonOk(deleteOut);
            StringAssert.Contains("\"deleted\":true", deleteOut);
        }

        // ----------------------- Texture round-trip -----------------------

        [Test]
        public void Texture_SetImportThenGetImporter_RoundTripsMaxSizeAndCompression()
        {
            CreateTextureAsset(TexturePath);

            var setOut = TextureTools.SetImport(
                TexturePath,
                "{\"max_texture_size\":512,\"compression\":\"Compressed\"}",
                paths_hint: new[] { TexturePath });
            AssertJsonOk(setOut);
            StringAssert.Contains("\"field\":\"max_texture_size\"", setOut);
            StringAssert.Contains("\"field\":\"compression\"", setOut);

            var getImporterOut = TextureTools.GetImporter(TexturePath);
            AssertJsonOk(getImporterOut);
            StringAssert.Contains("\"maxTextureSize\":512", getImporterOut);
            StringAssert.Contains("\"textureCompression\":\"Compressed\"", getImporterOut);

            var getOut = TextureTools.Get(TexturePath);
            AssertJsonOk(getOut);
            StringAssert.Contains("\"width\":", getOut);
            StringAssert.Contains("\"height\":", getOut);
        }

        [Test]
        public void Texture_Reimport_ReturnsOk()
        {
            CreateTextureAsset(TexturePath);

            var reimportOut = TextureTools.Reimport(
                TexturePath, paths_hint: new[] { TexturePath });
            AssertJsonOk(reimportOut);
            StringAssert.Contains("\"reimported\":true", reimportOut);
        }

        // ----------------------- Helpers ----------------------------------

        private static void AssertJsonOk(string json)
        {
            Assert.IsNotNull(json);
            StringAssert.Contains("\"status\":\"ok\"", json);
            Assert.IsFalse(json.Contains("\"error\":"),
                "Expected ok envelope but got error: " + json);
        }

        private static void AssertErrorEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            bool sawEnvelope = (result.Output ?? "").Contains("\"code\":\"" + expectedCode + "\"");
            bool sawFail = !result.Success && result.ErrorCode == expectedCode;
            Assert.IsTrue(sawEnvelope || sawFail,
                $"Expected '{expectedCode}' envelope. Got Success={result.Success}, " +
                $"ErrorCode={result.ErrorCode}, Output={result.Output}");
        }

        private static void CreateSpriteAsset(string assetPath)
        {
            EnsureFolder(TestFolder);
            WritePng(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            Assert.IsNotNull(importer, "TextureImporter missing after import.");
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
        }

        private static void CreateTextureAsset(string assetPath)
        {
            EnsureFolder(TestFolder);
            WritePng(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static void WritePng(string assetPath)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.red;
            tex.SetPixels(pixels);
            tex.Apply();
            var bytes = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            var fullPath = Path.GetFullPath(assetPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(fullPath, bytes);
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) return;
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            var leaf = Path.GetFileName(folderPath);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
