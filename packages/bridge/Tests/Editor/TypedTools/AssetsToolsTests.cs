using NUnit.Framework;
using UnityEditor;
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

        // ---- T1.4 — MoveAsset called exactly once per move entry ----------

        [Test]
        public void Move_ValidSource_SucceedsInOneCall()
        {
            const string TmpDir = "Assets/TmpMoveTests";
            if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
            AssetDatabase.CreateFolder("Assets", "TmpMoveTests");
            try
            {
                // Seed a material asset at the source path so MoveAsset has a
                // real asset to move (not just a folder).
                const string Src = TmpDir + "/SrcMove.mat";
                AssetDatabase.CreateAsset(new UnityEngine.Material(UnityEngine.Shader.Find("Standard")), Src);
                Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(Src),
                    "sanity: source asset must exist before the move");

                const string Dst = TmpDir + "/DstMove.mat";
                Assert.IsFalse(AssetDatabase.LoadMainAssetAtPath(Dst) != null,
                    "sanity: destination must not pre-exist");

                var result = AssetsTools.Move(
                    "{\"entries\":[{\"source\":\"" + Src + "\",\"destination\":\"" + Dst + "\"}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"moved\"", result.Output);
                // The move happened exactly once: source gone, destination present.
                Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(Src),
                    "source must be gone after a successful move");
                Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(Dst),
                    "destination must hold the moved asset");
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        [Test]
        public void Move_FailingTargetPath_ReportsSingleAccurateError()
        {
            const string TmpDir = "Assets/TmpMoveErrTests";
            if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
            AssetDatabase.CreateFolder("Assets", "TmpMoveErrTests");
            try
            {
                const string Src = TmpDir + "/SrcErr.mat";
                AssetDatabase.CreateAsset(new UnityEngine.Material(UnityEngine.Shader.Find("Standard")), Src);
                Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(Src),
                    "sanity: source asset must exist before the move");

                // Destination under a NON-EXISTENT parent folder: the dest
                // itself does not exist (so the FileOrFolderExists guard does
                // not pre-block), but MoveAsset rejects because the target
                // folder is missing. This is the path that previously invoked
                // MoveAsset TWICE — once for the == "" success check and again
                // to harvest the error string.
                const string Dst = "Assets/__Nope_NonExistentParent/Out.mat";
                Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/__Nope_NonExistentParent"),
                    "sanity: target parent folder must not exist");

                var result = AssetsTools.Move(
                    "{\"entries\":[{\"source\":\"" + Src + "\",\"destination\":\"" + Dst + "\"}]}");
                // The call succeeds overall (per-entry errors collected); the
                // error is recorded in the output JSON.
                Assert.IsTrue(result.Success);

                // After the fix MoveAsset runs exactly once and the move is
                // rejected, so the source is STILL at the original path. With
                // the old double-call, the second MoveAsset could observe a
                // moved-or-vanished source and report a misleading message.
                Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(Src),
                    "source must still exist — MoveAsset called once, the move rejected");
                StringAssert.Contains("failed", result.Output);
                StringAssert.Contains(Src, result.Output);
                // The error must come from the single MoveAsset call, not a
                // second invocation on a moved source ("source not found").
                StringAssert.DoesNotContain("source not found", result.Output);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
            }
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
            // Cross-platform invalid character list.
            Assert.Contains('/', AssetsTools.InvalidFileNameChars);
            Assert.Contains('\\', AssetsTools.InvalidFileNameChars);
            Assert.Contains(':', AssetsTools.InvalidFileNameChars);
            Assert.Contains('*', AssetsTools.InvalidFileNameChars);
        }
    }
}
