using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Pins the documented gate precedence (packages/bridge/AGENTS.md §Gate policy):
    //   request body `gate`  >  project default (BridgeGateDefaultPolicy)  >  tool attribute.
    //
    // Regression guard: a previous version of BridgeHttpServer overrode the
    // project default with the [BridgeTool].Gate attribute for registry tools
    // when the request omitted `gate`. That silently bypassed the project-wide
    // default the user set in the Settings/Gate tab. ExtractGateMode is the
    // single source of truth for precedence steps (1)→(2); the dispatcher must
    // not re-resolve it against the tool attribute.
    public class GatePrecedenceTests
    {
        private string _previousDefault;

        [SetUp]
        public void SetUp()
        {
            _previousDefault = BridgeGateDefaultPolicy.GetDefault();
        }

        [TearDown]
        public void TearDown()
        {
            BridgeGateDefaultPolicy.SetDefault(_previousDefault);
        }

        // --- precedence step 1: explicit request `gate` always wins ---

        [Test]
        public void ExplicitRequestGate_OverridesProjectDefault_Enforce()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Off);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Enforce,
                BridgeRequestBody.ExtractGateMode("{\"gate\":\"enforce\"}"),
                "explicit request gate must win over the project default");
        }

        [Test]
        public void ExplicitRequestGate_OverridesProjectDefault_Off()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Enforce);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Off,
                BridgeRequestBody.ExtractGateMode("{\"gate\":\"off\"}"),
                "explicit request gate must win over the project default");
        }

        // --- precedence step 2: omitted request `gate` → project default ---

        [Test]
        public void NoRequestGate_FallsToProjectDefault_Warn()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Warn);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Warn,
                BridgeRequestBody.ExtractGateMode("{\"code\":\"return 1;\"}"),
                "with no request gate, the project default (warn) must apply");
        }

        [Test]
        public void NoRequestGate_FallsToProjectDefault_Off()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Off);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Off,
                BridgeRequestBody.ExtractGateMode("{\"code\":\"return 1;\"}"),
                "with no request gate, the project default (off) must apply — " +
                "registry tool attributes must NOT override it");
        }

        [Test]
        public void EmptyBody_FallsToProjectDefault()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Warn);
            Assert.AreEqual(BridgeGateDefaultPolicy.Warn, BridgeRequestBody.ExtractGateMode(""));
            Assert.AreEqual(BridgeGateDefaultPolicy.Warn, BridgeRequestBody.ExtractGateMode(null));
        }

        // --- robustness: malformed gate values fall back to project default ---

        [Test]
        public void InvalidRequestGateValue_FallsToProjectDefault()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Warn);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Warn,
                BridgeRequestBody.ExtractGateMode("{\"gate\":\"bogus\"}"),
                "an invalid gate string must fall back to the project default, not throw");
        }

        [Test]
        public void GarbledGateJson_FallsToProjectDefault()
        {
            BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.Enforce);
            Assert.AreEqual(
                BridgeGateDefaultPolicy.Enforce,
                BridgeRequestBody.ExtractGateMode("{\"gate\":}"),
                "malformed gate JSON falls back to the project default");
        }
    }
}
