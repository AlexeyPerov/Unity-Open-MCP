import type { ProjectEntry } from "$lib/services/config";

/**
 * Frecency half-life in days. After this many days since the last launch,
 * the frecency contribution is decayed by half. Picked to match the
 * spec: "decay over time (e.g. half-life of 14 days)".
 */
export const FRECENCY_HALF_LIFE_DAYS = 14;

/**
 * Compute the frecency sort score for a project. The score is the raw
 * counter multiplied by an exponential decay based on how long ago the
 * project was last launched:
 *
 *     score = frecency * exp(-daysSinceLastLaunch * ln(2) / halfLife)
 *
 * The ln(2) / halfLife conversion is the standard continuous-time half-life
 * form: at t=halfLife the multiplier is exactly 0.5, at t=2*halfLife it's
 * 0.25, etc. A project with `frecency=0` (never launched) returns 0; the
 * sort naturally falls back to `lastModifiedAt` as the tiebreaker.
 *
 * `lastLaunchAt` is an ISO 8601 string (RFC 3339). When the value is
 * missing or unparseable we treat the project as "never launched" and
 * return 0 — the same effect as `frecency=0` so the call is safe to
 * make on any project entry.
 */
export function frecencyScore(
  project: ProjectEntry,
  nowMs: number = Date.now(),
  halfLifeDays: number = FRECENCY_HALF_LIFE_DAYS
): number {
  const counter = project.frecency ?? 0;
  if (counter <= 0) return 0;
  const lastLaunchIso = project.lastLaunchAt;
  if (!lastLaunchIso) return 0;
  const lastMs = Date.parse(lastLaunchIso);
  if (Number.isNaN(lastMs)) return 0;
  const ageDays = Math.max(0, (nowMs - lastMs) / 86_400_000);
  const decay = Math.exp(-ageDays * (Math.LN2 / halfLifeDays));
  return counter * decay;
}

/**
 * Compare two projects for the frecency sort: descending score, then
 * descending `lastModifiedAt` as a stable tiebreaker. Used by the default
 * sort key. Returns 0 when both projects have identical scores and
 * timestamps (sort is then stable on the underlying list order).
 */
export function compareFrecency(
  a: ProjectEntry,
  b: ProjectEntry,
  nowMs: number = Date.now()
): number {
  const diff = frecencyScore(b, nowMs) - frecencyScore(a, nowMs);
  if (diff !== 0) return diff;
  // Tiebreaker: most recently modified first. `undefined` sorts last
  // because `undefined < string` in the default comparator and we
  // want descending order.
  const am = a.lastModifiedAt ?? "";
  const bm = b.lastModifiedAt ?? "";
  if (am === bm) return 0;
  return am < bm ? 1 : -1;
}

/**
 * Compare two projects for the lastModified sort: descending timestamp.
 * Mirrors the previous (pre-frecency) default ordering.
 */
export function compareLastModified(
  a: ProjectEntry,
  b: ProjectEntry
): number {
  const am = a.lastModifiedAt ?? "";
  const bm = b.lastModifiedAt ?? "";
  if (am === bm) return 0;
  return am < bm ? 1 : -1;
}
