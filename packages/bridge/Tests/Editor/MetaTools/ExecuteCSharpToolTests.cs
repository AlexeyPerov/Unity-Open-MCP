using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class ExecuteCSharpToolTests
    {
        [Test]
        public static void Execute_MissingCode_ReturnsValidationError()
        {
            var result = ExecuteCSharpTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
            Assert.IsTrue(result.ErrorMessage.Contains("code"));
        }

        [Test]
        public static void Execute_EmptyCode_ReturnsValidationError()
        {
            var result = ExecuteCSharpTool.Execute("{\"code\":\"\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
        }
    }
}
