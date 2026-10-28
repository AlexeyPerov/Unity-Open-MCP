using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Counts uGUI Button.onClick invocations routed by the scene's EventSystem
    // (StandaloneInputModule, which reads legacy UnityEngine.Input). A raycast
    // tap that reaches a button increments its counter; a tap absorbed by a
    // cover graphic does not.
    public sealed class LegacyInputClickCounter : MonoBehaviour
    {
        public int clicks;

        public void Increment()
        {
            clicks++;
            Debug.Log($"[LegacyInputFixture] {name} clicked ({clicks})");
        }
    }
}
