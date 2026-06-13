// Unit tests for M4.5-7 (project default gate mode) and M4.5-9 (gate run history).
using NUnit.Framework;
using UnityAgentBridge;

namespace UnityAgentBridge.Tests
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
}
