// EditMode tests for the M9 Plan 1 reserialize round-trip meta-tool.
// Covers parameter parsing and pre-flight validation branches that do NOT
// invoke AssetDatabase.ForceReserializeAssets (those run in EditMode against
// the live demo project and would mutate fixtures).
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

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
    }
}
