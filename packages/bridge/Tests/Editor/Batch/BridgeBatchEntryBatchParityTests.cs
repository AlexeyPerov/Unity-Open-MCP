using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.Batch;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    /// <summary>
    /// M26 Plan 3 — batch parity coverage for the execute_csharp /
    /// invoke_method / execute_menu meta-tool operations added to
    /// <see cref="BridgeBatchEntry"/>. The dispatch path is exercised directly
    /// via the internal <see cref="BridgeBatchEntry.Execute"/> entry point,
    /// which is the same method the -batchmode -executeMethod entry invokes.
    /// </summary>
    public static class BridgeBatchEntryBatchParityTests
    {
        // -------------------------------------------------------------------
        // SupportedOperations / dispatch routing
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_DispatchesExecuteCSharp()
        {
            // A trivial snippet that compiles and returns null. Roslyn is
            // available in the test Editor, so this exercises the full
            // parse → body-build → ExecuteCSharpTool.Execute → envelope path.
            var args = new[]
            {
                "-executeMethod", "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run",
                "--", "execute_csharp", "--code", "return null;",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitPass, exitCode, json);
            AssertSuccessEnvelope(json);
        }

        [Test]
        public static void Execute_ExecuteCSharpCompilationErrorFails()
        {
            // Intentionally broken snippet — Roslyn reports a compile error
            // which surfaces as a failure envelope with compilation_error.
            var args = new[]
            {
                "--", "execute_csharp", "--code", "return UndefinedThing();",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("compilation_error", json);
        }

        [Test]
        public static void Execute_ExecuteCSharpMissingCodeFails()
        {
            var args = new[] { "--", "execute_csharp" };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("batch_error", json);
            StringAssert.Contains("--code is required", json);
        }

        [Test]
        public static void Execute_DispatchesInvokeMethod()
        {
            // Mathf.Max is a static method that runs cleanly headless and does
            // not depend on any live Editor state — a safe reflection target.
            var args = new[]
            {
                "--", "invoke_method",
                "--type-name", "UnityEngine.Mathf",
                "--method-name", "Max",
                "--is-static", "true",
                "--arg", "3",
                "--arg", "7",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitPass, exitCode, json);
            AssertSuccessEnvelope(json);
            // Mathf.Max(3,7) == 7
            StringAssert.Contains("7", json);
        }

        [Test]
        public static void Execute_InvokeMethodTypeNotFoundFails()
        {
            var args = new[]
            {
                "--", "invoke_method",
                "--type-name", "Namespace.That.Does.Not.Exist",
                "--method-name", "Foo",
                "--is-static", "true",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("type_not_found", json);
        }

        [Test]
        public static void Execute_InvokeMethodMissingTypeOrMethodFails()
        {
            var noType = BridgeBatchEntry.Execute(new[] { "--", "invoke_method", "--method-name", "M" });
            Assert.AreEqual(BridgeBatchEntry.ExitFail, noType.exitCode);
            StringAssert.Contains("--type-name is required", noType.json);

            var noMethod = BridgeBatchEntry.Execute(new[]
            {
                "--", "invoke_method", "--type-name", "UnityEngine.Mathf",
            });
            Assert.AreEqual(BridgeBatchEntry.ExitFail, noMethod.exitCode);
            StringAssert.Contains("--method-name is required", noMethod.json);
        }

        [Test]
        public static void Execute_DispatchesExecuteMenuAllowListed()
        {
            // Assets/Refresh is on the batch-viable allow-list; it runs the
            // AssetDatabase refresh, which is safe under -batchmode and returns
            // the standard "ok" success envelope.
            var args = new[]
            {
                "--", "execute_menu", "--menu-path", "Assets/Refresh",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitPass, exitCode, json);
            AssertSuccessEnvelope(json);
        }

        [Test]
        public static void Execute_ExecuteMenuNonViableReturnsMenuNotViableInBatchmode()
        {
            // A window-opening menu that is NOT on the batch-viable allow-list.
            // The batch entry point must reject it before attempting the menu,
            // surfacing menu_not_viable_in_batchmode so an agent knows to
            // connect a live Editor.
            var args = new[]
            {
                "--", "execute_menu", "--menu-path", "Window/General/Console",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("menu_not_viable_in_batchmode", json);
            StringAssert.Contains("batch-viable allow-list", json);
        }

        [Test]
        public static void Execute_ExecuteMenuMissingMenuPathFails()
        {
            var args = new[] { "--", "execute_menu" };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--menu-path is required", json);
        }

        // -------------------------------------------------------------------
        // Unknown operation
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_UnknownOperationListsAllSupported()
        {
            var args = new[] { "--", "not_a_real_operation" };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Unknown meta-tool operation", json);
            // The error message must advertise the newly-supported operations.
            StringAssert.Contains("execute_csharp", json);
            StringAssert.Contains("invoke_method", json);
            StringAssert.Contains("execute_menu", json);
        }

        // -------------------------------------------------------------------
        // execute_csharp space-encoding round-trip (0x1f ↔ space)
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ExecuteCSharpDecodesUnitSeparatorInCode()
        {
            // The MCP server encodes spaces in --code as ASCII unit separator
            // (0x1f) so the snippet survives argv splitting. The entry point
            // must decode them back to spaces before passing to Roslyn. A
            // snippet with a space ("return 1;") compiles only when the space
            // is correctly restored.
            var args = new[]
            {
                "--", "execute_csharp",
                "--code", "return\x1f1;",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitPass, exitCode, json);
        }

        // -------------------------------------------------------------------
        // confirm_bypass forwarding (gate:"off" + confirm_bypass:true)
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ExecuteCsharpConfirmBypassForwardsGateOff()
        {
            // When --confirm-bypass true is set, the body must carry
            // gate:"off" + confirm_bypass:true so BridgeDenyBypass honors it.
            // A benign snippet with no denied pattern compiles either way; the
            // point here is that the flags parse and reach ExecuteCSharpTool
            // without a flag-validation error.
            var args = new[]
            {
                "--", "execute_csharp",
                "--code", "return null;",
                "--confirm-bypass", "true",
            };
            var (exitCode, json) = BridgeBatchEntry.Execute(args);

            Assert.AreEqual(BridgeBatchEntry.ExitPass, exitCode, json);
        }

        // -------------------------------------------------------------------
        // ExecuteMenuTool.IsBatchViable allow-list
        // -------------------------------------------------------------------

        [Test]
        public static void IsBatchViable_AllowListedMenusReturnTrue()
        {
            Assert.IsTrue(ExecuteMenuTool.IsBatchViable("Assets/Refresh"));
            Assert.IsTrue(ExecuteMenuTool.IsBatchViable("Assets/Reimport All"));
            Assert.IsTrue(ExecuteMenuTool.IsBatchViable("File/Save Project"));
            Assert.IsTrue(ExecuteMenuTool.IsBatchViable("File/Save Scenes"));
        }

        [Test]
        public static void IsBatchViable_WindowMenusReturnFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsBatchViable("Window/General/Console"));
            Assert.IsFalse(ExecuteMenuTool.IsBatchViable("Edit/Project Settings"));
            Assert.IsFalse(ExecuteMenuTool.IsBatchViable("File/Quit"));
        }

        [Test]
        public static void IsBatchViable_EmptyOrNullOriginalReturnsFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsBatchViable(""));
            Assert.IsFalse(ExecuteMenuTool.IsBatchViable(null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static void AssertSuccessEnvelope(string json)
        {
            StringAssert.Contains("\"mutation\"", json);
            StringAssert.Contains("\"success\":true", json);
            // The headless gate is intentionally skipped (gate.mode:"off").
            StringAssert.Contains("\"gate\"", json);
            StringAssert.Contains("\"skipped\":true", json);
        }
    }
}
