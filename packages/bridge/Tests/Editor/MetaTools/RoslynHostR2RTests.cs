using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class RoslynHostR2RTests
    {
        // feedback-01-08-glm §1 — Unity 6000.0.x ships DotNetSdkRoslyn as
        // ReadyToRun (R2R) composite images the Mono runtime cannot load. The
        // bridge now detects non-ILOnly PE headers BEFORE attempting LoadFrom,
        // so an unloadable candidate is skipped cleanly instead of producing a
        // generic "Roslyn init failed". These tests pin the detector's contract
        // without requiring a real R2R binary on disk.

        [Test]
        public static void IsReadyToRunImage_PureIlAssembly_ReturnsFalse()
        {
            // The test assembly itself is a normal pure-IL managed assembly, so
            // it MUST NOT be classified as R2R (ILOnly is set).
            var self = typeof(RoslynHostR2RTests).Assembly.Location;
            Assume.That(File.Exists(self), "test assembly must have a location on disk");
            Assert.IsFalse(RoslynHost.IsReadyToRunImage(self),
                "a pure-IL managed assembly must not be flagged as R2R");
        }

        [Test]
        public static void IsReadyToRunImage_NonExistentFile_ReturnsFalseWithoutThrowing()
        {
            Assert.IsFalse(RoslynHost.IsReadyToRunImage("/no/such/path/missing.dll"));
        }

        [Test]
        public static void IsReadyToRunImage_GarbageFile_ReturnsFalseWithoutThrowing()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"roslynhost-notpe-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(tmp, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
                Assert.IsFalse(RoslynHost.IsReadyToRunImage(tmp));
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        [Test]
        public static void IsReadyToRunImage_TinyMZFile_ReturnsFalseWithoutThrowing()
        {
            // An MZ stub with no valid PE header must not crash the parser.
            var tmp = Path.Combine(Path.GetTempPath(), $"roslynhost-mz-{Guid.NewGuid():N}.bin");
            try
            {
                var bytes = new byte[128];
                bytes[0] = (byte)'M';
                bytes[1] = (byte)'Z';
                File.WriteAllBytes(tmp, bytes);
                Assert.IsFalse(RoslynHost.IsReadyToRunImage(tmp));
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
