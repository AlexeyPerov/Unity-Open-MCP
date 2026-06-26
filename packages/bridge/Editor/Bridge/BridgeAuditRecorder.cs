using System;

namespace UnityOpenMcpBridge
{
    // Gate-run + on-disk audit recording. After every mutating dispatch the
    // orchestrator calls RecordGateRun, which:
    //   1. captures a BridgeGateRunRecord into the in-memory run history
    //      (BridgeGateRunHistory — surfaced by the gate UI / diagnostics), and
    //   2. when the opt-in audit log is on (BridgeAuditLog.Enabled), appends a
    //      BridgeAuditRecord to the rolling JSON-lines audit file.
    // Both paths are best-effort: a capture/write failure is swallowed and never
    // breaks the dispatch response.

    internal static class BridgeAuditRecorder
    {
        internal static void RecordGateRun(string toolName, string effectiveMode, GateDispatchResult result, string[] pathsHint)
        {
            try
            {
                var record = new BridgeGateRunRecord
                {
                    ToolName = toolName,
                    RequestedMode = effectiveMode,
                    EffectiveMode = effectiveMode,
                    Outcome = result.Outcome,
                    GateRan = result.GateRan,
                    GateFailed = result.GateFailed,
                    NewErrors = result.Delta?.NewErrors ?? 0,
                    NewWarnings = result.Delta?.NewWarnings ?? 0,
                    ResolvedErrors = result.Delta?.ResolvedErrors ?? 0,
                    ResolvedWarnings = result.Delta?.ResolvedWarnings ?? 0,
                    CheckpointDurationMs = result.CheckpointDurationMs,
                    ValidationDurationMs = result.ValidationDurationMs,
                    TotalGateDurationMs = result.TotalGateDurationMs,
                    CategoriesRun = result.CategoriesRun,
                    AgentNextSteps = result.AgentNextSteps,
                    MutationError = result.Mutation?.ErrorMessage,
                    Timestamp = DateTime.Now
                };
                BridgeGateRunHistory.Record(record);
            }
            catch
            {
                // History capture is best-effort; never let it break the response.
            }

            // M14 T5.5 — on-disk audit log (opt-in). Mirrors the gate-run
            // record shape so an auditor can correlate the two. Best-effort:
            // a write failure is logged once and the record dropped, never
            // breaking the dispatch path.
            try
            {
                RecordAudit(toolName, effectiveMode, result, pathsHint);
            }
            catch
            {
                // ignored — audit logging is non-essential
            }
        }

        // M14 T5.5 — build + persist the audit record. Outcome vocabulary is
        // the GateOutcome enum lowercased, plus "denied" when the deny
        // heuristic refused the mutation (carried as the mutation error code).
        internal static void RecordAudit(string toolName, string effectiveMode, GateDispatchResult result, string[] pathsHint)
        {
            if (!BridgeAuditLog.Enabled) return;

            var mutationError = result.Mutation?.ErrorCode;
            var denied = mutationError == "denied_by_policy" || mutationError == "menu_blocked";
            var outcome = denied
                ? "denied"
                : result.Outcome.ToString().ToLowerInvariant();

            var record = new BridgeAuditRecord
            {
                Timestamp = DateTime.UtcNow,
                ProjectHash = ResolveAuditProjectHash(),
                Tool = toolName,
                GateMode = effectiveMode,
                PathsHint = pathsHint,
                Outcome = outcome,
                GateRan = result.GateRan,
                NewErrors = result.Delta?.NewErrors ?? 0,
                NewWarnings = result.Delta?.NewWarnings ?? 0,
                ResolvedErrors = result.Delta?.ResolvedErrors ?? 0,
                ResolvedWarnings = result.Delta?.ResolvedWarnings ?? 0,
                CheckpointId = result.CheckpointId,
                TotalGateDurationMs = result.TotalGateDurationMs,
                MutationErrorCode = mutationError,
                BypassedDenyList = effectiveMode == BridgeGateDefaultPolicy.Off && !denied,
                DeniedPattern = ExtractDeniedPattern(result.Mutation?.ErrorMessage)
            };
            BridgeAuditLog.Record(record);
        }

        internal static string ResolveAuditProjectHash()
        {
            var projectPath = BridgeSession.ProjectPath ?? BridgeHttpServer.GetProjectPathForPort();
            try { return InstancePortResolver.ProjectHash(projectPath); }
            catch { return "unknown"; }
        }

        // The deny heuristic embeds "Matched pattern: <pat>." in the error
        // message. Extract it so the audit record has a structured field for
        // grep / SIEM correlation. Returns null when the message isn't a deny
        // refusal.
        internal static string ExtractDeniedPattern(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return null;
            const string marker = "Matched pattern: ";
            var idx = errorMessage.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = idx + marker.Length;
            var end = errorMessage.IndexOf('.', start);
            if (end < 0) end = errorMessage.Length;
            return errorMessage.Substring(start, end - start);
        }
    }
}
