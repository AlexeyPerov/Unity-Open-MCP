using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class BridgeGateDefaultPolicyTests
    {
        [SetUp]
        public void SetUp()
        {
            // Ensure a clean settings state for each test.
            var data = BridgeProjectSettings.Data;
            data.defaultGateMode = BridgeGateDefaultPolicy.Enforce;
            data.disabledTools = System.Array.Empty<string>();
            BridgeProjectSettings.Save();
        }

        [Test]
        public void GetDefault_ReturnsEnforce_WhenUnset()
        {
            var data = BridgeProjectSettings.Data;
            data.defaultGateMode = null;
            BridgeProjectSettings.Save();

            Assert.AreEqual(BridgeGateDefaultPolicy.Enforce, BridgeGateDefaultPolicy.GetDefault());
        }

        [Test]
        public void GetDefault_ReturnsEnforce_WhenUnknown()
        {
            var data = BridgeProjectSettings.Data;
            data.defaultGateMode = "bogus";
            BridgeProjectSettings.Save();

            Assert.AreEqual(BridgeGateDefaultPolicy.Enforce, BridgeGateDefaultPolicy.GetDefault());
        }

        [Test]
        public void SetDefault_PersistsAndReloads()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Warn);
            Assert.AreEqual(BridgeGateDefaultPolicy.Warn, BridgeGateDefaultPolicy.GetDefault());

            // Force a reload from disk to verify persistence.
            BridgeProjectSettings.Load();
            Assert.AreEqual(BridgeGateDefaultPolicy.Warn, BridgeGateDefaultPolicy.GetDefault());

            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Off);
            BridgeProjectSettings.Load();
            Assert.AreEqual(BridgeGateDefaultPolicy.Off, BridgeGateDefaultPolicy.GetDefault());
        }

        [Test]
        public void SetDefault_IgnoresInvalidValue()
        {
            BridgeGateDefaultPolicy.SetDefault("nope");
            Assert.AreEqual(BridgeGateDefaultPolicy.Enforce, BridgeGateDefaultPolicy.GetDefault());
        }

        [Test]
        public void IsValid_OnlyAcceptsKnownModes()
        {
            Assert.IsTrue(BridgeGateDefaultPolicy.IsValid("enforce"));
            Assert.IsTrue(BridgeGateDefaultPolicy.IsValid("warn"));
            Assert.IsTrue(BridgeGateDefaultPolicy.IsValid("off"));
            Assert.IsFalse(BridgeGateDefaultPolicy.IsValid(null));
            Assert.IsFalse(BridgeGateDefaultPolicy.IsValid(""));
            Assert.IsFalse(BridgeGateDefaultPolicy.IsValid("ENFORCE"));
            Assert.IsFalse(BridgeGateDefaultPolicy.IsValid("unknown"));
        }
    }

    public class BridgeGateRunHistoryTests
    {
        [SetUp]
        public void SetUp()
        {
            BridgeGateRunHistory.Clear();
        }

        [Test]
        public void Record_TracksLatest()
        {
            var first = new BridgeGateRunRecord { ToolName = "a", EffectiveMode = "enforce", Outcome = GateOutcome.Passed };
            var second = new BridgeGateRunRecord { ToolName = "b", EffectiveMode = "warn", Outcome = GateOutcome.Warned };

            BridgeGateRunHistory.Record(first);
            Assert.AreSame(first, BridgeGateRunHistory.Latest);

            BridgeGateRunHistory.Record(second);
            Assert.AreSame(second, BridgeGateRunHistory.Latest);
            Assert.AreEqual(2, BridgeGateRunHistory.Records.Count);
        }

        [Test]
        public void Record_TrimsToCapacity()
        {
            for (int i = 0; i < BridgeGateRunHistory.Capacity + 5; i++)
            {
                BridgeGateRunHistory.Record(new BridgeGateRunRecord { ToolName = "tool_" + i });
            }
            Assert.AreEqual(BridgeGateRunHistory.Capacity, BridgeGateRunHistory.Records.Count);
            Assert.AreEqual("tool_" + (BridgeGateRunHistory.Capacity + 4), BridgeGateRunHistory.Latest.ToolName);
        }

        [Test]
        public void Clear_ResetsState()
        {
            BridgeGateRunHistory.Record(new BridgeGateRunRecord { ToolName = "a" });
            BridgeGateRunHistory.Clear();
            Assert.AreEqual(0, BridgeGateRunHistory.Count);
            Assert.IsNull(BridgeGateRunHistory.Latest);
        }
    }

    public class BridgeActivityLogTests
    {
        [SetUp]
        public void SetUp()
        {
            BridgeActivityLog.Clear();
            BridgeProjectSettings.SetVerboseActivityLog(false);
        }

        [TearDown]
        public void TearDown()
        {
            BridgeActivityLog.Clear();
            BridgeProjectSettings.SetVerboseActivityLog(false);
        }

        [Test]
        public void Record_StoresMetadata()
        {
            var evt = new BridgeActivityEvent
            {
                Kind = BridgeActivityKind.ToolRequest,
                ToolName = "unity_open_mcp_execute_csharp",
                GateMode = "enforce",
                Outcome = BridgeActivityOutcome.Success,
                HttpStatus = 200,
                DurationMs = 42
            };
            BridgeActivityLog.Record(evt);
            Assert.AreEqual(1, BridgeActivityLog.Count);
            var stored = BridgeActivityLog.Events[0];
            Assert.AreEqual("unity_open_mcp_execute_csharp", stored.ToolName);
            Assert.AreEqual("enforce", stored.GateMode);
            Assert.AreEqual(BridgeActivityOutcome.Success, stored.Outcome);
        }

        [Test]
        public void Record_StripsVerboseSnippets_WhenVerboseOff()
        {
            var evt = new BridgeActivityEvent
            {
                Kind = BridgeActivityKind.ToolRequest,
                ToolName = "t",
                RequestSnippet = "{\"code\":\"x\"}",
                ResponseSnippet = "should-not-keep"
            };
            BridgeActivityLog.Record(evt);
            Assert.IsNull(BridgeActivityLog.Events[0].RequestSnippet);
            Assert.IsNull(BridgeActivityLog.Events[0].ResponseSnippet);
        }

        [Test]
        public void Record_KeepsVerboseSnippets_WhenVerboseOn()
        {
            BridgeActivityLog.Verbose = true;
            var snippet = "{\"code\":\"hello world\"}";
            var evt = new BridgeActivityEvent
            {
                Kind = BridgeActivityKind.ToolRequest,
                ToolName = "t",
                RequestSnippet = BridgeActivityLog.TruncateSnippet(snippet)
            };
            BridgeActivityLog.Record(evt);
            Assert.IsNotNull(BridgeActivityLog.Events[0].RequestSnippet);
            StringAssert.Contains("hello world", BridgeActivityLog.Events[0].RequestSnippet);
        }

        [Test]
        public void TruncateSnippet_CapsAtMax()
        {
            var big = new string('x', BridgeActivityLog.SnippetMaxChars + 50);
            var truncated = BridgeActivityLog.TruncateSnippet(big);
            Assert.IsNotNull(truncated);
            Assert.IsTrue(truncated.Length <= BridgeActivityLog.SnippetMaxChars + 1,
                "Truncated snippet must be capped (plus ellipsis).");
            StringAssert.EndsWith("…", truncated);
        }

        [Test]
        public void TruncateSnippet_StripsControlChars()
        {
            var raw = "hello\nworld\tfoo\rbar";
            var truncated = BridgeActivityLog.TruncateSnippet(raw);
            StringAssert.DoesNotContain("\n", truncated);
            StringAssert.DoesNotContain("\t", truncated);
            StringAssert.DoesNotContain("\r", truncated);
        }

        [Test]
        public void Record_TrimsToCapacity()
        {
            for (int i = 0; i < BridgeActivityLog.Capacity + 5; i++)
            {
                BridgeActivityLog.Record(new BridgeActivityEvent
                {
                    Kind = BridgeActivityKind.ToolRequest,
                    ToolName = "tool_" + i
                });
            }
            Assert.AreEqual(BridgeActivityLog.Capacity, BridgeActivityLog.Count);
            Assert.AreEqual(BridgeActivityLog.Capacity + 5, BridgeActivityLog.TotalRecorded);
            Assert.GreaterOrEqual(BridgeActivityLog.TotalDroppedTrim, 5);
        }

        [Test]
        public void Clear_ResetsState()
        {
            BridgeActivityLog.Record(new BridgeActivityEvent { Kind = BridgeActivityKind.Ping });
            BridgeActivityLog.Clear();
            Assert.AreEqual(0, BridgeActivityLog.Count);
            Assert.AreEqual(0, BridgeActivityLog.TotalRecorded);
            Assert.AreEqual(0, BridgeActivityLog.TotalDroppedTrim);
        }
    }

    public class BridgeProjectSettingsM45Plan4Tests
    {
        [SetUp]
        public void SetUp()
        {
            var data = BridgeProjectSettings.Data;
            data.autoStart = true;
            data.verboseActivityLog = false;
            data.disabledTools = System.Array.Empty<string>();
            data.defaultGateMode = "enforce";
            BridgeProjectSettings.Save();
        }

        [Test]
        public void AutoStart_DefaultsToTrue()
        {
            // Fresh settings file (set in SetUp) → autoStart stays true.
            Assert.IsTrue(BridgeProjectSettings.AutoStart);
        }

        [Test]
        public void SetAutoStart_PersistsAndReloads()
        {
            BridgeProjectSettings.SetAutoStart(false);
            Assert.IsFalse(BridgeProjectSettings.AutoStart);
            BridgeProjectSettings.Load();
            Assert.IsFalse(BridgeProjectSettings.AutoStart);

            BridgeProjectSettings.SetAutoStart(true);
            BridgeProjectSettings.Load();
            Assert.IsTrue(BridgeProjectSettings.AutoStart);
        }

        [Test]
        public void VerboseActivityLog_DefaultsToFalse()
        {
            Assert.IsFalse(BridgeProjectSettings.VerboseActivityLog);
        }

        [Test]
        public void SetVerboseActivityLog_PersistsAndReloads()
        {
            BridgeProjectSettings.SetVerboseActivityLog(true);
            Assert.IsTrue(BridgeProjectSettings.VerboseActivityLog);
            BridgeProjectSettings.Load();
            Assert.IsTrue(BridgeProjectSettings.VerboseActivityLog);
        }

        [Test]
        public void BridgeActivityLog_VerboseDelegatesToSettings()
        {
            BridgeProjectSettings.SetVerboseActivityLog(true);
            Assert.IsTrue(BridgeActivityLog.Verbose);

            BridgeActivityLog.Verbose = false;
            Assert.IsFalse(BridgeProjectSettings.VerboseActivityLog);
            Assert.IsFalse(BridgeActivityLog.Verbose);
        }
    }
}
