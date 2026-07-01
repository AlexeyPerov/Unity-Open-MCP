// M26 Plan 1 — CLI exit-code contract.
//
// The automation CLI exposes a deterministic 4-level exit-code scheme so CI
// pipelines can branch on outcome without parsing JSON:
//
//   0  success           — no issues (or no regression).
//   1  warnings-only     — only warnings/info below the fail threshold.
//   2  errors            — errors present (or a regression was detected).
//   3  timeout/unreachable — the bridge never became reachable, or a tool
//                            call timed out.
//
// Per execution-plan-1-cli-surface.md scope §2, this 4-level scheme is net-new
// design (UCP's reference contract is binary 0/1; we adopt the `--json`
// everywhere model from it, not the exit-code model).
//
// Helpers here centralize the mapping so every command agrees on the codes and
// the docs can reference one source of truth.

/** Canonical CLI exit codes. */
export const EXIT = {
  SUCCESS: 0,
  WARNINGS: 1,
  ERRORS: 2,
  TIMEOUT: 3,
} as const;

/** Severity ordering used to classify verify/regression outcomes. */
export type Severity = "error" | "warn" | "info" | "verbose";

/** Threshold level a fail_on_severity value resolves to. */
export type SeverityLevel = 0 | 1 | 2 | 3 | 4;

const SEVERITY_LEVEL: Record<Severity, SeverityLevel> = {
  verbose: 0,
  info: 1,
  warn: 2,
  error: 3,
};

/** Per-issue severity counts from a folded verify result. */
export interface SeverityCounts {
  error?: number;
  warn?: number;
  info?: number;
  verbose?: number;
}

/**
 * Resolve a `fail_on_severity` string to a numeric level so counts can be
 * compared against it. Lower level = stricter. Default threshold (omitted or
 * the verify default) is "error" — i.e. only errors fail the exit code.
 */
export function severityThreshold(
  failOnSeverity: string | undefined,
  fallback: Severity = "error",
): SeverityLevel {
  const sev = (failOnSeverity as Severity) ?? fallback;
  return SEVERITY_LEVEL[sev] ?? SEVERITY_LEVEL[fallback];
}

/**
 * Classify a verify/regression outcome into an exit code from its severity
 * counts and resolved threshold. The threshold decides which severities count
 * as "failing" (errors vs warnings-only):
 *
 *   - any severity >= threshold present  → EXIT.ERRORS (2)
 *   - only severities < threshold present → EXIT.WARNINGS (1)
 *   - no issues at all                    → EXIT.SUCCESS (0)
 *
 * `timeout`/`unreachable` outcomes are caller-supplied (they cannot be inferred
 * from counts) — see withTimeout below.
 */
export function classifyBySeverity(
  counts: SeverityCounts,
  threshold: SeverityLevel,
): number {
  const errorCount = counts.error ?? 0;
  const warnCount = counts.warn ?? 0;
  const infoCount = counts.info ?? 0;
  const verboseCount = counts.verbose ?? 0;

  // Failing severities are those at or above the threshold.
  const failing =
    (threshold <= SEVERITY_LEVEL.error ? errorCount : 0) +
    (threshold <= SEVERITY_LEVEL.warn ? warnCount : 0) +
    (threshold <= SEVERITY_LEVEL.info ? infoCount : 0) +
    (threshold <= SEVERITY_LEVEL.verbose ? verboseCount : 0);

  if (failing > 0) return EXIT.ERRORS;
  // Any non-failing issue present → warnings-only exit code.
  if (errorCount + warnCount + infoCount + verboseCount > 0) {
    return EXIT.WARNINGS;
  }
  return EXIT.SUCCESS;
}

/**
 * Promote an exit code to the timeout code when the bridge was unreachable.
 * Timeout always wins: even if the tool returned a body, an unreachable bridge
 * means the result is not trustworthy for CI gating.
 */
export function withTimeout(exitCode: number, unreachable: boolean): number {
  return unreachable ? EXIT.TIMEOUT : exitCode;
}
