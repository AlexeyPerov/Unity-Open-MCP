using System;
using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 T5.5 — On-disk audit log tests. Uses AuditDirOverride + the
    // auditLogEnabled setting to sandbox I/O into a per-test temp dir.
    public class BridgeAuditLogTests
    {
        string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(),
                "unity-audit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            BridgeAuditLog.AuditDirOverride = _tempDir;
            BridgeAuditLog.ResetWarningFlagForTests();

            BridgeProjectSettings.SetAuditLogEnabled(true);
        }

        [TearDown]
        public void TearDown()
        {
            BridgeProjectSettings.SetAuditLogEnabled(false);
            BridgeAuditLog.AuditDirOverride = null;
            BridgeAuditLog.ResetWarningFlagForTests();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Test]
        public void Record_WritesJsonLine_WhenEnabled()
        {
            var record = SampleRecord();
            BridgeAuditLog.Record(record);

            var file = AuditFileFor(record.ProjectHash);
            Assert.IsTrue(File.Exists(file), "audit file should exist");
            var lines = File.ReadAllLines(file);
            Assert.AreEqual(1, lines.Length, "one line per record");
            StringAssert.Contains("\"tool\":\"unity_open_mcp_execute_csharp\"", lines[0]);
            StringAssert.Contains("\"outcome\":\"passed\"", lines[0]);
            StringAssert.Contains("\"bypassedDenyList\":false", lines[0]);
        }

        [Test]
        public void Record_AppendsMultipleRecords_ToSameFile()
        {
            BridgeAuditLog.Record(SampleRecord());
            BridgeAuditLog.Record(SampleRecord());
            BridgeAuditLog.Record(SampleRecord());

            var lines = File.ReadAllLines(AuditFileFor(SampleRecord().ProjectHash));
            Assert.AreEqual(3, lines.Length);
        }

        [Test]
        public void Record_NoOp_WhenDisabled()
        {
            BridgeProjectSettings.SetAuditLogEnabled(false);
            BridgeAuditLog.Record(SampleRecord());

            Assert.IsFalse(Directory.GetFiles(_tempDir, "*.jsonl").Length > 0,
                "no audit file should be created when disabled");
        }

        [Test]
        public void Record_NoOp_WhenNullRecord()
        {
            BridgeAuditLog.Record(null);
            Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.jsonl").Length);
        }

        [Test]
        public void Record_DeniedOutcome_SerializesDeniedPattern()
        {
            var record = SampleRecord();
            record.Outcome = "denied";
            record.DeniedPattern = "EditorApplication\\.Exit";
            record.MutationErrorCode = "denied_by_policy";
            BridgeAuditLog.Record(record);

            var line = File.ReadAllText(AuditFileFor(record.ProjectHash));
            StringAssert.Contains("\"outcome\":\"denied\"", line);
            StringAssert.Contains("\"deniedPattern\":\"EditorApplication", line);
        }

        [Test]
        public void Record_BypassedFlag_Serialized()
        {
            var record = SampleRecord();
            record.BypassedDenyList = true;
            record.GateMode = "off";
            BridgeAuditLog.Record(record);

            var line = File.ReadAllText(AuditFileFor(record.ProjectHash));
            StringAssert.Contains("\"bypassedDenyList\":true", line);
            StringAssert.Contains("\"gate\":\"off\"", line);
        }

        [Test]
        public void Record_Rotates_WhenFileExceedsCap()
        {
            // Force a rotation by writing a single record whose size pushes the
            // active file past the cap. We can't easily reach MaxFileBytes (5
            // MiB) in a unit test, so this test sets up a pre-existing file at
            // the cap and verifies a new record rotates it to .1.
            var hash = SampleRecord().ProjectHash;
            var active = AuditFileFor(hash);

            // Pre-fill the active file to exactly the cap with stale content.
            var staleLine = new string('x', 1000) + "\n";
            // Need the cap in bytes; 5 MiB default. Fill with repeated lines.
            var target = BridgeAuditLog.MaxFileBytes;
            File.WriteAllText(active, "");
            using (var fs = File.Open(active, FileMode.Append, FileAccess.Write))
            using (var w = new StreamWriter(fs))
            {
                long written = 0;
                while (written < target)
                {
                    w.Write(staleLine);
                    written += staleLine.Length;
                }
            }

            BridgeAuditLog.Record(SampleRecord());

            // The original content should now live in the .1 rotation; the
            // active file should hold only the fresh record.
            var rotated = Path.Combine(_tempDir, $"audit-{hash}.1.jsonl");
            Assert.IsTrue(File.Exists(rotated), "rotated file should exist");
            var activeLines = File.ReadAllLines(active);
            Assert.AreEqual(1, activeLines.Length, "active file should hold only the fresh record");
        }

        [Test]
        public void Record_EscapesQuotesInFields()
        {
            var record = SampleRecord();
            record.MutationErrorCode = "code\"with\"quotes";
            BridgeAuditLog.Record(record);

            var line = File.ReadAllText(AuditFileFor(record.ProjectHash));
            // The embedded quotes must be escaped; the line is still valid.
            StringAssert.Contains("\"mutationError\":\"code\\\"with\\\"quotes\"", line);
        }

        string AuditFileFor(string hash) =>
            Path.Combine(_tempDir, $"audit-{hash}.jsonl");

        static BridgeAuditRecord SampleRecord()
        {
            return new BridgeAuditRecord
            {
                Timestamp = DateTime.UtcNow,
                ProjectHash = "deadbeef",
                Tool = "unity_open_mcp_execute_csharp",
                GateMode = "enforce",
                PathsHint = new[] { "Assets/Prefabs/Player.prefab" },
                Outcome = "passed",
                GateRan = true,
                NewErrors = 0,
                NewWarnings = 0,
                ResolvedErrors = 0,
                ResolvedWarnings = 0,
                CheckpointId = "cp_abc",
                TotalGateDurationMs = 123,
                MutationErrorCode = null,
                BypassedDenyList = false,
                DeniedPattern = null
            };
        }
    }
}
