using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityOpenMcpVerify.Fixes
{
    // File-level snapshot/restore for safe auto-fix rollback.
    //
    // The gate's checkpoint fingerprint is a hash used for COMPARISON (did the
    // fix make things worse?), not a restore mechanism. To actually undo a fix
    // that failed or introduced new errors, FixRollback keeps byte-level
    // backups of every predicted touched path BEFORE the fix runs, and can
    // restore them on demand. Three cases are covered:
    //   - rewrite: file existed before, fix rewrote it  -> restore backup copy
    //   - delete:  file existed before, fix deleted it  -> restore backup copy
    //   - create:  file did NOT exist before, fix created it -> delete it
    //
    // Restores happen via plain File.Copy/File.Delete against absolute paths;
    // callers then AssetDatabase.Refresh() so Unity re-imports. Keeps the
    // verify package bridge-free (no AssetDatabase dependency here) so it stays
    // standalone-testable.
    public class FixRollback
    {
        private struct BackupEntry
        {
            public string OriginalPath;   // absolute path the fix may touch
            public string BackupPath;     // temp copy of the pre-fix bytes
            public bool ExistedBefore;    // false => a fix-created file is rolled back by deleting it
        }

        private readonly List<BackupEntry> _entries = new List<BackupEntry>();
        private readonly string _backupRoot;

        public FixRollback()
        {
            // One temp dir per snapshot so concurrent fixes (different agents)
            // don't collide. Persist under the OS temp tree, not Assets/, so
            // Unity never tries to import the backups.
            _backupRoot = Path.Combine(Path.GetTempPath(), "unity-open-mcp-fix-rollback",
                System.Guid.NewGuid().ToString("N"));
        }

        /// <summary>Snapshot every path the fix may touch. Paths that do not
        /// exist are recorded as ExistedBefore=false so a fix that creates them
        /// can be rolled back by deleting. Returns the count of paths that had
        /// a file backup taken.</summary>
        public int Snapshot(IEnumerable<string> absolutePaths)
        {
            int backedUp = 0;
            Directory.CreateDirectory(_backupRoot);
            var index = 0;
            foreach (var path in absolutePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var existed = File.Exists(path);
                var entry = new BackupEntry
                {
                    OriginalPath = path,
                    BackupPath = Path.Combine(_backupRoot, (++index).ToString()),
                    ExistedBefore = existed,
                };

                if (existed)
                {
                    try
                    {
                        File.Copy(path, entry.BackupPath, overwrite: true);
                        backedUp++;
                    }
                    catch (System.Exception e)
                    {
                        // A backup failure must NOT silently let a failed fix
                        // go unrestored. Log and skip — Restore() will report
                        // the unrestored path.
                        Debug.LogWarning($"[FixRollback] Could not back up '{path}': {e.Message}");
                        entry.ExistedBefore = false; // treat as unrestorable
                    }
                }

                _entries.Add(entry);
            }
            return backedUp;
        }

        /// <summary>Restore every snapshotted path to its pre-fix state.
        /// Returns the list of paths actually restored (rewritten or
        /// deleted).</summary>
        public RestoreResult Restore()
        {
            var restored = new List<string>();
            var unrestored = new List<string>();
            var ok = true;

            foreach (var entry in _entries)
            {
                try
                {
                    if (entry.ExistedBefore)
                    {
                        // Rewrite / undelete case: copy the backup back over
                        // the original (creating it if the fix deleted it).
                        if (File.Exists(entry.BackupPath))
                        {
                            var dir = Path.GetDirectoryName(entry.OriginalPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                            restored.Add(entry.OriginalPath);
                        }
                        else
                        {
                            unrestored.Add(entry.OriginalPath);
                            ok = false;
                        }
                    }
                    else
                    {
                        // Create case: the fix created a file that did not
                        // exist before — rolling back means deleting it. If the
                        // fix never created it, the delete is a no-op.
                        if (File.Exists(entry.OriginalPath))
                        {
                            File.Delete(entry.OriginalPath);
                            restored.Add(entry.OriginalPath);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[FixRollback] Restore failed for '{entry.OriginalPath}': {e.Message}");
                    unrestored.Add(entry.OriginalPath);
                    ok = false;
                }
            }

            return new RestoreResult { Success = ok, RestoredPaths = restored.ToArray(), UnrestoredPaths = unrestored.ToArray() };
        }

        /// <summary>Delete the temp backup dir. Call after a successful fix
        /// (no rollback needed) or after Restore(). Best-effort: a failure to
        /// clean up the temp tree is logged but not fatal.</summary>
        public void Discard()
        {
            try
            {
                if (Directory.Exists(_backupRoot))
                    Directory.Delete(_backupRoot, recursive: true);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FixRollback] Could not delete backup dir '{_backupRoot}': {e.Message}");
            }
            _entries.Clear();
        }

        /// <summary>True when Snapshot() has been called with at least one
        /// recorded path.</summary>
        public bool HasSnapshot => _entries.Count > 0;
    }

    public struct RestoreResult
    {
        public bool Success;
        public string[] RestoredPaths;
        public string[] UnrestoredPaths;
    }
}
