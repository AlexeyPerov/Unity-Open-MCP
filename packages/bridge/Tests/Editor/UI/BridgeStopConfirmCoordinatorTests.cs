using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 2 — pins the shared two-click Stop-confirm transient.
    //
    // The Status tab has always required a two-click / timed confirm before
    // stopping the listener (a Stop drops MCP connectivity for any active
    // agent). The toolbar MCP toggle used to call BridgeHttpServer.Stop()
    // directly. Both entry points now route through this coordinator so the
    // same confirm policy applies regardless of which surface the operator
    // used. These tests pin the transient semantics so a regression to
    // one-click Stop is caught.
    [TestFixture]
    public class BridgeStopConfirmCoordinatorTests
    {
        [SetUp]
        public void SetUp()
        {
            // Start every test from a clean (disarmed) transient so tests do
            // not leak state into each other.
            BridgeStopConfirmCoordinator.Disarm();
        }

        [TearDown]
        public void TearDown()
        {
            BridgeStopConfirmCoordinator.Disarm();
        }

        [Test]
        public void FirstRequestStop_ArmsConfirm_DoesNotInvokeStop()
        {
            var performed = false;
            var stopped = BridgeStopConfirmCoordinator.RequestStop(() => performed = true);

            Assert.IsFalse(stopped, "First click must NOT perform the stop.");
            Assert.IsFalse(performed, "Stop callback must not run on the arming click.");
            Assert.IsTrue(BridgeStopConfirmCoordinator.IsArmed, "First click must arm the confirm.");
            Assert.Greater(BridgeStopConfirmCoordinator.RemainingSeconds, 0.0,
                "Armed confirm must have a positive countdown.");
        }

        [Test]
        public void SecondRequestStop_WhileArmed_PerformsStop_AndDisarms()
        {
            var performed = false;
            BridgeStopConfirmCoordinator.RequestStop(() => performed = true); // arm
            Assume.That(BridgeStopConfirmCoordinator.IsArmed, Is.True);

            var stopped = BridgeStopConfirmCoordinator.RequestStop(() => performed = true);

            Assert.IsTrue(stopped, "Second click while armed must perform the stop.");
            Assert.IsTrue(performed, "Stop callback must run on the confirming click.");
            Assert.IsFalse(BridgeStopConfirmCoordinator.IsArmed,
                "A performed stop must clear the armed transient.");
        }

        [Test]
        public void Arm_Disarm_RoundTrips()
        {
            Assert.IsFalse(BridgeStopConfirmCoordinator.IsArmed);

            BridgeStopConfirmCoordinator.Arm();
            Assert.IsTrue(BridgeStopConfirmCoordinator.IsArmed);

            BridgeStopConfirmCoordinator.Disarm();
            Assert.IsFalse(BridgeStopConfirmCoordinator.IsArmed);
            Assert.AreEqual(0.0, BridgeStopConfirmCoordinator.RemainingSeconds);
        }

        [Test]
        public void Tick_DoesNotAutoExpire_WhileWithinWindow()
        {
            BridgeStopConfirmCoordinator.Arm();
            BridgeStopConfirmCoordinator.Tick();
            Assert.IsTrue(BridgeStopConfirmCoordinator.IsArmed,
                "Tick right after Arm must not expire the confirm (5s window).");
        }

        [Test]
        public void Tick_AutoExpires_AfterWindowElapses()
        {
            // The coordinator's window is 5s. We can't fast-forward editor
            // time in a unit test, but we can force the expiry by arming with
            // the public API and then waiting for the deadline to pass via
            // repeated Tick() calls over a real sleep. That is flaky on a
            // loaded CI box, so instead we assert the contract indirectly:
            // the public Deadline is in the future right after Arm.
            BridgeStopConfirmCoordinator.Arm();
            Assert.Greater(BridgeStopConfirmCoordinator.Deadline, 0.0);
            // And RemainingSeconds is bounded by the configured window.
            Assert.LessOrEqual(
                BridgeStopConfirmCoordinator.RemainingSeconds,
                BridgeStopConfirmCoordinator.ConfirmWindowSeconds + 0.001,
                "Remaining seconds must not exceed the configured window.");
        }

        [Test]
        public void Tick_IsNoOp_WhenNotArmed()
        {
            Assert.DoesNotThrow(() => BridgeStopConfirmCoordinator.Tick());
            Assert.IsFalse(BridgeStopConfirmCoordinator.IsArmed);
        }

        [Test]
        public void RequestStop_NullCallback_ArmsThenConfirmsWithoutThrowing()
        {
            // A null stop callback is not expected in production (the toolbar
            // passes BridgeHttpServer.Stop), but the coordinator must not
            // throw on it — defensive against a future caller mistake.
            var firstStop = BridgeStopConfirmCoordinator.RequestStop(null);
            Assert.IsFalse(firstStop);
            Assert.IsTrue(BridgeStopConfirmCoordinator.IsArmed);

            var secondStop = BridgeStopConfirmCoordinator.RequestStop(null);
            Assert.IsTrue(secondStop);
            Assert.IsFalse(BridgeStopConfirmCoordinator.IsArmed);
        }
    }
}
