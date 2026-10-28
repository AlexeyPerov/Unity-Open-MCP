// Shared play-mode scheduler: start / tick / complete, cancel, exit-play and
// reload cleanup, exclusive resources, the /playmode-operations handler, and a
// gesture driven through it against a recording Input System backend (so
// "abort leaves no pressed finger" is proven without play mode). Ticks are fed
// synthetically; the player loop is never touched.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class PlayModeSchedulerTests
    {
        private sealed class FakeOperation : PlayModeOperation
        {
            public int Ticks, Cleanups;
            public PlayModeEndReason? CleanupReason;
            public int CompleteAfter = int.MaxValue;
            public bool Throw;
            public string Resource;
            public readonly List<double> Elapsed = new List<double>();
            public override string Kind => "test_kind";
            public override string ExclusiveResource => Resource;

            protected internal override bool Tick(PlayModeFrame frame)
            {
                if (Throw) throw new System.InvalidOperationException("boom");
                Elapsed.Add(frame.ElapsedMs);
                return ++Ticks >= CompleteAfter;
            }

            protected internal override void Cleanup(PlayModeEndReason reason)
            {
                Cleanups++;
                CleanupReason = reason;
            }

            protected internal override string BuildResult(PlayModeEndReason reason, string error)
                => "{\"ticks\":" + Ticks + "}";
        }

        private sealed class RecordingBackend : IInputSystemTouchBackend
        {
            public readonly List<(int Finger, DeviceTouchPhase Phase, Vector2 Point)> Queued = new List<(int, DeviceTouchPhase, Vector2)>();
            public string PackageVersion => "test";
            public bool HasTouchscreen => true;
            public bool RespectsGameViewFocus => false;
            public void Queue(int finger, DeviceTouchPhase phase, Vector2 position) => Queued.Add((finger, phase, position));
            public void UpdateNow() { }
            public void StepFrames(int frames) { }
        }

        private IInputSystemTouchBackend savedBackend;

        [SetUp]
        public void SetUp()
        {
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = () => true;
            PlayModeScheduler.SuppressPlayerLoop = true;
            savedBackend = InputBackends.InputSystem;
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeScheduler.EndAll(PlayModeEndReason.Cancelled);
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = null;
            PlayModeScheduler.SuppressPlayerLoop = false;
            InputBackends.InputSystem = savedBackend;
        }

        private static void Tick(int frame, float dt = 0.1f, float timeScale = 1f)
            => PlayModeScheduler.TickAll(frame, dt * timeScale, dt, timeScale, 1000, 800);

        private const string Id = "op-0000-0001";

        private static void AssertElapsed(FakeOperation op, params double[] expected)
        {
            Assert.AreEqual(expected.Length, op.Elapsed.Count);
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], op.Elapsed[i], 0.001, "tick " + i);
        }

        [Test]
        public void StartRefusedOutsidePlayMode()
        {
            PlayModeScheduler.IsPlayingOverride = () => false;
            Assert.IsNull(PlayModeScheduler.Start(new FakeOperation(), Id, out var error));
            StringAssert.Contains("play_mode_required", error);
        }

        [Test]
        public void RunsToCompletionWithRealtimeElapsed()
        {
            var op = new FakeOperation { CompleteAfter = 3 };
            var snapshot = PlayModeScheduler.Start(op, Id, out var error);
            Assert.IsNull(error);
            StringAssert.Contains("\"state\":\"running\"", snapshot);
            Tick(10); Tick(11); Tick(12);
            AssertElapsed(op, 0, 100, 200);
            Assert.AreEqual(1, op.Cleanups);
            Assert.AreEqual(PlayModeEndReason.Completed, op.CleanupReason);
            var status = PlayModeScheduler.Status(Id);
            StringAssert.Contains("\"state\":\"succeeded\"", status);
            StringAssert.Contains("\"result\":{\"ticks\":3}", status);
            StringAssert.Contains("\"startFrame\":10", status);
            Assert.IsFalse(PlayModeScheduler.AnyRunning);
        }

        [Test]
        public void GameplayTimeFollowsTimeScale()
        {
            var op = new FakeOperation { TimeBase = PlayModeTimeBase.Gameplay };
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1, 0.1f, 0.5f); Tick(2, 0.1f, 0.5f); Tick(3, 0.1f, 0.5f);
            AssertElapsed(op, 0, 50, 100);
        }

        [Test]
        public void RealtimeStepIsClampedAcrossHitches()
        {
            var op = new FakeOperation();
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1); Tick(2, 5f);
            Assert.AreEqual(PlayModeScheduler.MaxFrameStepMs, op.Elapsed[1], 0.001);
        }

        [Test]
        public void CancelCleansUpOnceAndIsIdempotent()
        {
            var op = new FakeOperation();
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1);
            StringAssert.Contains("\"state\":\"cancelled\"", PlayModeScheduler.Cancel(Id));
            PlayModeScheduler.Cancel(Id);
            Tick(2);
            Assert.AreEqual(1, op.Cleanups);
            Assert.AreEqual(1, op.Ticks, "a cancelled operation is never ticked again");
        }

        [Test]
        public void ExitPlayAndReloadEndRunningOperations()
        {
            var a = new FakeOperation();
            PlayModeScheduler.Start(a, Id, out _);
            PlayModeScheduler.EndAll(PlayModeEndReason.PlayModeExited);
            Assert.AreEqual(PlayModeEndReason.PlayModeExited, a.CleanupReason);
            StringAssert.Contains("\"reason\":\"play_mode_exited\"", PlayModeScheduler.Status(Id));

            var b = new FakeOperation();
            PlayModeScheduler.Start(b, "op-0000-0002", out _);
            PlayModeScheduler.EndAll(PlayModeEndReason.DomainReload);
            Assert.AreEqual(1, b.Cleanups);
            StringAssert.Contains("\"state\":\"orphaned\"", PlayModeScheduler.Status("op-0000-0002"));
        }

        [Test]
        public void TickExceptionFailsAndCleansUp()
        {
            var op = new FakeOperation { Throw = true };
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1);
            Assert.AreEqual(1, op.Cleanups);
            var status = PlayModeScheduler.Status(Id);
            StringAssert.Contains("\"state\":\"failed\"", status);
        }

        [Test]
        public void ExclusiveResourceRefusesSecondOperation()
        {
            PlayModeScheduler.Start(new FakeOperation { Resource = "input" }, Id, out _);
            Assert.AreEqual(Id, PlayModeScheduler.RunningWithResource("input"));
            Assert.IsNull(PlayModeScheduler.Start(new FakeOperation { Resource = "input" }, "op-0000-0002", out var error));
            StringAssert.Contains("playmode_operation_busy", error);
            Assert.IsNotNull(PlayModeScheduler.Start(new FakeOperation(), "op-0000-0003", out _), "shareable ops still start");
        }

        [Test]
        public void RetriedStartReturnsTheSameRecord()
        {
            var op = new FakeOperation();
            PlayModeScheduler.Start(op, Id, out _);
            var again = PlayModeScheduler.Start(new FakeOperation(), Id, out var error);
            Assert.IsNull(error);
            StringAssert.Contains(Id, again);
        }

        [Test]
        public void HandlerValidatesAndReportsUnknownOperations()
        {
            StringAssert.Contains("invalid_arguments", PlayModeScheduler.Handle("[]"));
            StringAssert.Contains("invalid_arguments", PlayModeScheduler.Handle("{\"action\":\"status\"}"));
            StringAssert.Contains("invalid_arguments", PlayModeScheduler.Handle("{\"action\":\"status\",\"operation_id\":\"x\",\"extra\":1}"));
            StringAssert.Contains("operation_not_found", PlayModeScheduler.Handle("{\"action\":\"status\",\"operation_id\":\"op-unknown-1\"}"));
            PlayModeScheduler.Start(new FakeOperation(), Id, out _);
            StringAssert.Contains("\"state\":\"running\"", PlayModeScheduler.Handle("{\"action\":\"status\",\"operation_id\":\"" + Id + "\"}"));
            StringAssert.Contains("\"state\":\"cancelled\"", PlayModeScheduler.Handle("{\"action\":\"cancel\",\"operation_id\":\"" + Id + "\"}"));
            Assert.IsNull(PlayModeScheduler.Start(new FakeOperation(), "bad id!", out var error));
            StringAssert.Contains("invalid_arguments", error);
        }

        // --- gesture operation over the scheduler --------------------------------

        private static GestureOperation NewGesture(RecordingBackend backend, string fingers)
        {
            InputBackends.InputSystem = backend;
            var plan = GesturePlan.Parse(fingers, "screen", "realtime", out var code, out var message);
            Assert.IsNotNull(plan, message);
            plan.ResolvePoints(new Vector2(1000, 800), null, out _, out _);
            var route = new RouteDecision { Environment = new InputEnvironment() };
            route.Backends.Add(InputRouting.InputSystem);
            return new GestureOperation(plan, route, false);
        }

        [Test]
        public void GestureInjectsPhasesPerFrameAndTracesKeyframes()
        {
            var backend = new RecordingBackend();
            var op = NewGesture(backend,
                "[{\"id\":0,\"path\":[{\"at_ms\":0,\"x\":100,\"y\":100},{\"at_ms\":200,\"x\":300,\"y\":100},{\"at_ms\":400,\"x\":300,\"y\":100,\"phase\":\"hold\"},{\"at_ms\":500,\"x\":300,\"y\":300}]}]");
            PlayModeScheduler.Start(op, Id, out _);
            for (int frame = 1; frame <= 8; frame++) Tick(frame);

            var phases = backend.Queued.ConvertAll(q => q.Phase);
            Assert.AreEqual(DeviceTouchPhase.Began, phases[0]);
            Assert.AreEqual(new Vector2(100, 100), backend.Queued[0].Point);
            Assert.AreEqual(DeviceTouchPhase.Moved, phases[1]);
            Assert.AreEqual(200f, backend.Queued[1].Point.x, 0.01f, "100 ms into a 200 ms segment");
            Assert.AreEqual(DeviceTouchPhase.Stationary, phases[3], "hold reports Stationary");
            Assert.AreEqual(DeviceTouchPhase.Ended, phases[phases.Count - 1]);
            Assert.AreEqual(new Vector2(300, 300), backend.Queued[phases.Count - 1].Point);

            var status = PlayModeScheduler.Status(Id);
            StringAssert.Contains("\"state\":\"succeeded\"", status);
            StringAssert.Contains("\"backend\":[\"input_system\"]", status);
            StringAssert.Contains("\"keyframe\":3", status);
            StringAssert.Contains("\"phase\":\"ended\"", status);
            StringAssert.Contains("\"released\":true", status);
            Assert.AreEqual(phases.Count, phases.FindAll(p => p != DeviceTouchPhase.Canceled).Count, "a completed gesture needs no Canceled release");
        }

        [Test]
        public void CancelledGestureReleasesTheHeldFinger()
        {
            var backend = new RecordingBackend();
            var op = NewGesture(backend, "[{\"path\":[{\"at_ms\":0,\"x\":10,\"y\":10},{\"at_ms\":5000,\"x\":20,\"y\":20}]}]");
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1); Tick(2);
            PlayModeScheduler.Cancel(Id);
            Assert.AreEqual(DeviceTouchPhase.Canceled, backend.Queued[backend.Queued.Count - 1].Phase);
            StringAssert.Contains("\"reason\":\"cancelled\"", PlayModeScheduler.Status(Id));
        }

        [Test]
        public void ExitPlayMidGestureReleasesTheHeldFinger()
        {
            var backend = new RecordingBackend();
            var op = NewGesture(backend, "[{\"path\":[{\"at_ms\":0,\"x\":10,\"y\":10},{\"at_ms\":5000,\"x\":20,\"y\":20}]}]");
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1);
            PlayModeScheduler.EndAll(PlayModeEndReason.PlayModeExited);
            Assert.AreEqual(DeviceTouchPhase.Canceled, backend.Queued[backend.Queued.Count - 1].Phase);
            PlayModeScheduler.EndAll(PlayModeEndReason.DomainReload);
            Assert.AreEqual(1, backend.Queued.FindAll(q => q.Phase == DeviceTouchPhase.Canceled).Count, "released exactly once");
        }

        [Test]
        public void GestureNotYetStartedIsNotReleased()
        {
            var backend = new RecordingBackend();
            var op = NewGesture(backend, "[{\"path\":[{\"at_ms\":1000,\"x\":10,\"y\":10},{\"at_ms\":2000,\"x\":20,\"y\":20}]}]");
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1);
            PlayModeScheduler.Cancel(Id);
            Assert.AreEqual(0, backend.Queued.Count, "no finger was down, so nothing is injected");
        }
    }
}
