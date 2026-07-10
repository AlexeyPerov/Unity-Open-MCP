using System;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Guards the clamping / validation branches in BridgeProjectSettings that
    // are NOT already covered by VerifyCacheTtlSettingsTests (which owns the
    // verify-cache TTL clamping + runtime-service pushdown end to end).
    //
    // Focus: settle caps, fair-queue reads, batch_execute cap, auth-mode
    // transitions, bind-address refusal, and deny-pattern normalization. These
    // are the security/operator-facing settings where a silent regression
    // (refusing a valid value, accepting an invalid one) is most dangerous.
    public class BridgeProjectSettingsTests
    {
        // Capture the live static state once per fixture; restore in TearDown so
        // the global BridgeProjectSettings singleton never bleeds across tests.
        private string _prevAuthMode;
        private string _prevBindAddress;
        private string[] _prevCSharpDenyPatterns;
        private string[] _prevMenuDenyPatterns;

        [SetUp]
        public void SetUp()
        {
            _prevAuthMode = BridgeProjectSettings.AuthMode;
            _prevBindAddress = BridgeProjectSettings.BindAddress;
            _prevCSharpDenyPatterns = BridgeProjectSettings.CSharpDenyPatterns;
            _prevMenuDenyPatterns = BridgeProjectSettings.MenuDenyPatterns;
        }

        [TearDown]
        public void TearDown()
        {
            BridgeProjectSettings.SetAuthMode(_prevAuthMode);
            BridgeProjectSettings.SetBindAddress(_prevBindAddress);
            BridgeProjectSettings.SetCSharpDenyPatterns(_prevCSharpDenyPatterns);
            BridgeProjectSettings.SetMenuDenyPatterns(_prevMenuDenyPatterns);
        }

        // -----------------------------------------------------------------------
        // Settle-cap clamping (EditorSettleCapMs / RestartSettleCapMs)
        // -----------------------------------------------------------------------

        [Test]
        public void EditorSettleCap_BelowMinimum_ClampsToEditorDefault()
        {
            BridgeProjectSettings.EditorSettleCapMs = 0;
            Assert.AreEqual(
                BridgeProjectSettings.DefaultEditorSettleCapMs,
                BridgeProjectSettings.EditorSettleCapMs,
                "below-min editor cap must fall back to the editor default (5000), not 0.");

            BridgeProjectSettings.EditorSettleCapMs = 500;
            Assert.AreEqual(BridgeProjectSettings.DefaultEditorSettleCapMs, BridgeProjectSettings.EditorSettleCapMs);
        }

        [Test]
        public void RestartSettleCap_BelowMinimum_ClampsToRestartDefault()
        {
            BridgeProjectSettings.RestartSettleCapMs = 100;
            Assert.AreEqual(
                BridgeProjectSettings.DefaultRestartSettleCapMs,
                BridgeProjectSettings.RestartSettleCapMs,
                "below-min restart cap must fall back to the restart default (60000).");
        }

        [Test]
        public void SettleCap_AboveMaximum_ClampsToCeiling()
        {
            BridgeProjectSettings.EditorSettleCapMs = 999_999_999;
            Assert.AreEqual(BridgeProjectSettings.MaxSettleCapMs, BridgeProjectSettings.EditorSettleCapMs);

            BridgeProjectSettings.RestartSettleCapMs = 999_999_999;
            Assert.AreEqual(BridgeProjectSettings.MaxSettleCapMs, BridgeProjectSettings.RestartSettleCapMs);
        }

        [Test]
        public void SettleCap_InRange_PassesThrough()
        {
            BridgeProjectSettings.EditorSettleCapMs = BridgeProjectSettings.MinSettleCapMs;
            Assert.AreEqual(BridgeProjectSettings.MinSettleCapMs, BridgeProjectSettings.EditorSettleCapMs);

            BridgeProjectSettings.RestartSettleCapMs = BridgeProjectSettings.MaxSettleCapMs;
            Assert.AreEqual(BridgeProjectSettings.MaxSettleCapMs, BridgeProjectSettings.RestartSettleCapMs);
        }

        [Test]
        public void EditorAndRestartSettleCaps_AreIndependent()
        {
            // The two caps clamp to DIFFERENT defaults; setting one must not
            // affect the other.
            BridgeProjectSettings.EditorSettleCapMs = 0; // → editor default (5000)
            BridgeProjectSettings.RestartSettleCapMs = 45_000;
            Assert.AreEqual(BridgeProjectSettings.DefaultEditorSettleCapMs, BridgeProjectSettings.EditorSettleCapMs);
            Assert.AreEqual(45_000, BridgeProjectSettings.RestartSettleCapMs);
        }

        // -----------------------------------------------------------------------
        // Fair-queue reads-per-frame clamping
        // -----------------------------------------------------------------------

        [Test]
        public void FairQueueReads_BelowMinimum_ClampsToDefault()
        {
            BridgeProjectSettings.FairQueueReadsPerFrame = 0;
            Assert.AreEqual(
                BridgeProjectSettings.DefaultFairQueueReadsPerFrame,
                BridgeProjectSettings.FairQueueReadsPerFrame);

            BridgeProjectSettings.FairQueueReadsPerFrame = -5;
            Assert.AreEqual(BridgeProjectSettings.DefaultFairQueueReadsPerFrame, BridgeProjectSettings.FairQueueReadsPerFrame);
        }

        [Test]
        public void FairQueueReads_AboveMaximum_ClampsToCeiling()
        {
            BridgeProjectSettings.FairQueueReadsPerFrame = 999;
            Assert.AreEqual(
                BridgeProjectSettings.MaxFairQueueReadsPerFrame,
                BridgeProjectSettings.FairQueueReadsPerFrame);
        }

        [Test]
        public void FairQueueReads_InRange_PassesThrough()
        {
            BridgeProjectSettings.FairQueueReadsPerFrame = BridgeProjectSettings.MinFairQueueReadsPerFrame;
            Assert.AreEqual(BridgeProjectSettings.MinFairQueueReadsPerFrame, BridgeProjectSettings.FairQueueReadsPerFrame);

            BridgeProjectSettings.FairQueueReadsPerFrame = BridgeProjectSettings.MaxFairQueueReadsPerFrame;
            Assert.AreEqual(BridgeProjectSettings.MaxFairQueueReadsPerFrame, BridgeProjectSettings.FairQueueReadsPerFrame);
        }

        // -----------------------------------------------------------------------
        // batch_execute nested-command cap clamping
        // -----------------------------------------------------------------------

        [Test]
        public void BatchExecuteMaxCommands_BelowMinimum_ClampsToDefault()
        {
            BridgeProjectSettings.BatchExecuteMaxCommands = 0;
            Assert.AreEqual(
                BridgeProjectSettings.DefaultBatchExecuteMaxCommands,
                BridgeProjectSettings.BatchExecuteMaxCommands);

            BridgeProjectSettings.BatchExecuteMaxCommands = -3;
            Assert.AreEqual(BridgeProjectSettings.DefaultBatchExecuteMaxCommands, BridgeProjectSettings.BatchExecuteMaxCommands);
        }

        [Test]
        public void BatchExecuteMaxCommands_AboveMaximum_ClampsToCeiling()
        {
            BridgeProjectSettings.BatchExecuteMaxCommands = 500;
            Assert.AreEqual(
                BridgeProjectSettings.MaxBatchExecuteMaxCommands,
                BridgeProjectSettings.BatchExecuteMaxCommands);
        }

        [Test]
        public void BatchExecuteMaxCommands_InRange_PassesThrough()
        {
            BridgeProjectSettings.BatchExecuteMaxCommands = BridgeProjectSettings.MinBatchExecuteMaxCommands;
            Assert.AreEqual(BridgeProjectSettings.MinBatchExecuteMaxCommands, BridgeProjectSettings.BatchExecuteMaxCommands);

            BridgeProjectSettings.BatchExecuteMaxCommands = 50;
            Assert.AreEqual(50, BridgeProjectSettings.BatchExecuteMaxCommands);

            BridgeProjectSettings.BatchExecuteMaxCommands = BridgeProjectSettings.MaxBatchExecuteMaxCommands;
            Assert.AreEqual(BridgeProjectSettings.MaxBatchExecuteMaxCommands, BridgeProjectSettings.BatchExecuteMaxCommands);
        }

        // -----------------------------------------------------------------------
        // Auth-mode transitions
        // -----------------------------------------------------------------------

        [Test]
        public void AuthMode_InvalidValue_NotAccepted()
        {
            var before = BridgeProjectSettings.AuthMode;
            BridgeProjectSettings.SetAuthMode("totally-bogus");
            Assert.AreEqual(before, BridgeProjectSettings.AuthMode,
                "invalid authMode must be rejected, not stored.");
        }

        [Test]
        public void AuthMode_ValidValue_Accepted()
        {
            // "none" and "required" are the valid modes (BridgeAuthPolicy).
            BridgeProjectSettings.SetAuthMode("required");
            Assert.AreEqual("required", BridgeProjectSettings.AuthMode);

            BridgeProjectSettings.SetAuthMode("none");
            Assert.AreEqual("none", BridgeProjectSettings.AuthMode);
        }

        [Test]
        public void AuthMode_NullOrEmpty_Rejected()
        {
            var before = BridgeProjectSettings.AuthMode;
            BridgeProjectSettings.SetAuthMode(null);
            Assert.AreEqual(before, BridgeProjectSettings.AuthMode);

            BridgeProjectSettings.SetAuthMode("");
            Assert.AreEqual(before, BridgeProjectSettings.AuthMode);
        }

        // -----------------------------------------------------------------------
        // Bind-address refusal
        // -----------------------------------------------------------------------

        [Test]
        public void BindAddress_InvalidValue_Rejected()
        {
            var before = BridgeProjectSettings.BindAddress;
            BridgeProjectSettings.SetBindAddress("192.168.1.1");
            Assert.AreEqual(before, BridgeProjectSettings.BindAddress,
                "an arbitrary IP must be rejected — only loopback + any are valid.");
        }

        [Test]
        public void BindAddress_ValidLoopback_Accepted()
        {
            BridgeProjectSettings.SetBindAddress("127.0.0.1");
            Assert.AreEqual("127.0.0.1", BridgeProjectSettings.BindAddress);
        }

        // -----------------------------------------------------------------------
        // C# deny-pattern normalization (de-dup, whitespace strip, regex validation)
        // -----------------------------------------------------------------------

        [Test]
        public void CSharpDenyPatterns_Null_YieldsNullDefaultsSignal()
        {
            // null ⇒ the evaluator applies built-in defaults (distinct from an
            // explicit empty array which means "off").
            BridgeProjectSettings.SetCSharpDenyPatterns(null);
            Assert.IsNull(BridgeProjectSettings.CSharpDenyPatterns,
                "null input must stay null so the deny-list evaluator falls back to built-in defaults.");
        }

        [Test]
        public void CSharpDenyPatterns_EmptyArray_YieldsEmptyArray()
        {
            BridgeProjectSettings.SetCSharpDenyPatterns(Array.Empty<string>());
            Assert.IsNotNull(BridgeProjectSettings.CSharpDenyPatterns);
            Assert.AreEqual(0, BridgeProjectSettings.CSharpDenyPatterns.Length,
                "an explicit empty array is the 'deny list off' signal and must be preserved.");
        }

        [Test]
        public void CSharpDenyPatterns_WhitespaceEntriesStripped()
        {
            BridgeProjectSettings.SetCSharpDenyPatterns(new[] { "  ", "", "Process\\.Kill", "   " });
            var result = BridgeProjectSettings.CSharpDenyPatterns;
            Assert.AreEqual(1, result.Length);
            Assert.AreEqual("Process\\.Kill", result[0]);
        }

        [Test]
        public void CSharpDenyPatterns_DuplicatesRemoved()
        {
            BridgeProjectSettings.SetCSharpDenyPatterns(new[]
            {
                "Process\\.Kill",
                "Process\\.Kill",
                "File\\.Delete",
                "Process\\.Kill",
            });
            var result = BridgeProjectSettings.CSharpDenyPatterns;
            // De-duped; order preserved (first occurrence wins).
            Assert.AreEqual(2, result.Length);
            Assert.AreEqual("Process\\.Kill", result[0]);
            Assert.AreEqual("File\\.Delete", result[1]);
        }

        [Test]
        public void CSharpDenyPatterns_InvalidRegexDropped()
        {
            // An unbalanced bracket is not a valid regex — the normalizer must
            // compile-and-validate each entry and drop the bad ones, keeping
            // the valid ones.
            BridgeProjectSettings.SetCSharpDenyPatterns(new[]
            {
                "Process\\.Kill", // valid
                "[unclosed",      // invalid regex
                "File\\.Delete",  // valid
            });
            var result = BridgeProjectSettings.CSharpDenyPatterns;
            Assert.AreEqual(2, result.Length);
            CollectionAssert.AreEquivalent(new[] { "Process\\.Kill", "File\\.Delete" }, result);
        }

        // -----------------------------------------------------------------------
        // Menu deny-pattern normalization shares the same path; one test
        // confirms it is wired (not silently ignored).
        // -----------------------------------------------------------------------

        [Test]
        public void MenuDenyPatterns_NormalizesLikeCSharp()
        {
            BridgeProjectSettings.SetMenuDenyPatterns(new[] { "File/Quit", "  ", "File/Quit" });
            var result = BridgeProjectSettings.MenuDenyPatterns;
            Assert.AreEqual(1, result.Length);
            Assert.AreEqual("File/Quit", result[0]);
        }
    }
}
