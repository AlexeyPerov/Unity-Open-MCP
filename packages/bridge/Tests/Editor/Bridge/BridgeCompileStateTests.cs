using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    public class BridgeCompileStateTests
    {
        [TestCase("{\"a\":1}{\"b\":2}", false)]
        [TestCase("{\"a\":]}", false)]
        [TestCase("{\"a\":NaN}", false)]
        [TestCase("{\"a\":01}", false)]
        [TestCase("{\"a\":true,}", false)]
        [TestCase("{\"a\":[null,false,1.2e-3,\"ok\"]}", true)]
        public void Transport_RequiresSingleCompleteJson(string text, bool valid)
        {
            Assert.AreEqual(valid, BridgeJson.IsCompleteJson(text));
        }

        [TestCase(512, "warning")]
        [TestCase(4096, "warning")]
        [TestCase(128, "warning")]
        [TestCase(4, "log")]
        [TestCase(1024, "log")]
        [TestCase(2048, "error")]
        [TestCase(131072, "error")]
        [TestCase(256 | 512, "error")]
        public void ConsoleSeverity_UsesUnityModeFlags(int mode, string severity)
        {
            Assert.AreEqual(severity, UnityOpenMcpBridge.Console.LogEntriesReader.Classify(mode));
        }

        [Test]
        public void SourceFingerprint_TracksContentAndMembership_NotTouchTime()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "Probe.cs");
                File.WriteAllText(path, "class Probe {}");
                var hashes = new BridgeCompileState.SourceHashes(File.ReadAllBytes);
                var before = hashes.Fingerprint(new[] { path });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
                Assert.AreEqual(before, hashes.Fingerprint(new[] { path }));
                File.WriteAllText(path, "class Probe { int value; }");
                Assert.AreNotEqual(before, hashes.Fingerprint(new[] { path }));
                Assert.AreNotEqual(before, hashes.Fingerprint(Array.Empty<string>()));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void SourceFingerprint_RereadsOnlyFilesWhoseStatMoved()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var edited = Path.Combine(root, "Edited.cs");
                var untouched = Path.Combine(root, "Untouched.cs");
                File.WriteAllText(edited, "class Edited {}");
                File.WriteAllText(untouched, "class Untouched {}");
                var paths = new[] { edited, untouched };
                var reads = new List<string>();
                var hashes = new BridgeCompileState.SourceHashes(p => { reads.Add(p); return File.ReadAllBytes(p); });

                var before = hashes.Fingerprint(paths);
                CollectionAssert.AreEquivalent(paths, reads);

                reads.Clear();
                Assert.AreEqual(before, hashes.Fingerprint(paths), "unchanged content keeps its fingerprint");
                CollectionAssert.IsEmpty(reads, "an unchanged stat must not reread the file");

                reads.Clear();
                File.WriteAllText(edited, "class Edited { int value; }");
                var after = hashes.Fingerprint(paths);
                CollectionAssert.AreEqual(new[] { edited }, reads, "only the edited file is reread");
                Assert.AreNotEqual(before, after);
                Assert.AreEqual(new BridgeCompileState.SourceHashes(File.ReadAllBytes).Fingerprint(paths), after,
                    "a partly cached pass yields the value of hashing every file afresh");

                // A deleted input fails the pass instead of reusing its cached hash.
                File.Delete(untouched);
                Assert.That(() => hashes.Fingerprint(paths), Throws.InstanceOf<IOException>());
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
