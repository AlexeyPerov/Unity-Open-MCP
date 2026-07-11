using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 1 — pins the scroll + repaint contract for the bridge window
    // without rendering any IMGUI (an EditorWindow host cannot be laid out in
    // EditMode). The contract has two halves, both expressed as compile-time
    // structure rather than runtime GUI behavior:
    //
    //   1. SINGLE SCROLL OWNER — the shell (DrawContent) wraps every tab in one
    //      page-level BeginScrollView. Tabs must not open a second full-page
    //      scroll (nested IMGUI scrolls fight the mouse wheel). The per-tab
    //      full-page scroll fields were removed; bounded list scrolls
    //      (Gate snippets, Batch rows, Tools token-summary/pagination) stay
    //      because they are nested list regions with MaxHeight/MinHeight.
    //
    //   2. NO UNCONDITIONAL EVERY-FRAME REPAINT — the old RepaintTick()
    //      unconditionally called Repaint() on every EditorApplication.update
    //      while the window was open. It was split into:
    //        - EditorUpdateTick: repaints ONLY while the Stop-confirm transient
    //          is active (≤ 5s countdown), no-op otherwise.
    //        - OnDataChanged: the *.Changed handler — always repaints on real
    //          data changes (activity, gate runs, batch progress, settings).
    //      So an idle window (e.g. Settings) burns zero repaints from the
    //      update loop.
    //
    // If these tests fail, the scroll/repaint contract was broken: a per-tab
    // full-page scroll came back, or the unconditional repaint tick returned.
    [TestFixture]
    public class BridgeWindowScrollRepaintContractTests
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // The per-tab full-page scroll fields removed in M29 Plan 1. Re-adding
        // any of them is the smell that a nested full-page scroll returned.
        private static readonly string[] RemovedTabScrollFields =
        {
            "_gateTabScroll",
            "_activityTabScroll",
            "_settingsTabScroll",
            "_extensionsTabScroll",
            "_infoTabScroll",
            "_batchTabScroll",
        };

        [Test]
        public void NoPerTabFullScreenScrollFieldsRemain()
        {
            var windowType = typeof(UnityOpenMcpBridgeWindow);
            var fieldNames = windowType
                .GetFields(InstanceFlags)
                .Select(f => f.Name)
                .ToHashSet();

            foreach (var removed in RemovedTabScrollFields)
            {
                Assert.IsFalse(
                    fieldNames.Contains(removed),
                    $"Per-tab full-page scroll field '{removed}' is present on " +
                    $"{nameof(UnityOpenMcpBridgeWindow)}. The shell owns the single " +
                    "page scroll (DrawContent._contentScroll); tabs must not open a " +
                    "second full-page BeginScrollView (nested scrolls fight the wheel). " +
                    "Use a bounded MaxHeight/MinHeight scroll for list regions instead.");
            }
        }

        [Test]
        public void ShellRetainsSingleContentScroll()
        {
            // The shell's single page-scroll field must remain — removing it
            // would leave long tabs (Settings, Gate) with no scroll at all.
            var field = typeof(UnityOpenMcpBridgeWindow)
                .GetField("_contentScroll", InstanceFlags);
            Assert.IsNotNull(
                field,
                "_contentScroll (the shell's single page scroll) is missing. " +
                "DrawContent must wrap every tab in exactly one BeginScrollView.");
        }

        [Test]
        public void UnconditionalRepaintTickIsGone()
        {
            var windowType = typeof(UnityOpenMcpBridgeWindow);
            // The old every-frame RepaintTick must not exist.
            Assert.IsNull(
                windowType.GetMethod("RepaintTick", InstanceFlags),
                "RepaintTick (unconditional every-frame Repaint) is back. The " +
                "update handler must be EditorUpdateTick, which repaints ONLY " +
                "while the Stop-confirm transient is active.");
            // Its conditional replacement must exist.
            Assert.IsNotNull(
                windowType.GetMethod("EditorUpdateTick", InstanceFlags),
                "EditorUpdateTick (conditional Stop-confirm repaint) is missing.");
        }

        [Test]
        public void DataChangedHandlerIsWired()
        {
            // The *.Changed → repaint path must exist as a distinct handler so
            // live tabs (Activity, Batch, Gate runs) refresh on data changes.
            Assert.IsNotNull(
                typeof(UnityOpenMcpBridgeWindow).GetMethod("OnDataChanged", InstanceFlags),
                "OnDataChanged handler is missing. Data-change repaints must be " +
                "wired from the *.Changed events (see OnEnable).");
        }
    }
}
