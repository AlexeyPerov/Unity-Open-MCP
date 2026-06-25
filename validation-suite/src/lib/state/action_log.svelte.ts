/**
 * Per-step action log store (phase-2 deliverable: action log panel).
 *
 * Holds the log lines produced by running/resetting a step's actions,
 * keyed by `${scenarioId}/${stepId}`. The UI renders the active step's
 * log in an inline panel so the operator sees per-action success/failure
 * and CLI output snippets without opening the global log drawer.
 *
 * Everything the runner surfaces (`ActionResult.logs`, reset warnings,
 * CLI stderr snippets) flows through here so a step has one log trail.
 */

import type { ActionLogLine } from "../services/backend.ts";

const MAX_PER_STEP = 200;

class ActionLogStore {
  logs = $state<Record<string, ActionLogLine[]>>({});

  /** Storage key for a scenario + step. */
  private key(scenarioId: string, stepId: string): string {
    return `${scenarioId}/${stepId}`;
  }

  /** Append lines for a step (bounded ring per step). */
  append(scenarioId: string, stepId: string, lines: ActionLogLine[]): void {
    const k = this.key(scenarioId, stepId);
    const cur = this.logs[k] ?? [];
    this.logs[k] = [...cur, ...lines].slice(-MAX_PER_STEP);
  }

  /** Replace a step's log (used when a fresh run starts). */
  set(scenarioId: string, stepId: string, lines: ActionLogLine[]): void {
    this.logs[this.key(scenarioId, stepId)] = [...lines].slice(-MAX_PER_STEP);
  }

  /** Read a step's log lines (empty when none). */
  get(scenarioId: string, stepId: string): ActionLogLine[] {
    return this.logs[this.key(scenarioId, stepId)] ?? [];
  }

  /** Clear a single step's log. */
  clear(scenarioId: string, stepId: string): void {
    delete this.logs[this.key(scenarioId, stepId)];
  }

  /** Clear all logs (e.g. on project switch). */
  clearAll(): void {
    this.logs = {};
  }
}

export const actionLog = new ActionLogStore();
