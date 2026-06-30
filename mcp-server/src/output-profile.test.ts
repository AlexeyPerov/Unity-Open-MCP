// Tests for the M22 output-profile + uniform-paging module.
//
// Pins the two envelope concerns shared by the heavy tools:
//  1. profile -> detail mapping (and the back-compat alias resolution).
//  2. cursor-based paging count accuracy + cursor round-trip.
//  3. verify-result folding (compact strips issues, balanced keeps + pages).
//  4. result-block helpers (parseResultBody / withResultBody preserve blocks).
//
// Pure-function tests — no bridge, no filesystem. Runs under
// `node --test --experimental-strip-types`.

import test from "node:test";
import assert from "node:assert/strict";
import {
  isOutputProfile,
  profileToDetail,
  resolveDetail,
  readProfileAndDetail,
  encodeCursor,
  splitCursor,
  applyPaging,
  attachPagination,
  foldVerifyResult,
  parseResultBody,
  withResultBody,
  type OutputProfile,
  type DetailLevel,
} from "./output-profile.js";

// ---------------------------------------------------------------------------
// Output profiles.
// ---------------------------------------------------------------------------

test("isOutputProfile accepts the three documented values", () => {
  assert.equal(isOutputProfile("compact"), true);
  assert.equal(isOutputProfile("balanced"), true);
  assert.equal(isOutputProfile("full"), true);
  assert.equal(isOutputProfile("verbose"), false);
  assert.equal(isOutputProfile(undefined), false);
  assert.equal(isOutputProfile(42), false);
});

test("profileToDetail maps compact/balanced/full onto summary/normal/verbose", () => {
  assert.equal(profileToDetail("compact"), "summary");
  assert.equal(profileToDetail("balanced"), "normal");
  assert.equal(profileToDetail("full"), "verbose");
});

test("resolveDetail: explicit profile wins over legacy detail", () => {
  assert.equal(resolveDetail("compact", "verbose", "normal"), "summary");
  assert.equal(resolveDetail("full", "summary", "normal"), "verbose");
});

test("resolveDetail: legacy detail honored when no profile is set", () => {
  assert.equal(resolveDetail(undefined, "verbose", "normal"), "verbose");
  assert.equal(resolveDetail(undefined, "summary", "normal"), "summary");
});

test("resolveDetail: falls back to per-tool default when neither is set", () => {
  assert.equal(resolveDetail(undefined, undefined, "summary"), "summary");
  assert.equal(resolveDetail(undefined, undefined, "normal"), "normal");
});

test("resolveDetail: an invalid legacy detail string falls back to default", () => {
  assert.equal(resolveDetail(undefined, "wat", "summary"), "summary");
});

test("readProfileAndDetail reads profile/detail off a raw args object", () => {
  const a = readProfileAndDetail({ profile: "full" }, "summary");
  assert.deepEqual(a, { detail: "verbose", profile: "full" });

  const b = readProfileAndDetail({ detail: "normal" }, "summary");
  assert.deepEqual(b, { detail: "normal", profile: undefined });

  const c = readProfileAndDetail({}, "summary");
  assert.deepEqual(c, { detail: "summary", profile: undefined });

  // An invalid profile string is ignored (not coerced), so legacy detail wins.
  const d = readProfileAndDetail({ profile: "nope", detail: "normal" }, "summary");
  assert.deepEqual(d, { detail: "normal", profile: undefined });
});

// ---------------------------------------------------------------------------
// Cursor encode / decode.
// ---------------------------------------------------------------------------

test("encodeCursor / splitCursor round-trip for the same tool", () => {
  const cursor = encodeCursor("read_asset", 120);
  assert.equal(splitCursor(cursor, "read_asset"), 120);
});

test("splitCursor returns 0 for an undefined / empty cursor", () => {
  assert.equal(splitCursor(undefined, "read_asset"), 0);
  assert.equal(splitCursor("", "read_asset"), 0);
});

test("splitCursor resets to 0 when the tool key does not match", () => {
  // A cursor minted for a different tool must not silently page the wrong data.
  const cursor = encodeCursor("search_assets", 50);
  assert.equal(splitCursor(cursor, "read_asset"), 0);
});

test("splitCursor returns 0 for a malformed cursor", () => {
  assert.equal(splitCursor("nope", "read_asset"), 0);
  assert.equal(splitCursor("read_asset:abc", "read_asset"), 0);
  assert.equal(splitCursor("read_asset:-5", "read_asset"), 0);
});

test("splitCursor tolerates a tool key that itself contains a colon", () => {
  // lastIndexOf(":") splits on the FINAL colon, so a namespaced key works.
  const cursor = encodeCursor("unity:scene_get_data", 7);
  assert.equal(splitCursor(cursor, "unity:scene_get_data"), 7);
});

// ---------------------------------------------------------------------------
// Paging — applyPaging.
// ---------------------------------------------------------------------------

test("applyPaging with page_size <= 0 returns the whole list (back-compat / alias path)", () => {
  const items = [1, 2, 3];
  const { page, block } = applyPaging(items, "t", { page_size: 0 });
  assert.deepEqual(page, [1, 2, 3]);
  assert.equal(block.next_cursor, null);
  assert.equal(block.truncated, 0);
  assert.equal(block.cursor, null);
});

test("applyPaging returns the first page + a next_cursor when more remain", () => {
  const items = Array.from({ length: 10 }, (_, i) => i);
  const { page, block } = applyPaging(items, "t", { page_size: 4 });
  assert.deepEqual(page, [0, 1, 2, 3]);
  assert.equal(block.page_size, 4);
  assert.equal(block.cursor, null);
  assert.equal(block.next_cursor, "t:4");
  assert.equal(block.truncated, 6);
});

test("applyPaging with a cursor returns the middle page", () => {
  const items = Array.from({ length: 10 }, (_, i) => i);
  const { page, block } = applyPaging(items, "t", { page_size: 4, cursor: "t:4" });
  assert.deepEqual(page, [4, 5, 6, 7]);
  assert.equal(block.cursor, "t:4");
  assert.equal(block.next_cursor, "t:8");
  assert.equal(block.truncated, 2);
});

test("applyPaging returns a null next_cursor on the last page", () => {
  const items = Array.from({ length: 10 }, (_, i) => i);
  const { page, block } = applyPaging(items, "t", { page_size: 4, cursor: "t:8" });
  assert.deepEqual(page, [8, 9]);
  assert.equal(block.cursor, "t:8");
  assert.equal(block.next_cursor, null);
  assert.equal(block.truncated, 0);
});

test("applyPaging with page_size larger than items returns everything", () => {
  const items = [1, 2, 3];
  const { page, block } = applyPaging(items, "t", { page_size: 100 });
  assert.deepEqual(page, [1, 2, 3]);
  assert.equal(block.next_cursor, null);
  assert.equal(block.truncated, 0);
});

test("applyPaging on an empty list returns an empty page with a terminal block", () => {
  const { page, block } = applyPaging([], "t", { page_size: 10 });
  assert.deepEqual(page, []);
  assert.equal(block.next_cursor, null);
  assert.equal(block.truncated, 0);
});

test("applyPaging count accuracy: page.length + truncated == total across pages", () => {
  // Walk a 25-item list in pages of 7 and assert the invariant at every step.
  const total = 25;
  const items = Array.from({ length: total }, (_, i) => i);
  const seen: number[] = [];
  let cursor: string | undefined;
  let guard = 0;
  do {
    const { page, block } = applyPaging(items, "t", { page_size: 7, cursor });
    assert.equal(
      page.length + block.truncated,
      total - (cursor ? splitCursor(cursor, "t") : 0),
      "count invariant violated",
    );
    seen.push(...page);
    cursor = block.next_cursor ?? undefined;
    guard++;
    assert.ok(guard < 10, "paging did not terminate");
  } while (cursor);
  assert.equal(seen.length, total);
  assert.deepEqual(seen, items);
});

test("applyPaging clamps an out-of-range cursor to the list length", () => {
  const items = [1, 2, 3];
  // A cursor past the end yields an empty page (no crash, no negative truncate).
  const { page, block } = applyPaging(items, "t", { page_size: 5, cursor: "t:999" });
  assert.deepEqual(page, []);
  assert.equal(block.next_cursor, null);
  assert.equal(block.truncated, 0);
});

// ---------------------------------------------------------------------------
// attachPagination.
// ---------------------------------------------------------------------------

test("attachPagination merges a pagination block into a result object", () => {
  const result = { foo: 1, bar: "x" };
  const out = attachPagination(result, {
    page_size: 4,
    cursor: null,
    next_cursor: "t:4",
    truncated: 6,
  });
  assert.deepEqual(out, {
    foo: 1,
    bar: "x",
    pagination: { page_size: 4, cursor: null, next_cursor: "t:4", truncated: 6 },
  });
});

test("attachPagination is idempotent — an existing block is not overwritten", () => {
  const existing = {
    pagination: { page_size: 1, cursor: null, next_cursor: null, truncated: 0 },
  };
  const out = attachPagination(existing, {
    page_size: 99,
    cursor: "t:5",
    next_cursor: "t:999",
    truncated: 999,
  });
  assert.deepEqual(out, existing);
});

test("attachPagination accepts typed result interfaces (no index signature needed)", () => {
  // Mirrors the CompactSearchResult shape — proves generic constraint works.
  interface Typed { shown: number; matches: number[] }
  const result: Typed = { shown: 3, matches: [1, 2, 3] };
  const out = attachPagination(result, {
    page_size: 2, cursor: null, next_cursor: "t:2", truncated: 1,
  });
  assert.equal(out.shown, 3);
  assert.equal(out.pagination.next_cursor, "t:2");
});

// ---------------------------------------------------------------------------
// Verify-result folding (validate_edit / scan_paths).
// ---------------------------------------------------------------------------

function makeVerifyResult(issueCount: number): Record<string, unknown> {
  const severities = ["error", "warn", "warn", "info"];
  const issues = Array.from({ length: issueCount }, (_, i) => ({
    ruleId: "missing_references",
    severity: severities[i % severities.length],
    assetPath: `Assets/F${i}.prefab`,
    description: `desc ${i}`,
  }));
  return {
    passed: false,
    issues,
    categoriesRun: ["missing_references"],
    rulesApplied: ["missing_references"],
    durationMs: 42,
  };
}

test("foldVerifyResult compact strips issues[] and emits counts", () => {
  const result = makeVerifyResult(6);
  const folded = foldVerifyResult(result, "unity_open_mcp_validate_edit", "compact", {});
  assert.equal(folded.passed, false);
  assert.equal((folded as { issues?: unknown }).issues, undefined, "issues[] stripped in compact");
  assert.equal(folded.issueCount, 6);
  assert.deepEqual(folded.issuesBySeverity, { error: 2, warn: 3, info: 1 });
  // verify metadata passes through.
  assert.deepEqual(folded.categoriesRun, ["missing_references"]);
  assert.equal(folded.durationMs, 42);
  assert.equal((folded as { pagination?: unknown }).pagination, undefined, "no pagination in compact");
});

test("foldVerifyResult balanced keeps issues[] (no paging) with counts attached", () => {
  const result = makeVerifyResult(3);
  const folded = foldVerifyResult(result, "unity_open_mcp_validate_edit", "balanced", {});
  assert.ok(Array.isArray(folded.issues));
  assert.equal((folded.issues as unknown[]).length, 3);
  assert.equal(folded.issueCount, 3);
  assert.equal((folded as { pagination?: unknown }).pagination, undefined);
});

test("foldVerifyResult balanced pages issues[] when page_size is set", () => {
  const result = makeVerifyResult(10);
  const folded = foldVerifyResult(result, "unity_open_mcp_validate_edit", "balanced", { page_size: 4 });
  assert.equal((folded.issues as unknown[]).length, 4, "first page has 4 issues");
  assert.equal(folded.issueCount, 10, "total count preserved");
  assert.deepEqual(folded.pagination, {
    page_size: 4,
    cursor: null,
    next_cursor: "unity_open_mcp_validate_edit:4",
    truncated: 6,
  });
});

test("foldVerifyResult paging resumes from the cursor", () => {
  const result = makeVerifyResult(10);
  const folded = foldVerifyResult(result, "unity_open_mcp_validate_edit", "full", {
    page_size: 4,
    cursor: "unity_open_mcp_validate_edit:4",
  });
  assert.equal((folded.issues as unknown[]).length, 4, "second page has 4 issues");
  assert.deepEqual(folded.pagination, {
    page_size: 4,
    cursor: "unity_open_mcp_validate_edit:4",
    next_cursor: "unity_open_mcp_validate_edit:8",
    truncated: 2,
  });
});

test("foldVerifyResult paging last page has a null next_cursor", () => {
  const result = makeVerifyResult(10);
  const folded = foldVerifyResult(result, "unity_open_mcp_validate_edit", "balanced", {
    page_size: 4,
    cursor: "unity_open_mcp_validate_edit:8",
  });
  const pagination = folded.pagination as { next_cursor: string | null; truncated: number };
  assert.equal((folded.issues as unknown[]).length, 2);
  assert.equal(pagination.next_cursor, null);
  assert.equal(pagination.truncated, 0);
});

test("foldVerifyResult handles a result with no issues", () => {
  const result = { passed: true, issues: [], categoriesRun: [], rulesApplied: [], durationMs: 1 };
  const compact = foldVerifyResult(result, "t", "compact", {});
  assert.equal(compact.issueCount, 0);
  assert.deepEqual(compact.issuesBySeverity, {});
});

test("foldVerifyResult paging count accuracy across a full walk", () => {
  const result = makeVerifyResult(20);
  let cursor: string | undefined;
  let seen = 0;
  let guard = 0;
  do {
    const folded = foldVerifyResult(result, "unity_open_mcp_scan_paths", "balanced", {
      page_size: 6,
      cursor,
    });
    seen += (folded.issues as unknown[]).length;
    const pagination = folded.pagination as { next_cursor: string | null };
    cursor = pagination.next_cursor ?? undefined;
    guard++;
    assert.ok(guard < 10, "paging did not terminate");
  } while (cursor);
  assert.equal(seen, 20);
});

// ---------------------------------------------------------------------------
// Result-block helpers — parseResultBody / withResultBody.
// ---------------------------------------------------------------------------

function textBlock(text: string) {
  return { type: "text" as const, text };
}

test("parseResultBody parses a JSON text block", () => {
  const body = parseResultBody({ content: [textBlock(JSON.stringify({ passed: true, issues: [] }))] });
  assert.deepEqual(body, { passed: true, issues: [] });
});

test("parseResultBody returns null for a non-text / non-JSON block", () => {
  assert.equal(parseResultBody({ content: [{ type: "image", text: "x" }] }), null);
  assert.equal(parseResultBody({ content: [textBlock("not json")] }), null);
  assert.equal(parseResultBody({ content: [textBlock("[1,2,3]")] }), null, "arrays are not objects");
  assert.equal(parseResultBody({ content: [] }), null);
});

test("withResultBody rewrites the first text block and preserves isError + other blocks", () => {
  const result = {
    content: [
      { type: "image" as const, data: "base64", mimeType: "image/png" },
      textBlock(JSON.stringify({ old: true })),
    ],
    isError: false,
  };
  const out = withResultBody(result, { new: true });
  assert.equal(out.isError, false);
  assert.equal(out.content.length, 2);
  assert.equal(out.content[0].type, "image", "non-text block preserved");
  assert.equal(out.content[1].type, "text");
  assert.deepEqual(JSON.parse((out.content[1] as { text: string }).text), { new: true });
});

test("withResultBody prepends a text block when none exists", () => {
  const result = { content: [{ type: "image" as const, data: "x", mimeType: "image/png" }], isError: true };
  const out = withResultBody(result, { x: 1 });
  assert.equal(out.content.length, 2);
  assert.equal(out.content[0].type, "text", "text block inserted first");
  assert.equal(out.isError, true, "isError preserved");
});

// ---------------------------------------------------------------------------
// Back-compat alias: round-trip through readProfileAndDetail proves a caller
// passing only legacy `detail` still resolves the right detail level (the
// alias path is honored, no profile required).
// ---------------------------------------------------------------------------

test("back-compat: legacy detail arg resolves without a profile", () => {
  const cases: Array<{ detail?: string; profile?: OutputProfile; want: DetailLevel }> = [
    { detail: "summary", want: "summary" },
    { detail: "normal", want: "normal" },
    { detail: "verbose", want: "verbose" },
    { profile: "compact", detail: "verbose", want: "summary" }, // profile wins
    { want: "summary" }, // default fallback
  ];
  for (const c of cases) {
    const { detail } = readProfileAndDetail(c, "summary");
    assert.equal(detail, c.want);
  }
});
