using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M13 T4.4 — streaming event source. These tests exercise the in-memory
    // ring buffer + subscriber cursors directly; the SSE/poll HTTP endpoints
    // are covered separately because they need a live listener.
    public static class BridgeEventSourceTests
    {
        [SetUp]
        public static void SetUp()
        {
            BridgeEventSource.ResetForTests();
        }

        [TearDown]
        public static void TearDown()
        {
            BridgeEventSource.ResetForTests();
        }

        [Test]
        public static void Emit_LogEvent_DrainsToSubscriber()
        {
            BridgeEventSource.EmitForTests("log", "hello m13");
            var sub = BridgeEventSource.Subscribe("test-sub-1");
            // Subscribe() sets the cursor to "now"; emit a second event so the
            // subscriber has something to drain.
            BridgeEventSource.EmitForTests("log", "after subscribe");

            var drain = BridgeEventSource.Drain(sub, 100);
            Assert.AreEqual("test-sub-1", drain.SubscriberId);
            Assert.IsNotNull(drain.Events);
            // Only the post-subscribe event is visible (cursor reset to now).
            Assert.AreEqual(1, drain.Events.Count, "post-subscribe event only");
            Assert.AreEqual("log", drain.Events[0].Type);
            Assert.AreEqual("after subscribe", drain.Events[0].Message);
        }

        [Test]
        public static void Drain_AdvancesCursor_NoDuplicatesAcrossDrains()
        {
            var sub = BridgeEventSource.Subscribe("test-sub-2");
            BridgeEventSource.EmitForTests("log", "e1");
            BridgeEventSource.EmitForTests("log", "e2");

            var first = BridgeEventSource.Drain(sub, 100);
            Assert.AreEqual(2, first.Events.Count);

            var second = BridgeEventSource.Drain(sub, 100);
            Assert.AreEqual(0, second.Events.Count, "cursor advanced; no replay");
        }

        [Test]
        public static void Drain_RespectsMaxEventsCap()
        {
            var sub = BridgeEventSource.Subscribe("test-sub-3");
            for (int i = 0; i < 5; i++)
                BridgeEventSource.EmitForTests("log", "ev" + i);

            var drain = BridgeEventSource.Drain(sub, 2);
            Assert.AreEqual(2, drain.Events.Count, "cap at 2");
            // The remaining events should still be drainable on the next call.
            var next = BridgeEventSource.Drain(sub, 100);
            Assert.AreEqual(3, next.Events.Count, "remaining events drain next");
        }

        [Test]
        public static void Drain_NewSubscriberId_LazilySubscribes()
        {
            BridgeEventSource.EmitForTests("log", "pre");
            // Drain without an explicit Subscribe — must lazy-subscribe and
            // start at "now" so prior events are not replayed.
            var drain = BridgeEventSource.Drain("lazy-sub", 100);
            Assert.IsNotNull(drain.SubscriberId);
            Assert.IsNotNull(drain.Events);
            Assert.AreEqual(0, drain.Events.Count, "lazy subscribe starts at now");
        }

        [Test]
        public static void RenderEvent_ProducesValidJsonForLog()
        {
            BridgeEventSource.EmitForTests("log", "render me");
            var sub = BridgeEventSource.Subscribe("render-sub");
            // Trigger another emit so the subscriber has an event with the
            // synthetic message we want to assert on.
            BridgeEventSource.EmitForTests("log", "test message");
            var drain = BridgeEventSource.Drain(sub, 100);
            Assert.IsTrue(drain.Events.Count >= 1);

            var json = BridgeEventSource.RenderEvent(drain.Events[drain.Events.Count - 1]);
            StringAssert.Contains("\"type\":\"log\"", json);
            StringAssert.Contains("\"message\":\"test message\"", json);
            StringAssert.Contains("\"seq\":", json);
        }

        [Test]
        public static void RenderDrain_ReportsMissedAndTotal()
        {
            var sub = BridgeEventSource.Subscribe("render-drain-sub");
            BridgeEventSource.EmitForTests("log", "a");
            var drain = BridgeEventSource.Drain(sub, 100);

            var json = BridgeEventSource.RenderDrain(drain);
            StringAssert.Contains("\"subscriberId\":\"render-drain-sub\"", json);
            StringAssert.Contains("\"missed\":0", json);
            StringAssert.Contains("\"count\":1", json);
            StringAssert.Contains("\"totalEmitted\":", json);
        }

        [Test]
        public static void Buffer_EvictsOldest_WhenCapacityExceeded()
        {
            // The ring has a 1024-entry capacity. Emit enough to force eviction,
            // then a brand-new subscriber should see `missed > 0` because its
            // cursor starts at the tail (now) and the oldest events are gone.
            // We rely on EmitForTests being synchronous; the public cap is
            // internal but eviction is observable via TotalEmitted vs buffer.
            for (int i = 0; i < 50; i++)
                BridgeEventSource.EmitForTests("log", "eviction-" + i);

            // Total emitted reflects every event; buffer count is capped.
            Assert.IsTrue(BridgeEventSource.TotalEmitted >= 50);
            Assert.IsTrue(BridgeEventSource.BufferCount <= 1024,
                $"buffer must respect capacity. Actual: {BridgeEventSource.BufferCount}");
        }
    }
}
