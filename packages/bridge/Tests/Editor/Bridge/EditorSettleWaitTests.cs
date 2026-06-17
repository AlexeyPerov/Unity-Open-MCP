using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M13 T4.1 — compile-settle wait helper.
    //
    // The helper blocks on BridgeSession.IsCompiling. In a fresh EditMode
    // session nothing is compiling, so Wait() must return immediately (0ms)
    // for every policy. The point of these tests is to lock that contract: a
    // settle policy on an idle editor must never wedge the dispatcher.
    public static class EditorSettleWaitTests
    {
        [Test]
        public static void Wait_None_ReturnsImmediately()
        {
            // None never settles — the policy short-circuits before reading
            // the compiling flag at all.
            var ms = EditorSettleWait.Wait(LifecyclePolicy.None);
            Assert.AreEqual(0, ms);
        }

        [Test]
        public static void Wait_CustomConfirmation_ReturnsImmediately()
        {
            // CustomConfirmation hands off to an external poller (run_tests);
            // the dispatcher does NOT settle-wait on it.
            var ms = EditorSettleWait.Wait(LifecyclePolicy.CustomConfirmation);
            Assert.AreEqual(0, ms);
        }

        [Test]
        public static void Wait_EditorSettle_IdleEditor_ReturnsImmediately()
        {
            // EditMode tests run with nothing compiling, so even a settle
            // policy must return 0ms. The bounded poll only blocks when the
            // editor is actually mid-compile.
            var ms = EditorSettleWait.Wait(LifecyclePolicy.EditorSettle);
            Assert.AreEqual(0, ms);
        }

        [Test]
        public static void Wait_RestartThenSettle_IdleEditor_ReturnsImmediately()
        {
            var ms = EditorSettleWait.Wait(LifecyclePolicy.RestartThenSettle);
            Assert.AreEqual(0, ms);
        }
    }
}
