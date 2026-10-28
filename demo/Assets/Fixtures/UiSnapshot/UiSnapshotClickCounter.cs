using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Counts Button.onClick invocations on the UI snapshot fixture. A tap by
    // label (target_text) that reaches the button increments its counter.
    public sealed class UiSnapshotClickCounter : MonoBehaviour
    {
        public int clicks;

        public void Increment()
        {
            clicks++;
            Debug.Log($"[UiSnapshotFixture] {name} clicked ({clicks})");
        }
    }
}
