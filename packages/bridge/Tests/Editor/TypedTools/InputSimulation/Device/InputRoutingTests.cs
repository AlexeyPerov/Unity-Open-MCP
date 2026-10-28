// Backend routing for device-level input simulation: the version-table adapter,
// the auto rule for every activeInputHandler value, and the stable refusals
// (input_backend_unavailable, legacy_multitouch_unsupported,
// unsupported_editor_version, no_device). Pure — no play mode needed.
using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class InputRoutingTests
    {
        private static InputEnvironment Env(int handler, bool touchscreen = false, bool versionOk = true,
            bool entryPoint = true, bool isPackage = true)
        {
            return new InputEnvironment
            {
                ActiveInputHandler = handler,
                LegacyEnabled = handler == 0 || handler == 2,
                InputSystemEnabled = handler == 1 || handler == 2,
                InputSystemPackage = isPackage && handler != 0,
                InputSystemVersion = "1.19.0",
                Touchscreen = touchscreen,
                UnityVersion = versionOk ? "6000.3.21f1" : "2022.3.40f1",
                EditorVersionSupported = versionOk,
                EditorVersionDetail = versionOk ? "ok" : "Unity 2022.3.40f1 is not in the table.",
                LegacyEntryPoint = entryPoint,
                LegacyEntryPointError = entryPoint ? null : "missing",
            };
        }

        private static readonly int[] OneFinger = { 0 };

        [Test]
        public void LegacyOnly_AutoRoutesToLegacyTouch()
        {
            var d = InputRouting.Decide(Env(0), "auto", OneFinger);
            Assert.IsTrue(d.Ok, d.ErrorMessage);
            CollectionAssert.AreEqual(new[] { "legacy_touch" }, d.Backends);
            Assert.AreEqual("[\"legacy_touch\"]", d.BackendJson());
        }

        [Test]
        public void Both_WithTouchscreen_InjectsIntoBoth()
        {
            var d = InputRouting.Decide(Env(2, touchscreen: true), "auto", OneFinger);
            Assert.IsTrue(d.Ok);
            CollectionAssert.AreEqual(new[] { "legacy_touch", "input_system" }, d.Backends);
            Assert.AreEqual(0, d.Skipped.Count);
        }

        [Test]
        public void Both_WithoutTouchscreen_LegacyOnlyAndSkipsInputSystem()
        {
            var d = InputRouting.Decide(Env(2), "auto", OneFinger);
            Assert.IsTrue(d.Ok);
            CollectionAssert.AreEqual(new[] { "legacy_touch" }, d.Backends);
            Assert.AreEqual("input_system", d.Skipped[0].Key);
            Assert.AreEqual("no_touchscreen", d.Skipped[0].Value);
            StringAssert.Contains("no_touchscreen", d.SkippedJson());
        }

        [Test]
        public void InputSystemOnly_NoTouchscreen_RefusesWithDetectedConfiguration()
        {
            var d = InputRouting.Decide(Env(1), "auto", OneFinger);
            Assert.AreEqual("input_backend_unavailable", d.ErrorCode);
            var json = d.ErrorJson();
            StringAssert.Contains("\"code\":\"input_backend_unavailable\"", json);
            StringAssert.Contains("\"detected\":{\"activeInputHandler\":1", json);
            StringAssert.Contains("\"touchscreen\":false", json);
            StringAssert.Contains("agentNextSteps", json);
            StringAssert.Contains("game_view set_device", json);
        }

        [Test]
        public void LegacyOnly_UnverifiedEditor_RefusesUnsupportedEditorVersion()
        {
            var d = InputRouting.Decide(Env(0, versionOk: false), "auto", OneFinger);
            Assert.AreEqual("unsupported_editor_version", d.ErrorCode);
            StringAssert.Contains("unsupported_editor_version", d.ErrorJson());
        }

        [Test]
        public void LegacyOnly_MissingEntryPoint_RefusesUnavailable()
        {
            var d = InputRouting.Decide(Env(0, entryPoint: false), "auto", OneFinger);
            Assert.AreEqual("input_backend_unavailable", d.ErrorCode);
            Assert.AreEqual("legacy_entry_point_missing", d.Skipped[0].Value);
        }

        [Test]
        public void LegacyRoute_SecondFingerRefuses()
        {
            var d = InputRouting.Decide(Env(0), "auto", new List<int> { 0, 1 });
            Assert.AreEqual("legacy_multitouch_unsupported", d.ErrorCode);
            d = InputRouting.Decide(Env(2, touchscreen: true), "auto", new[] { 1 });
            Assert.AreEqual("legacy_multitouch_unsupported", d.ErrorCode,
                "auto with Both still routes to legacy, so a non-zero finger refuses.");
        }

        [Test]
        public void ExplicitInputSystem_AllowsMultiTouch()
        {
            var d = InputRouting.Decide(Env(2, touchscreen: true), "input_system", new List<int> { 0, 1 });
            Assert.IsTrue(d.Ok);
            CollectionAssert.AreEqual(new[] { "input_system" }, d.Backends);
        }

        [Test]
        public void ExplicitInputSystem_NoTouchscreen_KeepsNoDeviceCode()
        {
            var d = InputRouting.Decide(Env(2), "input_system", OneFinger);
            Assert.AreEqual("no_device", d.ErrorCode);
        }

        [Test]
        public void ExplicitLegacy_InInputSystemOnlyProject_RefusesUnavailable()
        {
            var d = InputRouting.Decide(Env(1, touchscreen: true), "legacy_touch", OneFinger);
            Assert.AreEqual("input_backend_unavailable", d.ErrorCode);
            Assert.AreEqual("legacy_input_disabled", d.Skipped[0].Value);
        }

        [Test]
        public void ExplicitInputSystem_InLegacyOnlyProject_RefusesUnavailable()
        {
            var d = InputRouting.Decide(Env(0), "input_system", OneFinger);
            Assert.AreEqual("input_backend_unavailable", d.ErrorCode);
        }

        [Test]
        public void UnknownBackend_Refuses()
        {
            Assert.AreEqual("invalid_backend", InputRouting.Decide(Env(2), "gameview", OneFinger).ErrorCode);
        }

        // --- version table adapter -------------------------------------------

        [TestCase("6000.3.21f1", true)]
        [TestCase("6000.0.23f1", true)]
        [TestCase("6000.5.0a1", true)]
        [TestCase("2022.3.40f1", false)]
        [TestCase("2023.2.20f1", false)]
        [TestCase("2021.3.1f1", false)]
        [TestCase("7000.0.0f1", false)]
        [TestCase("garbage", false)]
        [TestCase("", false)]
        public void VersionTable(string version, bool supported)
        {
            Assert.AreEqual(supported, EditorInputAdapter.IsVersionSupported(version, out var detail), detail);
            Assert.IsFalse(string.IsNullOrEmpty(detail));
        }

        [Test]
        public void VersionParse_HandlesSuffixes()
        {
            Assert.IsTrue(EditorInputAdapter.TryParseVersion("6000.3.21f1", out var major, out var minor));
            Assert.AreEqual(6000, major);
            Assert.AreEqual(3, minor);
            Assert.IsTrue(EditorInputAdapter.TryParseVersion("2022.3", out major, out minor));
            Assert.AreEqual(2022, major);
            Assert.IsFalse(EditorInputAdapter.TryParseVersion("6000", out _, out _));
        }

        [Test]
        public void Adapter_ResolvesEntryPointOnThisEditor()
        {
            Assert.IsTrue(EditorInputAdapter.TryResolve(out var error), error);
        }

        [Test]
        public void Adapter_VersionOverrideRefusesUnverifiedEditor()
        {
            EditorInputAdapter.UnityVersionOverride = "2022.3.40f1";
            try
            {
                Assert.IsFalse(EditorInputAdapter.IsAvailable(out var code, out _));
                Assert.AreEqual("unsupported_editor_version", code);
                var env = InputEnvironment.Detect();
                Assert.IsFalse(env.EditorVersionSupported);
            }
            finally { EditorInputAdapter.UnityVersionOverride = null; }
        }

        [Test]
        public void Detect_ReflectsThisEditor()
        {
            var env = InputEnvironment.Detect();
            Assert.AreEqual(env.LegacyEnabled && env.InputSystemEnabled ? 2 : env.InputSystemEnabled ? 1 : 0, env.ActiveInputHandler);
            StringAssert.Contains("\"activeInputHandler\":", env.ToJson());
            StringAssert.Contains("\"focusRequirement\":", env.ToJson());
        }
    }
}
