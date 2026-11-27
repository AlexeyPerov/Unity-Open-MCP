// inputsim_pointer routing: raycast_real — argument validation and the top-hit
// probe registration. Delivery through a real input module is verified live on
// the demo legacy-input fixture (manual checklist): it needs play mode.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using NUnit.Framework;
using UnityEngine.EventSystems;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class RaycastRealPointerTests
    {
        private static string Run(string action, PointerEventData.InputButton button = PointerEventData.InputButton.Left)
            => RaycastRealPointer.Run(action, button, 8, null, null, 10f, 10f, null, null, null, null, null, null, "auto");

        [Test]
        public void RefusesActionsWithoutATouchEquivalent()
        {
            foreach (var action in new[] { "hover", "hover_exit", "submit" })
                StringAssert.Contains("\"invalid_routing_action\"", Run(action), action);
        }

        [Test]
        public void RefusesNonLeftButtons()
        {
            StringAssert.Contains("\"invalid_button\"", Run("click", PointerEventData.InputButton.Right));
        }

        [Test]
        public void PointerGuardStillRunsFirst()
        {
            var json = PointerTools.Pointer(action: "click", screen_x: 1f, screen_y: 1f, routing: "raycast_real");
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        // The click witness: a Button handler's onClick, found from the hit
        // graphic the way the input module finds it, counted only while watched.
        [Test]
        public void ClickDelivery_WatchesTheButtonThatHandlesTheHit()
        {
            var root = new UnityEngine.GameObject("__MCPTest_ClickDelivery", typeof(UnityEngine.RectTransform), typeof(UnityEngine.UI.Button));
            var label = new UnityEngine.GameObject("Label", typeof(UnityEngine.RectTransform));
            label.transform.SetParent(root.transform, false);
            var plain = new UnityEngine.GameObject("__MCPTest_ClickDelivery_Plain");
            try
            {
                var button = root.GetComponent<UnityEngine.UI.Button>();
                var delivery = ClickDelivery.Watch(label);
                Assert.AreSame(root, delivery.ClickHandler);
                Assert.IsTrue(delivery.HasButtonWitness);
                Assert.AreEqual(false, delivery.Delivered, "no click yet");
                button.onClick.Invoke();
                Assert.AreEqual(true, delivery.Delivered);
                Assert.AreEqual(1, delivery.Clicks);
                delivery.Dispose();
                button.onClick.Invoke();
                Assert.AreEqual(1, delivery.Clicks, "the listener is removed on Dispose");

                using var none = ClickDelivery.Watch(plain);
                Assert.IsNull(none.ClickHandler);
                Assert.IsNull(none.Delivered, "nothing to witness: unknown, not false");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(plain);
            }
        }

        [Test]
        public void UiHitProbeRegistered()
        {
            Assert.IsNotNull(InputBackends.Ui, "the uGUI sub-assembly registers the gesture trace's top-hit probe");
        }
    }
}
#endif
