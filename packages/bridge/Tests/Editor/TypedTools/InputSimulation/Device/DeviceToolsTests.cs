// Backend-aware device tools in the bridge root assembly: registry metadata,
// play-mode guards, and the target resolver they share.
using NUnit.Framework;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class DeviceToolsTests
    {
        private static readonly string[] Tools =
        {
            "unity_open_mcp_inputsim_touch",
            "unity_open_mcp_inputsim_gesture",
        };

        [Test]
        public void Registry_DeviceToolsAreGateFreeNonMutatingInputSimulation()
        {
            foreach (var id in Tools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info), id);
                Assert.IsFalse(info.IsMutating, id);
                Assert.AreEqual(GateMode.Off, info.Gate, id);
                Assert.AreEqual(LifecyclePolicy.None, info.Lifecycle, id);
                Assert.AreEqual("input-simulation", info.Group, id);
            }
        }

        [Test]
        public void Guard_TouchRefusesOutsidePlayMode()
        {
            StringAssert.Contains("\"play_mode_required\"", TouchTools.Touch(action: "tap", screen_x: 10f, screen_y: 10f));
            StringAssert.Contains("\"play_mode_required\"", TouchTools.Touch(action: "swipe", from_x: 1f, from_y: 1f, to_x: 5f, to_y: 5f, advance_frames: 3));
        }

        [Test]
        public void Guard_GestureRefusesOutsidePlayMode()
        {
            var json = GestureTools.Gesture("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]");
            StringAssert.Contains("\"play_mode_required\"", json);
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
                Assert.IsFalse(DeviceTargets.TryResolveTarget("DeviceResolverChild", out _, out var err));
                Assert.AreEqual("ambiguous_target", err.Code);
                Assert.IsTrue(DeviceTargets.TryResolveTarget("DeviceResolverRoot/DeviceResolverChild", out var point, out _));
                Assert.AreEqual(DeviceTargets.ScreenPointOf(child), point);
                Assert.IsFalse(DeviceTargets.TryResolveTarget("NoSuchObject_42", out _, out err));
                Assert.AreEqual("target_not_found", err.Code);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void SequenceFraming_LegacyNeedsFramesBetweenPhases()
        {
            var legacy = new RouteDecision();
            legacy.Backends.Add(InputRouting.LegacyTouch);
            var inputSystem = new RouteDecision();
            inputSystem.Backends.Add(InputRouting.InputSystem);
            Assert.AreEqual(1, TouchSequence.FramesBetween(legacy, 0), "legacy phases collapse without a frame between them");
            Assert.AreEqual(3, TouchSequence.FramesBetween(legacy, 3));
            Assert.AreEqual(0, TouchSequence.FramesBetween(inputSystem, 0), "Input System keeps its immediate path");
            Assert.AreEqual(60, TouchSequence.FramesBetween(inputSystem, 500));
            Assert.AreEqual(1, TouchSequence.FramesAfterLast(legacy));
            Assert.AreEqual(0, TouchSequence.FramesAfterLast(inputSystem));
        }
    }
}
