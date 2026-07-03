export interface MutationEnvelope {
  mutation: {
    success: boolean;
    output: unknown;
    error: { code: string; message: string } | null;
  };
  gate: {
    mode: string;
    skipped: boolean;
    validation: unknown;
    delta: Record<string, unknown> | null;
  };
  // M22 T22.1.3 — per-call `logs`: console entries emitted during this
  // dispatch. Captured by the bridge (Unity's console lives in the Editor
  // process) as a before/after delta and forwarded opaquely. Empty array when
  // nothing was emitted. Does not replace read_console (global buffer + stacks).
  logs?: LogEntry[];
  agentNextSteps: string[];
}

// Inline log entry shape surfaced on every tool response. `severity` is
// info/warning/error; `source` is the origin (currently always "unity" for
// console-captured entries). Stacks are omitted here — read_console is the
// verbose path with stack traces.
export interface LogEntry {
  severity: "info" | "warning" | "error";
  message: string;
  source?: string;
}

export function deriveIsError(envelope: MutationEnvelope): boolean {
  // Defensive: the envelope is only a MutationEnvelope when the caller has
  // already confirmed `body.mutation` is an object (postTool's shape check).
  // Guard anyway so a malformed/partial body can never throw here — historically
  // a missing `mutation` field threw TypeError, which postTool's catch
  // misclassified as a connection failure (specs/feedback.md entry 2026-07-02-b).
  if (
    !envelope ||
    typeof envelope !== "object" ||
    typeof envelope.mutation !== "object" ||
    envelope.mutation === null
  ) {
    return false;
  }
  if (envelope.mutation.success === false) return true;

  if (envelope.gate?.mode === "enforce" && envelope.gate.delta) {
    const newErrors = envelope.gate.delta.newErrors;
    if (typeof newErrors === "number" && newErrors > 0) return true;
  }

  return false;
}
