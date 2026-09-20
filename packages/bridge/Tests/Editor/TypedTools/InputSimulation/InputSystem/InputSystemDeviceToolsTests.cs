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
        [Test]
        public void NamedReleasePreservesOtherHeldKeysAndModifiers()
        {
            var keyboard = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
            try
            {
                var method = typeof(InputSystemDeviceTools).GetMethod("QueueKeyboardState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var mods = new System.Collections.Generic.List<UnityEngine.InputSystem.Key>();
                System.Action<UnityEngine.InputSystem.Key, bool> queue = (key, pressed) =>
                {
                    method.Invoke(null, new object[] { keyboard, key, mods, pressed });
                    UnityEngine.InputSystem.InputSystem.Update();
                };
                queue(UnityEngine.InputSystem.Key.W, true);
                queue(UnityEngine.InputSystem.Key.LeftShift, true);
                queue(UnityEngine.InputSystem.Key.Space, true);
                Assert.IsTrue(keyboard.wKey.isPressed);
                queue(UnityEngine.InputSystem.Key.Space, false);
                Assert.IsFalse(keyboard.spaceKey.isPressed);
                Assert.IsTrue(keyboard.wKey.isPressed);
                Assert.IsTrue(keyboard.leftShiftKey.isPressed);
                mods.Add(UnityEngine.InputSystem.Key.LeftShift);
                queue(UnityEngine.InputSystem.Key.W, false);
                Assert.IsFalse(keyboard.wKey.isPressed);
                Assert.IsFalse(keyboard.leftShiftKey.isPressed);
            }
            finally { UnityEngine.InputSystem.InputSystem.RemoveDevice(keyboard); }
        }

        [Test]
        public void TouchResolverRejectsAmbiguityAndPrefersRootAnchoredPath()
        {
            var root = new UnityEngine.GameObject("DeviceResolverRoot");
            var child = new UnityEngine.GameObject("DeviceResolverChild");
            child.transform.SetParent(root.transform);
            var other = new UnityEngine.GameObject("DeviceResolverOuter");
            var nestedRoot = new UnityEngine.GameObject("DeviceResolverRoot");
            nestedRoot.transform.SetParent(other.transform);
            var nestedChild = new UnityEngine.GameObject("DeviceResolverChild");
            nestedChild.transform.SetParent(nestedRoot.transform);
            try
            {
                var method = typeof(InputSystemDeviceTools).GetMethod("TryResolveTarget",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                object[] args = { "DeviceResolverChild", null, null };
                Assert.IsFalse((bool)method.Invoke(null, args));
                StringAssert.Contains("ambiguous_target", args[2].ToString());
                args[0] = "DeviceResolverRoot/DeviceResolverChild";
                Assert.IsTrue((bool)method.Invoke(null, args));
                var center = typeof(InputSystemDeviceTools).GetMethod("ScreenPointOf",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.AreEqual(center.Invoke(null, new object[] { child }), args[1]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

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
