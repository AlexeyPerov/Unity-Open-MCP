// Input simulation — Input System keyboard/touch device tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_INPUTSIM_IS via the owning test asmdef's
// defineConstraints, so the suite compiles + runs when com.unity.inputsystem
// is present.
//
// Coverage: keyboard registry discovery + metadata + play-mode guards, the
// Input System touch backend registration and its Touchscreen queueing (touch
// routing itself is covered by the root Device tests). Framed delivery through
// EditorApplication.Step needs PlayMode (manual checklist).
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

        // inputsim_touch moved to the bridge root assembly (backend-aware); this
        // sub-assembly keeps the keyboard tool and registers the touch backend.
        private static readonly string[] DeviceTools =
        {
            "unity_open_mcp_inputsim_key",
        };

        [Test]
        public void TouchBackend_RegistersIntoBridgeRouting()
        {
            Assert.IsNotNull(InputBackends.InputSystem,
                "The Input System sub-assembly must register its touch backend on load.");
            Assert.AreEqual("InputSystemTouchBackend", InputBackends.InputSystem.GetType().Name);
            Assert.IsFalse(string.IsNullOrEmpty(InputBackends.InputSystem.PackageVersion));
        }

        [Test]
        public void TouchBackend_QueuesIntoATouchscreenDevice()
        {
            var previous = UnityEngine.InputSystem.Touchscreen.current;
            var touchscreen = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Touchscreen>();
            touchscreen.MakeCurrent();
            try
            {
                Assert.IsTrue(InputBackends.InputSystem.HasTouchscreen);
                InputBackends.InputSystem.Queue(0, DeviceTouchPhase.Began, new UnityEngine.Vector2(120, 80));
                UnityEngine.InputSystem.InputSystem.Update();
                Assert.IsTrue(touchscreen.primaryTouch.press.isPressed);
                Assert.AreEqual(new UnityEngine.Vector2(120, 80), touchscreen.primaryTouch.position.ReadValue());
                InputBackends.InputSystem.Queue(0, DeviceTouchPhase.Canceled, new UnityEngine.Vector2(120, 80));
                UnityEngine.InputSystem.InputSystem.Update();
                Assert.IsFalse(touchscreen.primaryTouch.press.isPressed, "Canceled must release the finger.");
            }
            finally
            {
                UnityEngine.InputSystem.InputSystem.RemoveDevice(touchscreen);
                previous?.MakeCurrent();
            }
        }

        [Test]
        public void KeyResponseReportsInputSystemBackend()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var build = typeof(InputSystemDeviceTools).GetMethod("BuildKeyOk", flags);
            var body = (string)build.Invoke(null, new object[]
            {
                UnityEngine.InputSystem.Key.Space, "tap", false, false, false, 100, 1,
                new System.Collections.Generic.List<string> { "keyDown", "keyUp" },
            });
            StringAssert.Contains("\"backend\":[\"input_system\"]", body);
        }

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
    }
}
#endif
