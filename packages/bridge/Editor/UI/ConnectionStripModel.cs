using System;
using System.IO;

namespace UnityOpenMcpBridge
{
    // M29 Plan 2 — pure model for the Status-tab connection strip.
    //
    // The Status tab used to lead with prose + foldouts, so an operator
    // could not answer "why isn't the agent talking?" in one glance.
    // This model computes three compact stages (Bridge / Discovery / Client)
    // from EXISTING signals only — no new probes, no new endpoints. The
    // builder is pure (no Unity APIs, no GUI), so it is unit-testable
    // without an EditorWindow host (the IMGUI layer cannot render in
    // EditMode). The Status tab calls Build() each repaint and hands the
    // result to ConnectionStripUI.Draw.
    //
    // Stage semantics:
    //   - Bridge    — is the local HTTP listener up? (BridgeHttpServer.IsRunning,
    //                 compiling tint via BridgeSession.IsCompiling)
    //   - Discovery — has this instance published its lock/heartbeat so the
    //                 MCP server can find it? (BridgeInstanceLock.IsAcquired +
    //                 last heartbeat state)
    //   - Client    — is at least one known AI client configured against this
    //                 project? (configure-client heuristic over the catalog)
    //
    // A degraded Bridge/Discovery stage is always actionable (Start the
    // listener / reopen Unity). A "not checked" Client stage is informational
    // only — it is NOT a failure, because the bridge has no way to know
    // whether a CLI-only client (Claude Code) is pointed at it.

    /// <summary>
    /// Coarse health of a single connection-strip stage. Drives the dot color
    /// and whether a one-line reason is shown underneath.
    /// </summary>
    public enum StripStageState
    {
        /// <summary>Green — healthy / running.</summary>
        Ok,
        /// <summary>Yellow — degraded but functional (e.g. compiling).</summary>
        Warning,
        /// <summary>Red — broken / stopped.</summary>
        Bad,
        /// <summary>Gray — no signal available / not checked.</summary>
        Unknown,
    }

    /// <summary>
    /// One stage of the connection strip. <see cref="Reason"/> is empty when
    /// the stage is <see cref="StripStageState.Ok"/>; it carries a single line
    /// of degraded explanation otherwise.
    /// </summary>
    public readonly struct StripStage
    {
        public readonly string Label;
        public readonly StripStageState State;
        public readonly string Reason;

        public StripStage(string label, StripStageState state, string reason)
        {
            Label = label ?? "";
            State = state;
            Reason = reason ?? "";
        }
    }

    /// <summary>
    /// Aggregate model for the three-stage connection strip. The stages are
    /// always rendered in order: Bridge, Discovery, Client.
    /// </summary>
    public readonly struct ConnectionStripModel
    {
        public readonly StripStage Bridge;
        public readonly StripStage Discovery;
        public readonly StripStage Client;

        public ConnectionStripModel(StripStage bridge, StripStage discovery, StripStage client)
        {
            Bridge = bridge;
            Discovery = discovery;
            Client = client;
        }
    }

    /// <summary>
    /// Pure inputs to the strip builder, captured as a struct so the builder
    /// has zero hidden dependencies on static singletons. The Status tab
    /// reads the live singletons (BridgeHttpServer / BridgeInstanceLock /
    /// BridgeSession / the catalog) into this struct each repaint, then calls
    /// <see cref="ConnectionStripBuilder.Build"/>. Tests construct it directly.
    /// </summary>
    public readonly struct ConnectionStripInputs
    {
        // Bridge stage
        public readonly bool ListenerRunning;
        public readonly bool IsCompiling;
        public readonly string LastStartError;

        // Discovery stage
        public readonly bool LockAcquired;
        public readonly string LockState;
        public readonly bool LockSnapshotValid;

        // Client stage
        public readonly bool AnyClientConfigured;
        public readonly bool ClientCheckAvailable;

        public ConnectionStripInputs(
            bool listenerRunning,
            bool isCompiling,
            string lastStartError,
            bool lockAcquired,
            string lockState,
            bool lockSnapshotValid,
            bool anyClientConfigured,
            bool clientCheckAvailable)
        {
            ListenerRunning = listenerRunning;
            IsCompiling = isCompiling;
            LastStartError = lastStartError;
            LockAcquired = lockAcquired;
            LockState = lockState;
            LockSnapshotValid = lockSnapshotValid;
            AnyClientConfigured = anyClientConfigured;
            ClientCheckAvailable = clientCheckAvailable;
        }
    }

    /// <summary>
    /// Pure builder that turns <see cref="ConnectionStripInputs"/> into a
    /// <see cref="ConnectionStripModel"/>. No Unity APIs, no I/O, no GUI —
    /// fully deterministic and unit-testable.
    /// </summary>
    public static class ConnectionStripBuilder
    {
        public static ConnectionStripModel Build(ConnectionStripInputs inputs)
        {
            return new ConnectionStripModel(
                BuildBridgeStage(inputs),
                BuildDiscoveryStage(inputs),
                BuildClientStage(inputs));
        }

        // Bridge stage: listener up = Ok, up+compiling = Warning, down = Bad.
        // A port-in-use start error is surfaced as the Bad reason so the
        // operator sees WHY the listener is down at a glance.
        private static StripStage BuildBridgeStage(ConnectionStripInputs inputs)
        {
            if (inputs.ListenerRunning)
            {
                if (inputs.IsCompiling)
                {
                    return new StripStage(
                        "Bridge",
                        StripStageState.Warning,
                        "Running, recompiling — tool dispatch is paused until compile finishes.");
                }
                return new StripStage("Bridge", StripStageState.Ok, "");
            }

            // Listener down. Prefer a concrete start error (e.g. port in use)
            // when present, otherwise the generic "stopped" reason.
            var reason = "Listener stopped — press Start so MCP clients can connect.";
            if (BridgeStartRecovery.IsPortInUseError(inputs.LastStartError))
            {
                reason = "Port in use — see the recovery steps below.";
            }
            else if (!string.IsNullOrEmpty(inputs.LastStartError))
            {
                reason = $"Start failed: {inputs.LastStartError}";
            }
            return new StripStage("Bridge", StripStageState.Bad, reason);
        }

        // Discovery stage: lock acquired = Ok, acquired+reloading/compiling =
        // Warning (transient editor state the MCP server can read but signals
        // a non-idle bridge), not acquired = Bad when the listener is up
        // (expected to be there) / Unknown when the listener is down (the
        // lock is normally released on graceful stop, so absent is correct).
        private static StripStage BuildDiscoveryStage(ConnectionStripInputs inputs)
        {
            const string label = "Discovery";

            if (!inputs.LockAcquired)
            {
                if (inputs.ListenerRunning)
                {
                    return new StripStage(
                        label,
                        StripStageState.Warning,
                        "Instance lock not published — the MCP server may not auto-discover this bridge.");
                }
                // Listener down + no lock is the expected stopped state.
                return new StripStage(label, StripStageState.Unknown, "No lock held (listener stopped).");
            }

            // Lock acquired. If the lock JSON is present but unparseable, the
            // MCP server cannot read the heartbeat — surface a warning rather
            // than a green Ok so the operator does not trust a possibly-stale
            // lock. Otherwise a non-idle state is worth surfacing as a warning
            // because the MCP server's classification (idle/compiling/reloading)
            // is derived from this field — it is not an error, just not "ready".
            if (!inputs.LockSnapshotValid)
            {
                return new StripStage(
                    label,
                    StripStageState.Warning,
                    "Lock acquired but its heartbeat payload could not be read — the MCP server may see stale discovery data.");
            }
            var state = inputs.LockState ?? "";
            if (IsTransientBusyState(state))
            {
                return new StripStage(
                    label,
                    StripStageState.Warning,
                    $"Lock acquired; bridge is '{state}' (MCP server sees this state).");
            }
            return new StripStage(label, StripStageState.Ok, "");
        }

        // Client stage: this is informational, never a hard failure. The
        // bridge cannot see CLI-only clients (Claude Code) or clients
        // configured under a path it does not check, so Unknown is the
        // honest default. A positive signal is Ok; a negative signal from
        // the file-backed heuristic is a Warning (the operator probably
        // still needs to wire a client).
        private static StripStage BuildClientStage(ConnectionStripInputs inputs)
        {
            const string label = "Client";

            if (!inputs.ClientCheckAvailable)
            {
                return new StripStage(
                    label,
                    StripStageState.Unknown,
                    "Not checked — open 'Configure AI client' below to verify.");
            }

            if (inputs.AnyClientConfigured)
            {
                return new StripStage(label, StripStageState.Ok, "");
            }

            return new StripStage(
                label,
                StripStageState.Warning,
                "No known client config detected for this project — see 'Configure AI client' below.");
        }

        // The lock states that represent a busy (non-idle, non-playing) bridge.
        // Matches the vocabulary in BridgeInstanceLock.State*.
        private static bool IsTransientBusyState(string state)
        {
            return state == BridgeInstanceLock.StateCompiling
                || state == BridgeInstanceLock.StateReloading
                || state == BridgeInstanceLock.StateEnteringPlaymode
                || state == BridgeInstanceLock.StateExitingPlaymode;
        }
    }
}
