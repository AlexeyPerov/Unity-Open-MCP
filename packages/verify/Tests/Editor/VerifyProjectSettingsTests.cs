using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class VerifyProjectSettingsTests
    {
        // -------------------------------------------------------------------
        // Default contract — no settings file, no override -> error threshold.
        // -------------------------------------------------------------------

        [Test]
        public void SeverityThreshold_DefaultsToError_WhenNoFileAndNoOverride()
        {
            // Force a reload against a project that has no settings file. We
            // cannot easily redirect Application.dataPath, so the override-
            // for-tests entry point is the contract we exercise here. The
            // default constant is the documented behaviour.
            VerifyProjectSettings.OverrideForTests(null);

            Assert.AreEqual(VerifyProjectSettings.DefaultSeverityThreshold,
                VerifyProjectSettings.SeverityThreshold);
            Assert.AreEqual("error", VerifyProjectSettings.SeverityThreshold);
        }

        // -------------------------------------------------------------------
        // Human spellings — settings.json reads "warning" / "info" naturally.
        // -------------------------------------------------------------------

        [TestCase("error", "error")]
        [TestCase("warning", "warn")]
        [TestCase("warn", "warn")]
        [TestCase("info", "info")]
        [TestCase("verbose", "verbose")]
        [TestCase("never", "never")]
        [TestCase("off", "never")]
        [TestCase("ERROR", "error")] // case-insensitive
        [TestCase("Warning", "warn")]
        public void SeverityThreshold_NormalizesHumanSpellings(string input, string expected)
        {
            VerifyProjectSettings.OverrideForTests(input);
            Assert.AreEqual(expected, VerifyProjectSettings.SeverityThreshold);
        }

        [TestCase("garbage")]
        [TestCase("")]
        [TestCase(" ")]
        public void SeverityThreshold_FallsBackToDefault_OnGarbage(string input)
        {
            VerifyProjectSettings.OverrideForTests(input);
            Assert.AreEqual(VerifyProjectSettings.DefaultSeverityThreshold,
                VerifyProjectSettings.SeverityThreshold);
        }

        [Test]
        public void SeverityThreshold_IsAlwaysOneOfTheValidFailValues()
        {
            // Whatever ends up in the settings file, the resolved threshold must
            // be a member of SeverityThreshold.ValidValues so the gate code can
            // feed it straight into SeverityThreshold.Parse.
            foreach (var input in new[] { null, "", "error", "warning", "info", "garbage", "OFF" })
            {
                VerifyProjectSettings.OverrideForTests(input);
                CollectionAssert.Contains(
                    System.Array.IndexOf(SeverityThreshold.ValidValues, VerifyProjectSettings.SeverityThreshold) >= 0
                        ? SeverityThreshold.ValidValues
                        : new[] { VerifyProjectSettings.SeverityThreshold },
                    VerifyProjectSettings.SeverityThreshold);
                CollectionAssert.Contains(SeverityThreshold.ValidValues, VerifyProjectSettings.SeverityThreshold);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Reset to a clean state so leakage between fixtures can't poison
            // other tests that read the project threshold.
            VerifyProjectSettings.OverrideForTests(null);
        }
    }
}
