using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.ObjectRefs;
using UnityEngine;

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

        // feedback-01-08-glm §2 — Vector3 positional args. Vector3.Distance is
        // static and takes two Vector3s; before the ConvertArg fix, a JSON
        // array arg ("[0,0,0]") arrived as a raw string and method.Invoke
        // rejected it ("Object of type 'System.String' cannot be converted to
        // type 'UnityEngine.Vector3'").
        [Test]
        public static void Execute_StaticMethod_Vector3Args_ConvertedFromJsonArray()
        {
            var result = InvokeMethodTool.Execute(
                "{\"type_name\":\"UnityEngine.Vector3\",\"method_name\":\"Distance\",\"is_static\":true," +
                "\"args\":[\"[1,2,0]\",\"[4,6,0]\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage ?? "");
            // Distance from (1,2,0) to (4,6,0) = 5.
            StringAssert.Contains("5", result.Output ?? "");
        }

        // feedback-01-08-glm §2 — GameObject positional arg via instance id.
        // A static helper that takes a GameObject and returns its name lets us
        // verify a bare instance id resolves to the live Object instead of
        // leaking through as a long that method.Invoke rejects.
        [Test]
        public static void Execute_StaticMethod_GameObjectArg_ByInstanceId_Resolved()
        {
            var go = new GameObject("InvokeMethodArgTarget");
            try
            {
                // InstanceId.Of is the version-gated read path (GetInstanceID
                // is a CS0619 error on Unity 6000.5+; see docs/code-conventions
                // §Instance IDs).
                var id = InstanceId.Of(go);
                var result = InvokeMethodTool.Execute(
                    "{\"type_name\":\"UnityOpenMcpBridge.Tests.InvokeMethodArgProbe\"," +
                    "\"assembly_name\":\"com.alexeyperov.unity-open-mcp-bridge.Editor.Tests\"," +
                    "\"method_name\":\"NameOf\",\"is_static\":true,\"args\":[" + id + "]}");
                Assert.IsTrue(result.Success, result.ErrorMessage ?? "");
                StringAssert.Contains("InvokeMethodArgTarget", result.Output ?? "");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // feedback-01-08-glm §2 — Scene positional arg via selector. Expects a
        // clear execution_error (arg conversion runs before method.Invoke, so a
        // resolution miss surfaces as execution_error, not invocation_error)
        // when the named scene is not loaded, proving the Scene branch is
        // reachable instead of leaking an ArgumentException out of dispatch.
        [Test]
        public static void Execute_StaticMethod_SceneArg_NotLoaded_ReturnsExecutionError()
        {
            var result = InvokeMethodTool.Execute(
                "{\"type_name\":\"UnityEngine.SceneManagement.SceneManager\",\"method_name\":\"SetActiveScene\"," +
                "\"is_static\":true,\"args\":[{\"scene_name\":\"__definitely_not_loaded__\"}]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("execution_error", result.ErrorCode);
            StringAssert.Contains("not loaded", result.ErrorMessage ?? "");
        }
    }

    /// <summary>Static helpers used by the GameObject-arg test above so a
    /// UnityEngine.Object can be passed as a positional arg (the field-report
    /// gap). Kept in the test assembly so it does not pollute the shipped
    /// surface.</summary>
    public static class InvokeMethodArgProbe
    {
        public static string NameOf(GameObject go) => go == null ? "<null>" : go.name;
    }
}
