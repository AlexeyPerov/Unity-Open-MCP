// Playtest — player-state sandbox (unity_open_mcp_player_state).
//
// Actions:
//   snapshot — flush PlayerPrefs (PlayerPrefs.Save), read the platform store
//              (all keys, typed), optionally EditorPrefs under opted-in key
//              prefixes, and copy persistentDataPath (globs excluded, size
//              cap) into a named snapshot in the app-data store outside the
//              project (PlayerStateModel.cs). Read-only for the project.
//   diff     — snapshot vs the current state (or vs another snapshot): added /
//              removed / changed keys and files; small values shown, long
//              strings and files as size + sha256.
//   restore  — edit mode only. Without confirm: a dry run returning the diff
//              it would apply (current → snapshot; every removal and
//              overwrite listed) and its `plan` id. With confirm + that plan
//              (refused when the state moved since the dry run): after
//              proving the store reader sees a fresh key, PlayerPrefs.DeleteAll
//              + typed Set* + Save (so the
//              Editor's in-memory cache and the store agree; excluded keys keep
//              their current values), EditorPrefs only under the snapshot's
//              prefixes (never DeleteAll), and persistentDataPath mirrored
//              exactly (files not in the snapshot are removed; excluded paths
//              untouched); then re-read and verified.
//   list / delete — the snapshots of a company/product store.
//
// Gate: the bridge treats only `restore` with `confirm: true` as a mutation
// (EffectiveToolContract): it runs the normal gate + audit path with the
// mandatory paths_hint. Restore writes no project asset; its declared scope is
// ProjectSettings/ProjectSettings.asset — the asset holding the company /
// product identity the PlayerPrefs store, persistentDataPath and the snapshot
// are bound to — which the MCP schema supplies as the default paths_hint.
// Every other action is effectively read-only (no gate, no scope).
//
// Refusals return a failed ToolDispatchResult (mutation.success:false with a
// stable error code), so a refused restore is recorded as a failed mutation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    // PlayerPrefs access behind a seam so tests never touch the real store.
    internal interface IPlayerPrefsBackend
    {
        void Flush();
        PrefsReadResult Read(string company, string product);
        void DeleteAll();
        void Delete(string key);
        void Set(PrefEntry entry);
    }

    internal interface IEditorPrefsBackend
    {
        // False when this Editor has no way to flush its in-memory EditorPrefs.
        bool CanFlush { get; }
        void Flush();
        PrefsReadResult Read(IList<string> prefixes);
        void Delete(string key);
        void Set(PrefEntry entry);
    }

    internal sealed class UnityPlayerPrefsBackend : IPlayerPrefsBackend
    {
        public void Flush() => PlayerPrefs.Save();
        public PrefsReadResult Read(string company, string product) => PrefsStore.ReadPlayerPrefs(company, product);
        public void DeleteAll() => PlayerPrefs.DeleteAll();
        public void Delete(string key) => PlayerPrefs.DeleteKey(key);

        public void Set(PrefEntry e)
        {
            switch (e.Type)
            {
                case PrefType.Int: PlayerPrefs.SetInt(e.Key, e.IntValue); break;
                case PrefType.Float: PlayerPrefs.SetFloat(e.Key, e.FloatValue); break;
                default: PlayerPrefs.SetString(e.Key, e.StringValue ?? ""); break;
            }
        }
    }

    // The Editor keeps EditorPrefs in memory as well and writes the shared store
    // only from time to time; the internal EditorPrefs.Sync() writes it now
    // (verified on Unity 6). Without it the store can miss recent keys, so
    // EditorPrefs capture refuses instead of reading a stale store.
    internal sealed class UnityEditorPrefsBackend : IEditorPrefsBackend
    {
        private static readonly System.Reflection.MethodInfo SyncMethod = typeof(EditorPrefs).GetMethod("Sync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        public bool CanFlush => SyncMethod != null;
        public void Flush() => SyncMethod?.Invoke(null, null);
        public PrefsReadResult Read(IList<string> prefixes) => PrefsStore.ReadEditorPrefs(prefixes);
        public void Delete(string key) => EditorPrefs.DeleteKey(key);

        public void Set(PrefEntry e)
        {
            switch (e.Type)
            {
                case PrefType.Int: EditorPrefs.SetInt(e.Key, e.IntValue); break;
                case PrefType.Float: EditorPrefs.SetFloat(e.Key, e.FloatValue); break;
                default: EditorPrefs.SetString(e.Key, e.StringValue ?? ""); break;
            }
        }
    }

    [BridgeToolType]
    public static class PlayerStateTools
    {
        internal const string ToolName = "unity_open_mcp_player_state";
        internal const string RestoreScope = "ProjectSettings/ProjectSettings.asset";
        internal const int DefaultLimit = 100;
        internal const int MaxLimit = 1000;
        internal const int MaxShownString = 256;
        internal const int MinPrefixLength = 3;
        private static readonly string[] Actions = { "snapshot", "diff", "restore", "list", "delete" };

        // Test seams (null = the real Editor).
        internal static IPlayerPrefsBackend PlayerPrefsOverride;
        internal static IEditorPrefsBackend EditorPrefsOverride;
        internal static Func<string> PersistentDataPathOverride;
        internal static Func<string[]> IdentityOverride;
        internal static Func<bool> PlayingOverride;

        private static IPlayerPrefsBackend Prefs => PlayerPrefsOverride ?? new UnityPlayerPrefsBackend();
        private static IEditorPrefsBackend EditorPrefsBackend => EditorPrefsOverride ?? new UnityEditorPrefsBackend();
        private static string PersistentRoot => PersistentDataPathOverride != null ? PersistentDataPathOverride() : Application.persistentDataPath;
        private static bool IsPlaying => PlayingOverride != null
            ? PlayingOverride()
            : EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;

        // [company, product] of the open project.
        private static string[] Identity() =>
            IdentityOverride != null ? IdentityOverride() : new[] { PlayerSettings.companyName, PlayerSettings.productName };

        // Only restore with confirm:true writes state; every other request is
        // read-only for the project (EffectiveToolContract). Mutating when either
        // the depth-aware reader or the registry's argument binder (which reads
        // the first occurrence of a key anywhere in the body) sees restore +
        // confirm, so a crafted body cannot bind a restore the gate classified
        // as a read.
        internal static bool IsMutatingRequest(string body)
        {
            body = body ?? "{}";
            return IsRestoreConfirm(JsonBody.TopLevelField(body, "action"), JsonBody.TopLevelField(body, "confirm"))
                || IsRestoreConfirm(body, body);
        }

        private static bool IsRestoreConfirm(string actionJson, string confirmJson) =>
            string.Equals(JsonBody.GetString(actionJson, "action")?.Trim(), "restore", StringComparison.OrdinalIgnoreCase)
            && JsonBody.GetBool(confirmJson, "confirm");

        [BridgeTool(ToolName,
            Title = "Playtest: Player State Sandbox",
            IsMutating = true,
            Gate = GateMode.Enforce,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = true,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Snapshot, diff and restore the player state a playtest changes: PlayerPrefs (all keys, typed, " +
            "read from the platform store after PlayerPrefs.Save), opt-in EditorPrefs by key prefix, and " +
            "persistentDataPath files. Snapshots live in the app-data folder outside the project. restore is " +
            "edit-mode only, a dry run unless confirm:true, and the only gated action.")]
        public static ToolDispatchResult Run(
            string action = null,
            string name = null,
            string against = null,
            bool confirm = false,
            string plan = null,
            string[] editor_prefs_prefixes = null,
            string[] exclude = null,
            string[] exclude_keys = null,
            int? max_files_mb = null,
            bool overwrite = false,
            string company = null,
            string product = null,
            int? limit = null,
            string[] paths_hint = null)
        {
            action = (action ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(Actions, action) < 0)
                return Invalid("Unknown action '" + action + "'. Valid: " + string.Join(", ", Actions) + ".");

            int lim = limit ?? DefaultLimit;
            if (lim < 1 || lim > MaxLimit) return Invalid("limit must be 1.." + MaxLimit + ".");

            var misplaced = MisplacedArguments(action, against, confirm, plan, editor_prefs_prefixes, exclude, exclude_keys,
                max_files_mb, overwrite, company, product);
            if (misplaced != null) return Invalid(misplaced);

            if (action != "list" && !SnapshotStore.IsValidName(name))
                return Invalid("'name' is required for " + action + ": 1-64 characters [A-Za-z0-9._-], starting with a letter or digit" +
                    (name == null ? "." : " (got '" + name + "')."));

            var id = Identity();
            try
            {
                switch (action)
                {
                    case "snapshot":
                        return Snapshot(name, id, editor_prefs_prefixes, exclude, exclude_keys,
                            max_files_mb ?? PlayerStateDefaults.DefaultMaxFilesMb, overwrite);
                    case "diff":
                        return Diff(name, against, company ?? id[0], product ?? id[1], id, lim);
                    case "restore":
                        return Restore(name, confirm, plan, company ?? id[0], product ?? id[1], id, lim);
                    case "list":
                        return List(company ?? id[0], product ?? id[1], id);
                    default:
                        return Delete(name, company ?? id[0], product ?? id[1]);
                }
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.GetType().Name + ": " + e.Message);
            }
        }

        private static string MisplacedArguments(string action, string against, bool confirm, string plan, string[] prefixes,
            string[] exclude, string[] excludeKeys, int? maxMb, bool overwrite, string company, string product)
        {
            var wrong = new List<string>();
            if (action != "snapshot")
            {
                if (prefixes != null) wrong.Add("editor_prefs_prefixes");
                if (exclude != null) wrong.Add("exclude");
                if (excludeKeys != null) wrong.Add("exclude_keys");
                if (maxMb != null) wrong.Add("max_files_mb");
                if (overwrite) wrong.Add("overwrite");
            }
            else
            {
                if (company != null) wrong.Add("company");
                if (product != null) wrong.Add("product");
            }
            if (action != "diff" && against != null) wrong.Add("against");
            if (action != "restore" && confirm) wrong.Add("confirm");
            if (plan != null && !(action == "restore" && confirm)) wrong.Add("plan");
            if (wrong.Count == 0) return null;
            return string.Join(", ", wrong) + (wrong.Count == 1 ? " does" : " do") + " not apply to action '" + action + "'" +
                (wrong.Contains("plan") && action == "restore" ? " without confirm: true" : "") + "." +
                (action == "snapshot" && (company != null || product != null)
                    ? " A snapshot always captures the open project."
                    : "");
        }

        // ==================================================================
        // snapshot
        // ==================================================================

        private static ToolDispatchResult Snapshot(string name, string[] id, string[] prefixesArg, string[] excludeArg,
            string[] excludeKeysArg, int maxMb, bool overwrite)
        {
            if (maxMb < 1 || maxMb > PlayerStateDefaults.MaxMaxFilesMb)
                return Invalid("max_files_mb must be 1.." + PlayerStateDefaults.MaxMaxFilesMb + ".");
            string[] prefixes = null;
            if (prefixesArg != null)
            {
                prefixes = prefixesArg.Select(p => p ?? "").Distinct(StringComparer.Ordinal).ToArray();
                var bad = prefixes.Where(p => p.Trim().Length < MinPrefixLength || p.Trim() != p).ToArray();
                if (prefixes.Length == 0 || bad.Length > 0)
                    return Invalid("editor_prefs_prefixes: each prefix needs at least " + MinPrefixLength +
                        " characters and no surrounding whitespace (EditorPrefs are shared by every project on this machine; " +
                        "pick a prefix that only covers your own keys).");
            }
            var fileExcludes = Merge(PlayerStateDefaults.FileExcludes, excludeArg);
            var keyExcludes = Merge(PlayerStateDefaults.KeyExcludes, excludeKeysArg);

            var productDir = SnapshotStore.ProductDir(id[0], id[1]);
            var dir = Path.Combine(productDir, name);
            if (Directory.Exists(dir) && !overwrite)
            {
                var existing = SnapshotStore.Load(dir, out _);
                return ToolDispatchResult.FailWithDetail("snapshot_exists",
                    "Snapshot '" + name + "' already exists for " + id[0] + "/" + id[1] + ".",
                    "{\"name\":" + Q(name) + ",\"created_at\":" + Q(existing?.createdUtc) + ",\"path\":" + Q(dir) + "}",
                    new[] { "Pick another name, pass overwrite: true to replace it, or delete it first (action delete)." });
            }

            long limitBytes = (long)maxMb * 1024 * 1024;
            var current = CaptureCurrent(id, keyExcludes, prefixes, fileExcludes, hash: false,
                limitBytes: limitBytes, limitFiles: PlayerStateDefaults.MaxFiles, out var failure);
            if (failure != null) return failure;

            var root = PersistentRoot;
            Directory.CreateDirectory(productDir);
            var tmp = Path.Combine(productDir, ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var filesDir = Path.Combine(tmp, SnapshotStore.FilesDir);
                Directory.CreateDirectory(filesDir);
                foreach (var d in current.Dirs)
                    Directory.CreateDirectory(PersistentFiles.ToNative(filesDir, d));
                long bytes = 0;
                foreach (var f in current.Files)
                {
                    var dst = PersistentFiles.ToNative(filesDir, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(PersistentFiles.ToNative(root, f.Path), dst, overwrite: false);
                    // The copy is what restore writes back: describe it, not the source.
                    f.Size = new FileInfo(dst).Length;
                    f.Sha256 = PersistentFiles.Sha256File(dst);
                    bytes += f.Size;
                }
                current.State.FilesBytes = bytes;

                var doc = new PlayerStateSnapshotData
                {
                    format = PlayerStateDefaults.Format,
                    name = name,
                    company = id[0],
                    product = id[1],
                    createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    platform = PrefsStore.Current.ToString(),
                    projectPath = BridgeSession.ProjectPath,
                    persistentDataPath = root,
                    prefsSource = current.PrefsSource,
                    keyExcludes = keyExcludes,
                    prefs = current.State.Prefs.Select(SnapshotStore.ToRecord).ToArray(),
                    unsupportedPrefs = current.State.UnsupportedPrefs.ToArray(),
                    editorPrefsCaptured = prefixes != null,
                    editorPrefsPrefixes = prefixes ?? Array.Empty<string>(),
                    editorPrefs = (current.State.EditorPrefs ?? new List<PrefEntry>()).Select(SnapshotStore.ToRecord).ToArray(),
                    unsupportedEditorPrefs = current.State.UnsupportedEditorPrefs.ToArray(),
                    fileExcludes = fileExcludes,
                    files = current.Files.Select(f => new PlayerStateFileRecord { path = f.Path, size = f.Size, sha256 = f.Sha256 }).ToArray(),
                    dirs = current.Dirs.ToArray(),
                    filesBytes = bytes,
                };
                SnapshotStore.WriteMeta(tmp, doc);

                if (Directory.Exists(dir))
                {
                    var old = Path.Combine(productDir, ".old-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(dir, old);
                    try { Directory.Move(tmp, dir); }
                    catch
                    {
                        // Put the previous snapshot back under its name.
                        Directory.Move(old, dir);
                        throw;
                    }
                    TryDeleteDirectory(old);
                }
                else
                {
                    Directory.Move(tmp, dir);
                }

                var sb = new StringBuilder(1024);
                sb.Append("{\"status\":\"ok\",\"action\":\"snapshot\"");
                AppendSnapshotHeader(sb, doc, dir);
                sb.Append(",\"mode\":").Append(Q(IsPlaying ? "play" : "edit"));
                sb.Append(",\"flushed\":true");
                sb.Append(",\"prefs\":{\"count\":").Append(doc.prefs.Length);
                sb.Append(",\"excluded_keys\":").Append(current.State.ExcludedKeys);
                sb.Append(",\"key_excludes\":"); AppendStrings(sb, keyExcludes);
                sb.Append(",\"unsupported\":"); AppendStrings(sb, doc.unsupportedPrefs);
                sb.Append(",\"store\":").Append(Q(current.PrefsSource)).Append('}');
                sb.Append(",\"editor_prefs\":");
                if (prefixes == null) sb.Append("null");
                else
                {
                    sb.Append("{\"prefixes\":"); AppendStrings(sb, prefixes);
                    sb.Append(",\"count\":").Append(doc.editorPrefs.Length);
                    sb.Append(",\"unsupported\":"); AppendStrings(sb, doc.unsupportedEditorPrefs);
                    sb.Append('}');
                }
                sb.Append(",\"files\":{\"root\":").Append(Q(root));
                sb.Append(",\"count\":").Append(doc.files.Length);
                sb.Append(",\"bytes\":").Append(bytes);
                sb.Append(",\"dirs\":").Append(doc.dirs.Length);
                sb.Append(",\"max_mb\":").Append(maxMb);
                sb.Append(",\"exclude\":"); AppendStrings(sb, fileExcludes);
                sb.Append(",\"excluded_files\":").Append(current.State.ExcludedFiles);
                sb.Append(",\"excluded_dirs\":"); AppendStrings(sb, current.State.ExcludedDirs);
                sb.Append(",\"skipped_links\":"); AppendStrings(sb, current.State.SkippedLinks);
                sb.Append('}');
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch
            {
                TryDeleteDirectory(tmp);
                throw;
            }
        }

        // ==================================================================
        // diff
        // ==================================================================

        private static ToolDispatchResult Diff(string name, string against, string company, string product, string[] id, int limit)
        {
            if (against != null && !SnapshotStore.IsValidName(against))
                return Invalid("'against' must be a snapshot name (got '" + against + "').");
            var from = LoadSnapshot(company, product, name, out var fromDir, out var failure);
            if (failure != null) return failure;

            PlayerState to;
            PlayerStateSnapshotData toDoc = null;
            if (against != null)
            {
                toDoc = LoadSnapshot(company, product, against, out _, out failure);
                if (failure != null) return failure;
                to = SnapshotStore.ToState(toDoc);
            }
            else
            {
                if (!SameProject(from, id))
                    return Mismatch(from, id, "diff",
                        "The current state is the open project's; compare a snapshot of another project only with another " +
                        "snapshot (against).");
                var current = CaptureCurrent(id, from.keyExcludes, from.editorPrefsCaptured ? from.editorPrefsPrefixes : null,
                    from.fileExcludes, hash: true, limitBytes: 0, limitFiles: 0, out failure);
                if (failure != null) return failure;
                to = current.State;
            }

            var diff = StateDiff.Compute(SnapshotStore.ToState(from), to);
            var sb = new StringBuilder(2048);
            sb.Append("{\"status\":\"ok\",\"action\":\"diff\"");
            sb.Append(",\"from\":"); AppendSide(sb, from, fromDir);
            sb.Append(",\"to\":");
            if (toDoc != null) AppendSide(sb, toDoc, SnapshotStore.SnapshotDir(company, product, against));
            else sb.Append("{\"current\":true,\"mode\":").Append(Q(IsPlaying ? "play" : "edit")).Append(",\"flushed\":true}");
            AppendDiff(sb, diff, limit, listEveryLoss: false);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ==================================================================
        // restore
        // ==================================================================

        private static ToolDispatchResult Restore(string name, bool confirm, string planArg, string company, string product,
            string[] id, int limit)
        {
            if (IsPlaying)
                return ToolDispatchResult.FailWithDetail("edit_mode_required",
                    "restore runs in edit mode only: the game would keep running on (and could re-save) the state being replaced.",
                    "{\"mode\":\"play\"}",
                    new[] { "Stop play mode (unity_open_mcp_editor_set_state state:\"stop\"), then call restore again. " +
                            "Use action diff to see what the session changed while it is still running." });

            var snap = LoadSnapshot(company, product, name, out var snapDir, out var failure);
            if (failure != null) return failure;
            if (!SameProject(snap, id))
                return Mismatch(snap, id, "restore",
                    "restore only applies a snapshot of the open project (same company and product name).");

            var target = SnapshotStore.ToState(snap);
            var badPath = target.Files.Select(f => f.Path).Concat(target.Dirs).FirstOrDefault(p => !PersistentFiles.IsSafeRelative(p));
            if (badPath != null)
                return ToolDispatchResult.FailWithDetail("snapshot_corrupt",
                    "Snapshot manifest path '" + badPath + "' is not a plain path under persistentDataPath; nothing was changed.",
                    "{\"name\":" + Q(name) + ",\"path\":" + Q(badPath) + "}",
                    new[] { "Take a new snapshot; this one cannot be restored safely." });

            var prefixes = snap.editorPrefsCaptured ? snap.editorPrefsPrefixes : null;
            var current = CaptureCurrent(id, snap.keyExcludes, prefixes, snap.fileExcludes, hash: true,
                limitBytes: 0, limitFiles: 0, out failure);
            if (failure != null) return failure;
            var plan = StateDiff.Compute(current.State, target);
            var unsupportedDeleted = current.State.UnsupportedPrefs.Concat(current.ExcludedUnsupported).ToList();
            var planId = PlanId(plan, unsupportedDeleted);

            if (!confirm)
            {
                var sb = new StringBuilder(2048);
                sb.Append("{\"status\":\"ok\",\"action\":\"restore\",\"dry_run\":true");
                sb.Append(",\"plan\":").Append(Q(planId));
                sb.Append(",\"from\":{\"current\":true,\"mode\":\"edit\"}");
                sb.Append(",\"to\":"); AppendSide(sb, snap, snapDir);
                AppendDiff(sb, plan, limit, listEveryLoss: true);
                sb.Append(",\"unsupported_deleted\":"); AppendStrings(sb, unsupportedDeleted);
                sb.Append(",\"warning\":").Append(Q(RestoreWarning(plan, prefixes, unsupportedDeleted.Count)));
                sb.Append(",\"agentNextSteps\":"); AppendStrings(sb, new[]
                {
                    plan.Identical
                        ? "Nothing to restore: the current state already matches the snapshot."
                        : "Review prefs.removed / files.removed and the changed lists (everything restore deletes or " +
                          "overwrites), then call restore again with confirm: true and plan: \"" + planId + "\" to apply. " +
                          "Nothing has changed yet.",
                });
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }

            // confirm applies exactly the plan the caller reviewed: a state that
            // moved since the dry run (a new save file, a changed key) refuses.
            if (planArg != planId)
                return ToolDispatchResult.FailWithDetail(planArg == null ? "plan_required" : "plan_changed",
                    planArg == null
                        ? "restore confirm:true needs the `plan` id of the dry run it applies; nothing was changed."
                        : "The current state changed since the dry run that returned plan '" + planArg +
                          "'; nothing was changed.",
                    "{\"plan\":" + Q(planId) + ",\"given\":" + Q(planArg) + "}",
                    new[] { "Call restore without confirm, review its removals and overwrites, then confirm with the plan it returns." });

            // Integrity first: every stored file must still match its manifest.
            var filesDir = Path.Combine(snapDir, SnapshotStore.FilesDir);
            foreach (var f in target.Files)
            {
                var src = PersistentFiles.ToNative(filesDir, f.Path);
                if (!File.Exists(src) || PersistentFiles.Sha256File(src) != f.Sha256)
                    return ToolDispatchResult.FailWithDetail("snapshot_corrupt",
                        "Stored file '" + f.Path + "' is missing or no longer matches the snapshot manifest; nothing was changed.",
                        "{\"name\":" + Q(name) + ",\"path\":" + Q(f.Path) + "}",
                        new[] { "Take a new snapshot; this one cannot be restored faithfully." });
            }

            // Every path restore writes or removes stays under persistentDataPath:
            // none may pass through a symbolic link (the scan skips links, so a
            // link is never in the plan as itself).
            var root = PersistentRoot;
            var linked = string.IsNullOrEmpty(root)
                ? new List<string>()
                : plan.Files.Added.Select(f => f.Path)
                    .Concat(plan.Files.Changed.Select(kv => kv.Value.Path))
                    .Concat(plan.Files.Removed.Select(f => f.Path))
                    .Concat(plan.Files.DirsAdded).Concat(plan.Files.DirsRemoved)
                    .Where(p => PersistentFiles.PassesThroughLink(root, p)).Distinct(StringComparer.Ordinal).ToList();
            if (linked.Count > 0)
            {
                var detail = new StringBuilder(256);
                detail.Append("{\"root\":").Append(Q(root)).Append(",\"paths\":"); AppendStrings(detail, linked.Take(20));
                detail.Append('}');
                return ToolDispatchResult.FailWithDetail("persistent_path_link",
                    linked.Count + " path(s) restore would write or remove pass through a symbolic link out of persistentDataPath; " +
                    "nothing was changed.", detail.ToString(),
                    new[] { "Remove or replace the link (see paths), then run the dry run again." });
            }

            // The dry run's prefs.removed is only as complete as the store read:
            // prove the reader sees a key written and saved through PlayerPrefs
            // before DeleteAll (DeleteAll removes the probe again).
            var backend = Prefs;
            if (!StoreSeesWrites(backend, id, out var storeSource))
                return ToolDispatchResult.FailWithDetail("prefs_store_unverified",
                    "A probe key written through PlayerPrefs and saved did not appear in the store read (" + storeSource +
                    "), so the dry run's prefs lists cannot be trusted; nothing was restored.",
                    "{\"store\":" + Q(storeSource) + ",\"probe_key\":" + Q(StoreProbeKey) + "}",
                    new[] { "The PlayerPrefs store this Editor writes is not the one the bridge reads on this platform; " +
                            "report it with the store path. Files and PlayerPrefs were left as they are." });

            var errors = new List<string>();
            int editorDeleted = 0, editorSet = 0;
            int written = 0, removed = 0, dirsCreated = 0, dirsRemoved = 0;
            var keptDirs = new List<string>();
            try
            {
                // PlayerPrefs: the API (not the store) so the Editor's in-memory cache
                // and the store agree. Excluded keys keep their current values.
                backend.DeleteAll();
                foreach (var e in target.Prefs) backend.Set(e);
                foreach (var e in current.ExcludedEntries) backend.Set(e);
                backend.Flush();

                // EditorPrefs: only keys under the snapshot's prefixes, never DeleteAll.
                if (prefixes != null)
                {
                    var ep = EditorPrefsBackend;
                    foreach (var e in plan.EditorPrefs.Removed) { ep.Delete(e.Key); editorDeleted++; }
                    foreach (var kv in plan.EditorPrefs.Changed) { ep.Set(kv.Value); editorSet++; }
                    foreach (var e in plan.EditorPrefs.Added) { ep.Set(e); editorSet++; }
                }

                // Files: exact mirror of the snapshot (excluded paths untouched).
                // Removals first: on a case-insensitive file system a case-only
                // rename would otherwise delete the file just written.
                if (!string.IsNullOrEmpty(root))
                {
                    Directory.CreateDirectory(root);
                    foreach (var f in plan.Files.Removed)
                    {
                        try { File.Delete(PersistentFiles.ToNative(root, f.Path)); removed++; }
                        catch (Exception e) { errors.Add("remove " + f.Path + ": " + e.Message); }
                    }
                    foreach (var d in plan.Files.DirsAdded)
                    {
                        try { Directory.CreateDirectory(PersistentFiles.ToNative(root, d)); dirsCreated++; }
                        catch (Exception e) { errors.Add("mkdir " + d + ": " + e.Message); }
                    }
                    foreach (var f in plan.Files.Added.Concat(plan.Files.Changed.Select(kv => kv.Value)))
                    {
                        try
                        {
                            var dst = PersistentFiles.ToNative(root, f.Path);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Copy(PersistentFiles.ToNative(filesDir, f.Path), dst, overwrite: true);
                            written++;
                        }
                        catch (Exception e) { errors.Add("write " + f.Path + ": " + e.Message); }
                    }
                    foreach (var d in plan.Files.DirsRemoved.OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            var abs = PersistentFiles.ToNative(root, d);
                            if (!Directory.Exists(abs)) continue;
                            if (Directory.EnumerateFileSystemEntries(abs).Any()) { keptDirs.Add(d); continue; }
                            Directory.Delete(abs, recursive: false);
                            dirsRemoved++;
                        }
                        catch (Exception e) { errors.Add("rmdir " + d + ": " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                errors.Add("aborted: " + e.GetType().Name + ": " + e.Message);
            }

            // Verify: re-read everything and compare with the snapshot.
            Capture after = null;
            string verifyError = null;
            try
            {
                after = CaptureCurrent(id, snap.keyExcludes, prefixes, snap.fileExcludes, hash: true,
                    limitBytes: 0, limitFiles: 0, out var verifyFailure);
                verifyError = verifyFailure?.ErrorMessage;
            }
            catch (Exception e)
            {
                verifyError = e.GetType().Name + ": " + e.Message;
            }
            StateDiff residual = after != null ? StateDiff.Compute(after.State, target) : null;
            bool verified = residual != null && residual.Identical && keptDirs.Count == 0;

            var outSb = new StringBuilder(2048);
            outSb.Append("{\"status\":\"ok\",\"action\":\"restore\",\"dry_run\":false");
            outSb.Append(",\"plan\":").Append(Q(planId));
            outSb.Append(",\"from\":{\"current\":true,\"mode\":\"edit\"}");
            outSb.Append(",\"to\":"); AppendSide(outSb, snap, snapDir);
            AppendDiff(outSb, plan, limit, listEveryLoss: true);
            outSb.Append(",\"unsupported_deleted\":"); AppendStrings(outSb, unsupportedDeleted);
            outSb.Append(",\"applied\":{\"prefs\":{\"deleted_all\":true,\"set\":").Append(target.Prefs.Count);
            outSb.Append(",\"kept_excluded\":").Append(current.ExcludedEntries.Count).Append('}');
            outSb.Append(",\"editor_prefs\":");
            if (prefixes == null) outSb.Append("null");
            else outSb.Append("{\"deleted\":").Append(editorDeleted).Append(",\"set\":").Append(editorSet).Append('}');
            outSb.Append(",\"files\":{\"written\":").Append(written).Append(",\"removed\":").Append(removed);
            outSb.Append(",\"dirs_created\":").Append(dirsCreated).Append(",\"dirs_removed\":").Append(dirsRemoved);
            outSb.Append(",\"dirs_kept_not_empty\":"); AppendStrings(outSb, keptDirs);
            outSb.Append("}}");
            outSb.Append(",\"verified\":").Append(verified ? "true" : "false");
            if (!verified)
            {
                outSb.Append(",\"residual\":");
                if (residual == null) outSb.Append("{\"error\":").Append(Q(verifyError)).Append('}');
                else { outSb.Append('{'); AppendDiffBody(outSb, residual, limit, listEveryLoss: true); outSb.Append('}'); }
            }
            outSb.Append(",\"errors\":"); AppendStrings(outSb, errors);
            outSb.Append('}');

            if (errors.Count > 0)
                return ToolDispatchResult.PartialFailure("restore_incomplete",
                    errors.Count + " operation(s) failed; the state is partly restored (see output.errors and residual).",
                    outSb.ToString());
            if (!verified)
                return ToolDispatchResult.PartialFailure("restore_incomplete",
                    "Every operation ran, but the re-read state does not match the snapshot (see output.residual and " +
                    "dirs_kept_not_empty).", outSb.ToString());
            return ToolDispatchResult.Ok(outSb.ToString());
        }

        internal const string StoreProbeKey = "unity_open_mcp.store_probe";

        // Writes a fresh probe key through PlayerPrefs, saves and reads the store
        // back. On failure the probe is deleted again; on success the caller's
        // DeleteAll removes it.
        private static bool StoreSeesWrites(IPlayerPrefsBackend backend, string[] id, out string source)
        {
            var value = Guid.NewGuid().ToString("N");
            backend.Set(PrefEntry.OfString(StoreProbeKey, value));
            backend.Flush();
            var read = backend.Read(id[0], id[1]);
            source = read.Source;
            bool seen = read.Error == null && read.Entries.Any(e =>
                e.Key == StoreProbeKey && e.Type == PrefType.String && e.StringValue == value);
            if (!seen)
            {
                backend.Delete(StoreProbeKey);
                backend.Flush();
            }
            return seen;
        }

        // Stable id of what restore would do: every pref, editor pref and file
        // removal, overwrite and addition with its value or content hash, plus the
        // unsupported keys DeleteAll drops. confirm must echo the dry run's id.
        internal static string PlanId(StateDiff plan, IEnumerable<string> unsupportedDeleted)
        {
            var sb = new StringBuilder(1024);
            AppendPlanKeys(sb, 'p', plan.Prefs);
            if (plan.EditorPrefs != null) AppendPlanKeys(sb, 'e', plan.EditorPrefs);
            foreach (var f in plan.Files.Removed) AppendPlanLine(sb, "f-", f.Path, f.Sha256);
            foreach (var kv in plan.Files.Changed) AppendPlanLine(sb, "f~", kv.Key.Path, kv.Key.Sha256 + ">" + kv.Value.Sha256);
            foreach (var f in plan.Files.Added) AppendPlanLine(sb, "f+", f.Path, f.Sha256);
            foreach (var d in plan.Files.DirsAdded) AppendPlanLine(sb, "d+", d, "");
            foreach (var d in plan.Files.DirsRemoved) AppendPlanLine(sb, "d-", d, "");
            foreach (var k in unsupportedDeleted) AppendPlanLine(sb, "u-", k, "");
            return PersistentFiles.Sha256Text(sb.ToString()).Substring(0, 16);
        }

        private static void AppendPlanKeys(StringBuilder sb, char tag, KeyDiff d)
        {
            foreach (var e in d.Removed) AppendPlanLine(sb, tag + "-", e.Key, PlanValue(e));
            foreach (var kv in d.Changed) AppendPlanLine(sb, tag + "~", kv.Key.Key, PlanValue(kv.Key) + ">" + PlanValue(kv.Value));
            foreach (var e in d.Added) AppendPlanLine(sb, tag + "+", e.Key, PlanValue(e));
        }

        private static string PlanValue(PrefEntry e)
        {
            switch (e.Type)
            {
                case PrefType.Int: return "i" + e.IntValue.ToString(CultureInfo.InvariantCulture);
                case PrefType.Float: return "f" + PrefEntry.FloatBits(e.FloatValue).ToString(CultureInfo.InvariantCulture);
                default: return "s" + PersistentFiles.Sha256Text(e.StringValue ?? "");
            }
        }

        // Length-prefixed so no key or path can forge another entry's line.
        private static void AppendPlanLine(StringBuilder sb, string tag, string key, string value)
        {
            key = key ?? "";
            sb.Append(tag).Append(key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(key)
              .Append(' ').Append(value).Append('\n');
        }

        internal static string RestoreWarning(StateDiff plan, string[] prefixes, int unsupportedDeleted)
        {
            var sb = new StringBuilder("Dry run - nothing changed. restore confirm:true overwrites the current state: ");
            sb.Append("PlayerPrefs.DeleteAll, then the snapshot's ").Append("keys (").Append(plan.Prefs.Removed.Count)
              .Append(" key(s) not in the snapshot are deleted; excluded keys of a supported type keep their current values");
            if (unsupportedDeleted > 0)
                sb.Append("; ").Append(unsupportedDeleted).Append(" key(s) of a type PlayerPrefs cannot hold are deleted for good (unsupported_deleted)");
            sb.Append(')');
            if (prefixes != null)
                sb.Append("; EditorPrefs under ").Append(string.Join(", ", prefixes)).Append(" (")
                  .Append(plan.EditorPrefs.Removed.Count).Append(" deleted)");
            sb.Append("; persistentDataPath mirrored exactly: ").Append(plan.Files.Removed.Count)
              .Append(" file(s) not in the snapshot are removed (files.removed), ")
              .Append(plan.Files.Added.Count + plan.Files.Changed.Count).Append(" written.");
            return sb.ToString();
        }

        // ==================================================================
        // list / delete
        // ==================================================================

        private static ToolDispatchResult List(string company, string product, string[] id)
        {
            var productDir = SnapshotStore.ProductDir(company, product);
            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"action\":\"list\"");
            sb.Append(",\"company\":").Append(Q(company)).Append(",\"product\":").Append(Q(product));
            sb.Append(",\"open_project\":").Append(company == id[0] && product == id[1] ? "true" : "false");
            sb.Append(",\"store\":").Append(Q(productDir));
            sb.Append(",\"snapshots\":[");
            bool first = true;
            foreach (var n in SnapshotStore.Names(productDir))
            {
                if (!first) sb.Append(',');
                first = false;
                var dir = Path.Combine(productDir, n);
                var doc = SnapshotStore.Load(dir, out var error);
                if (doc == null)
                {
                    sb.Append("{\"name\":").Append(Q(n)).Append(",\"unreadable\":").Append(Q(error)).Append('}');
                    continue;
                }
                sb.Append("{\"name\":").Append(Q(n));
                sb.Append(",\"created_at\":").Append(Q(doc.createdUtc));
                sb.Append(",\"prefs\":").Append(doc.prefs?.Length ?? 0);
                sb.Append(",\"editor_prefs_prefixes\":");
                if (doc.editorPrefsCaptured) AppendStrings(sb, doc.editorPrefsPrefixes); else sb.Append("null");
                sb.Append(",\"files\":").Append(doc.files?.Length ?? 0);
                sb.Append(",\"bytes\":").Append(doc.filesBytes);
                if (doc.company != company || doc.product != product)
                    sb.Append(",\"company\":").Append(Q(doc.company)).Append(",\"product\":").Append(Q(doc.product));
                sb.Append('}');
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        private static ToolDispatchResult Delete(string name, string company, string product)
        {
            var dir = SnapshotStore.SnapshotDir(company, product, name);
            if (!Directory.Exists(dir)) return NotFound(company, product, name);
            Directory.Delete(dir, recursive: true);
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"action\":\"delete\",\"name\":").Append(Q(name));
            sb.Append(",\"company\":").Append(Q(company)).Append(",\"product\":").Append(Q(product));
            sb.Append(",\"path\":").Append(Q(dir)).Append(",\"deleted\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ==================================================================
        // Current state
        // ==================================================================

        internal sealed class Capture
        {
            public PlayerState State;
            public List<PrefEntry> ExcludedEntries = new List<PrefEntry>();
            // Excluded keys of a type PlayerPrefs cannot write back: DeleteAll
            // still removes them, so restore reports them as deleted.
            public List<string> ExcludedUnsupported = new List<string>();
            public List<FileEntry> Files => State.Files;
            public List<string> Dirs => State.Dirs;
            public string PrefsSource;
        }

        // Flush PlayerPrefs, read the store (minus excluded keys), EditorPrefs
        // under `prefixes` (null = not captured) and persistentDataPath.
        internal static Capture CaptureCurrent(string[] id, string[] keyExcludes, string[] prefixes, string[] fileExcludes,
            bool hash, long limitBytes, int limitFiles, out ToolDispatchResult failure)
        {
            failure = null;
            var backend = Prefs;
            backend.Flush();
            var read = backend.Read(id[0], id[1]);
            if (read.Error != null)
            {
                failure = ToolDispatchResult.FailWithDetail("prefs_store_unreadable",
                    "Could not read the PlayerPrefs store (" + read.Source + "): " + read.Error,
                    "{\"store\":" + Q(read.Source) + "}", null);
                return null;
            }
            var cap = new Capture { State = new PlayerState(), PrefsSource = read.Source };
            foreach (var e in read.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (Glob.IsKeyExcluded(keyExcludes, e.Key)) { cap.ExcludedEntries.Add(e); cap.State.ExcludedKeys++; }
                else cap.State.Prefs.Add(e);
            }
            foreach (var k in read.Unsupported)
            {
                if (Glob.IsKeyExcluded(keyExcludes, k)) cap.ExcludedUnsupported.Add(k);
                else cap.State.UnsupportedPrefs.Add(k);
            }

            if (prefixes != null)
            {
                var ep = EditorPrefsBackend;
                if (!ep.CanFlush)
                {
                    failure = ToolDispatchResult.FailWithDetail("editor_internals_unavailable",
                        "This Editor (Unity " + Application.unityVersion + ") has no EditorPrefs flush (internal EditorPrefs.Sync); " +
                        "its EditorPrefs store can miss recent keys, so EditorPrefs are not captured, diffed or restored.",
                        "{\"detected\":{\"unityVersion\":" + Q(Application.unityVersion) + ",\"missing\":[\"UnityEditor.EditorPrefs.Sync\"]}}",
                        new[] { "Snapshot without editor_prefs_prefixes (PlayerPrefs and files are unaffected)." });
                    return null;
                }
                ep.Flush();
                var er = ep.Read(prefixes);
                if (er.Error != null)
                {
                    failure = ToolDispatchResult.FailWithDetail("prefs_store_unreadable",
                        "Could not read the EditorPrefs store (" + er.Source + "): " + er.Error,
                        "{\"store\":" + Q(er.Source) + "}", null);
                    return null;
                }
                cap.State.EditorPrefsPrefixes = prefixes;
                cap.State.EditorPrefs = er.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                cap.State.UnsupportedEditorPrefs.AddRange(er.Unsupported);
            }

            var root = PersistentRoot;
            var scan = PersistentFiles.Scan(root, fileExcludes, limitBytes, limitFiles, hash);
            if (scan.OverLimit)
            {
                var largest = PersistentFiles.Largest(root, fileExcludes, 10);
                var detail = new StringBuilder(512);
                detail.Append("{\"root\":").Append(Q(root));
                detail.Append(",\"limit\":").Append(Q(scan.LimitReason));
                detail.Append(",\"exclude\":"); AppendStrings(detail, fileExcludes);
                detail.Append(",\"largest\":[");
                for (int i = 0; i < largest.Count; i++)
                {
                    if (i > 0) detail.Append(',');
                    detail.Append("{\"path\":").Append(Q(largest[i].Path)).Append(",\"size\":").Append(largest[i].Size).Append('}');
                }
                detail.Append("]}");
                failure = ToolDispatchResult.FailWithDetail("persistent_data_too_large",
                    "persistentDataPath holds " + scan.LimitReason + " after excludes; nothing was written.",
                    detail.ToString(),
                    new[] { "Exclude caches / downloads with `exclude` globs (see `largest`), or raise max_files_mb (up to " +
                            PlayerStateDefaults.MaxMaxFilesMb + "). At most " + PlayerStateDefaults.MaxFiles + " files are captured." });
                return null;
            }
            cap.State.Files = scan.Files;
            cap.State.Dirs = scan.Dirs;
            cap.State.ExcludedFiles = scan.ExcludedFiles;
            cap.State.ExcludedDirs = scan.ExcludedDirs;
            cap.State.SkippedLinks = scan.SkippedLinks;
            cap.State.FilesBytes = scan.Bytes;
            return cap;
        }

        // ==================================================================
        // Snapshot lookup and refusals
        // ==================================================================

        private static PlayerStateSnapshotData LoadSnapshot(string company, string product, string name, out string dir,
            out ToolDispatchResult failure)
        {
            failure = null;
            dir = SnapshotStore.SnapshotDir(company, product, name);
            if (!Directory.Exists(dir)) { failure = NotFound(company, product, name); return null; }
            var doc = SnapshotStore.Load(dir, out var error);
            if (doc == null)
                failure = ToolDispatchResult.FailWithDetail("snapshot_unreadable",
                    "Snapshot '" + name + "' cannot be read: " + error + ".",
                    "{\"path\":" + Q(dir) + "}", new[] { "Delete it (action delete) and take a new snapshot." });
            return doc;
        }

        private static ToolDispatchResult NotFound(string company, string product, string name)
        {
            var productDir = SnapshotStore.ProductDir(company, product);
            var names = SnapshotStore.Names(productDir);
            var detail = new StringBuilder(256);
            detail.Append("{\"name\":").Append(Q(name)).Append(",\"company\":").Append(Q(company)).Append(",\"product\":").Append(Q(product));
            detail.Append(",\"available\":"); AppendStrings(detail, names);
            detail.Append('}');
            return ToolDispatchResult.FailWithDetail("snapshot_not_found",
                "No snapshot '" + name + "' for " + company + "/" + product + ".", detail.ToString(),
                new[] { "Call action list for the available names." });
        }

        private static bool SameProject(PlayerStateSnapshotData doc, string[] id) =>
            string.Equals(doc.company, id[0], StringComparison.Ordinal) && string.Equals(doc.product, id[1], StringComparison.Ordinal);

        private static ToolDispatchResult Mismatch(PlayerStateSnapshotData doc, string[] id, string action, string why)
        {
            var detail = "{\"snapshot\":{\"company\":" + Q(doc.company) + ",\"product\":" + Q(doc.product) + "}," +
                         "\"open_project\":{\"company\":" + Q(id[0]) + ",\"product\":" + Q(id[1]) + "}}";
            return ToolDispatchResult.FailWithDetail("snapshot_project_mismatch",
                "Snapshot '" + doc.name + "' belongs to " + doc.company + "/" + doc.product + ", not the open project " +
                id[0] + "/" + id[1] + ". " + why, detail,
                new[] { action == "restore"
                    ? "Open the project the snapshot was taken in, or take a snapshot of this project."
                    : "Pass `against` with another snapshot of the same project." });
        }

        private static ToolDispatchResult Invalid(string message) => ToolDispatchResult.Fail("invalid_arguments", message);

        // ==================================================================
        // JSON
        // ==================================================================

        private static void AppendSnapshotHeader(StringBuilder sb, PlayerStateSnapshotData doc, string dir)
        {
            sb.Append(",\"name\":").Append(Q(doc.name));
            sb.Append(",\"company\":").Append(Q(doc.company));
            sb.Append(",\"product\":").Append(Q(doc.product));
            sb.Append(",\"created_at\":").Append(Q(doc.createdUtc));
            sb.Append(",\"path\":").Append(Q(dir));
        }

        private static void AppendSide(StringBuilder sb, PlayerStateSnapshotData doc, string dir)
        {
            sb.Append("{\"snapshot\":").Append(Q(doc.name));
            sb.Append(",\"created_at\":").Append(Q(doc.createdUtc));
            sb.Append(",\"company\":").Append(Q(doc.company));
            sb.Append(",\"product\":").Append(Q(doc.product));
            sb.Append(",\"path\":").Append(Q(dir));
            sb.Append('}');
        }

        // Appends ",\"identical\":..,\"prefs\":{..},\"editor_prefs\":..,\"files\":{..}".
        // listEveryLoss: removed and changed lists ignore `limit` (restore shows
        // every value it deletes or overwrites); added lists stay bounded.
        internal static void AppendDiff(StringBuilder sb, StateDiff diff, int limit, bool listEveryLoss)
        {
            sb.Append(',');
            AppendDiffBody(sb, diff, limit, listEveryLoss);
        }

        internal static void AppendDiffBody(StringBuilder sb, StateDiff diff, int limit, bool listEveryLoss)
        {
            sb.Append("\"identical\":").Append(diff.Identical ? "true" : "false");
            sb.Append(",\"prefs\":"); AppendKeyDiff(sb, diff.Prefs, limit, listEveryLoss);
            sb.Append(",\"editor_prefs\":");
            if (diff.EditorPrefs == null) sb.Append("null");
            else
            {
                AppendKeyDiff(sb, diff.EditorPrefs, limit, listEveryLoss);
                sb.Length -= 1;
                sb.Append(",\"prefixes\":"); AppendStrings(sb, diff.EditorPrefsPrefixes);
                sb.Append('}');
            }
            sb.Append(",\"files\":"); AppendFileDiff(sb, diff.Files, limit, listEveryLoss);
        }

        private static void AppendKeyDiff(StringBuilder sb, KeyDiff d, int limit, bool listEveryLoss)
        {
            int truncated = 0;
            int lossLimit = listEveryLoss ? int.MaxValue : limit;
            sb.Append("{\"added\":[");
            truncated += AppendBounded(sb, d.Added, limit, AppendPref);
            sb.Append("],\"removed\":[");
            truncated += AppendBounded(sb, d.Removed, lossLimit, AppendPref);
            sb.Append("],\"changed\":[");
            truncated += AppendBounded(sb, d.Changed, lossLimit, (s, kv) =>
            {
                s.Append("{\"key\":").Append(Q(kv.Key.Key));
                s.Append(",\"from\":{"); AppendTypedValue(s, kv.Key); s.Append('}');
                s.Append(",\"to\":{"); AppendTypedValue(s, kv.Value); s.Append('}');
                s.Append('}');
            });
            sb.Append("],\"counts\":{\"added\":").Append(d.Added.Count);
            sb.Append(",\"removed\":").Append(d.Removed.Count);
            sb.Append(",\"changed\":").Append(d.Changed.Count);
            sb.Append(",\"unchanged\":").Append(d.Unchanged).Append('}');
            sb.Append(",\"truncated\":").Append(truncated).Append('}');
        }

        private static void AppendFileDiff(StringBuilder sb, FileDiff d, int limit, bool listEveryLoss)
        {
            int truncated = 0;
            int lossLimit = listEveryLoss ? int.MaxValue : limit;
            sb.Append("{\"added\":[");
            truncated += AppendBounded(sb, d.Added, limit, AppendFile);
            sb.Append("],\"removed\":[");
            truncated += AppendBounded(sb, d.Removed, lossLimit, AppendFile);
            sb.Append("],\"changed\":[");
            truncated += AppendBounded(sb, d.Changed, lossLimit, (s, kv) =>
            {
                s.Append("{\"path\":").Append(Q(kv.Key.Path));
                s.Append(",\"from\":{\"size\":").Append(kv.Key.Size).Append(",\"sha256\":").Append(Q(kv.Key.Sha256)).Append('}');
                s.Append(",\"to\":{\"size\":").Append(kv.Value.Size).Append(",\"sha256\":").Append(Q(kv.Value.Sha256)).Append('}');
                s.Append('}');
            });
            sb.Append("],\"dirs_added\":"); AppendStrings(sb, d.DirsAdded);
            sb.Append(",\"dirs_removed\":"); AppendStrings(sb, d.DirsRemoved);
            sb.Append(",\"counts\":{\"added\":").Append(d.Added.Count);
            sb.Append(",\"removed\":").Append(d.Removed.Count);
            sb.Append(",\"changed\":").Append(d.Changed.Count);
            sb.Append(",\"unchanged\":").Append(d.Unchanged).Append('}');
            sb.Append(",\"truncated\":").Append(truncated).Append('}');
        }

        private static int AppendBounded<T>(StringBuilder sb, IList<T> items, int limit, Action<StringBuilder, T> append)
        {
            int n = Math.Min(items.Count, limit);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                append(sb, items[i]);
            }
            return items.Count - n;
        }

        private static void AppendPref(StringBuilder sb, PrefEntry e)
        {
            sb.Append("{\"key\":").Append(Q(e.Key)).Append(',');
            AppendTypedValue(sb, e);
            sb.Append('}');
        }

        private static void AppendFile(StringBuilder sb, FileEntry f)
        {
            sb.Append("{\"path\":").Append(Q(f.Path)).Append(",\"size\":").Append(f.Size);
            sb.Append(",\"sha256\":").Append(Q(f.Sha256)).Append('}');
        }

        // "type":..,"value":.. — strings longer than MaxShownString are shown as
        // length + sha256 instead of the value.
        internal static void AppendTypedValue(StringBuilder sb, PrefEntry e)
        {
            sb.Append("\"type\":").Append(Q(PrefEntry.TypeName(e.Type)));
            switch (e.Type)
            {
                case PrefType.Int:
                    sb.Append(",\"value\":").Append(e.IntValue.ToString(CultureInfo.InvariantCulture));
                    break;
                case PrefType.Float:
                    sb.Append(",\"value\":").Append(FloatJson(e.FloatValue));
                    break;
                default:
                    var s = e.StringValue ?? "";
                    if (s.Length <= MaxShownString) sb.Append(",\"value\":").Append(Q(s));
                    else sb.Append(",\"length\":").Append(s.Length).Append(",\"sha256\":").Append(Q(PersistentFiles.Sha256Text(s)));
                    break;
            }
        }

        internal static string FloatJson(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return Q(f.ToString(CultureInfo.InvariantCulture));
            return f.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void AppendStrings(StringBuilder sb, IEnumerable<string> items)
        {
            sb.Append('[');
            bool first = true;
            if (items != null)
                foreach (var s in items)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Q(s));
                }
            sb.Append(']');
        }

        private static string Q(string s) => BridgeJson.EscapeString(s);

        private static string[] Merge(string[] defaults, string[] extra)
        {
            var list = new List<string>(defaults);
            if (extra != null)
                foreach (var e in extra)
                    if (!string.IsNullOrWhiteSpace(e) && !list.Contains(e)) list.Add(e.Trim());
            return list.ToArray();
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort: a leftover temp folder is ignored by list (not a valid name) */ }
        }
    }
}
