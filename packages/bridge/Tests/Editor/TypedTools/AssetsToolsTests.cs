// EditMode tests for the M16 Plan 1 typed asset tools (AssetsTools).
// Mirrors the ReserializeAssetsToolTests pattern: covers the parameter
// parsing and validation branches that do NOT invoke AssetDatabase
// mutating APIs (those run in EditMode against the live demo project and
// would mutate fixtures).
using NUnit.Framework;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class AssetsToolsTests
    {
        [Test]
        public void CreateFolder_MissingFoldersArray_ReturnsMissingParameter()
        {
            var result = AssetsTools.CreateFolder("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'folders'", result.ErrorMessage);
        }

        [Test]
        public void CreateFolder_EmptyFoldersArray_ReturnsMissingParameter()
        {
            var result = AssetsTools.CreateFolder("{\"folders\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Copy_MissingEntries_ReturnsMissingParameter()
        {
            var result = AssetsTools.Copy("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'entries'", result.ErrorMessage);
        }

        [Test]
        public void Move_MissingEntries_ReturnsMissingParameter()
        {
            var result = AssetsTools.Move("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Delete_MissingPaths_ReturnsMissingParameter()
        {
            var result = AssetsTools.Delete("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'paths'", result.ErrorMessage);
        }

        [Test]
        public void Delete_EmptyPathsArray_ReturnsMissingParameter()
        {
            var result = AssetsTools.Delete("{\"paths\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Delete_NonExistentPath_ReportsError()
        {
            var result = AssetsTools.Delete("{\"paths\":[\"Assets/__DefinitelyNotHere.prefab\"]}");
            // The call succeeds overall (per-entry errors collected); the
            // deleted list is empty and the error is recorded.
            Assert.IsTrue(result.Success);
            StringAssert.Contains("not found", result.Output);
            StringAssert.Contains("__DefinitelyNotHere.prefab", result.Output);
            StringAssert.Contains("\"count\":0", result.Output);
        }

        [Test]
        public void Refresh_DefaultWholeProject_ReturnsRefreshed()
        {
            // AssetDatabase.Refresh is safe to call in EditMode; it's a no-op
            // when nothing changed.
            var result = AssetsTools.Refresh("{}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"refreshed\":true", result.Output);
            StringAssert.Contains("\"wholeProject\":true", result.Output);
        }

        [Test]
        public void Refresh_Scoped_ReturnsWholeProjectFalse()
        {
            var result = AssetsTools.Refresh("{\"whole_project\":false}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"wholeProject\":false", result.Output);
        }

        [Test]
        public void BuildFolderOpResult_EmitsDoneAndErrors()
        {
            var done = new System.Collections.Generic.List<string> { "Assets/A", "Assets/B" };
            var errors = new System.Collections.Generic.List<string> { "boom" };
            var json = AssetsTools.BuildFolderOpResult(done, errors, "created");
            StringAssert.Contains("\"created\":[\"Assets/A\",\"Assets/B\"]", json);
            StringAssert.Contains("\"count\":2", json);
            StringAssert.Contains("\"errors\":[\"boom\"]", json);
        }

        [Test]
        public void InvalidFileNameChars_IncludesSlashAndBackslash()
        {
            // Cross-platform invalid character list (mirrors Unity-MCP).
            System.Collections.Generic.CollectionAssert.Contains(AssetsTools.InvalidFileNameChars, '/');
            System.Collections.Generic.CollectionAssert.Contains(AssetsTools.InvalidFileNameChars, '\\');
            System.Collections.Generic.CollectionAssert.Contains(AssetsTools.InvalidFileNameChars, ':');
            System.Collections.Generic.CollectionAssert.Contains(AssetsTools.InvalidFileNameChars, '*');
        }
    }
}
