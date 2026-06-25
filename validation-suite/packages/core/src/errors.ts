/**
 * Typed load/validation errors for scenario + profile loading.
 *
 * The UI renders these as readable messages (phase-1 validation:
 * "Invalid scenario file produces readable UI error without
 * crashing"). Each error carries enough context to point at the
 * offending field.
 */

import type { ActionType, PatchOp, RequirementLevel, StepType } from "./types.ts";

/** Vocabularies recognized by the loader. Kept here so tests + the
 *  loader share one source of truth. */
export const STEP_TYPES: readonly StepType[] = [
  "info",
  "setup",
  "agent_prompt",
  "expected",
  "actual",
  "external_doc",
  "mark_done",
] as const;

export const ACTION_TYPES: readonly ActionType[] = [
  "fs_copy",
  "fs_patch",
  "fs_delete",
  "mcp_tool",
  "manual",
] as const;

/**
 * Patch-op vocabulary for `fs_patch` (pinned in the Phase 2 spec). The
 * loader rejects any op outside this list at scenario-load time so a
 * typo never reaches the executor (config-action drift guard).
 */
export const PATCH_OPS: readonly PatchOp[] = [
  "replace_line_contains",
  "insert_after_line_contains",
  "insert_before_line_contains",
  "trim_trailing_whitespace",
] as const;

export const REQUIREMENT_LEVELS: readonly RequirementLevel[] = [
  "required-core",
  "required-extended",
  "optional",
] as const;

/** Build a load-error message for an unknown field value. */
export function unknownValue(field: string, value: unknown, allowed: readonly string[]): string {
  return `Unknown ${field} "${String(value)}". Allowed: ${allowed.join(", ")}.`;
}

/** Build a load-error message for a missing required field. */
export function missingField(field: string): string {
  return `Missing required field "${field}".`;
}
