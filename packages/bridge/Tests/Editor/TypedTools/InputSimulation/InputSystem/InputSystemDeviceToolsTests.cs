// Input simulation — Input System keyboard/touch device tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_INPUTSIM_IS via the owning test asmdef's
// defineConstraints, so the suite compiles + runs when com.unity.inputsystem
// is present.
//
// Coverage: registry discovery + metadata + the play-mode guard refusal for the
// new advance_frames path on tap/hold/swipe. Actual device-event delivery
// (Keyboard.current / Touchscreen.current, EditorApplication.Step) needs PlayMode.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_IS
using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class InputSystemDeviceToolsTests
    {
        private static readonly string[] DeviceTools =
        {
            "unity_open_mcp_inputsim_key",
            "unity_open_mcp_inputsim_touch",
        };

        [Test]
        public void Registry_BothDeviceToolsDiscovered()
        {
            foreach (var id in DeviceTools)
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected '{id}' to be discovered by BridgeToolRegistry.");
        }

        [Test]
        public void Registry_AllDeviceToolsAreGateFreeNonMutatingInputSimulation()
        {
            foreach (var id in DeviceTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.IsFalse(info.IsMutating, $"{id} must be non-mutating.");
                Assert.AreEqual(GateMode.Off, info.Gate);
                Assert.AreEqual(LifecyclePolicy.None, info.Lifecycle);
                Assert.AreEqual("input-simulation", info.Group);
            }
        }

        [Test]
        public void Guard_KeyTapWithAdvanceFramesRefusesOutsidePlayMode()
        {
            // K1: advance_frames path must still guard play mode.
            var json = InputSystemDeviceTools.Key(action: "tap", key: "Space", advance_frames: 3);
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_KeyHoldWithAdvanceFramesRefusesOutsidePlayMode()
        {
            var json = InputSystemDeviceTools.Key(action: "hold", key: "W", advance_frames: 10);
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_TouchSwipeWithAdvanceFramesRefusesOutsidePlayMode()
        {
            // K2: advance_frames path on swipe must guard play mode.
            var json = InputSystemDeviceTools.Touch(
                action: "swipe", from_x: 100f, from_y: 100f, to_x: 200f, to_y: 100f,
                advance_frames: 5);
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_TouchTapRefusesOutsidePlayMode()
        {
            var json = InputSystemDeviceTools.Touch(
                action: "tap", screen_x: 100f, screen_y: 100f);
            StringAssert.Contains("\"play_mode_required\"", json);
        }
    }
}
#endif
