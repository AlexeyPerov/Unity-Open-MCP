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
    }
}
