using System;
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
                var before = BridgeCompileState.Fingerprint(new[] { path });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
                Assert.AreEqual(before, BridgeCompileState.Fingerprint(new[] { path }));
                File.WriteAllText(path, "class Probe { int value; }");
                Assert.AreNotEqual(before, BridgeCompileState.Fingerprint(new[] { path }));
                Assert.AreNotEqual(before, BridgeCompileState.Fingerprint(Array.Empty<string>()));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void StatSignature_ChangesOnWriteOrMembership_WithoutReadingContent()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "Probe.cs");
                File.WriteAllText(path, "class Probe {}");
                var before = BridgeCompileState.StatSignature(new[] { path });
                Assert.AreEqual(before, BridgeCompileState.StatSignature(new[] { path }));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
                Assert.AreNotEqual(before, BridgeCompileState.StatSignature(new[] { path }));
                Assert.AreNotEqual(before, BridgeCompileState.StatSignature(Array.Empty<string>()));
                // A missing file still yields a deterministic signature instead of throwing.
                var missing = Path.Combine(root, "Missing.cs");
                Assert.AreEqual(
                    BridgeCompileState.StatSignature(new[] { missing }),
                    BridgeCompileState.StatSignature(new[] { missing }));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
