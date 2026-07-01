using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Fair round-robin request scheduler (multi-agent scheduling primitive).
    //
    // The scheduler is a pure decision over per-agent queues + a round-robin
    // cursor: given the pending requests, pick which to dispatch this frame.
    // These tests lock the fairness contract:
    //   - single agent → head-of-queue bypass (no round-robin overhead),
    //   - ≥2 agents → up to N reads/frame round-robin + exactly 1 write/frame,
    //   - a write-heavy agent cannot starve a read-heavy agent,
    //   - exactly one mutating request per frame (write serialization).
    //
    // The runtime queue (BridgeRequestQueue) wraps this decision with
    // EditorApplication.update ticking + a write-in-flight sentinel; that layer
    // is integration-tested separately. Here we drive the pure scheduler
    // directly so the fairness assertions are deterministic and fast.
    public static class BridgeRequestQueueTests
    {
        private static BridgeRequestQueue.PendingRequest Read(string agent, string tag = "read")
        {
            return new BridgeRequestQueue.PendingRequest(agent, tag, false, () => { });
        }

        private static BridgeRequestQueue.PendingRequest Write(string agent, string tag = "write")
        {
            return new BridgeRequestQueue.PendingRequest(agent, tag, true, () => { });
        }

        private static Dictionary<string, Queue<BridgeRequestQueue.PendingRequest>> NewQueues()
        {
            return new Dictionary<string, Queue<BridgeRequestQueue.PendingRequest>>();
        }

        private static void Enqueue(
            Dictionary<string, Queue<BridgeRequestQueue.PendingRequest>> queues,
            List<string> order,
            params BridgeRequestQueue.PendingRequest[] reqs)
        {
            foreach (var r in reqs)
            {
                if (!queues.TryGetValue(r.AgentId, out var q))
                {
                    q = new Queue<BridgeRequestQueue.PendingRequest>();
                    queues[r.AgentId] = q;
                    order.Add(r.AgentId);
                }
                q.Enqueue(r);
            }
        }

        // -------------------------------------------------------------------
        // Single-agent bypass.
        // -------------------------------------------------------------------

        [Test]
        public static void SingleAgent_DispatchesHeadReadImmediately()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order, Read("A", "r1"), Read("A", "r2"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            Assert.AreEqual(1, picked.Count);
            Assert.AreEqual("r1", picked[0].ToolName);
            // The dequeued request is gone; r2 remains.
            Assert.AreEqual(1, queues["A"].Count);
        }

        [Test]
        public static void SingleAgent_DispatchesHeadWriteImmediately()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order, Write("A", "w1"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            Assert.AreEqual(1, picked.Count);
            Assert.AreEqual("w1", picked[0].ToolName);
        }

        // -------------------------------------------------------------------
        // Multi-agent read batching (round-robin).
        // -------------------------------------------------------------------

        [Test]
        public static void TwoAgents_ReadsAreRoundRobinAcrossAgents()
        {
            // Agent A has 3 reads; agent B has 3 reads. With readsPerFrame=5,
            // the scheduler must interleave: A, B, A, B, A (round-robin), NOT
            // drain all of A first.
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Read("A", "a1"), Read("A", "a2"), Read("A", "a3"),
                Read("B", "b1"), Read("B", "b2"), Read("B", "b3"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            Assert.AreEqual(5, picked.Count);
            var agents = new List<string> { picked[0].AgentId, picked[1].AgentId, picked[2].AgentId, picked[3].AgentId, picked[4].AgentId };
            // The first two picks must be different agents (round-robin, not
            // drain-one-then-the-other).
            Assert.AreNotEqual(agents[0], agents[1]);
            // Both agents must be represented in the batch.
            CollectionAssert.Contains(agents, "A");
            CollectionAssert.Contains(agents, "B");
        }

        [Test]
        public static void TwoAgents_ReadBatchDoesNotExceedLimit()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Read("A", "a1"), Read("A", "a2"), Read("A", "a3"),
                Read("B", "b1"), Read("B", "b2"), Read("B", "b3"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 2);

            Assert.AreEqual(2, picked.Count);
        }

        // -------------------------------------------------------------------
        // Write serialization — exactly one write per frame.
        // -------------------------------------------------------------------

        [Test]
        public static void TwoAgents_AtMostOneWritePerFrame()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Write("A", "w1"), Write("A", "w2"),
                Write("B", "w3"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            int writeCount = 0;
            foreach (var p in picked)
            {
                if (p.IsMutating) writeCount++;
            }
            Assert.LessOrEqual(writeCount, 1, "at most one write per frame");
        }

        [Test]
        public static void TwoAgents_WriteIsDispatchedWhenNoReadsAvailable()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order, Write("A", "w1"), Write("B", "w2"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            Assert.AreEqual(1, picked.Count);
            Assert.IsTrue(picked[0].IsMutating);
        }

        // -------------------------------------------------------------------
        // Non-starvation: a read-heavy agent makes progress every frame even
        // while a write-heavy agent backlogs writes.
        // -------------------------------------------------------------------

        [Test]
        public static void WriteHeavyAgentDoesNotStarveReadHeavyAgent()
        {
            // Agent A has a deep backlog of writes; agent B has reads. Across
            // several frames, B's reads must be picked every frame (the read
            // batch runs before the write slot), while A's writes trickle out
            // one per frame.
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Write("A", "aw1"), Write("A", "aw2"), Write("A", "aw3"), Write("A", "aw4"),
                Read("B", "br1"), Read("B", "br2"), Read("B", "br3"), Read("B", "br4"));
            int cursor = 0;

            int bReadsPickedTotal = 0;
            int aWritesPickedTotal = 0;
            // Simulate 4 frames.
            for (int frame = 0; frame < 4; frame++)
            {
                var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);
                foreach (var p in picked)
                {
                    if (p.AgentId == "B") bReadsPickedTotal++;
                    if (p.AgentId == "A" && p.IsMutating) aWritesPickedTotal++;
                }
            }

            // B's 4 reads must ALL have been picked across 4 frames (non-
            // starvation). A's writes progress at most one per frame.
            Assert.AreEqual(4, bReadsPickedTotal, "B's reads must all complete within 4 frames");
            Assert.LessOrEqual(aWritesPickedTotal, 4, "A's writes trickle out one per frame");
            Assert.GreaterOrEqual(aWritesPickedTotal, 1, "A's writes must make some progress");
        }

        [Test]
        public static void MixedBacklog_ReadsRunBeforeWriteInSameFrame()
        {
            // Agent A has a read + a write; agent B has a read. The read batch
            // runs first, so both reads should be picked before A's write.
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Read("A", "ar1"), Write("A", "aw1"),
                Read("B", "br1"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            // Two reads + at most one write.
            int reads = 0, writes = 0;
            foreach (var p in picked)
            {
                if (p.IsMutating) writes++;
                else reads++;
            }
            Assert.AreEqual(2, reads, "both reads picked");
            Assert.LessOrEqual(writes, 1, "at most one write");
        }

        // -------------------------------------------------------------------
        // Disabled (fairEnabled=false): FIFO drain, no round-robin.
        // -------------------------------------------------------------------

        [Test]
        public static void Disabled_DrainsFifoCappedPerFrame()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order,
                Read("A", "a1"), Write("A", "a2"),
                Read("B", "b1"), Write("B", "b2"));
            int cursor = 0;

            var picked = BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, false, 5);

            // All 4 dispatched (under the disabled cap of 32), in FIFO order.
            Assert.AreEqual(4, picked.Count);
            Assert.AreEqual("a1", picked[0].ToolName);
            Assert.AreEqual("a2", picked[1].ToolName);
            Assert.AreEqual("b1", picked[2].ToolName);
            Assert.AreEqual("b2", picked[3].ToolName);
        }

        // -------------------------------------------------------------------
        // Compaction: empty agent queues are removed from the order.
        // -------------------------------------------------------------------

        [Test]
        public static void EmptyAgentQueueIsRemovedAfterDrain()
        {
            var queues = NewQueues();
            var order = new List<string>();
            Enqueue(queues, order, Read("A", "a1"), Read("B", "b1"));
            int cursor = 0;

            BridgeRequestScheduler.PickForFrame(queues, order, ref cursor, true, 5);

            // Both queues are now empty → both agents removed from the order.
            Assert.AreEqual(0, order.Count);
        }
    }
}
