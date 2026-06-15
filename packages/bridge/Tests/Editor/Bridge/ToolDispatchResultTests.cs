using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public static class ToolDispatchResultTests
    {
        [Test]
        public static void Ok_NoOutput_SuccessWithNullOutput()
        {
            var result = ToolDispatchResult.Ok();
            Assert.IsTrue(result.Success);
            Assert.IsNull(result.Output);
            Assert.IsNull(result.ErrorCode);
            Assert.IsNull(result.ErrorMessage);
        }

        [Test]
        public static void Ok_WithOutput_SuccessWithOutput()
        {
            var result = ToolDispatchResult.Ok("\"hello\"");
            Assert.IsTrue(result.Success);
            Assert.AreEqual("\"hello\"", result.Output);
        }

        [Test]
        public static void Fail_SetsErrorFields()
        {
            var result = ToolDispatchResult.Fail("test_error", "Something went wrong");
            Assert.IsFalse(result.Success);
            Assert.IsNull(result.Output);
            Assert.AreEqual("test_error", result.ErrorCode);
            Assert.AreEqual("Something went wrong", result.ErrorMessage);
        }
    }
}
