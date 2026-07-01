using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    // Multi-agent fair round-robin request queue.
    //
    // When multiple agents (distinct X-Agent-Id headers) share one bridge
    // instance, unbounded thread-pool dispatch lets a write-heavy agent starve
    // read heavy agents: every mutating call grabs the main thread for a gate
    // cycle, and with no ordering guarantee a flood of writes from agent A can
    // push agent B's cheap reads to the back of the EditorApplication.update
    // queue indefinitely.
    //
    // This queue enforces fairness by draining a bounded number of requests
    // per Editor frame, round-robin across agents:
    //   - up to N reads per frame (default 5), drained round-robin across the
    //     active agent sub-queues so every agent with a pending read makes
    //     progress every frame;
    //   - exactly 1 write per frame, serialized — while a write is in flight
    //     (its main-thread task has not completed) no other read or write is
    //     dispatched until the next frame.
    //
    // The combination guarantees a read-heavy agent is never starved by a
    // write-heavy agent sharing the same bridge: even with an unbounded write
    // backlog, the read batch runs every frame.
    //
    // Opt-in: when only ONE agent is active (the common single-agent-per-
    // process case), the queue bypasses scheduling entirely — the request is
    // dispatched to the main thread immediately, identical to the pre-queue
    // behaviour. The fair scheduler activates only when a second distinct
    // agent id arrives.
    //
    // The pure scheduling decision (which request to run next, given the per-
    // agent queues + the in-flight write flag) lives in BridgeRequestScheduler
    // so it can be unit-tested without EditorApplication / HttpListener.

    public static class BridgeRequestQueue
    {
        // A pending request: the tool dispatch parameters + a completion handle
        // the worker thread awaits. The action is invoked on the main thread
        // by the per-frame drain; it is responsible for the gate + dispatch +
        // settle wait and signals the TCS when done.
        public class PendingRequest
        {
            public readonly string AgentId;
            public readonly string ToolName;
            public readonly bool IsMutating;
            public readonly Action Work;
            public readonly DateTime ArrivedAt;
            public readonly System.Threading.Tasks.TaskCompletionSource<bool> Completion;

            public PendingRequest(string agentId, string toolName, bool isMutating, Action work)
            {
                AgentId = agentId ?? "";
                ToolName = toolName ?? "";
                IsMutating = isMutating;
                Work = work;
                ArrivedAt = DateTime.UtcNow;
                Completion = new System.Threading.Tasks.TaskCompletionSource<bool>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        // Per-agent FIFO. Insertion order of the agent ids in this dictionary
        // is preserved (Dictionary iteration is insertion-ordered in practice
        // for .NET, and we additionally track an explicit round-robin cursor).
        private static readonly Dictionary<string, Queue<PendingRequest>> _agentQueues = new();
        private static readonly List<string> _agentOrder = new();
        private static int _rrCursor = 0;
        private static bool _initialized = false;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= ProcessQueue;
            EditorApplication.update += ProcessQueue;
            _initialized = true;
        }

        /// Number of distinct agents with pending (undispatched) requests.
        public static int ActiveAgentCount
        {
            get
            {
                lock (_agentQueues)
                {
                    return _agentOrder.Count;
                }
            }
        }

        /// Whether the fair scheduler is currently active (≥2 distinct agents
        /// have ever enqueued in the current window). Exposed for diagnostics.
        public static bool IsFairSchedulingActive
        {
            get
            {
                lock (_agentQueues)
                {
                    return _agentOrder.Count >= 2;
                }
            }
        }

        /// Reset all queue state. Used by tests + on bridge stop so a stale
        /// agent set does not bleed across runs.
        public static void Reset()
        {
            lock (_agentQueues)
            {
                _agentQueues.Clear();
                _agentOrder.Clear();
                _rrCursor = 0;
            }
        }

        /// Enqueue a request and return a task the worker thread awaits. The
        /// `work` action runs on the main thread when the scheduler picks this
        /// request; the queue signals `request.Completion` after `work`
        /// returns (success → TrySetResult, throw → TrySetException) so the
        /// worker unblocks. The single-agent bypass is applied by the scheduler
        /// (PickForFrame dispatches the head request directly when only one
        /// agent is active) — no round-robin overhead for the common case.
        public static System.Threading.Tasks.Task Enqueue(
            string agentId,
            string toolName,
            bool isMutating,
            Action work)
        {
            var req = new PendingRequest(agentId, toolName, isMutating, work);

            lock (_agentQueues)
            {
                if (!_agentQueues.TryGetValue(agentId, out var queue))
                {
                    queue = new Queue<PendingRequest>();
                    _agentQueues[agentId] = queue;
                    _agentOrder.Add(agentId);
                }
                queue.Enqueue(req);
                return req.Completion.Task;
            }
        }

        // Per-frame drain. Runs on EditorApplication.update (main thread).
        private static void ProcessQueue()
        {
            if (!_initialized) return;

            // Honor the operator kill-switch: when the fair queue is disabled
            // in settings, every pending request is dispatched FIFO with no
            // per-frame batching (the pre-queue behaviour, applied uniformly).
            bool fairEnabled;
            int readsPerFrame;
            try
            {
                fairEnabled = BridgeProjectSettings.FairQueueEnabled;
                readsPerFrame = BridgeProjectSettings.FairQueueReadsPerFrame;
            }
            catch
            {
                fairEnabled = true;
                readsPerFrame = 5;
            }

            // Snapshot the dispatch plan under the lock, then run the work
            // outside the lock so a long-running gate does not block enqueues.
            List<PendingRequest> toDispatch;
            lock (_agentQueues)
            {
                if (_agentOrder.Count == 0)
                {
                    return;
                }

                toDispatch = BridgeRequestScheduler.PickForFrame(
                    _agentQueues,
                    _agentOrder,
                    ref _rrCursor,
                    fairEnabled,
                    readsPerFrame);
            }

            // Dispatch the chosen requests. Each Work action runs a gate cycle
            // on the main thread and completes within this update tick (the
            // gate + dispatch are synchronous). Reads are gate-free and cheap;
            // the write (if any) runs last and is the only mutating request in
            // the batch — the scheduler's "at most one write per frame" pick is
            // the write-serialization contract. Completion is signalled after
            // Work returns so the worker thread unblocks.
            foreach (var req in toDispatch)
            {
                var captured = req;
                try
                {
                    captured.Work();
                    captured.Completion.TrySetResult(true);
                }
                catch (Exception e)
                {
                    // The work action is responsible for its own error envelope
                    // (it builds the HTTP response). If it throws, unblock the
                    // worker with the exception so it does not hang.
                    captured.Completion.TrySetException(e);
                }
            }
        }
    }

    // Pure scheduling decision: given the per-agent queues + the round-robin
    // cursor + the per-frame limits, decide which requests to dispatch this
    // frame. Extracted so it is unit-testable without EditorApplication.
    //
    // Contract:
    //   - When fairEnabled is false, dispatch every pending request FIFO
    //     (the pre-queue behaviour). The caller still serializes by frame,
    //     but no round-robin reordering is applied.
    //   - When exactly one agent is active, dispatch that agent's head
    //     request(s) directly — no round-robin needed.
    //   - When ≥2 agents are active, dispatch up to `readsPerFrame` READS
    //     round-robin across agents, then at most ONE WRITE. The write is
    //     always last so the caller can mark it as the in-flight sentinel.
    //   - A single write per frame: once a mutating request is picked, no
    //     further requests (read or write) are picked this frame.
    public static class BridgeRequestScheduler
    {
        public static List<BridgeRequestQueue.PendingRequest> PickForFrame(
            Dictionary<string, Queue<BridgeRequestQueue.PendingRequest>> agentQueues,
            List<string> agentOrder,
            ref int rrCursor,
            bool fairEnabled,
            int readsPerFrame)
        {
            var picked = new List<BridgeRequestQueue.PendingRequest>();
            if (agentOrder.Count == 0) return picked;

            // Disabled: drain everything FIFO (capped to a sane per-frame max
            // so a huge backlog does not freeze the Editor on one frame).
            if (!fairEnabled)
            {
                const int disabledCap = 32;
                int count = 0;
                foreach (var agent in agentOrder)
                {
                    var q = agentQueues[agent];
                    while (q.Count > 0 && count < disabledCap)
                    {
                        picked.Add(q.Dequeue());
                        count++;
                    }
                }
                CompactAgentOrder(agentQueues, agentOrder, ref rrCursor);
                return picked;
            }

            // Single agent: dispatch its head request directly.
            if (agentOrder.Count == 1)
            {
                var q = agentQueues[agentOrder[0]];
                if (q.Count > 0) picked.Add(q.Dequeue());
                CompactAgentOrder(agentQueues, agentOrder, ref rrCursor);
                return picked;
            }

            // ≥2 agents: round-robin reads first, then at most one write.
            int readsPicked = 0;
            int agentsScanned = 0;
            int startCursor = rrCursor;
            int n = agentOrder.Count;

            // Pass 1 — reads (round-robin). Stop after a full sweep with no
            // pick (every agent's head is a write or its queue is empty).
            while (readsPicked < readsPerFrame && agentsScanned < n)
            {
                string agent = agentOrder[rrCursor % n];
                rrCursor = (rrCursor + 1) % n;
                agentsScanned++;

                if (!agentQueues.TryGetValue(agent, out var q) || q.Count == 0)
                {
                    continue;
                }
                var head = q.Peek();
                if (head.IsMutating)
                {
                    // Leave writes for pass 2; keep sweeping for reads from
                    // other agents so a read-heavy agent is not blocked behind
                    // a write-heavy agent's head.
                    continue;
                }
                picked.Add(q.Dequeue());
                readsPicked++;
                // Reset the sweep counter: we made progress, so a full sweep
                // for more reads is worthwhile.
                agentsScanned = 0;
            }

            // Pass 2 — one write (round-robin, starting from the current cursor).
            if (picked.Count == 0 || readsPicked < readsPerFrame)
            {
                // Only pick a write when we did not already saturate the frame
                // with reads (or when no reads were available). This keeps a
                // write-heavy agent making progress without starving reads.
                agentsScanned = 0;
                while (agentsScanned < n)
                {
                    string agent = agentOrder[rrCursor % n];
                    rrCursor = (rrCursor + 1) % n;
                    agentsScanned++;

                    if (!agentQueues.TryGetValue(agent, out var q) || q.Count == 0)
                    {
                        continue;
                    }
                    var head = q.Peek();
                    if (head.IsMutating)
                    {
                        picked.Add(q.Dequeue());
                        break; // exactly one write per frame
                    }
                }
            }

            CompactAgentOrder(agentQueues, agentOrder, ref rrCursor);
            return picked;
        }

        // Remove agents whose queues are empty so ActiveAgentCount reflects
        // only agents with pending work. Resets the cursor to 0 when the agent
        // at the cursor is removed to avoid skipping the next agent.
        private static void CompactAgentOrder(
            Dictionary<string, Queue<BridgeRequestQueue.PendingRequest>> agentQueues,
            List<string> agentOrder,
            ref int rrCursor)
        {
            for (int i = agentOrder.Count - 1; i >= 0; i--)
            {
                var agent = agentOrder[i];
                if (!agentQueues.TryGetValue(agent, out var q) || q.Count == 0)
                {
                    agentQueues.Remove(agent);
                    agentOrder.RemoveAt(i);
                    if (i <= rrCursor && rrCursor > 0) rrCursor--;
                }
            }
            if (agentOrder.Count > 0) rrCursor %= agentOrder.Count;
            else rrCursor = 0;
        }
    }
}
