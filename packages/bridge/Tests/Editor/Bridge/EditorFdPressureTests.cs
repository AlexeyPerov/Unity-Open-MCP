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

        // ---- Ceiling resolution --------------------------------------------
        //
        // The advisory rides EVERY execute_csharp response, so it must resolve
        // the same ceiling `resource_pressure` does. A hard-coded 1024 against
        // a project that set `resourcePressure.fdCeiling: 4096` (the documented
        // Unity 6 / CoreCLR knob) would scream "CRITICAL, restart the Editor"
        // in-band on every call while the server-side tool reported `ok`.

        private string _dir;

        [TearDown]
        public void TearDown()
        {
            if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true);
            _dir = null;
        }

        private string WriteSettings(string body)
        {
            _dir = Path.Combine(Path.GetTempPath(), "uom-fd-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path, body);
            return path;
        }

        [Test]
        public void ReadCeilingFromFile_HonorsAConfiguredOverride()
        {
            var path = WriteSettings(
                "{\n  \"autoStart\": true,\n  \"resourcePressure\": { \"fdCeiling\": 4096 }\n}");
            Assert.AreEqual(4096, EditorFdPressure.ReadCeilingFromFile(path));

            // …and the advisory then stays silent where 1024 would have fired.
            Assert.IsNull(EditorFdPressure.BuildAdvisory(900, 4096));
            Assert.IsNotNull(EditorFdPressure.BuildAdvisory(900, 1024));
        }

        [Test]
        public void ReadCeilingFromFile_FallsBackOnAbsentOrUnusableValues()
        {
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling,
                EditorFdPressure.ReadCeilingFromFile(WriteSettings("{\"autoStart\": true}")),
                "no resourcePressure slice → default");
            TearDown();
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling,
                EditorFdPressure.ReadCeilingFromFile(WriteSettings("{ not json at all")),
                "unparseable file → default, never a throw");
            TearDown();
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling,
                EditorFdPressure.ReadCeilingFromFile(Path.Combine(Path.GetTempPath(), "uom-absent.json")),
                "missing file → default");
        }

        [Test]
        public void ClampCeiling_MirrorsTheServersResolveConfigured()
        {
            // Below the min is treated as "not configured"; above the max
            // clamps down; in range is kept. Same policy as readFdCeiling in
            // mcp-server/src/project-settings.ts.
            Assert.AreEqual(EditorFdPressure.MonoFdCeiling, EditorFdPressure.ClampCeiling(0));
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling,
                EditorFdPressure.ClampCeiling(EditorFdPressure.FdCeilingMin - 1));
            Assert.AreEqual(
                EditorFdPressure.FdCeilingMin,
                EditorFdPressure.ClampCeiling(EditorFdPressure.FdCeilingMin));
            Assert.AreEqual(
                EditorFdPressure.FdCeilingMax,
                EditorFdPressure.ClampCeiling((long)EditorFdPressure.FdCeilingMax + 1));
            Assert.AreEqual(4096, EditorFdPressure.ClampCeiling(4096));
        }

        [Test]
        public void ClampCeiling_Fractional_FloorsLikeReadFdCeiling()
        {
            // TS: Math.floor(raw) — 4096.7 configures 4096, not 4097.
            Assert.AreEqual(4096, EditorFdPressure.ClampCeiling(4096.7));
        }

        [Test]
        public void ReadCeilingFromFile_ExponentNotation_ReadsTheFullNumber()
        {
            // JSON.parse reads 1e6 as 1000000; the old regex stopped at the
            // digits and read "1" → below-min → default. One-sided ceiling.
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"fdCeiling\": 1e6 }\n}");
            Assert.AreEqual(1_000_000, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ReadCeilingFromFile_ExponentOverflowToInfinity_FallsBack()
        {
            // JSON.parse yields Infinity for 1e999 and readFdCeiling's
            // isFinite check rejects it → default. ClampCeiling must not
            // "fix" it to the max instead.
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"fdCeiling\": 1e999 }\n}");
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ReadCeilingFromFile_FractionalValue_Floors()
        {
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"fdCeiling\": 4096.7 }\n}");
            Assert.AreEqual(4096, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ReadCeilingFromFile_NestedObjectBeforeFdCeiling_StillReadsIt()
        {
            // JSON.parse has no trouble with a sibling object; the old
            // `[^{}]*` span could not cross "trend"'s braces and defaulted.
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"trend\": { \"window\": 5 }, \"fdCeiling\": 4096 }\n}");
            Assert.AreEqual(4096, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ReadCeilingFromFile_FdCeilingNestedInASibling_IsIgnored()
        {
            // resourcePressure.fdCeiling does not exist here (the only
            // fdCeiling lives inside "trend") — JSON.parse would surface
            // undefined, so this side must default rather than read 8.
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"trend\": { \"fdCeiling\": 8 } }\n}");
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ReadCeilingFromFile_QuotedValue_FallsBack()
        {
            // typeof "4096" is string — readFdCeiling rejects it.
            var path = WriteSettings(
                "{\n  \"resourcePressure\": { \"fdCeiling\": \"4096\" }\n}");
            Assert.AreEqual(
                EditorFdPressure.MonoFdCeiling, EditorFdPressure.ReadCeilingFromFile(path));
        }

        [Test]
        public void ResolveCeiling_NeverThrowsOnTheLiveProject()
        {
            // Runs on the dispatcher's worker thread in production, against
            // whatever the project's settings file happens to be.
            Assert.Greater(EditorFdPressure.ResolveCeiling(), 0);
        }
    }
}
