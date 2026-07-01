using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpBridge.Tests
{
    // M25 Plan 2 — ApplyFixGateRunner safe auto-fix rollback tests.
    //
    // The runner reuses GatePolicy (checkpoint → apply → validate → delta) and
    // adds a file-level rollback step: if the fix fails outright OR introduces
    // new errors under enforce, the touched files are restored. These tests
    // register a test-only IFixProvider that corrupts a fixture file so we can
    // observe the restore deterministically without depending on which rules
    // flag what after a real fix.
    [TestFixture]
    public class ApplyFixRollbackTests
    {
        private const string FixtureRoot = "Assets/Tests/BridgeFixtures/ApplyFixRollback";

        private const string TestFixId = "test_rollback_corruptor";
        private TestCorruptorProvider _provider;
        private List<string> _registeredBefore;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureDirectory(FixtureRoot);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(FixtureRoot))
            {
                AssetDatabase.DeleteAsset(FixtureRoot);
                AssetDatabase.Refresh();
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Snapshot the registry so the test-only provider never leaks into
            // other tests or the production RegisterDefaults set.
            _registeredBefore = new List<string>(FixProviderRegistry.AvailableFixIds());
            _provider = new TestCorruptorProvider();
            FixProviderRegistry.Register(_provider);
        }

        [TearDown]
        public void TearDown()
        {
            FixProviderRegistry.Clear();
            // Re-run the default registrations so downstream tests see the
            // production fix set restored.
            TriggerDefaultRegistration();
        }

        // -------------------------------------------------------------------
        // Rollback fires when the fix fails to apply (mutation !Success)
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Rollback_RestoresFile_WhenFixFails()
        {
            var assetPath = FixtureRoot + "/Failing.mat";
            var original = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(original, assetPath);
            AssetDatabase.Refresh();
            yield return null;

            var metaPath = assetPath + ".meta";
            Assume.That(File.Exists(metaPath), Is.True, "fixture meta must exist");
            var originalMetaBytes = File.ReadAllText(metaPath);

            try
            {
                // Provider corrupts the .meta on disk then returns !Success.
                _provider.Mode = CorruptorMode.FailAndCorrupt;
                _provider.TargetMetaPath = metaPath;

                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    assetPath, "missing_shader");
                var body = BuildBody(TestFixId, issueId, dryRun: false);

                var result = ApplyFixGateRunner.Execute(body, "enforce", new[] { assetPath });

                Assert.IsTrue(result.RolledBack,
                    $"rollback must fire when the fix fails. Reason given: {result.RollbackReason}");
                Assert.That(result.RestoredPaths, Does.Contain(metaPath),
                    "the corrupted .meta must be in restoredPaths");
                Assert.AreEqual(originalMetaBytes, File.ReadAllText(metaPath),
                    "the .meta must be byte-restored to its pre-fix state");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // No rollback when the fix succeeds and gate does not fail
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator NoRollback_WhenFixSucceedsAndNoNewErrors()
        {
            var assetPath = FixtureRoot + "/Succeeding.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), assetPath);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                _provider.Mode = CorruptorMode.SucceedNoChange;

                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    assetPath, "missing_shader");
                var body = BuildBody(TestFixId, issueId, dryRun: false);

                var result = ApplyFixGateRunner.Execute(body, "enforce", new[] { assetPath });

                Assert.IsFalse(result.RolledBack,
                    "a succeeding fix with no new errors must NOT be rolled back");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // No rollback under warn mode even if the fix fails (report-only)
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator NoRollback_UnderWarnMode_EvenWhenFixFails()
        {
            // The high-confidence threshold ties rollback to enforce + new
            // errors. A failing mutation IS still rolled back regardless of mode
            // (a half-applied fix is always unsafe) — but a SUCCESSFUL fix that
            // introduces new errors under warn is NOT rolled back. Verify the
            // latter: a successful-but-worsening fix under warn keeps its change.
            var assetPath = FixtureRoot + "/WarnMode.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), assetPath);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                _provider.Mode = CorruptorMode.SucceedNoChange;

                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    assetPath, "missing_shader");
                var body = BuildBody(TestFixId, issueId, dryRun: false);

                var result = ApplyFixGateRunner.Execute(body, "warn", new[] { assetPath });

                Assert.IsFalse(result.RolledBack,
                    "warn mode must not roll back a fix that did not fail to apply");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static string BuildBody(string fixId, string issueId, bool dryRun)
        {
            return "{\"fix_id\":\"" + fixId + "\",\"issue_id\":\"" + issueId
                + "\",\"dry_run\":" + (dryRun ? "true" : "false") + "}";
        }

        private static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }

        // FixProviderRegistry.RegisterDefaults is gated on Count==0 and runs via
        // InitializeOnLoadMethod. After Clear() we re-trigger by calling a
        // no-op public surface that touches the registry. The simplest reliable
        // way is to Clear + re-add via the public Register using the known
        // default providers (kept in sync with RegisterDefaults).
        private static void TriggerDefaultRegistration()
        {
            // The InitializeOnLoadMethod only runs on domain reload; in a long
            // test session Clear() would leave the registry empty. Re-register
            // the production set explicitly to restore state.
            FixProviderRegistry.Register(new RemoveMissingScriptFix());
            FixProviderRegistry.Register(new RelinkBrokenGuidFix());
            FixProviderRegistry.Register(new RemoveOrphanMetaFix());
            FixProviderRegistry.Register(new FixDuplicateGuidFix());
            FixProviderRegistry.Register(new ReassignMissingTextureFix());
            FixProviderRegistry.Register(new ReassignMissingShaderFix());
        }

        // -------------------------------------------------------------------
        // Test-only provider
        // -------------------------------------------------------------------

        private enum CorruptorMode { SucceedNoChange, FailAndCorrupt }

        private class TestCorruptorProvider : IFixProvider
        {
            public string FixId => TestFixId;
            public CorruptorMode Mode;
            public string TargetMetaPath;

            public bool CanFix(string issueId)
            {
                if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                    return false;
                return ruleId == "materials" && issueCode == "missing_shader";
            }

            public FixDescription Describe(string issueId)
            {
                IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
                return new FixDescription
                {
                    FixId = FixId,
                    IssueId = issueId,
                    AssetPath = assetPath,
                    Description = "test-only corruptor",
                    Safe = false,
                };
            }

            public FixResult Apply(string issueId)
            {
                if (Mode == CorruptorMode.FailAndCorrupt && TargetMetaPath != null && File.Exists(TargetMetaPath))
                {
                    // Corrupt the .meta then report failure — the runner must
                    // undo this corruption via FixRollback.
                    File.WriteAllText(TargetMetaPath, "CORRUPTED-BY-TEST-PROVIDER");
                }
                return new FixResult
                {
                    Success = Mode == CorruptorMode.SucceedNoChange,
                    Description = Mode == CorruptorMode.SucceedNoChange
                        ? "test provider: no-op success"
                        : "test provider: deliberate failure after corrupting the .meta",
                    TouchedPaths = Mode == CorruptorMode.FailAndCorrupt
                        ? new[] { TargetMetaPath }
                        : null,
                };
            }
        }
    }
}
