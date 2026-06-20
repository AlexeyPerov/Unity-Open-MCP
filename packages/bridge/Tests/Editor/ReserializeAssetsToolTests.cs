using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityEditor;

namespace UnityOpenMcpBridge.Tests
{
    public class ReserializeAssetsToolTests
    {
        [Test]
        public void Execute_MissingPaths_ReturnsMissingParameter()
        {
            var result = ReserializeAssetsTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'paths'", result.ErrorMessage);
        }

        [Test]
        public void Execute_NullPaths_ReturnsMissingParameter()
        {
            var result = ReserializeAssetsTool.Execute("{\"paths\":null}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Execute_EmptyPathsArray_ReturnsMissingParameter()
        {
            var result = ReserializeAssetsTool.Execute("{\"paths\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("Whole-project reserialize is not supported", result.ErrorMessage);
        }

        [Test]
        public void Execute_UnsupportedExtension_ReturnsInvalidPaths()
        {
            // .txt is not in the supported extension list — the pre-flight check
            // must reject before any AssetDatabase call.
            var result = ReserializeAssetsTool.Execute("{\"paths\":[\"Assets/SomeText.txt\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("unsupported extension", result.ErrorMessage);
            StringAssert.Contains(".txt", result.ErrorMessage);
        }

        [Test]
        public void Execute_FileNotFound_ReturnsInvalidPaths()
        {
            // .prefab is supported, but the file does not exist on disk.
            var path = "Assets/__ReserializeToolTest_NonExistent.prefab";
            var result = ReserializeAssetsTool.Execute("{\"paths\":[\"" + path + "\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("file not found", result.ErrorMessage);
            StringAssert.Contains(path, result.ErrorMessage);
        }

        [Test]
        public void SupportedExtensions_IncludesAllRequiredFormats()
        {
            // Acceptance criterion: reserialize works on .prefab/.unity/.asset/.mat/.controller/.anim.
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".prefab");
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".unity");
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".asset");
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".mat");
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".controller");
            CollectionAssert.Contains(ReserializeAssetsTool.SupportedExtensions, ".anim");
        }

        // T2.7 — by default reserialize targets asset YAML only so a direct body
        // edit does not churn the companion .meta with empty importer-field
        // whitespace (userData:/assetBundleName:). include_meta: true opts in to
        // importer-metadata round-trip for upgrade/importer-change workflows.
        // ResolveOptions is a pure function so the mapping can be unit-tested
        // without driving AssetDatabase.ForceReserializeAssets from EditMode.
        [Test]
        public void ResolveOptions_Default_ReturnsAssetsOnly()
        {
            Assert.AreEqual(
                ForceReserializeAssetsOptions.ReserializeAssets,
                ReserializeAssetsTool.ResolveOptions(false));
        }

        [Test]
        public void ResolveOptions_IncludeMeta_ReturnsAssetsAndMetadata()
        {
            Assert.AreEqual(
                ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata,
                ReserializeAssetsTool.ResolveOptions(true));
        }
    }
}
