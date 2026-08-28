using System.IO;
using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    // The execute_csharp fd-pressure advisory (EditorFdPressure): silent below
    // the warn threshold, "elevated" at ≥80% of the Mono ceiling, "CRITICAL"
    // at ≥90%, and never emitted from garbage inputs. The live sampling half
    // is exercised only where a per-process fd directory exists (macOS /
    // Linux) — on Windows the probe deliberately reports failure so the
    // advisory stays silent (HandleCount would cry wolf).
    public class EditorFdPressureTests
    {
        [Test]
        public void BuildAdvisory_BelowWarnThreshold_ReturnsNull()
        {
            Assert.IsNull(EditorFdPressure.BuildAdvisory(500, 1024));
            // 79.98% — just under the 80% warn line.
            Assert.IsNull(EditorFdPressure.BuildAdvisory(818, 1024));
        }

        [Test]
        public void BuildAdvisory_WarnBand_MentionsElevatedAndNumbers()
        {
            // 820/1024 = 80.1%.
            var advisory = EditorFdPressure.BuildAdvisory(820, 1024);
            Assert.IsNotNull(advisory);
            StringAssert.Contains("elevated", advisory);
            StringAssert.Contains("820", advisory);
            StringAssert.Contains("1024", advisory);
            StringAssert.Contains("resource_pressure", advisory);
        }

        [Test]
        public void BuildAdvisory_CriticalBand_MentionsCritical()
        {
            // 940/1024 = 91.8%.
            var advisory = EditorFdPressure.BuildAdvisory(940, 1024);
            Assert.IsNotNull(advisory);
            StringAssert.Contains("CRITICAL", advisory);
        }

        [Test]
        public void BuildAdvisory_GarbageInputs_ReturnNull()
        {
            Assert.IsNull(EditorFdPressure.BuildAdvisory(-1, 1024));
            Assert.IsNull(EditorFdPressure.BuildAdvisory(900, 0));
            Assert.IsNull(EditorFdPressure.BuildAdvisory(900, -1));
        }

        [Test]
        public void TryCountOpenFds_OnPosix_ReturnsPositiveCount()
        {
            if (!Directory.Exists("/dev/fd") && !Directory.Exists("/proc/self/fd"))
                Assert.Ignore("No per-process fd directory on this platform (Windows) — the probe reports failure by design.");

            Assert.IsTrue(EditorFdPressure.TryCountOpenFds(out var count));
            Assert.Greater(count, 0, "a live editor process always has open descriptors");
        }
    }
}
