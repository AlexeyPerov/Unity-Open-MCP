using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Counts Button.onClick invocations on the safe-area fixture, so a tap in
    // the Device Simulator (legacy touch or target_text) can be confirmed.
    public sealed class SafeAreaTapCounter : MonoBehaviour
    {
        public int clicks;

        public void Increment()
        {
            clicks++;
            Debug.Log($"[SafeAreaFixture] {name} clicked ({clicks})");
        }
    }
}
