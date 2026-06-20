using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ReadAssetToolTests
    {
        [Test]
        public void Execute_MissingAssetPath_ReturnsMissingParameter()
        {
            var result = ReadAssetTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'asset_path'", result.ErrorMessage);
        }

        [Test]
        public void Execute_NullAssetPath_ReturnsMissingParameter()
        {
            var result = ReadAssetTool.Execute("{\"asset_path\":null}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Execute_UnsupportedExtension_ReturnsInvalidPaths()
        {
            // .cs is not a text-serialized YAML asset — must be rejected pre-flight.
            var result = ReadAssetTool.Execute("{\"asset_path\":\"Assets/Scripts/Foo.cs\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("unsupported extension", result.ErrorMessage.ToLowerInvariant());
        }

        [Test]
        public void Execute_FileNotFound_ReturnsAssetNotFound()
        {
            // .prefab is supported, but the file does not exist in the project.
            var path = "Assets/__ReadAssetToolTest_NonExistent.prefab";
            var result = ReadAssetTool.Execute("{\"asset_path\":\"" + path + "\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("asset_not_found", result.ErrorCode);
            StringAssert.Contains("not found", result.ErrorMessage.ToLowerInvariant());
        }
    }
}
