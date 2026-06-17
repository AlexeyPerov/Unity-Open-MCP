using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class ExecuteMenuToolTests
    {
        [Test]
        public static void IsReadOnlyMenu_AllowlistedMenu_ReturnsTrue()
        {
            Assert.IsTrue(ExecuteMenuTool.IsReadOnlyMenu("Assets/Refresh"));
        }

        [Test]
        public static void IsReadOnlyMenu_AllowlistedMenu_CaseInsensitive()
        {
            Assert.IsTrue(ExecuteMenuTool.IsReadOnlyMenu("assets/refresh"));
        }

        [Test]
        public static void IsReadOnlyMenu_WindowHierarchy_ReturnsTrue()
        {
            Assert.IsTrue(ExecuteMenuTool.IsReadOnlyMenu("Window/General/Hierarchy"));
        }

        [Test]
        public static void IsReadOnlyMenu_PrefixMatch_ReturnsTrue()
        {
            Assert.IsTrue(ExecuteMenuTool.IsReadOnlyMenu("Edit/Selection/Select All"));
        }

        [Test]
        public static void IsReadOnlyMenu_NonAllowlisted_ReturnsFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsReadOnlyMenu("File/Save Project"));
        }

        [Test]
        public static void IsReadOnlyMenu_BlockedMenu_ReturnsFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsReadOnlyMenu("File/Quit"));
        }

        [Test]
        public static void IsReadOnlyMenu_Null_ReturnsFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsReadOnlyMenu(null));
        }

        [Test]
        public static void IsReadOnlyMenu_Empty_ReturnsFalse()
        {
            Assert.IsFalse(ExecuteMenuTool.IsReadOnlyMenu(""));
        }

        [Test]
        public static void Execute_MissingMenuPath_ReturnsValidationError()
        {
            var result = ExecuteMenuTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
        }

        [Test]
        public static void Execute_BlockedMenu_ReturnsBlockedError()
        {
            var result = ExecuteMenuTool.Execute("{\"menu_path\":\"File/Quit\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("menu_blocked", result.ErrorCode);
        }

        // M14 T5.3 — deny heuristic integration. Menu paths matching the
        // configurable deny list are refused with menu_blocked before
        // ExecuteMenuItem is ever called.

        [Test]
        public static void Execute_DenyList_BlocksFileExit()
        {
            var result = ExecuteMenuTool.Execute("{\"menu_path\":\"File/Exit\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("menu_blocked", result.ErrorCode);
        }

        [Test]
        public static void Execute_DenyList_BlocksReimportAll()
        {
            var result = ExecuteMenuTool.Execute("{\"menu_path\":\"Assets/Reimport All\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("menu_blocked", result.ErrorCode);
        }

        [Test]
        public static void Execute_Bypass_RequiresBothFlags()
        {
            // confirm_bypass alone is not enough — File/Quit still blocked.
            var r1 = ExecuteMenuTool.Execute(
                "{\"menu_path\":\"File/Quit\",\"confirm_bypass\":true}");
            Assert.IsFalse(r1.Success);
            Assert.AreEqual("menu_blocked", r1.ErrorCode);

            // gate=off alone is not enough either.
            var r2 = ExecuteMenuTool.Execute(
                "{\"menu_path\":\"File/Quit\",\"gate\":\"off\"}");
            Assert.IsFalse(r2.Success);
            Assert.AreEqual("menu_blocked", r2.ErrorCode);
        }
    }
}
