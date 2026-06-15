using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class InvokeMethodToolTests
    {
        [Test]
        public static void Execute_MissingTypeName_ReturnsValidationError()
        {
            var result = InvokeMethodTool.Execute("{\"method_name\":\"Test\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
        }

        [Test]
        public static void Execute_MissingMethodName_ReturnsValidationError()
        {
            var result = InvokeMethodTool.Execute("{\"type_name\":\"System.Environment\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
        }

        [Test]
        public static void Execute_UnknownType_ReturnsTypeNotFoundError()
        {
            var result = InvokeMethodTool.Execute(
                "{\"type_name\":\"NonExistent.Namespace.Foo\",\"method_name\":\"Bar\",\"is_static\":true}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("type_not_found", result.ErrorCode);
        }

        [Test]
        public static void Execute_UnknownMethod_ReturnsMethodNotFoundError()
        {
            var result = InvokeMethodTool.Execute(
                "{\"type_name\":\"System.Environment\",\"method_name\":\"NonExistentMethod\",\"is_static\":true}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("method_not_found", result.ErrorCode);
        }
    }
}
