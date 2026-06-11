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
  agentNextSteps: string[];
}

export function deriveIsError(envelope: MutationEnvelope): boolean {
  if (!envelope.mutation.success) return true;

  if (envelope.gate.mode === "enforce" && envelope.gate.delta) {
    const newErrors = envelope.gate.delta.newErrors;
    if (typeof newErrors === "number" && newErrors > 0) return true;
  }

  return false;
}
