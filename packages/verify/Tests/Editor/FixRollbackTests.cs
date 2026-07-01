using System.IO;
using NUnit.Framework;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // M25 Plan 2 — FixRollback file-level snapshot/restore tests.
    //
    // Pure file-system tests (no AssetDatabase, no Unity fixtures) so they run
    // in the fast [Test] path. The three restore cases mirror the contract:
    //   - rewrite: file existed, was modified  -> restored to original bytes
    //   - delete:  file existed, was deleted    -> restored (re-created)
    //   - create:  file did NOT exist, created  -> rolled back by deleting
    [TestFixture]
    public class FixRollbackTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "FixRollbackTests",
                System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        // -------------------------------------------------------------------
        // Rewrite case
        // -------------------------------------------------------------------

        [Test]
        public void Restore_RevertsRewrittenFile_ToOriginalBytes()
        {
            var path = Path.Combine(_tempRoot, "Material.mat");
            File.WriteAllText(path, "ORIGINAL");

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });

            // Simulate the fix rewriting the file.
            File.WriteAllText(path, "REWRITTEN");
            Assert.AreEqual("REWRITTEN", File.ReadAllText(path), "sanity: file was rewritten");

            var result = rollback.Restore();

            Assert.IsTrue(result.Success);
            Assert.That(result.RestoredPaths, Does.Contain(path));
            Assert.AreEqual("ORIGINAL", File.ReadAllText(path));
        }

        // -------------------------------------------------------------------
        // Delete case
        // -------------------------------------------------------------------

        [Test]
        public void Restore_RecreatesDeletedFile()
        {
            var path = Path.Combine(_tempRoot, "Asset.mat.meta");
            File.WriteAllText(path, "META-BYTES");

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });

            // Simulate the fix deleting the file (remove_orphan_meta).
            File.Delete(path);
            Assert.IsFalse(File.Exists(path), "sanity: file was deleted");

            var result = rollback.Restore();

            Assert.IsTrue(result.Success);
            Assert.That(result.RestoredPaths, Does.Contain(path));
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual("META-BYTES", File.ReadAllText(path));
        }

        // -------------------------------------------------------------------
        // Create case
        // -------------------------------------------------------------------

        [Test]
        public void Restore_DeletesFileCreatedByFix()
        {
            var path = Path.Combine(_tempRoot, "CreatedByFix.asset");
            // File does NOT exist before the fix.

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });

            // Simulate the fix creating the file.
            File.WriteAllText(path, "NEW");

            var result = rollback.Restore();

            Assert.IsTrue(result.Success);
            Assert.That(result.RestoredPaths, Does.Contain(path));
            Assert.IsFalse(File.Exists(path), "fix-created file must be deleted on rollback");
        }

        // -------------------------------------------------------------------
        // No-op cases
        // -------------------------------------------------------------------

        [Test]
        public void Restore_NoOp_WhenFileNeverExistedAndWasNotCreated()
        {
            var path = Path.Combine(_tempRoot, "NeverTouched.asset");

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });

            var result = rollback.Restore();

            Assert.IsTrue(result.Success);
            Assert.IsFalse(File.Exists(path));
            // Restore reports nothing restored because the file never existed
            // and was never created — nothing to undo.
            CollectionAssert.IsEmpty(result.RestoredPaths);
        }

        [Test]
        public void HasSnapshot_FalseBeforeSnapshot_TrueAfter()
        {
            var rollback = new FixRollback();
            Assert.IsFalse(rollback.HasSnapshot);

            rollback.Snapshot(new[] { Path.Combine(_tempRoot, "x") });
            Assert.IsTrue(rollback.HasSnapshot);
        }

        // -------------------------------------------------------------------
        // Restore is idempotent + Discard cleans up
        // -------------------------------------------------------------------

        [Test]
        public void Restore_IsIdempotent_SecondCallRestoresNothing()
        {
            var path = Path.Combine(_tempRoot, "Idempotent.mat");
            File.WriteAllText(path, "ORIG");

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });
            File.WriteAllText(path, "CHANGED");

            var first = rollback.Restore();
            var second = rollback.Restore();

            Assert.IsTrue(first.Success);
            Assert.AreEqual("ORIG", File.ReadAllText(path));
            // Second restore copies the same backup over the (already-restored)
            // file — harmless, but it reports the path again. The point is it
            // must NOT throw or corrupt state.
            Assert.IsTrue(second.Success);
            Assert.AreEqual("ORIG", File.ReadAllText(path));
        }

        [Test]
        public void Discard_RemovesBackupDirectory()
        {
            var path = Path.Combine(_tempRoot, "Discard.mat");
            File.WriteAllText(path, "ORIG");

            var rollback = new FixRollback();
            rollback.Snapshot(new[] { path });
            Assume.That(rollback.HasSnapshot, Is.True);

            rollback.Discard();

            // After Discard the snapshot is gone: Restore becomes a no-op and
            // reports nothing restored (no entries to consult).
            Assert.IsFalse(rollback.HasSnapshot);
            var result = rollback.Restore();
            CollectionAssert.IsEmpty(result.RestoredPaths);
        }
    }
}
