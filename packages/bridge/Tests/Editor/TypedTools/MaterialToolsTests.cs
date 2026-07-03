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

        // Hosts that box array args as JSON strings (the color/vector feedback
        // case) must round-trip the same as a bare array.
        [Test]
        public void ParseFloatArray_StringifiedArray_StripsQuotesAndParses()
        {
            var parts = MaterialTools.ParseFloatArray("\"[1, 0, 0, 1]\"");
            Assert.IsNotNull(parts);
            Assert.AreEqual(new[] { 1f, 0f, 0f, 1f }, parts);
        }

        [Test]
        public void ParseFloatArray_ObjectRgba_ReadsChannelsInOrder()
        {
            var parts = MaterialTools.ParseFloatArray("{\"r\":1,\"g\":0,\"b\":0,\"a\":1}");
            Assert.IsNotNull(parts);
            Assert.AreEqual(new[] { 1f, 0f, 0f, 1f }, parts);
        }

        [Test]
        public void ParseFloatArray_ObjectXyzw_FallsBackWhenNoRgba()
        {
            var parts = MaterialTools.ParseFloatArray("{\"x\":2,\"y\":3,\"z\":4,\"w\":5}");
            Assert.IsNotNull(parts);
            Assert.AreEqual(new[] { 2f, 3f, 4f, 5f }, parts);
        }

        [Test]
        public void ParseFloatArray_ObjectPartial_DropsAbsentChannels()
        {
            // Only r/g/b present — caller (color) supplies alpha=1 default.
            var parts = MaterialTools.ParseFloatArray("{\"r\":0.5,\"g\":0.25,\"b\":0.1}");
            Assert.IsNotNull(parts);
            Assert.AreEqual(new[] { 0.5f, 0.25f, 0.1f }, parts);
        }

        [Test]
        public void ParseFloatArray_ObjectExplicitZero_NotConfusedWithAbsent()
        {
            // "a":0 must be preserved (not dropped as if absent).
            var parts = MaterialTools.ParseFloatArray("{\"r\":1,\"g\":1,\"b\":1,\"a\":0}");
            Assert.IsNotNull(parts);
            Assert.AreEqual(4, parts.Length);
            Assert.AreEqual(0f, parts[3]);
        }

        [Test]
        public void ParseFloatArray_EmptyObject_ReturnsNull()
        {
            Assert.IsNull(MaterialTools.ParseFloatArray("{}"));
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
