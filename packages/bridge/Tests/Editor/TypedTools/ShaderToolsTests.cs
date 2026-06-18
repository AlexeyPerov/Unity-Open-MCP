// EditMode tests for the M16 Plan 1 typed shader tools (ShaderTools).
using NUnit.Framework;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ShaderToolsTests
    {
        [Test]
        public void ListAll_DefaultCap_ReturnsShaders()
        {
            // The Editor always has built-in shaders (Sprites/Default, etc.).
            var result = ShaderTools.ListAll("{}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"shaders\":[", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"truncated\":", result.Output);
        }

        [Test]
        public void ListAll_SmallCap_ReportsTruncation()
        {
            // The project has more than 1 shader; cap=1 must report
            // truncated > 0.
            var result = ShaderTools.ListAll("{\"max_results\":1}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"count\":1", result.Output);
            // truncated must be a positive integer when there are extra shaders.
            StringAssert.Contains("\"truncated\":", result.Output);
            // Extract the truncated value via a simple substring check.
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                result.Output, "\"truncated\":[1-9]"),
                "Expected truncated to be a positive value when max_results caps below the actual count: " + result.Output);
        }

        [Test]
        public void GetData_MissingResolver_ReturnsMissingParameter()
        {
            var result = ShaderTools.GetData("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void GetData_ByName_ResolvesHiddenInternalShader()
        {
            // 'Hidden/InternalErrorShader' is a built-in Unity shader name.
            var result = ShaderTools.GetData("{\"name\":\"Sprites/Default\"}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"name\":\"Sprites/Default\"", result.Output);
            StringAssert.Contains("\"propertyCount\":", result.Output);
            StringAssert.Contains("\"errors\":", result.Output);
        }

        [Test]
        public void GetData_UnknownName_ReturnsShaderNotFound()
        {
            var result = ShaderTools.GetData("{\"name\":\"__DefinitelyNotAShader__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("shader_not_found", result.ErrorCode);
        }
    }
}
