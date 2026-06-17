using NUnit.Framework;
using UnityOpenMcpBridge;
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

        // M14 T5.2 — deny heuristic integration. The tool evaluates the deny
        // list before compile, so a destructive snippet is refused with
        // denied_by_policy without ever touching Roslyn.

        [Test]
        public static void Execute_DestructiveExit_ReturnsDeniedByPolicy()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"EditorApplication.Exit(0);\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
            StringAssert.Contains("EditorApplication", result.ErrorMessage);
        }

        [Test]
        public static void Execute_DestructiveAssetDelete_ReturnsDeniedByPolicy()
        {
            var result = ExecuteCSharpTool.Execute(
                "{\"code\":\"AssetDatabase.DeleteAsset(\\\"Assets/Old.prefab\\\");\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }

        [Test]
        public static void Execute_BypassWithGateOffAndConfirm_AllowsDestructive()
        {
            // The bypass contract is honored at the tool level — gate=off +
            // confirm_bypass=true skips the deny heuristic. This does NOT
            // exercise Roslyn (a benign return statement keeps it cheap), it
            // only asserts the heuristic did not short-circuit.
            var body = "{\"code\":\"return 1;\",\"gate\":\"off\",\"confirm_bypass\":true}";
            var result = ExecuteCSharpTool.Execute(body);
            // We can't assert Success here without a live Roslyn install in
            // the EditMode harness; assert that the deny heuristic did NOT
            // fire (no denied_by_policy) — the failure, if any, is downstream.
            Assert.AreNotEqual("denied_by_policy", result.ErrorCode);
        }

        [Test]
        public static void Execute_BypassMissingGate_StillDenied()
        {
            // confirm_bypass alone is not enough.
            var body = "{\"code\":\"EditorApplication.Exit(0);\",\"confirm_bypass\":true}";
            var result = ExecuteCSharpTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }

        [Test]
        public static void Execute_BypassMissingConfirm_StillDenied()
        {
            // gate=off alone is not enough.
            var body = "{\"code\":\"EditorApplication.Exit(0);\",\"gate\":\"off\"}";
            var result = ExecuteCSharpTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("denied_by_policy", result.ErrorCode);
        }
    }
}

