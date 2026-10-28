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

        [Test]
        public void UiHitProbeRegistered()
        {
            Assert.IsNotNull(InputBackends.Ui, "the uGUI sub-assembly registers the gesture trace's top-hit probe");
        }
    }
}
#endif
