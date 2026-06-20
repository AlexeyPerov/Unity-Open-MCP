using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    // M13 T4.4 — streaming notifications source.
    //
    // The bridge is otherwise pure request/response. Long ops (builds, PlayMode
    // tests, recompiles) force the agent to poll. This module is the producer
    // side of a notification channel: it captures console log entries and
    // editor state transitions (compile start/stop, play-mode) into a ring
    // buffer that the `/events` SSE endpoint and `/events/poll` JSON endpoint
    // drain.
    //
    // Subscribers are identified by an opaque string id. Each subscriber tracks
    // its own cursor over the ring buffer so a slow client never blocks a fast
    // one — it just sees fewer events. The ring evicts the oldest event when
    // full; a subscriber whose cursor falls behind the evicted tail gets a
    // `missed` count on its next drain so the loss is never silent.
    //
    // Event types (the `type` field on each event):
    //   log          — a console log entry (error/warning/log)
    //   editor_state — a compile/play-mode transition
    //
    // All Unity API access happens on the main thread via the registered
    // callbacks (Application.logMessageReceived, EditorApplication updates).
    // Subscribers drain the buffer from any thread (HTTP listener workers).
    public static class BridgeEventSource
    {
        // Cap the in-memory ring. Each event is a few hundred bytes; 1024 keeps
        // ~5 minutes of typical console chatter without unbounded growth.
        private const int BufferCapacity = 1024;

        private static readonly ConcurrentQueue<BridgeEvent> _buffer = new();
        private static int _bufferCount;
        private static long _totalEmitted;

        // Per-subscriber cursor: how many events from the head this subscriber
        // has already drained. Keyed by the opaque subscriber id assigned on
        // Subscribe().
        private static readonly ConcurrentDictionary<string, SubscriberState> _subscribers = new();

        private static bool _registered;

        // Hooked from BridgeEventSource.Initialize; unsubscribed on Stop.
        private static void LogCallback(string condition, string stackTrace, LogType type)
        {
            Emit(new BridgeEvent
            {
                Type = "log",
                LogType = type.ToString().ToLowerInvariant(),
                Message = condition,
                Stack = stackTrace,
            });
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            if (_registered) return;
            _registered = true;

            Application.logMessageReceived += LogCallback;

            // State transitions reuse the heartbeat's forced-state vocabulary so
            // the SSE stream and the on-disk heartbeat stay consistent.
            AssemblyReloadEvents.beforeAssemblyReload += () => EmitState(BridgeInstanceLock.StateReloading, true, BridgeSession.IsPlaying);
            EditorApplication.playModeStateChanged += change =>
            {
                switch (change)
                {
                    case PlayModeStateChange.ExitingEditMode:
                        EmitState(BridgeInstanceLock.StateEnteringPlaymode, true, false);
                        break;
                    case PlayModeStateChange.EnteredPlayMode:
                        EmitState(BridgeInstanceLock.StatePlaying, false, true);
                        break;
                    case PlayModeStateChange.ExitingPlayMode:
                        EmitState(BridgeInstanceLock.StateExitingPlaymode, false, true);
                        break;
                    case PlayModeStateChange.EnteredEditMode:
                        EmitState(BridgeInstanceLock.StateIdle, false, false);
                        break;
                }
            };

            // Compile-start is best-effort: EditorApplication.isCompiling flips
            // on update; emit a `compiling` transition when we first observe it.
            // The compile-finished event is the important one (frees a polling
            // agent); the compile-start is informational.
            EditorApplication.update += () =>
            {
                if (EditorApplication.isCompiling && !_compileStartEmitted)
                {
                    _compileStartEmitted = true;
                    EmitState(BridgeInstanceLock.StateCompiling, true, BridgeSession.IsPlaying);
                }
                else if (!EditorApplication.isCompiling && _compileStartEmitted)
                {
                    _compileStartEmitted = false;
                    EmitState(BridgeInstanceLock.StateIdle, false, BridgeSession.IsPlaying);
                }
            };
        }

        private static bool _compileStartEmitted;

        private static void EmitState(string state, bool isCompiling, bool isPlaying)
        {
            Emit(new BridgeEvent
            {
                Type = "editor_state",
                State = state,
                IsCompiling = isCompiling,
                IsPlaying = isPlaying,
            });
        }

        private static void Emit(BridgeEvent evt)
        {
            evt.Sequence = Interlocked.Increment(ref _totalEmitted);
            evt.Timestamp = DateTime.UtcNow;

            _buffer.Enqueue(evt);
            var countAfter = Interlocked.Increment(ref _bufferCount);

            // Evict oldest entries when over capacity. ConcurrentQueue has no
            // TryDequeue-N; drain one at a time until under cap.
            while (countAfter > BufferCapacity && _buffer.TryDequeue(out _))
            {
                countAfter = Interlocked.Decrement(ref _bufferCount);
            }
        }

        // Register a subscriber. Returns the id (caller-supplied) so the HTTP
        // layer can keep cursors across multiple polls/SSE reconnects. Idempotent
        // — re-subscribing with an existing id resets its cursor to "now".
        public static string Subscribe(string id)
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString("N");

            var state = new SubscriberState
            {
                Id = id,
                // Start at the tail of the current buffer so a fresh subscriber
                // only sees events emitted after Subscribe().
                NextSequence = _totalEmitted + 1,
            };
            _subscribers[id] = state;
            return id;
        }

        public static void Unsubscribe(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _subscribers.TryRemove(id, out _);
        }

        // Drain all events for `id` since its last drain. Returns up to
        // `maxEvents` events plus a `missed` count if any events were evicted
        // from the ring before the subscriber could read them. The drain call
        // advances the cursor; calling again immediately returns an empty list
        // unless new events arrived.
        public static DrainResult Drain(string id, int maxEvents)
        {
            if (maxEvents <= 0) maxEvents = 100;

            if (!_subscribers.TryGetValue(id, out var state))
            {
                // Lazily subscribe on first drain — convenient for HTTP clients
                // that don't want a separate subscribe round-trip.
                Subscribe(id);
                _subscribers.TryGetValue(id, out state);
            }
            if (state == null) return new DrainResult { SubscriberId = id };

            var events = new System.Collections.Generic.List<BridgeEvent>();
            long highestSeen = state.NextSequence - 1;

            foreach (var evt in _buffer)
            {
                if (evt.Sequence < state.NextSequence) continue;
                if (events.Count >= maxEvents) break;
                events.Add(evt);
                if (evt.Sequence > highestSeen) highestSeen = evt.Sequence;
            }

            // Did the ring evict events this subscriber never saw?
            long missed = 0;
            if (_buffer.Count > 0)
            {
                // Peek the oldest sequence still in the ring.
                long oldestInRing = long.MaxValue;
                foreach (var e in _buffer) { oldestInRing = e.Sequence; break; }
                if (oldestInRing != long.MaxValue && oldestInRing > state.NextSequence)
                {
                    missed = oldestInRing - state.NextSequence;
                }
            }

            state.NextSequence = highestSeen + 1;

            return new DrainResult
            {
                SubscriberId = id,
                Events = events,
                Missed = missed,
                TotalEmitted = _totalEmitted,
            };
        }

        public struct BridgeEvent
        {
            public long Sequence;
            public DateTime Timestamp;
            public string Type;        // "log" | "editor_state"
            // log fields
            public string LogType;     // "error" | "warning" | "log" | "exception" | "assert"
            public string Message;
            public string Stack;
            // editor_state fields
            public string State;       // BridgeInstanceLock.State*
            public bool IsCompiling;
            public bool IsPlaying;
        }

        public sealed class SubscriberState
        {
            public string Id;
            public long NextSequence;
        }

        public sealed class DrainResult
        {
            public string SubscriberId;
            public System.Collections.Generic.List<BridgeEvent> Events;
            public long Missed;
            public long TotalEmitted;
        }

        // Render one event as a compact JSON object. Used by both /events (SSE)
        // and /events/poll (plain JSON).
        public static string RenderEvent(BridgeEvent evt)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"seq\":").Append(evt.Sequence).Append(',');
            sb.Append("\"ts\":\"").Append(IsoUtc(evt.Timestamp)).Append("\",");
            sb.Append("\"type\":\"").Append(Escape(evt.Type)).Append('"');
            if (evt.Type == "log")
            {
                sb.Append(",\"logType\":\"").Append(Escape(evt.LogType)).Append('"');
                sb.Append(",\"message\":\"").Append(Escape(evt.Message)).Append('"');
                if (!string.IsNullOrEmpty(evt.Stack))
                    sb.Append(",\"stack\":\"").Append(Escape(evt.Stack)).Append('"');
            }
            else
            {
                sb.Append(",\"state\":\"").Append(Escape(evt.State)).Append('"');
                sb.Append(",\"isCompiling\":").Append(evt.IsCompiling ? "true" : "false");
                sb.Append(",\"isPlaying\":").Append(evt.IsPlaying ? "true" : "false");
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static string RenderDrain(DrainResult result)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"subscriberId\":\"").Append(Escape(result.SubscriberId)).Append('"');
            sb.Append(",\"events\":[");
            if (result.Events != null)
            {
                for (int i = 0; i < result.Events.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(RenderEvent(result.Events[i]));
                }
            }
            sb.Append(']');
            sb.Append(",\"count\":").Append(result.Events?.Count ?? 0);
            sb.Append(",\"missed\":").Append(result.Missed);
            sb.Append(",\"totalEmitted\":").Append(result.TotalEmitted);
            sb.Append('}');
            return sb.ToString();
        }

        // Test surface — clears the buffer and subscriber state. Only used by
        // EditMode tests to get a deterministic starting point.
        internal static void ResetForTests()
        {
            while (_buffer.TryDequeue(out _)) { }
            _bufferCount = 0;
            _totalEmitted = 0;
            _subscribers.Clear();
        }

        // Test surface — emit a synthetic event (e.g. a fake log line) so a
        // test can assert drain behavior without waiting on Unity log delivery.
        internal static void EmitForTests(string type, string message)
        {
            if (type == "log")
            {
                Emit(new BridgeEvent { Type = "log", LogType = "log", Message = message });
            }
            else
            {
                Emit(new BridgeEvent { Type = "editor_state", State = message ?? "idle", IsCompiling = false, IsPlaying = false });
            }
        }

        public static int BufferCount => _bufferCount;
        public static long TotalEmitted => _totalEmitted;

        private static string IsoUtc(DateTime dt) =>
            dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ");

        private static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
