// EditMode tests for the M16 Plan 1 typed material tools (MaterialTools).
// Covers parameter parsing and resolver branches that do NOT drive Unity
// material mutating APIs from EditMode.
using NUnit.Framework;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class MaterialToolsTests
    {
        [Test]
        public void Create_MissingAssetPath_ReturnsMissingParameter()
        {
            var result = MaterialTools.Create("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'asset_path'", result.ErrorMessage);
        }

        [Test]
        public void Create_NotAssetsRooted_ReturnsInvalidPaths()
        {
            var result = MaterialTools.Create("{\"asset_path\":\"Materials/Foo.mat\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("'Assets/'", result.ErrorMessage);
        }

        [Test]
        public void Create_NotDotMat_ReturnsInvalidPaths()
        {
            var result = MaterialTools.Create("{\"asset_path\":\"Assets/Foo.png\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("'.mat'", result.ErrorMessage);
        }

        [Test]
        public void Create_UnknownShader_ReturnsShaderNotFound()
        {
            var result = MaterialTools.Create(
                "{\"asset_path\":\"Assets/__MCPTest.mat\",\"shader_name\":\"__Nope__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("shader_not_found", result.ErrorCode);
        }

        [Test]
        public void ResolveMaterial_NoResolver_ReturnsMissingParameter()
        {
            var r = MaterialTools.ResolveMaterial("{}");
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("missing_parameter", r.Result.ErrorCode);
        }

        [Test]
        public void ResolveMaterial_BadPath_ReturnsNotFound()
        {
            var r = MaterialTools.ResolveMaterial("{\"asset_path\":\"Assets/__Nope.mat\"}");
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("material_not_found", r.Result.ErrorCode);
        }

        [Test]
        public void ResolveCreateShader_FallsBackToStandard()
        {
            // The fallback chain resolves Standard (always available in Editor).
            var shader = MaterialTools.ResolveCreateShader(null);
            Assert.IsNotNull(shader, "Default shader chain should resolve in EditMode");
        }

        [Test]
        public void ResolveCreateShader_Named_ReturnsFoundOrNull()
        {
            // 'Standard' always resolves; an unknown name resolves to null.
            var standard = MaterialTools.ResolveCreateShader("Standard");
            Assert.IsNotNull(standard);
            var unknown = MaterialTools.ResolveCreateShader("__Nope__");
            Assert.IsNull(unknown);
        }

        [Test]
        public void ParseFloatArray_ValidArray_Parses()
        {
            var parts = MaterialTools.ParseFloatArray("[1, 2.5, -3]");
            Assert.IsNotNull(parts);
            Assert.AreEqual(3, parts.Length);
            Assert.AreEqual(1f, parts[0]);
            Assert.AreEqual(2.5f, parts[1]);
            Assert.AreEqual(-3f, parts[2]);
        }

        [Test]
        public void ParseFloatArray_NotAnArray_ReturnsNull()
        {
            Assert.IsNull(MaterialTools.ParseFloatArray("not-an-array"));
            Assert.IsNull(MaterialTools.ParseFloatArray("\"string\""));
            Assert.IsNull(MaterialTools.ParseFloatArray("[1, abc]"));
        }

        [Test]
        public void ParseFloatArray_EmptyArray_ReturnsEmpty()
        {
            var parts = MaterialTools.ParseFloatArray("[]");
            Assert.IsNotNull(parts);
            Assert.AreEqual(0, parts.Length);
        }

        [Test]
        public void SetProperty_MissingProperty_ReturnsMissingParameter()
        {
            // First the resolver fails because we didn't pass asset_path or
            // instance_id. Verify that surface explicitly.
            var result = MaterialTools.SetProperty("{\"property\":\"_Color\",\"type\":\"color\",\"value\":[1,1,1,1]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'asset_path'", result.ErrorMessage);
        }
    }
}
