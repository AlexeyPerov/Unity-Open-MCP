// Player-state sandbox (unity_open_mcp_player_state): the platform store
// decoders (macOS plist XML, Windows registry names / raw values, Linux prefs
// XML) on synthetic data, globs, the diff, and snapshot → diff → restore end to
// end against an in-memory PlayerPrefs / EditorPrefs fake (an in-memory cache
// plus a store written on flush, like the Editor) and temporary folders for
// persistentDataPath and the snapshot store. The real PlayerPrefs, EditorPrefs
// and app-data folder are never touched here; the live store is covered by the
// manual checklist.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityOpenMcpBridge.Playtest;

namespace UnityOpenMcpBridge.Tests.Playtest
{
    public class PlayerStateTests
    {
        // ---- fakes --------------------------------------------------------

        // Unity-like: Set/DeleteAll change the in-memory cache, Flush writes the store.
        private sealed class FakePlayerPrefs : IPlayerPrefsBackend
        {
            public readonly Dictionary<string, PrefEntry> Memory = new Dictionary<string, PrefEntry>(StringComparer.Ordinal);
            public Dictionary<string, PrefEntry> Store = new Dictionary<string, PrefEntry>(StringComparer.Ordinal);
            // Keys the store holds with a type PlayerPrefs cannot represent.
            public readonly List<string> Unsupported = new List<string>();
            public int DeleteAllCalls;
            public void Flush() => Store = new Dictionary<string, PrefEntry>(Memory, StringComparer.Ordinal);
            public PrefsReadResult Read(string company, string product)
            {
                var r = new PrefsReadResult { Source = "fake store " + company + "/" + product };
                r.Entries.AddRange(Store.Values);
                r.Unsupported.AddRange(Unsupported);
                return r;
            }
            public void DeleteAll() { Memory.Clear(); DeleteAllCalls++; }
            public void Set(PrefEntry e) => Memory[e.Key] = e;
        }

        private sealed class FakeEditorPrefs : IEditorPrefsBackend
        {
            public readonly Dictionary<string, PrefEntry> Memory = new Dictionary<string, PrefEntry>(StringComparer.Ordinal);
            public Dictionary<string, PrefEntry> Store = new Dictionary<string, PrefEntry>(StringComparer.Ordinal);
            public bool Flushable = true;
            public int Reads;
            public bool CanFlush => Flushable;
            public void Flush() => Store = new Dictionary<string, PrefEntry>(Memory, StringComparer.Ordinal);
            public PrefsReadResult Read(IList<string> prefixes)
            {
                Reads++;
                var all = new PrefsReadResult { Source = "fake editor store" };
                all.Entries.AddRange(Store.Values);
                return PrefsStore.FilterByPrefix(all, prefixes);
            }
            public void Delete(string key) => Memory.Remove(key);
            public void Set(PrefEntry e) => Memory[e.Key] = e;
        }

        private FakePlayerPrefs prefs;
        private FakeEditorPrefs editorPrefs;
        private string temp, persistent, store;
        private bool playing;
        private string[] identity;

        [SetUp]
        public void SetUp()
        {
            temp = Path.Combine(Path.GetTempPath(), "uom-player-state-" + Guid.NewGuid().ToString("N"));
            persistent = Path.Combine(temp, "persistent");
            store = Path.Combine(temp, "store");
            Directory.CreateDirectory(persistent);
            prefs = new FakePlayerPrefs();
            editorPrefs = new FakeEditorPrefs();
            playing = false;
            identity = new[] { "TestCo", "Test Product" };
            PlayerStateTools.PlayerPrefsOverride = prefs;
            PlayerStateTools.EditorPrefsOverride = editorPrefs;
            PlayerStateTools.PersistentDataPathOverride = () => persistent;
            PlayerStateTools.IdentityOverride = () => identity;
            PlayerStateTools.PlayingOverride = () => playing;
            SnapshotStore.RootOverride = store;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerStateTools.PlayerPrefsOverride = null;
            PlayerStateTools.EditorPrefsOverride = null;
            PlayerStateTools.PersistentDataPathOverride = null;
            PlayerStateTools.IdentityOverride = null;
            PlayerStateTools.PlayingOverride = null;
            SnapshotStore.RootOverride = null;
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }

        private void WriteFile(string rel, string text)
        {
            var path = Path.Combine(persistent, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private bool FileExists(string rel) => File.Exists(Path.Combine(persistent, rel.Replace('/', Path.DirectorySeparatorChar)));
        private string ReadFile(string rel) => File.ReadAllText(Path.Combine(persistent, rel.Replace('/', Path.DirectorySeparatorChar)));

        private static ToolDispatchResult Ok(ToolDispatchResult r)
        {
            Assert.IsTrue(r.Success, r.ErrorCode + ": " + r.ErrorMessage);
            return r;
        }

        private static void Refused(ToolDispatchResult r, string code)
        {
            Assert.IsFalse(r.Success, "expected " + code + ", got success: " + r.Output);
            Assert.AreEqual(code, r.ErrorCode, r.ErrorMessage);
        }

        // Seed a "developer save": int / float / string prefs, a Unity session
        // key (excluded by default) and files, including an excluded folder.
        private void SeedDeveloperSave()
        {
            prefs.Set(PrefEntry.OfInt("coins", 100));
            prefs.Set(PrefEntry.OfFloat("volume", 0.25f));
            prefs.Set(PrefEntry.OfString("last_level", "level_1"));
            prefs.Set(PrefEntry.OfString("unity.player_session_count", "7"));
            WriteFile("save.json", "{\"coins\":100}");
            WriteFile("slots/slot1.dat", "one");
            WriteFile("Unity/analytics.bin", "engine");
            Directory.CreateDirectory(Path.Combine(persistent, "emptydir"));
        }

        // A session: changes each pref type, writes a new file, edits and deletes
        // others, adds a folder, and Unity bumps its session key.
        private void PlaySession()
        {
            prefs.Set(PrefEntry.OfInt("coins", 150));
            prefs.Set(PrefEntry.OfFloat("volume", 0.75f));
            prefs.Set(PrefEntry.OfString("last_level", "level_3"));
            prefs.Set(PrefEntry.OfString("first_clear", "paid"));
            prefs.Set(PrefEntry.OfString("unity.player_session_count", "8"));
            WriteFile("save.json", "{\"coins\":150}");
            File.Delete(Path.Combine(persistent, "slots", "slot1.dat"));
            WriteFile("new/run.json", "{}");
            WriteFile("Unity/analytics.bin", "engine2");
        }

        // ==================================================================
        // Store decoders
        // ==================================================================

        [Test]
        public void Plist_TypedValuesAndUnsupportedKinds()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict>" +
                "<key>coins</key><integer>-42</integer>" +
                "<key>volume</key><real>0.75</real>" +
                "<key>tenth</key><real>0.10000000149011612</real>" +
                "<key>nan</key><real>nan</real>" +
                "<key>name</key><string>Ünïcode &amp; &lt;tags&gt;</string>" +
                "<key>empty</key><string></string>" +
                "<key></key><integer>3</integer>" +
                "<key>wide</key><integer>9999999999</integer>" +
                "<key>blob</key><data>AAEC</data>" +
                "<key>flag</key><true/>" +
                "<key>nested</key><dict><key>a</key><integer>1</integer></dict>" +
                "</dict></plist>";
            var r = PrefsStore.ParsePlistXml(xml);
            Assert.IsNull(r.Error);
            var map = r.Entries.ToDictionary(e => e.Key);
            Assert.AreEqual(PrefType.Int, map["coins"].Type);
            Assert.AreEqual(-42, map["coins"].IntValue);
            Assert.AreEqual(0.75f, map["volume"].FloatValue);
            Assert.AreEqual(0.1f, map["tenth"].FloatValue, "a float stored as double round-trips exactly");
            Assert.IsTrue(float.IsNaN(map["nan"].FloatValue));
            Assert.AreEqual("Ünïcode & <tags>", map["name"].StringValue);
            Assert.AreEqual("", map["empty"].StringValue);
            Assert.AreEqual(3, map[""].IntValue);
            CollectionAssert.AreEquivalent(new[] { "wide", "blob", "flag", "nested" }, r.Unsupported);
            Assert.IsNotNull(PrefsStore.ParsePlistXml("<plist><array/></plist>").Error);
        }

        [Test]
        public void Registry_HashIsDjb2XorOverUtf8()
        {
            Assert.AreEqual(1669003810u, PrefsStore.UnityKeyHash("UnityGraphicsQuality"));
            Assert.AreEqual(182942802u, PrefsStore.UnityKeyHash("Screenmanager Resolution Width"));
            Assert.AreEqual(2627697771u, PrefsStore.UnityKeyHash("Screenmanager Resolution Height"));
            Assert.AreEqual(1351336811u, PrefsStore.UnityKeyHash("unity.player_sessionid"));
            Assert.AreEqual(5381u, PrefsStore.UnityKeyHash(""));

            Assert.IsTrue(PrefsStore.TrySplitValueName("UnityGraphicsQuality_h1669003810", out var key));
            Assert.AreEqual("UnityGraphicsQuality", key);
            // A key that itself contains "_h" splits at the last suffix.
            Assert.IsTrue(PrefsStore.TrySplitValueName("my_hero_h" + PrefsStore.UnityKeyHash("my_hero"), out key));
            Assert.AreEqual("my_hero", key);
            Assert.IsFalse(PrefsStore.TrySplitValueName("UnityGraphicsQuality_h1", out _), "wrong hash");
            Assert.IsFalse(PrefsStore.TrySplitValueName("UnityGraphicsQuality", out _), "no suffix");
            Assert.IsFalse(PrefsStore.TrySplitValueName("x_habc", out _), "non-numeric suffix");
        }

        private static string Name(string key) => key + "_h" + PrefsStore.UnityKeyHash(key);

        [Test]
        public void Registry_DecodesUnityTypeEncodings()
        {
            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("coins"), PrefsStore.RegDword, BitConverter.GetBytes(-5), 4, out var e, out var unsupported));
            Assert.IsFalse(unsupported);
            Assert.AreEqual(PrefType.Int, e.Type);
            Assert.AreEqual(-5, e.IntValue);
            Assert.AreEqual("coins", e.Key);

            // A float is tagged REG_DWORD but carries an 8-byte double.
            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("volume"), PrefsStore.RegDword, BitConverter.GetBytes((double)0.1f), 8, out e, out _));
            Assert.AreEqual(PrefType.Float, e.Type);
            Assert.AreEqual(0.1f, e.FloatValue);
            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("q"), PrefsStore.RegQword, BitConverter.GetBytes(2.5), 8, out e, out _));
            Assert.AreEqual(2.5f, e.FloatValue);

            // Strings are REG_BINARY UTF-8 with a trailing NUL; buffers may be larger.
            var bytes = Encoding.UTF8.GetBytes("Привет\0");
            var buffer = new byte[64];
            Array.Copy(bytes, buffer, bytes.Length);
            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("name"), PrefsStore.RegBinary, buffer, bytes.Length, out e, out _));
            Assert.AreEqual(PrefType.String, e.Type);
            Assert.AreEqual("Привет", e.StringValue);
            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("empty"), PrefsStore.RegBinary, new byte[] { 0 }, 1, out e, out _));
            Assert.AreEqual("", e.StringValue);

            Assert.IsTrue(PrefsStore.TryDecodeRegistryValue(Name("odd"), 7u, new byte[2], 2, out e, out unsupported));
            Assert.IsTrue(unsupported);
            Assert.AreEqual("odd", e.Key);
            Assert.IsFalse(PrefsStore.TryDecodeRegistryValue("NotAUnityValue", PrefsStore.RegDword, new byte[4], 4, out _, out _));
        }

        [Test]
        public void Linux_PrefsXmlWithBase64Strings()
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("level_3 ✓"));
            var xml = "<unity_prefs version_major=\"1\" version_minor=\"1\">" +
                      "<pref name=\"coins\" type=\"int\">150</pref>" +
                      "<pref name=\"volume\" type=\"float\">0.75</pref>" +
                      "<pref name=\"last_level\" type=\"string\">" + b64 + "</pref>" +
                      "<pref name=\"plain\" type=\"string\">not base64!</pref>" +
                      "<pref name=\"odd\" type=\"blob\">x</pref>" +
                      "</unity_prefs>";
            var r = PrefsStore.ParseLinuxPrefsXml(xml);
            var map = r.Entries.ToDictionary(e => e.Key);
            Assert.AreEqual(150, map["coins"].IntValue);
            Assert.AreEqual(0.75f, map["volume"].FloatValue);
            Assert.AreEqual("level_3 ✓", map["last_level"].StringValue);
            Assert.AreEqual("not base64!", map["plain"].StringValue, "non-base64 text is kept verbatim");
            CollectionAssert.AreEqual(new[] { "odd" }, r.Unsupported);
        }

        [Test]
        public void Locations_PerPlatform()
        {
            Assert.AreEqual("unity.TestCo.Test Product", PrefsStore.MacPlayerPrefsDomain("TestCo", "Test Product"));
            Assert.AreEqual(@"Software\Unity\UnityEditor\TestCo\Test Product", PrefsStore.WindowsPlayerPrefsKey("TestCo", "Test Product"));
            StringAssert.EndsWith(Path.Combine(".config", "unity3d", "TestCo", "Test Product", "prefs"),
                PrefsStore.LinuxPlayerPrefsFile("/home/u", "TestCo", "Test Product"));
            Func<string, string> env = k => k == "LOCALAPPDATA" ? @"C:\Users\u\AppData\Local" : k == "XDG_DATA_HOME" ? "/xdg" : null;
            Assert.AreEqual(Path.Combine("/Users/u", "Library", "Application Support", "unity-open-mcp", "player-state"),
                SnapshotStore.Root(PrefsPlatform.MacOS, env, "/Users/u"));
            Assert.AreEqual(Path.Combine(@"C:\Users\u\AppData\Local", "unity-open-mcp", "player-state"),
                SnapshotStore.Root(PrefsPlatform.Windows, env, "/Users/u"));
            Assert.AreEqual(Path.Combine("/xdg", "unity-open-mcp", "player-state"),
                SnapshotStore.Root(PrefsPlatform.Linux, env, "/home/u"));
            Assert.AreEqual(Path.Combine("/home/u", ".local", "share", "unity-open-mcp", "player-state"),
                SnapshotStore.Root(PrefsPlatform.Linux, k => null, "/home/u"));
            Assert.AreEqual("\"unity.A Co.\\\"Q\\\" Game\"", PrefsStore.QuoteArgument("unity.A Co.\"Q\" Game"));
        }

        [Test]
        public void StoreNamesAndSegments()
        {
            foreach (var ok in new[] { "before", "run-1", "a.b_c", new string('x', 64) })
                Assert.IsTrue(SnapshotStore.IsValidName(ok), ok);
            foreach (var bad in new[] { null, "", "-x", ".x", "a/b", "a..b", "x.", "a b", new string('x', 65) })
                Assert.IsFalse(SnapshotStore.IsValidName(bad), bad ?? "(null)");
            Assert.AreEqual("A_B_C", SnapshotStore.SafeSegment("A/B:C"));
            Assert.AreEqual("_", SnapshotStore.SafeSegment(".."));
            Assert.AreEqual("Game", SnapshotStore.SafeSegment(" Game. "));
        }

        // ==================================================================
        // Globs and diff
        // ==================================================================

        [Test]
        public void Globs_PathsAndKeys()
        {
            Assert.IsTrue(Glob.IsPathExcluded(new[] { "Unity/**" }, "Unity", isDirectory: true));
            Assert.IsTrue(Glob.IsPathMatch("Unity/**", "Unity/a/b.bin"));
            Assert.IsFalse(Glob.IsPathMatch("Unity/**", "Other/Unity/b.bin"));
            Assert.IsTrue(Glob.IsPathMatch("*.log", "logs/deep/x.LOG"), "name pattern at any depth, case-insensitive");
            Assert.IsTrue(Glob.IsPathMatch("TestResults.xml", "TestResults.xml"));
            Assert.IsTrue(Glob.IsPathMatch("**/cache/*", "a/b/cache/x"));
            Assert.IsTrue(Glob.IsPathMatch("**/cache/*", "cache/x"));
            Assert.IsFalse(Glob.IsPathMatch("saves/*.json", "saves/sub/a.json"));
            Assert.IsTrue(Glob.IsPathExcluded(new[] { "Cache" }, "deep/Cache", isDirectory: true));
            Assert.IsTrue(Glob.IsKeyMatch("unity.player_session*", "unity.player_sessionid"));
            Assert.IsTrue(Glob.IsKeyMatch("unity_connect.*", "unity_connect.session_id"));
            Assert.IsFalse(Glob.IsKeyMatch("unity_connect.*", "Unity_connect.session_id"), "keys are case-sensitive");
            Assert.IsTrue(Glob.IsKeyMatch("slot?", "slot1"));
            Assert.IsFalse(Glob.IsKeyMatch("slot?", "slot10"));
        }

        [Test]
        public void KeyDiff_AddedRemovedChangedAndTypeChanges()
        {
            var from = new[] { PrefEntry.OfInt("a", 1), PrefEntry.OfFloat("b", 0.1f), PrefEntry.OfString("c", "x"), PrefEntry.OfInt("same", 5), PrefEntry.OfInt("t", 1) };
            var to = new[] { PrefEntry.OfInt("a", 2), PrefEntry.OfFloat("b", 0.1f), PrefEntry.OfString("d", "y"), PrefEntry.OfInt("same", 5), PrefEntry.OfFloat("t", 1f) };
            var d = KeyDiff.Compute(from, to);
            CollectionAssert.AreEqual(new[] { "d" }, d.Added.Select(e => e.Key));
            CollectionAssert.AreEqual(new[] { "c" }, d.Removed.Select(e => e.Key));
            CollectionAssert.AreEqual(new[] { "a", "t" }, d.Changed.Select(kv => kv.Key.Key), "int 1 → float 1 is a change");
            Assert.AreEqual(2, d.Unchanged);
            Assert.IsTrue(PrefEntry.OfFloat("n", float.NaN).SameValue(PrefEntry.OfFloat("n", float.NaN)));
        }

        // ==================================================================
        // snapshot → play → diff → restore (fakes + temp folders)
        // ==================================================================

        [Test]
        public void Snapshot_FlushesAndCapturesPrefsAndFiles_MinusExcludes()
        {
            SeedDeveloperSave();
            Assert.AreEqual(0, prefs.Store.Count, "nothing flushed yet: the game did not call Save");
            var r = Ok(PlayerStateTools.Run("snapshot", "before"));
            StringAssert.Contains("\"flushed\":true", r.Output);
            StringAssert.Contains("\"editor_prefs\":null", r.Output);
            Assert.AreEqual(0, editorPrefs.Reads, "EditorPrefs are not read without editor_prefs_prefixes");

            var dir = Path.Combine(store, "TestCo", "Test Product", "before");
            var doc = SnapshotStore.Load(dir, out var error);
            Assert.IsNotNull(doc, error);
            Assert.AreEqual("TestCo", doc.company);
            Assert.AreEqual("Test Product", doc.product);
            CollectionAssert.AreEquivalent(new[] { "coins", "volume", "last_level" }, doc.prefs.Select(p => p.key),
                "the Unity session key is excluded by default");
            Assert.IsFalse(doc.editorPrefsCaptured);
            CollectionAssert.AreEquivalent(new[] { "save.json", "slots/slot1.dat" }, doc.files.Select(f => f.path));
            CollectionAssert.AreEquivalent(new[] { "emptydir", "slots" }, doc.dirs);
            Assert.IsTrue(File.Exists(Path.Combine(dir, "files", "slots", "slot1.dat")));
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "files", "Unity")), "excluded folder not copied");
            var volume = doc.prefs.Single(p => p.key == "volume");
            Assert.AreEqual(0.25f, PrefEntry.FromBits(volume.f));

            Refused(PlayerStateTools.Run("snapshot", "before"), "snapshot_exists");
            Ok(PlayerStateTools.Run("snapshot", "before", overwrite: true));
        }

        [Test]
        public void Diff_ListsExactlyWhatTheSessionChanged()
        {
            SeedDeveloperSave();
            Ok(PlayerStateTools.Run("snapshot", "before"));
            PlaySession();
            playing = true;
            var r = Ok(PlayerStateTools.Run("diff", "before"));
            var json = r.Output;
            StringAssert.Contains("\"to\":{\"current\":true,\"mode\":\"play\",\"flushed\":true}", json);
            StringAssert.Contains("\"added\":[{\"key\":\"first_clear\",\"type\":\"string\",\"value\":\"paid\"}]", json);
            StringAssert.Contains("{\"key\":\"coins\",\"from\":{\"type\":\"int\",\"value\":100},\"to\":{\"type\":\"int\",\"value\":150}}", json);
            StringAssert.Contains("{\"key\":\"volume\",\"from\":{\"type\":\"float\",\"value\":0.25},\"to\":{\"type\":\"float\",\"value\":0.75}}", json);
            StringAssert.Contains("\"to\":{\"type\":\"string\",\"value\":\"level_3\"}", json);
            StringAssert.DoesNotContain("unity.player_session_count", json);
            StringAssert.Contains("\"counts\":{\"added\":1,\"removed\":0,\"changed\":3,\"unchanged\":0}", json);
            // Files: new/run.json added, slots/slot1.dat removed, save.json changed; Unity/ ignored.
            StringAssert.Contains("{\"path\":\"new/run.json\",\"size\":2,", json);
            StringAssert.Contains("\"removed\":[{\"path\":\"slots/slot1.dat\"", json);
            StringAssert.Contains("\"changed\":[{\"path\":\"save.json\"", json);
            StringAssert.Contains("\"dirs_added\":[\"new\"]", json);
            StringAssert.DoesNotContain("analytics.bin", json);
            StringAssert.Contains("\"identical\":false", json);
        }

        [Test]
        public void Diff_LongStringsAreHashedAndListsAreBounded()
        {
            prefs.Set(PrefEntry.OfString("blob", new string('a', 300)));
            Ok(PlayerStateTools.Run("snapshot", "s"));
            prefs.Set(PrefEntry.OfString("blob", new string('b', 300)));
            for (int i = 0; i < 5; i++) prefs.Set(PrefEntry.OfInt("k" + i, i));
            var json = Ok(PlayerStateTools.Run("diff", "s", limit: 2)).Output;
            StringAssert.Contains("\"from\":{\"type\":\"string\",\"length\":300,\"sha256\":\"", json);
            StringAssert.DoesNotContain(new string('a', 300), json);
            StringAssert.Contains("\"counts\":{\"added\":5,", json);
            StringAssert.Contains("\"truncated\":3", json);
        }

        [Test]
        public void Restore_DryRunListsEveryRemovalAndChangesNothing()
        {
            SeedDeveloperSave();
            Ok(PlayerStateTools.Run("snapshot", "before"));
            PlaySession();
            prefs.Unsupported.Add("external.blob");
            var json = Ok(PlayerStateTools.Run("restore", "before", limit: 1)).Output;
            StringAssert.Contains("\"unsupported_deleted\":[\"external.blob\"]", json);
            StringAssert.Contains("deleted for good", json);
            StringAssert.Contains("\"dry_run\":true", json);
            // current → snapshot: the file the session created is a removal, listed.
            StringAssert.Contains("\"removed\":[{\"path\":\"new/run.json\"", json);
            StringAssert.Contains("\"removed\":[{\"key\":\"first_clear\"", json);
            StringAssert.Contains("\"dirs_removed\":[\"new\"]", json);
            StringAssert.Contains("file(s) not in the snapshot are removed", json);
            StringAssert.Contains("overwrites the current state", json);
            Assert.IsTrue(FileExists("new/run.json"), "a dry run removes nothing");
            Assert.AreEqual(150, prefs.Memory["coins"].IntValue);
            Assert.AreEqual(0, prefs.DeleteAllCalls);
        }

        [Test]
        public void Restore_ConfirmAppliesThroughTheApiAndMirrorsFiles()
        {
            SeedDeveloperSave();
            Ok(PlayerStateTools.Run("snapshot", "before"));
            PlaySession();
            var r = Ok(PlayerStateTools.Run("restore", "before", confirm: true));
            StringAssert.Contains("\"verified\":true", r.Output);
            StringAssert.Contains("\"errors\":[]", r.Output);
            Assert.AreEqual(1, prefs.DeleteAllCalls);

            // The in-memory cache (what the next play session reads) and the store agree.
            foreach (var view in new[] { prefs.Memory, prefs.Store })
            {
                Assert.AreEqual(100, view["coins"].IntValue);
                Assert.AreEqual(0.25f, view["volume"].FloatValue);
                Assert.AreEqual("level_1", view["last_level"].StringValue);
                Assert.IsFalse(view.ContainsKey("first_clear"));
                Assert.AreEqual("8", view["unity.player_session_count"].StringValue, "excluded keys keep their current value");
            }
            Assert.AreEqual("{\"coins\":100}", ReadFile("save.json"));
            Assert.AreEqual("one", ReadFile("slots/slot1.dat"));
            Assert.IsFalse(FileExists("new/run.json"), "a file not in the snapshot is removed");
            Assert.IsFalse(Directory.Exists(Path.Combine(persistent, "new")));
            Assert.IsTrue(Directory.Exists(Path.Combine(persistent, "emptydir")));
            Assert.AreEqual("engine2", ReadFile("Unity/analytics.bin"), "excluded paths are never touched");
            Assert.IsTrue(Ok(PlayerStateTools.Run("diff", "before")).Output.Contains("\"identical\":true"));
        }

        [Test]
        public void Restore_RefusesInPlayModeAndForAnotherProjectsSnapshot()
        {
            SeedDeveloperSave();
            Ok(PlayerStateTools.Run("snapshot", "before"));
            playing = true;
            Refused(PlayerStateTools.Run("restore", "before", confirm: true), "edit_mode_required");
            Refused(PlayerStateTools.Run("restore", "before"), "edit_mode_required");
            playing = false;

            // A snapshot taken by another product (same machine, its own store).
            identity = new[] { "TestCo", "Other Game" };
            prefs.Set(PrefEntry.OfInt("other", 1));
            Ok(PlayerStateTools.Run("snapshot", "theirs"));
            identity = new[] { "TestCo", "Test Product" };
            var r = PlayerStateTools.Run("restore", "theirs", confirm: true, product: "Other Game");
            Refused(r, "snapshot_project_mismatch");
            StringAssert.Contains("\"product\":\"Other Game\"", r.ErrorDetailJson);
            Refused(PlayerStateTools.Run("restore", "theirs", product: "Other Game"), "snapshot_project_mismatch");
            Refused(PlayerStateTools.Run("diff", "theirs", product: "Other Game"), "snapshot_project_mismatch");
            Assert.AreEqual(0, prefs.DeleteAllCalls);

            // A snapshot folder whose metadata names another product is refused too.
            var dir = Path.Combine(store, "TestCo", "Test Product", "before");
            var doc = SnapshotStore.Load(dir, out _);
            doc.product = "Renamed";
            SnapshotStore.WriteMeta(dir, doc);
            Refused(PlayerStateTools.Run("restore", "before", confirm: true), "snapshot_project_mismatch");
            Refused(PlayerStateTools.Run("restore", "missing"), "snapshot_not_found");
        }

        [Test]
        public void Restore_RefusesACorruptSnapshotBeforeChangingAnything()
        {
            SeedDeveloperSave();
            Ok(PlayerStateTools.Run("snapshot", "before"));
            PlaySession();
            File.WriteAllText(Path.Combine(store, "TestCo", "Test Product", "before", "files", "save.json"), "tampered");
            Refused(PlayerStateTools.Run("restore", "before", confirm: true), "snapshot_corrupt");
            Assert.AreEqual(0, prefs.DeleteAllCalls);
            Assert.IsTrue(FileExists("new/run.json"));
        }

        [Test]
        public void EditorPrefs_OptInByPrefixAndRestoreTouchesOnlyThoseKeys()
        {
            editorPrefs.Set(PrefEntry.OfInt("mytool.flag", 1));
            editorPrefs.Set(PrefEntry.OfString("mytool.name", "a"));
            editorPrefs.Set(PrefEntry.OfInt("OtherProject.Setting", 9));
            // Not flushed yet: the snapshot must flush to see them.
            var r = Ok(PlayerStateTools.Run("snapshot", "ep", editor_prefs_prefixes: new[] { "mytool." }));
            StringAssert.Contains("\"editor_prefs\":{\"prefixes\":[\"mytool.\"],\"count\":2", r.Output);

            editorPrefs.Set(PrefEntry.OfInt("mytool.flag", 2));
            editorPrefs.Delete("mytool.name");
            editorPrefs.Set(PrefEntry.OfFloat("mytool.extra", 0.5f));
            editorPrefs.Set(PrefEntry.OfInt("OtherProject.Setting", 10));
            var diff = Ok(PlayerStateTools.Run("diff", "ep")).Output;
            StringAssert.Contains("\"editor_prefs\":{\"added\":[{\"key\":\"mytool.extra\"", diff);
            StringAssert.DoesNotContain("OtherProject", diff);

            var applied = Ok(PlayerStateTools.Run("restore", "ep", confirm: true)).Output;
            StringAssert.Contains("\"verified\":true", applied);
            Assert.AreEqual(1, editorPrefs.Memory["mytool.flag"].IntValue);
            Assert.AreEqual("a", editorPrefs.Memory["mytool.name"].StringValue);
            Assert.IsFalse(editorPrefs.Memory.ContainsKey("mytool.extra"));
            Assert.AreEqual(10, editorPrefs.Memory["OtherProject.Setting"].IntValue, "keys outside the prefixes are untouched");

            // Without the internal flush, EditorPrefs are refused rather than read stale.
            editorPrefs.Flushable = false;
            Refused(PlayerStateTools.Run("snapshot", "ep2", editor_prefs_prefixes: new[] { "mytool." }), "editor_internals_unavailable");
            Ok(PlayerStateTools.Run("snapshot", "no-ep"));
        }

        [Test]
        public void Snapshot_OverTheSizeCapWritesNothing()
        {
            WriteFile("big.bin", new string('x', 2 * 1024 * 1024));
            var r = PlayerStateTools.Run("snapshot", "big", max_files_mb: 1);
            Refused(r, "persistent_data_too_large");
            StringAssert.Contains("big.bin", r.ErrorDetailJson);
            Assert.IsFalse(Directory.Exists(Path.Combine(store, "TestCo", "Test Product", "big")));
            Ok(PlayerStateTools.Run("snapshot", "big", exclude: new[] { "*.bin" }, max_files_mb: 1));
        }

        [Test]
        public void ListAndDelete()
        {
            Ok(PlayerStateTools.Run("snapshot", "a"));
            Ok(PlayerStateTools.Run("snapshot", "b", editor_prefs_prefixes: new[] { "mytool." }));
            var list = Ok(PlayerStateTools.Run("list")).Output;
            StringAssert.Contains("\"open_project\":true", list);
            StringAssert.Contains("{\"name\":\"a\",", list);
            StringAssert.Contains("\"editor_prefs_prefixes\":[\"mytool.\"]", list);
            Ok(PlayerStateTools.Run("delete", "a"));
            Refused(PlayerStateTools.Run("delete", "a"), "snapshot_not_found");
            StringAssert.DoesNotContain("{\"name\":\"a\",", Ok(PlayerStateTools.Run("list")).Output);
        }

        [Test]
        public void Arguments_AreValidatedPerAction()
        {
            Refused(PlayerStateTools.Run("zap"), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot"), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot", "../x"), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot", "x", company: "Other"), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot", "x", editor_prefs_prefixes: new[] { "ab" }), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot", "x", max_files_mb: 0), "invalid_arguments");
            Refused(PlayerStateTools.Run("diff", "x", exclude: new[] { "*.log" }), "invalid_arguments");
            Refused(PlayerStateTools.Run("list", against: "y"), "invalid_arguments");
            Refused(PlayerStateTools.Run("snapshot", "x", confirm: true), "invalid_arguments");
            Refused(PlayerStateTools.Run("diff", "x", limit: 0), "invalid_arguments");
        }

        // ==================================================================
        // Bridge contract
        // ==================================================================

        [Test]
        public void Contract_OnlyAnAppliedRestoreIsAMutation()
        {
            const string tool = PlayerStateTools.ToolName;
            Assert.IsTrue(EffectiveToolContract.IsMutating(tool, "{\"action\":\"restore\",\"name\":\"a\",\"confirm\":true}"));
            foreach (var body in new[]
            {
                "{\"action\":\"restore\",\"name\":\"a\"}",
                "{\"action\":\"restore\",\"name\":\"a\",\"confirm\":false}",
                "{\"action\":\"snapshot\",\"name\":\"a\"}",
                "{\"action\":\"diff\",\"name\":\"a\"}",
                "{\"action\":\"list\"}",
                "{\"action\":\"delete\",\"name\":\"a\"}",
            })
                Assert.IsFalse(EffectiveToolContract.IsMutating(tool, body), body);
            Assert.AreEqual(LifecyclePolicy.None, EffectiveToolContract.Lifecycle(tool, "{\"action\":\"restore\",\"confirm\":true}"));
        }

        [Test]
        public void Registry_PlaytestGroupAndFailedResultsPassThrough()
        {
            if (BridgeToolRegistry.Count == 0) BridgeToolRegistry.Scan();
            Assert.IsTrue(BridgeToolRegistry.TryGet(PlayerStateTools.ToolName, out var info));
            Assert.AreEqual("playtest", info.Group);
            Assert.IsTrue(info.IsMutating, "conservative catalog default; requests resolve per action");
            // A refusal comes back as a failed result with its code, not an error inside a success.
            var refused = BridgeToolRegistry.TryDispatch(PlayerStateTools.ToolName, "{\"action\":\"snapshot\"}");
            Assert.IsFalse(refused.Success);
            Assert.AreEqual("invalid_arguments", refused.ErrorCode);
            var listed = BridgeToolRegistry.TryDispatch(PlayerStateTools.ToolName,
                "{\"action\":\"list\",\"paths_hint\":[\"ProjectSettings/ProjectSettings.asset\"],\"gate\":\"enforce\"}");
            Assert.IsTrue(listed.Success, listed.ErrorMessage);
        }
    }
}
