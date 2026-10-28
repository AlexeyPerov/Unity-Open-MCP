// Playtest — player-state model for unity_open_mcp_player_state: the captured
// state (PlayerPrefs, opt-in EditorPrefs, persistentDataPath files), the
// snapshot store outside the project, the persistentDataPath walk (globs, size
// cap) and the diff between two states.
//
// Store layout (one folder per snapshot):
//   <app data>/unity-open-mcp/player-state/<company>/<product>/<name>/
//     snapshot.json   — metadata, typed prefs, file manifest (size + sha256)
//     files/          — copy of the captured persistentDataPath files
// <app data> = ~/Library/Application Support (macOS), %LOCALAPPDATA% (Windows),
// $XDG_DATA_HOME or ~/.local/share (Linux). Nothing is written inside the
// project.
//
// Excluded keys and paths sit outside the sandbox: they are not captured, not
// diffed and never touched by restore. Defaults cover Unity's own per-session
// bookkeeping (keys `unity.player_session*`, `unity_connect.*`; the `Unity/`
// folder) and the Test Runner's TestResults.xml.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    // ---------------------------------------------------------------------
    // On-disk snapshot document (JsonUtility; floats stored as their bits so
    // a round trip is exact).
    // ---------------------------------------------------------------------

    [Serializable]
    public class PlayerStatePrefRecord
    {
        public string key;
        public string type;
        public int i;
        public int f;
        public string s;
    }

    [Serializable]
    public class PlayerStateFileRecord
    {
        public string path;
        public long size;
        public string sha256;
    }

    [Serializable]
    public class PlayerStateSnapshotData
    {
        public int format;
        public string name;
        public string company;
        public string product;
        public string createdUtc;
        public string unityVersion;
        public string platform;
        public string projectPath;
        public string persistentDataPath;
        public string prefsSource;
        public string[] keyExcludes;
        public PlayerStatePrefRecord[] prefs;
        public string[] unsupportedPrefs;
        public bool editorPrefsCaptured;
        public string[] editorPrefsPrefixes;
        public PlayerStatePrefRecord[] editorPrefs;
        public string[] unsupportedEditorPrefs;
        public string[] fileExcludes;
        public PlayerStateFileRecord[] files;
        public string[] dirs;
        public long filesBytes;
    }

    internal sealed class FileEntry
    {
        public string Path;
        public long Size;
        public string Sha256;

        public bool SameContent(FileEntry other) =>
            other != null && Size == other.Size && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
    }

    // A captured or current player state. EditorPrefs == null means "not captured".
    internal sealed class PlayerState
    {
        public List<PrefEntry> Prefs = new List<PrefEntry>();
        public List<string> UnsupportedPrefs = new List<string>();
        public int ExcludedKeys;
        public string[] EditorPrefsPrefixes;
        public List<PrefEntry> EditorPrefs;
        public List<string> UnsupportedEditorPrefs = new List<string>();
        public List<FileEntry> Files = new List<FileEntry>();
        public List<string> Dirs = new List<string>();
        public int ExcludedFiles;
        public List<string> ExcludedDirs = new List<string>();
        public List<string> SkippedLinks = new List<string>();
        public long FilesBytes;
    }

    internal static class PlayerStateDefaults
    {
        internal const int Format = 1;
        internal static readonly string[] KeyExcludes = { "unity.player_session*", "unity_connect.*" };
        internal static readonly string[] FileExcludes = { "Unity/**", "TestResults.xml" };
        internal const int DefaultMaxFilesMb = 64;
        internal const int MaxMaxFilesMb = 1024;
        internal const int MaxFiles = 5000;
    }

    // ---------------------------------------------------------------------
    // Globs
    // ---------------------------------------------------------------------

    internal static class Glob
    {
        // Path globs over '/'-separated relative paths: `**` any depth, `*` and
        // `?` within one segment. A pattern without '/' matches the file or
        // folder name at any depth. Case-insensitive.
        internal static bool IsPathMatch(string pattern, string relPath)
        {
            if (string.IsNullOrEmpty(pattern) || relPath == null) return false;
            var p = Normalize(pattern);
            if (p.Length == 0) return false;
            if (p.IndexOf('/') < 0)
            {
                var name = relPath.TrimEnd('/');
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                return PathRegex(p).IsMatch(name);
            }
            return PathRegex(p).IsMatch(relPath);
        }

        internal static bool IsPathExcluded(IEnumerable<string> patterns, string relPath, bool isDirectory)
        {
            if (patterns == null) return false;
            foreach (var pattern in patterns)
            {
                if (IsPathMatch(pattern, relPath)) return true;
                if (isDirectory && IsPathMatch(pattern, relPath + "/")) return true;
            }
            return false;
        }

        // Key wildcards: `*` any run of characters, `?` one character. Ordinal.
        internal static bool IsKeyMatch(string pattern, string key)
        {
            if (string.IsNullOrEmpty(pattern) || key == null) return false;
            var sb = new StringBuilder("^");
            foreach (var c in pattern)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return Regex.IsMatch(key, sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        internal static bool IsKeyExcluded(IEnumerable<string> patterns, string key)
        {
            if (patterns == null) return false;
            foreach (var p in patterns)
                if (IsKeyMatch(p, key)) return true;
            return false;
        }

        private static string Normalize(string pattern)
        {
            var p = pattern.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            return p.TrimStart('/');
        }

        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>();

        private static Regex PathRegex(string p)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(p, out var cached)) return cached;
                var sb = new StringBuilder("^");
                for (int i = 0; i < p.Length; i++)
                {
                    char c = p[i];
                    if (c == '*')
                    {
                        bool doubleStar = i + 1 < p.Length && p[i + 1] == '*';
                        if (doubleStar)
                        {
                            i++;
                            if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                            else sb.Append(".*");
                        }
                        else sb.Append("[^/]*");
                    }
                    else if (c == '?') sb.Append("[^/]");
                    else sb.Append(Regex.Escape(c.ToString()));
                }
                sb.Append('$');
                var rx = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Cache[p] = rx;
                return rx;
            }
        }
    }

    // ---------------------------------------------------------------------
    // persistentDataPath walk
    // ---------------------------------------------------------------------

    internal sealed class FileScan
    {
        public readonly List<FileEntry> Files = new List<FileEntry>();
        public readonly List<string> Dirs = new List<string>();
        public readonly List<string> ExcludedDirs = new List<string>();
        public readonly List<string> SkippedLinks = new List<string>();
        public int ExcludedFiles;
        public long Bytes;
        public bool OverLimit;
        public string LimitReason;
    }

    internal static class PersistentFiles
    {
        // Walk `root` (missing root = empty), skipping excluded paths (excluded
        // folders are not descended into) and symbolic links. With `limitBytes` /
        // `limitFiles` > 0 the walk stops at the first overflow (OverLimit).
        internal static FileScan Scan(string root, IList<string> excludes, long limitBytes, int limitFiles, bool hash)
        {
            var scan = new FileScan();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return scan;
            var stack = new Stack<string>();
            stack.Push("");
            while (stack.Count > 0)
            {
                var rel = stack.Pop();
                var abs = rel.Length == 0 ? root : Path.Combine(root, rel);
                string[] dirs, files;
                try
                {
                    dirs = Directory.GetDirectories(abs);
                    files = Directory.GetFiles(abs);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    var childRel = Join(rel, Path.GetFileName(f));
                    if (Glob.IsPathExcluded(excludes, childRel, false)) { scan.ExcludedFiles++; continue; }
                    FileInfo info;
                    try { info = new FileInfo(f); }
                    catch { continue; }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { scan.SkippedLinks.Add(childRel); continue; }
                    scan.Bytes += info.Length;
                    if (limitFiles > 0 && scan.Files.Count + 1 > limitFiles)
                    {
                        scan.OverLimit = true;
                        scan.LimitReason = "more than " + limitFiles + " files";
                        return scan;
                    }
                    if (limitBytes > 0 && scan.Bytes > limitBytes)
                    {
                        scan.OverLimit = true;
                        scan.LimitReason = "more than " + (limitBytes / (1024 * 1024)) + " MB";
                        return scan;
                    }
                    scan.Files.Add(new FileEntry { Path = childRel, Size = info.Length, Sha256 = hash ? Sha256File(f) : null });
                }
                Array.Sort(dirs, StringComparer.Ordinal);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    var childRel = Join(rel, Path.GetFileName(dirs[i]));
                    if (Glob.IsPathExcluded(excludes, childRel, true)) { scan.ExcludedDirs.Add(childRel); continue; }
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(dirs[i]); }
                    catch { continue; }
                    if ((attrs & FileAttributes.ReparsePoint) != 0) { scan.SkippedLinks.Add(childRel + "/"); continue; }
                    scan.Dirs.Add(childRel);
                    stack.Push(childRel);
                }
            }
            scan.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            scan.Dirs.Sort(StringComparer.Ordinal);
            scan.ExcludedDirs.Sort(StringComparer.Ordinal);
            return scan;
        }

        // The largest files and top-level folders of `root` (excludes applied),
        // to help a caller choose excludes after a size refusal.
        internal static List<FileEntry> Largest(string root, IList<string> excludes, int count)
        {
            var all = Scan(root, excludes, 0, 0, hash: false);
            return all.Files.OrderByDescending(f => f.Size).Take(count).ToList();
        }

        internal static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Hex(sha.ComputeHash(stream));
        }

        internal static string Sha256Text(string s)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static string Join(string rel, string name) => rel.Length == 0 ? name : rel + "/" + name;

        // A manifest path restore may write: relative, '/'-separated, no empty,
        // '.' or '..' segment, so it always resolves under the root.
        internal static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.IndexOf('\\') >= 0 || rel.IndexOf(':') >= 0 || Path.IsPathRooted(rel))
                return false;
            foreach (var seg in rel.Split('/'))
                if (seg.Length == 0 || seg == "." || seg == "..") return false;
            return true;
        }

        // True when `rel` or one of its folders under `root` is a symbolic link
        // (reparse point): writing or deleting there would leave `root`.
        internal static bool PassesThroughLink(string root, string rel)
        {
            var path = root;
            foreach (var seg in rel.Split('/'))
            {
                path = Path.Combine(path, seg);
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }

        internal static string ToNative(string root, string rel) =>
            Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    // ---------------------------------------------------------------------
    // Diff
    // ---------------------------------------------------------------------

    internal sealed class KeyDiff
    {
        public readonly List<PrefEntry> Added = new List<PrefEntry>();
        public readonly List<PrefEntry> Removed = new List<PrefEntry>();
        public readonly List<KeyValuePair<PrefEntry, PrefEntry>> Changed = new List<KeyValuePair<PrefEntry, PrefEntry>>();
        public int Unchanged;
        public bool Empty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

        internal static KeyDiff Compute(IEnumerable<PrefEntry> from, IEnumerable<PrefEntry> to)
        {
            var d = new KeyDiff();
            var a = ToMap(from);
            var b = ToMap(to);
            foreach (var kv in a.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!b.TryGetValue(kv.Key, out var other)) d.Removed.Add(kv.Value);
                else if (!kv.Value.SameValue(other)) d.Changed.Add(new KeyValuePair<PrefEntry, PrefEntry>(kv.Value, other));
                else d.Unchanged++;
            }
            foreach (var kv in b.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (!a.ContainsKey(kv.Key)) d.Added.Add(kv.Value);
            return d;
        }

        private static Dictionary<string, PrefEntry> ToMap(IEnumerable<PrefEntry> entries)
        {
            var map = new Dictionary<string, PrefEntry>(StringComparer.Ordinal);
            if (entries != null)
                foreach (var e in entries) map[e.Key ?? ""] = e;
            return map;
        }
    }

    internal sealed class FileDiff
    {
        public readonly List<FileEntry> Added = new List<FileEntry>();
        public readonly List<FileEntry> Removed = new List<FileEntry>();
        public readonly List<KeyValuePair<FileEntry, FileEntry>> Changed = new List<KeyValuePair<FileEntry, FileEntry>>();
        public readonly List<string> DirsAdded = new List<string>();
        public readonly List<string> DirsRemoved = new List<string>();
        public int Unchanged;
        public bool Empty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0
                             && DirsAdded.Count == 0 && DirsRemoved.Count == 0;

        internal static FileDiff Compute(PlayerState from, PlayerState to)
        {
            var d = new FileDiff();
            var a = from.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var b = to.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var f in from.Files)
            {
                if (!b.TryGetValue(f.Path, out var other)) d.Removed.Add(f);
                else if (!f.SameContent(other)) d.Changed.Add(new KeyValuePair<FileEntry, FileEntry>(f, other));
                else d.Unchanged++;
            }
            foreach (var f in to.Files)
                if (!a.ContainsKey(f.Path)) d.Added.Add(f);
            var da = new HashSet<string>(from.Dirs, StringComparer.Ordinal);
            var db = new HashSet<string>(to.Dirs, StringComparer.Ordinal);
            foreach (var dir in to.Dirs) if (!da.Contains(dir)) d.DirsAdded.Add(dir);
            foreach (var dir in from.Dirs) if (!db.Contains(dir)) d.DirsRemoved.Add(dir);
            return d;
        }
    }

    // `from` → `to`: Added = only in `to`, Removed = only in `from`, Changed =
    // both with different values. EditorPrefs are compared only when either
    // side captured them (keys under the union of both sides' prefixes).
    internal sealed class StateDiff
    {
        public KeyDiff Prefs;
        public KeyDiff EditorPrefs;
        public string[] EditorPrefsPrefixes;
        public FileDiff Files;
        public bool Identical => Prefs.Empty && (EditorPrefs == null || EditorPrefs.Empty) && Files.Empty;

        internal static StateDiff Compute(PlayerState from, PlayerState to)
        {
            var d = new StateDiff
            {
                Prefs = KeyDiff.Compute(from.Prefs, to.Prefs),
                Files = FileDiff.Compute(from, to),
            };
            if (from.EditorPrefs != null || to.EditorPrefs != null)
            {
                var prefixes = (from.EditorPrefsPrefixes ?? Array.Empty<string>())
                    .Concat(to.EditorPrefsPrefixes ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).ToArray();
                d.EditorPrefsPrefixes = prefixes;
                d.EditorPrefs = KeyDiff.Compute(
                    (from.EditorPrefs ?? new List<PrefEntry>()).Where(e => PrefsStore.HasPrefix(e.Key, prefixes)),
                    (to.EditorPrefs ?? new List<PrefEntry>()).Where(e => PrefsStore.HasPrefix(e.Key, prefixes)));
            }
            return d;
        }
    }

    // ---------------------------------------------------------------------
    // Snapshot store
    // ---------------------------------------------------------------------

    internal static class SnapshotStore
    {
        internal const string MetaFile = "snapshot.json";
        internal const string FilesDir = "files";

        // Test seam: a temporary root instead of the platform app-data folder.
        internal static string RootOverride;

        private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        internal static bool IsValidName(string name) =>
            name != null && NamePattern.IsMatch(name) && !name.EndsWith(".", StringComparison.Ordinal) && name.IndexOf("..", StringComparison.Ordinal) < 0;

        internal static string Root()
        {
            if (!string.IsNullOrEmpty(RootOverride)) return RootOverride;
            return Root(PrefsStore.Current, Environment.GetEnvironmentVariable,
                Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        internal static string Root(PrefsPlatform platform, Func<string, string> env, string home)
        {
            string appData;
            switch (platform)
            {
                case PrefsPlatform.MacOS:
                    appData = Path.Combine(home, "Library", "Application Support");
                    break;
                case PrefsPlatform.Windows:
                    appData = env("LOCALAPPDATA");
                    if (string.IsNullOrEmpty(appData)) appData = Path.Combine(home, "AppData", "Local");
                    break;
                default:
                    appData = env("XDG_DATA_HOME");
                    if (string.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData))
                        appData = Path.Combine(home, ".local", "share");
                    break;
            }
            return Path.Combine(appData, "unity-open-mcp", "player-state");
        }

        // A company / product name as one path segment.
        internal static string SafeSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(invalid.Contains(c) || c < 32 ? '_' : c);
            var t = sb.ToString().Trim().TrimEnd('.');
            return t.Length == 0 || t == "." || t == ".." ? "_" : t;
        }

        internal static string ProductDir(string company, string product) =>
            Path.Combine(Root(), SafeSegment(company), SafeSegment(product));

        internal static string SnapshotDir(string company, string product, string name) =>
            Path.Combine(ProductDir(company, product), name);

        internal static PlayerStateSnapshotData Load(string dir, out string error)
        {
            error = null;
            var meta = Path.Combine(dir, MetaFile);
            if (!File.Exists(meta)) { error = "missing " + MetaFile; return null; }
            try
            {
                var doc = JsonUtility.FromJson<PlayerStateSnapshotData>(File.ReadAllText(meta, Encoding.UTF8));
                if (doc == null || doc.format != PlayerStateDefaults.Format)
                {
                    error = "unknown snapshot format " + (doc == null ? "(empty)" : doc.format.ToString());
                    return null;
                }
                return doc;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        internal static void WriteMeta(string dir, PlayerStateSnapshotData doc) =>
            File.WriteAllText(Path.Combine(dir, MetaFile), JsonUtility.ToJson(doc, true), new UTF8Encoding(false));

        // Snapshot names in a product folder (folders with a readable snapshot.json
        // first; unreadable ones are reported by the caller).
        internal static List<string> Names(string productDir)
        {
            var names = new List<string>();
            if (!Directory.Exists(productDir)) return names;
            foreach (var d in Directory.GetDirectories(productDir))
            {
                var n = Path.GetFileName(d);
                if (IsValidName(n)) names.Add(n);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // ---- conversions --------------------------------------------------

        internal static PlayerStatePrefRecord ToRecord(PrefEntry e) => new PlayerStatePrefRecord
        {
            key = e.Key,
            type = PrefEntry.TypeName(e.Type),
            i = e.Type == PrefType.Int ? e.IntValue : 0,
            f = e.Type == PrefType.Float ? PrefEntry.FloatBits(e.FloatValue) : 0,
            s = e.Type == PrefType.String ? e.StringValue : null,
        };

        internal static PrefEntry FromRecord(PlayerStatePrefRecord r)
        {
            PrefEntry.TryParseType(r.type, out var t);
            switch (t)
            {
                case PrefType.Int: return PrefEntry.OfInt(r.key, r.i);
                case PrefType.Float: return PrefEntry.OfFloat(r.key, PrefEntry.FromBits(r.f));
                default: return PrefEntry.OfString(r.key, r.s);
            }
        }

        internal static PlayerState ToState(PlayerStateSnapshotData doc)
        {
            var s = new PlayerState
            {
                Prefs = (doc.prefs ?? Array.Empty<PlayerStatePrefRecord>()).Select(FromRecord).ToList(),
                UnsupportedPrefs = (doc.unsupportedPrefs ?? Array.Empty<string>()).ToList(),
                Files = (doc.files ?? Array.Empty<PlayerStateFileRecord>())
                    .Select(f => new FileEntry { Path = f.path, Size = f.size, Sha256 = f.sha256 }).ToList(),
                Dirs = (doc.dirs ?? Array.Empty<string>()).ToList(),
                FilesBytes = doc.filesBytes,
            };
            if (doc.editorPrefsCaptured)
            {
                s.EditorPrefsPrefixes = doc.editorPrefsPrefixes ?? Array.Empty<string>();
                s.EditorPrefs = (doc.editorPrefs ?? Array.Empty<PlayerStatePrefRecord>()).Select(FromRecord).ToList();
                s.UnsupportedEditorPrefs = (doc.unsupportedEditorPrefs ?? Array.Empty<string>()).ToList();
            }
            return s;
        }
    }
}
