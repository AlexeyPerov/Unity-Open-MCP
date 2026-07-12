using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 2 — pins the pure connection-strip builder.
    //
    // The Status tab leads with a three-stage at-a-glance strip (Bridge /
    // Discovery / Client) built from EXISTING signals only. The builder is
    // pure (no Unity APIs, no GUI, no I/O) so it can be exercised in EditMode
    // without an EditorWindow host (IMGUI cannot lay out headlessly). The
    // contract pinned here is the color/label/reason policy the operator
    // reads at a glance — if it drifts, the "why isn't the agent talking?"
    // story breaks.
    [TestFixture]
    public class ConnectionStripBuilderTests
    {
        // ---- Bridge stage -------------------------------------------------

        [Test]
        public void Bridge_RunningAndIdle_IsOk_NoReason()
        {
            var m = ConnectionStripBuilder.Build(Inputs(listenerRunning: true, isCompiling: false));
            Assert.AreEqual(StripStageState.Ok, m.Bridge.State);
            Assert.IsEmpty(m.Bridge.Reason, "Ok stages carry no reason line.");
        }

        [Test]
        public void Bridge_RunningAndCompiling_IsWarning()
        {
            var m = ConnectionStripBuilder.Build(Inputs(listenerRunning: true, isCompiling: true));
            Assert.AreEqual(StripStageState.Warning, m.Bridge.State);
            Assert.That(m.Bridge.Reason, Does.Contain("recompiling").IgnoreCase,
                "Compiling reason should explain dispatch is paused.");
        }

        [Test]
        public void Bridge_Stopped_NoError_IsBad_WithStartHint()
        {
            var m = ConnectionStripBuilder.Build(Inputs(listenerRunning: false, lastStartError: null));
            Assert.AreEqual(StripStageState.Bad, m.Bridge.State);
            Assert.That(m.Bridge.Reason, Does.Contain("Start").IgnoreCase,
                "Stopped reason should point the operator at Start.");
        }

        [Test]
        public void Bridge_Stopped_PortInUse_ReasonCallsOutPortConflict()
        {
            // The exact substring BridgeStartRecovery matches on.
            var m = ConnectionStripBuilder.Build(
                Inputs(listenerRunning: false, lastStartError: "address already in use"));
            Assert.AreEqual(StripStageState.Bad, m.Bridge.State);
            Assert.That(m.Bridge.Reason, Does.Contain("Port in use").IgnoreCase,
                "A port-in-use start error must surface as the Bad reason.");
        }

        [Test]
        public void Bridge_Stopped_OtherError_ReasonIncludesErrorMessage()
        {
            var m = ConnectionStripBuilder.Build(
                Inputs(listenerRunning: false, lastStartError: "access denied"));
            Assert.AreEqual(StripStageState.Bad, m.Bridge.State);
            Assert.That(m.Bridge.Reason, Does.Contain("access denied").IgnoreCase);
        }

        // ---- Discovery stage ----------------------------------------------

        [Test]
        public void Discovery_LockAcquired_Idle_IsOk()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: BridgeInstanceLock.StateIdle, lockSnapshotValid: true));
            Assert.AreEqual(StripStageState.Ok, m.Discovery.State);
            Assert.IsEmpty(m.Discovery.Reason);
        }

        [Test]
        public void Discovery_LockAcquired_Compiling_IsWarning()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: BridgeInstanceLock.StateCompiling, lockSnapshotValid: true));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State);
            Assert.That(m.Discovery.Reason, Does.Contain("compiling").IgnoreCase);
        }

        [Test]
        public void Discovery_LockAcquired_Reloading_IsWarning()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: BridgeInstanceLock.StateReloading, lockSnapshotValid: true));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State);
        }

        [Test]
        public void Discovery_LockAcquired_EnteringPlaymode_IsWarning()
        {
            // The remaining two transient busy states must also surface as a
            // warning — IsTransientBusyState recognizes four, and all four
            // must be pinned so dropping one from the switch is caught.
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: BridgeInstanceLock.StateEnteringPlaymode, lockSnapshotValid: true));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State);
            Assert.That(m.Discovery.Reason, Does.Contain("play").IgnoreCase);
        }

        [Test]
        public void Discovery_LockAcquired_ExitingPlaymode_IsWarning()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: BridgeInstanceLock.StateExitingPlaymode, lockSnapshotValid: true));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State);
        }

        [Test]
        public void Discovery_LockAcquired_ButSnapshotInvalid_IsWarning()
        {
            // Lock acquired but the heartbeat JSON could not be parsed — the
            // MCP server may see stale discovery data. Must NOT read as Ok
            // (green), which would falsely reassure the operator.
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: true,
                lockState: null, lockSnapshotValid: false));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State,
                "An acquired-but-unreadable lock must warn, not report Ok.");
            Assert.That(m.Discovery.Reason, Does.Contain("heartbeat").IgnoreCase);
        }

        [Test]
        public void Discovery_ListenerUp_LockNotAcquired_IsWarning()
        {
            // The listener is running but no lock was published — the MCP
            // server cannot auto-discover this bridge. That is a degraded
            // (not broken) state worth surfacing as a warning.
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true, lockAcquired: false));
            Assert.AreEqual(StripStageState.Warning, m.Discovery.State);
            Assert.That(m.Discovery.Reason, Does.Contain("lock").IgnoreCase);
        }

        [Test]
        public void Discovery_ListenerDown_LockNotAcquired_IsUnknown_NotBad()
        {
            // Stopped bridge releasing its lock is the expected clean state;
            // it must NOT read as Bad (red) — that would cry wolf on a normal
            // stopped bridge.
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: false, lockAcquired: false));
            Assert.AreEqual(StripStageState.Unknown, m.Discovery.State);
        }

        // ---- Client stage -------------------------------------------------

        [Test]
        public void Client_CheckAvailable_Configured_IsOk()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                clientCheckAvailable: true, anyClientConfigured: true));
            Assert.AreEqual(StripStageState.Ok, m.Client.State);
            Assert.IsEmpty(m.Client.Reason);
        }

        [Test]
        public void Client_CheckAvailable_NotConfigured_IsWarning_NeverBad()
        {
            // The client stage is informational; a negative heuristic is a
            // warning (the operator may still have a CLI-only client we can't
            // see), never a hard failure.
            var m = ConnectionStripBuilder.Build(Inputs(
                clientCheckAvailable: true, anyClientConfigured: false));
            Assert.AreEqual(StripStageState.Warning, m.Client.State);
            Assert.That(m.Client.Reason, Does.Contain("Configure").IgnoreCase,
                "Reason should point at the Configure AI client panel.");
        }

        [Test]
        public void Client_CheckNotAvailable_IsUnknown()
        {
            var m = ConnectionStripBuilder.Build(Inputs(clientCheckAvailable: false));
            Assert.AreEqual(StripStageState.Unknown, m.Client.State);
        }

        // ---- happy path ---------------------------------------------------

        [Test]
        public void HappyPath_AllGreen_ListenerLockClient()
        {
            var m = ConnectionStripBuilder.Build(Inputs(
                listenerRunning: true,
                lockAcquired: true, lockState: BridgeInstanceLock.StateIdle, lockSnapshotValid: true,
                clientCheckAvailable: true, anyClientConfigured: true));
            Assert.AreEqual(StripStageState.Ok, m.Bridge.State);
            Assert.AreEqual(StripStageState.Ok, m.Discovery.State);
            Assert.AreEqual(StripStageState.Ok, m.Client.State);
        }

        // ---- helper -------------------------------------------------------

        // Helper that fills the inputs with sensible defaults so each test
        // only names the fields it cares about. Defaults represent a stopped,
        // unconfigured bridge — the "nothing running" baseline.
        private static ConnectionStripInputs Inputs(
            bool listenerRunning = false,
            bool isCompiling = false,
            string lastStartError = null,
            bool lockAcquired = false,
            string lockState = null,
            bool lockSnapshotValid = false,
            bool anyClientConfigured = false,
            bool clientCheckAvailable = false)
        {
            return new ConnectionStripInputs(
                listenerRunning, isCompiling, lastStartError,
                lockAcquired, lockState, lockSnapshotValid,
                anyClientConfigured, clientCheckAvailable);
        }
    }
}
