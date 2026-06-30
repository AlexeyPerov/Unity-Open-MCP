// M22 Plan 1 — Token-budget-aware output profiles + uniform paging.
//
// Single source of truth for two envelope concerns shared by the heavy tools
// (read_asset / search_assets / scene_get_data / find_references / validate_edit
// / scan_paths):
//
//  1. **Output profiles** (`compact` | `balanced` | `full`) — a uniform knob that
//     maps onto each tool's existing `detail` axis (summary / normal / verbose).
//     This reuses the M9 compression module instead of re-implementing folding:
//     `compact` IS the existing summary/folded shape; `balanced` is the current
//     default; `full` is verbose. The profile is the public, documented param;
//     `detail` remains as a backwards-compatible alias.
//
//  2. **Uniform paging** (`page_size` / `cursor` / `next_cursor`) — replaces the
//     ad-hoc `max_results` / `max_nodes` / `object_limit` caps with a resumable
//     cursor. The old caps stay as aliases (they request a single bounded page).
//     Every paginated response carries a `pagination` block.
//
// The module is pure (no I/O, no cross-file runtime imports) so it loads cleanly
// under `node --experimental-strip-types` in tests.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

// ===========================================================================
// Output profiles.
// ===========================================================================

export type OutputProfile = "compact" | "balanced" | "full";

export const PROFILES: readonly OutputProfile[] = ["compact", "balanced", "full"];

export function isOutputProfile(value: unknown): value is OutputProfile {
  return value === "compact" || value === "balanced" || value === "full";
}

/**
 * The per-tool `detail` string each profile maps to. The detail axis already
 * exists on every heavy tool (summary / normal / verbose); the profile is the
 * documented public name for it.
 */
export type DetailLevel = "summary" | "normal" | "verbose";

export function profileToDetail(profile: OutputProfile): DetailLevel {
  switch (profile) {
    case "compact":
      return "summary";
    case "balanced":
      return "normal";
    case "full":
      return "verbose";
  }
}

/**
 * Resolve the effective detail level for a call.
 *
 * Precedence: an explicit `profile` wins; otherwise an explicit legacy `detail`
 * is honored (back-compat alias); otherwise the per-tool `fallback` is used.
 * `compact` is the documented default for the heavy families, so most callers
 * should pass `fallback: "summary"`. Tools whose legacy default was `normal`
 * (find_references) still default to `summary` now — compact is the default
 * everywhere per the M22 design decision.
 */
export function resolveDetail(
  profile: OutputProfile | undefined,
  legacyDetail: string | undefined,
  fallback: DetailLevel,
): DetailLevel {
  if (profile) return profileToDetail(profile);
  if (legacyDetail === "summary" || legacyDetail === "normal" || legacyDetail === "verbose") {
    return legacyDetail;
  }
  return fallback;
}

/**
 * Read `profile` / `detail` off a raw args object and return the effective
 * detail level. Returns the profile too (when present) so callers can branch on
 * compact vs. expanded for result folding.
 */
export function readProfileAndDetail(
  args: Record<string, unknown>,
  fallback: DetailLevel = "summary",
): { detail: DetailLevel; profile: OutputProfile | undefined } {
  const profileRaw = args.profile;
  const profile = isOutputProfile(profileRaw) ? profileRaw : undefined;
  const legacyDetail = typeof args.detail === "string" ? args.detail : undefined;
  return { detail: resolveDetail(profile, legacyDetail, fallback), profile };
}

// ===========================================================================
// Cursor encode / decode.
//
// The cursor is an opaque continuation token, not a capability: it is a plain
// human-readable string of the form `<toolKey>:<offset>`. We keep it readable
// (no base64) so it shows up cleanly in logs and agent transcripts, and so a
// mismatched cursor (wrong tool) fails loudly instead of silently paging the
// wrong data.
// ===========================================================================

export interface CursorParts {
  toolKey: string;
  offset: number;
}

export function encodeCursor(toolKey: string, offset: number): string {
  return `${toolKey}:${offset}`;
}

export function splitCursor(cursor: string | undefined, expectedToolKey: string): number {
  if (typeof cursor !== "string" || cursor === "") return 0;
  const colon = cursor.lastIndexOf(":");
  if (colon <= 0) return 0;
  const toolKey = cursor.slice(0, colon);
  const offsetStr = cursor.slice(colon + 1);
  // A cursor minted for a different tool (or a stale cursor from before a tool
  // rename) must not silently page the wrong data — treat it as "start over".
  if (toolKey !== expectedToolKey) return 0;
  const offset = parseNonNegativeInt(offsetStr);
  return offset === null ? 0 : offset;
}

function parseNonNegativeInt(value: string): number | null {
  if (value.length === 0) return null;
  let out = 0;
  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    if (ch < "0" || ch > "9") return null;
    out = out * 10 + (ch.charCodeAt(0) - "0".charCodeAt(0));
  }
  return out;
}

// ===========================================================================
// Paging.
// ===========================================================================

export interface PagingInput {
  page_size?: number;
  cursor?: string;
}

export interface PaginationBlock {
  /** Effective page size applied (clamped to >=1). */
  page_size: number;
  /** Cursor this page was requested with (null when this is the first page). */
  cursor: string | null;
  /** Cursor to fetch the next page (null when this is the last page). */
  next_cursor: string | null;
  /** Items remaining after this page (the resumable tail). */
  truncated: number;
}

export interface PageResult<T> {
  page: T[];
  block: PaginationBlock;
}

/**
 * Slice `items` into one page starting at the cursor offset.
 *
 * - `page_size` <= 0 ⇒ disabled: returns the whole list with a terminal block
 *   (next_cursor null, truncated 0). This is the back-compat / alias path.
 * - A cursor with the wrong tool key resets to offset 0 (see splitCursor).
 * - Count invariant: `page.length + block.truncated == items.length - offset`.
 */
export function applyPaging<T>(
  items: readonly T[],
  toolKey: string,
  input: PagingInput,
): PageResult<T> {
  const pageSize = typeof input.page_size === "number" && input.page_size > 0
    ? Math.floor(input.page_size)
    : 0;

  if (pageSize <= 0) {
    return {
      page: items.slice(),
      block: {
        page_size: 0,
        cursor: typeof input.cursor === "string" && input.cursor !== "" ? input.cursor : null,
        next_cursor: null,
        truncated: 0,
      },
    };
  }

  const offset = Math.min(splitCursor(input.cursor, toolKey), items.length);
  const page = items.slice(offset, offset + pageSize);
  const remaining = items.length - (offset + page.length);

  const nextCursor = remaining > 0 ? encodeCursor(toolKey, offset + page.length) : null;

  return {
    page,
    block: {
      page_size: pageSize,
      cursor: typeof input.cursor === "string" && input.cursor !== "" ? input.cursor : null,
      next_cursor: nextCursor,
      truncated: remaining,
    },
  };
}

// ===========================================================================
// Envelope attachment.
//
// Heavy-tool results are plain objects serialized to the MCP text block. We
// merge a `pagination` block in (and optionally fold fields) as the last step
// of each route, mirroring how injectRouteMeta appends `_route`.
// ===========================================================================

/**
 * Return a shallow copy of `result` with `pagination` set. Idempotent: a result
 * that already carries a `pagination` block (e.g. built inline by a router) is
 * returned unchanged so two post-processors never clobber each other.
 *
 * Generic over the concrete result interface so callers can pass a typed
 * CompactSearchResult / FindReferencesOfflineResult without an index signature.
 */
export function attachPagination<T extends object>(
  result: T,
  block: PaginationBlock,
): T & { pagination: PaginationBlock } {
  const r = result as T & { pagination?: PaginationBlock };
  if (r.pagination !== undefined) return r as T & { pagination: PaginationBlock };
  return { ...result, pagination: block };
}

// ===========================================================================
// Verify-result folding (validate_edit / scan_paths).
//
// The bridge always returns the full `issues[]` list. `compact` profile strips
// it down to counts + metadata; `balanced`/`full` keep (and page) the issues.
// ===========================================================================

export interface VerifyIssue {
  severity?: string;
  ruleId?: string;
  issueCode?: string;
  assetPath?: string;
  [key: string]: unknown;
}

/**
 * Fold a verify result (`passed` + `issues[]` + metadata) per the requested
 * profile, optionally paging the issues list.
 *
 * - `compact`: drop `issues[]`, emit `issueCount` + `issuesBySeverity` counts.
 * - `balanced` / `full`: keep `issues[]` (paged when `page_size` is set).
 *
 * `fail_on_severity` and other verify-specific fields are passed through
 * untouched.
 */
export function foldVerifyResult(
  result: Record<string, unknown>,
  toolKey: string,
  profile: OutputProfile | undefined,
  paging: PagingInput,
): Record<string, unknown> {
  const issues = Array.isArray(result.issues)
    ? (result.issues as VerifyIssue[])
    : [];

  const issueCount = issues.length;
  const issuesBySeverity = countBySeverity(issues);

  // compact: counts only — strip the issues array.
  if (profile === "compact") {
    const { issues: _dropped, ...rest } = result;
    void _dropped;
    return {
      ...rest,
      issueCount,
      issuesBySeverity,
    };
  }

  // balanced / full: page the issues list when a page size is requested.
  const pageSize = typeof paging.page_size === "number" && paging.page_size > 0
    ? Math.floor(paging.page_size)
    : 0;

  if (pageSize <= 0) {
    return {
      ...result,
      issueCount,
      issuesBySeverity,
    };
  }

  const offset = Math.min(splitCursor(paging.cursor, toolKey), issues.length);
  const page = issues.slice(offset, offset + pageSize);
  const remaining = issues.length - (offset + page.length);
  const nextCursor = remaining > 0 ? encodeCursor(toolKey, offset + page.length) : null;

  const { issues: _replaced, pagination: _existingPagination, ...rest } = result;
  void _replaced;
  void _existingPagination;

  return {
    ...rest,
    issues: page,
    issueCount,
    issuesBySeverity,
    pagination: {
      page_size: pageSize,
      cursor: typeof paging.cursor === "string" && paging.cursor !== "" ? paging.cursor : null,
      next_cursor: nextCursor,
      truncated: remaining,
    },
  };
}

function countBySeverity(issues: VerifyIssue[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const issue of issues) {
    const sev = issue.severity ?? "unknown";
    counts[sev] = (counts[sev] ?? 0) + 1;
  }
  return counts;
}

// ===========================================================================
// Result-block helpers — operate on the CallToolResult text block.
// ===========================================================================

/**
 * Parse the first text content block of a CallToolResult as JSON. Returns null
 * when there is no text block or it is not a JSON object (so callers can fall
 * through without throwing).
 */
export function parseResultBody(result: {
  content: ReadonlyArray<{ type: string; text?: unknown }>;
}): Record<string, unknown> | null {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") return null;
  try {
    const parsed = JSON.parse(first.text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
  } catch {
    // fall through
  }
  return null;
}

/**
 * Rewrite the first text content block of a CallToolResult with a new JSON
 * body. Other content blocks (e.g. an MCP image block from capture_inline) are
 * preserved untouched. `isError` and `_meta` are preserved.
 */
export function withResultBody(
  result: CallToolResult,
  body: Record<string, unknown>,
): CallToolResult {
  const textIndex = result.content.findIndex((c) => c.type === "text");
  const newBlock = { type: "text" as const, text: JSON.stringify(body) };
  if (textIndex < 0) {
    return { ...result, content: [newBlock, ...result.content] };
  }
  const newContent = result.content.slice();
  newContent[textIndex] = newBlock;
  return { ...result, content: newContent };
}
