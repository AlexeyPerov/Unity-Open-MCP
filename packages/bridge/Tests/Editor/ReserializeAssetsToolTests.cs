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

        // B20 — paths that escape Assets/ via `..` MUST be rejected, not silently
        // prefixed. The previous form did `p = "Assets/" + p` for any non-Assets-
        // rooted input, so `../ProjectSettings/ProjectSettings.asset` became
        // `Assets/../ProjectSettings/ProjectSettings.asset`, passed File.Exists,
        // reached ForceReserializeAssets, and was reused verbatim as the gate's
        // paths_hint. Now the containment check fails before any mutation.
        [Test]
        public void Execute_ParentEscape_ReturnsInvalidPaths()
        {
            var result = ReserializeAssetsTool.Execute(
                "{\"paths\":[\"../ProjectSettings/ProjectSettings.asset\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("escapes Assets/", result.ErrorMessage);
            // Must NOT reach the mutation: the original escaped string must not
            // appear as a normalized Assets/-rooted path in the message.
            StringAssert.DoesNotContain("Assets/../ProjectSettings", result.ErrorMessage);
        }

        // B20 — absolute paths can never be a valid Assets-relative asset path.
        // Reject outright rather than prefixing (which would produce
        // `Assets//etc/passwd` or similar nonsense). The sample is JSON-escaped
        // (backslashes doubled) so the embedded Windows path stays valid JSON.
        private static readonly string SampleAbsolutePath =
            System.IO.Path.DirectorySeparatorChar == '/' ? "/etc/passwd" : @"C:\\Windows\\System32\\drivers\\etc\\hosts";

        [Test]
        public void Execute_AbsolutePath_ReturnsInvalidPaths()
        {
            var result = ReserializeAssetsTool.Execute(
                "{\"paths\":[\"" + SampleAbsolutePath + "\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("absolute paths are not allowed", result.ErrorMessage);
        }

        // B20 — IsUnderAssets is the pure containment decision split out so the
        // boundary cases are unit-testable without driving AssetDatabase. The
        // fake root is built from the system temp path so the absolute-path
        // arithmetic agrees with the OS's Path.GetFullPath semantics.
        private static readonly string FakeRoot =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "__uomcp_reserialize_test_proj");
        private static readonly string FakeAssets =
            System.IO.Path.Combine(FakeRoot, "Assets");

        [Test]
        public void IsUnderAssets_AcceptsAssetUnderAssets()
        {
            Assert.IsTrue(ReserializeAssetsTool.IsUnderAssets(
                "Assets/Foo.prefab", FakeRoot, FakeAssets));
        }

        [Test]
        public void IsUnderAssets_RejectsParentEscape()
        {
            // `Assets/../ProjectSettings/X.asset` resolves outside Assets/.
            Assert.IsFalse(ReserializeAssetsTool.IsUnderAssets(
                "Assets/../ProjectSettings/ProjectSettings.asset", FakeRoot, FakeAssets));
        }

        [Test]
        public void IsUnderAssets_RejectsSiblingEscape()
        {
            // A bare `../ProjectSettings/...` (before any Assets/ prefix) escapes.
            Assert.IsFalse(ReserializeAssetsTool.IsUnderAssets(
                "../ProjectSettings/ProjectSettings.asset", FakeRoot, FakeAssets));
        }

        [Test]
        public void IsUnderAssets_RejectsAssetsPrefixImpostor()
        {
            // `AssetsFoo` must not match `Assets` (trailing-separator guard).
            Assert.IsFalse(ReserializeAssetsTool.IsUnderAssets(
                "AssetsFoo/Bar.prefab", FakeRoot, FakeAssets));
        }
    }
}
