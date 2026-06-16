/**
 * Node:test harness for the frecency sort module.
 * Run with: `node --test --experimental-strip-types --no-warnings src/lib/frecency.test.ts`
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  FRECENCY_HALF_LIFE_DAYS,
  frecencyScore,
  compareFrecency,
  compareLastModified,
} from "./frecency.ts";
import type { ProjectEntry } from "./services/config.ts";

function project(overrides: Partial<ProjectEntry> = {}): ProjectEntry {
  return {
    id: "p",
    name: "P",
    path: "/p",
    ...overrides,
  };
}

const NOW = Date.UTC(2026, 5, 16, 0, 0, 0); // 2026-06-16T00:00:00Z
const MS_PER_DAY = 86_400_000;

function isoDaysAgo(days: number): string {
  return new Date(NOW - days * MS_PER_DAY).toISOString();
}

// ---------------------------------------------------------------------------
// frecencyScore
// ---------------------------------------------------------------------------

test("frecencyScore returns 0 for frecency=0", () => {
  const score = frecencyScore(project({ frecency: 0, lastLaunchAt: isoDaysAgo(1) }), NOW);
  assert.equal(score, 0);
});

test("frecencyScore returns 0 for missing frecency", () => {
  const score = frecencyScore(project({ lastLaunchAt: isoDaysAgo(1) }), NOW);
  assert.equal(score, 0);
});

test("frecencyScore returns 0 for negative frecency", () => {
  const score = frecencyScore(project({ frecency: -5, lastLaunchAt: isoDaysAgo(1) }), NOW);
  assert.equal(score, 0);
});

test("frecencyScore returns 0 when lastLaunchAt is missing", () => {
  const score = frecencyScore(project({ frecency: 10 }), NOW);
  assert.equal(score, 0);
});

test("frecencyScore returns 0 when lastLaunchAt is unparseable", () => {
  const score = frecencyScore(project({ frecency: 10, lastLaunchAt: "not-a-date" }), NOW);
  assert.equal(score, 0);
});

test("frecencyScore returns full counter when launched exactly now", () => {
  const score = frecencyScore(project({ frecency: 10, lastLaunchAt: new Date(NOW).toISOString() }), NOW);
  assert.equal(score, 10);
});

test("frecencyScore decays to half at the half-life", () => {
  const score = frecencyScore(
    project({ frecency: 10, lastLaunchAt: isoDaysAgo(FRECENCY_HALF_LIFE_DAYS) }),
    NOW,
  );
  assert.ok(Math.abs(score - 5) < 1e-9, `expected ~5, got ${score}`);
});

test("frecencyScore decays to a quarter at twice the half-life", () => {
  const score = frecencyScore(
    project({ frecency: 10, lastLaunchAt: isoDaysAgo(FRECENCY_HALF_LIFE_DAYS * 2) }),
    NOW,
  );
  assert.ok(Math.abs(score - 2.5) < 1e-9, `expected ~2.5, got ${score}`);
});

test("frecencyScore is monotonic decreasing as age grows", () => {
  const recent = frecencyScore(project({ frecency: 10, lastLaunchAt: isoDaysAgo(1) }), NOW);
  const older = frecencyScore(project({ frecency: 10, lastLaunchAt: isoDaysAgo(30) }), NOW);
  assert.ok(recent > older, `recent (${recent}) should outrank older (${older})`);
});

test("frecencyScore never goes negative", () => {
  // 1000 days ago is well past several half-lives but must stay >= 0.
  const score = frecencyScore(project({ frecency: 10, lastLaunchAt: isoDaysAgo(1000) }), NOW);
  assert.ok(score >= 0);
  assert.ok(score < 0.001);
});

test("frecencyScore clamps a future lastLaunchAt to full counter", () => {
  // Clock-skew / future timestamps must not inflate the score above the counter.
  const score = frecencyScore(
    project({ frecency: 10, lastLaunchAt: new Date(NOW + MS_PER_DAY).toISOString() }),
    NOW,
  );
  assert.equal(score, 10);
});

// ---------------------------------------------------------------------------
// compareFrecency
// ---------------------------------------------------------------------------

test("compareFrecency ranks higher score first", () => {
  const a = project({ frecency: 10, lastLaunchAt: isoDaysAgo(1) });
  const b = project({ frecency: 1, lastLaunchAt: isoDaysAgo(1) });
  // a has the higher score -> sort(a,b) < 0 means a comes first.
  assert.ok(compareFrecency(a, b, NOW) < 0);
  assert.ok(compareFrecency(b, a, NOW) > 0);
});

test("compareFrecency tiebreaks on lastModifiedAt (most recent first)", () => {
  const a = project({ frecency: 0, lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({ frecency: 0, lastModifiedAt: "2026-06-10T00:00:00Z" });
  // b is more recently modified -> b first -> sort(a,b) > 0.
  assert.ok(compareFrecency(a, b, NOW) > 0);
  assert.ok(compareFrecency(b, a, NOW) < 0);
});

test("compareFrecency returns 0 when scores and timestamps match", () => {
  const a = project({ frecency: 5, lastLaunchAt: isoDaysAgo(2), lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({ frecency: 5, lastLaunchAt: isoDaysAgo(2), lastModifiedAt: "2026-06-01T00:00:00Z" });
  assert.equal(compareFrecency(a, b, NOW), 0);
});

test("compareFrecency sorts a project with undefined lastModifiedAt last", () => {
  const a = project({ frecency: 0, lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({ frecency: 0 }); // no lastModifiedAt
  // a has a timestamp, b does not -> a first -> sort(a,b) < 0.
  assert.ok(compareFrecency(a, b, NOW) < 0);
  assert.ok(compareFrecency(b, a, NOW) > 0);
});

test("compareFrecency with a real array sorts by score then timestamp", () => {
  const items = [
    project({ id: "low", frecency: 1, lastLaunchAt: isoDaysAgo(1), lastModifiedAt: "2026-06-01T00:00:00Z" }),
    project({ id: "high", frecency: 10, lastLaunchAt: isoDaysAgo(1), lastModifiedAt: "2026-05-01T00:00:00Z" }),
    project({ id: "tie-old", frecency: 0, lastModifiedAt: "2026-06-01T00:00:00Z" }),
    project({ id: "tie-new", frecency: 0, lastModifiedAt: "2026-06-10T00:00:00Z" }),
  ];
  const sorted = [...items].sort((a, b) => compareFrecency(a, b, NOW));
  assert.deepEqual(sorted.map((p) => p.id), ["high", "low", "tie-new", "tie-old"]);
});

// ---------------------------------------------------------------------------
// compareLastModified
// ---------------------------------------------------------------------------

test("compareLastModified ranks most recent first", () => {
  const a = project({ lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({ lastModifiedAt: "2026-06-10T00:00:00Z" });
  assert.ok(compareLastModified(a, b) > 0);
  assert.ok(compareLastModified(b, a) < 0);
});

test("compareLastModified returns 0 when timestamps match", () => {
  const a = project({ lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({ lastModifiedAt: "2026-06-01T00:00:00Z" });
  assert.equal(compareLastModified(a, b), 0);
});

test("compareLastModified sorts undefined lastModifiedAt last", () => {
  const a = project({ lastModifiedAt: "2026-06-01T00:00:00Z" });
  const b = project({}); // no lastModifiedAt
  assert.ok(compareLastModified(a, b) < 0);
  assert.ok(compareLastModified(b, a) > 0);
});
