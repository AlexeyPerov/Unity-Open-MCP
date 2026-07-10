/**
 * Node:test coverage for the discovery store's pure analysis methods:
 * bucketCounts, missingVersionBuckets, healthFor, versions, installedSet.
 *
 * The store uses Svelte 5 `$state` runes; the `test:state` npm script injects
 * a shim (see _test-shim.ts) so the class constructs. These tests seed the
 * `installations` field directly and exercise the analysis methods — no Tauri
 * invoke, no async load.
 *
 * Run with: `npm run test:state`.
 */
import test from "node:test";
import assert from "node:assert/strict";

import { discoveryStore } from "./discovery.svelte.ts";
import type {
  ProjectEntry,
  UnityInstallation,
} from "$lib/services/config.ts";

function project(id: string, version: string | undefined): ProjectEntry {
  return { id, name: id, path: `/proj/${id}`, unityVersion: version };
}

function install(version: string): UnityInstallation {
  return { version, path: `/unity/${version}`, source: "hub" };
}

function reset() {
  discoveryStore.installations = [];
  discoveryStore.errors = [];
}

// ---------------------------------------------------------------------------
// versions / installedSet
// ---------------------------------------------------------------------------

test("versions lists installed editor version strings", () => {
  reset();
  discoveryStore.installations = [install("2022.3.10"), install("6000.0.1")];
  assert.deepEqual(discoveryStore.versions(), ["2022.3.10", "6000.0.1"]);
});

test("installedSet is a Set of installed versions", () => {
  reset();
  discoveryStore.installations = [install("2022.3.10"), install("6000.0.1")];
  assert.deepEqual(discoveryStore.installedSet(), new Set(["2022.3.10", "6000.0.1"]));
});

test("versions on an empty installation list is []", () => {
  reset();
  assert.deepEqual(discoveryStore.versions(), []);
  assert.equal(discoveryStore.installedSet().size, 0);
});

// ---------------------------------------------------------------------------
// bucketCounts — tally projects by unity version
// ---------------------------------------------------------------------------

test("bucketCounts tallies projects by version", () => {
  reset();
  const projects = [
    project("a", "2022.3.10"),
    project("b", "2022.3.10"),
    project("c", "6000.0.1"),
  ];
  const counts = discoveryStore.bucketCounts(projects);
  assert.equal(counts.get("2022.3.10"), 2);
  assert.equal(counts.get("6000.0.1"), 1);
});

test("bucketCounts skips projects with no unity version", () => {
  reset();
  const projects = [
    project("a", "2022.3.10"),
    project("b", undefined),
    project("c", undefined),
  ];
  const counts = discoveryStore.bucketCounts(projects);
  assert.equal(counts.size, 1);
  assert.equal(counts.get("2022.3.10"), 1);
});

test("bucketCounts on empty projects is an empty map", () => {
  reset();
  assert.equal(discoveryStore.bucketCounts([]).size, 0);
});

// ---------------------------------------------------------------------------
// missingVersionBuckets — versions projects use but no editor is installed
// ---------------------------------------------------------------------------

test("missingVersionBuckets reports versions with no matching install", () => {
  reset();
  discoveryStore.installations = [install("2022.3.10")];
  const projects = [
    project("a", "2022.3.10"), // installed → not missing
    project("b", "6000.0.1"), // missing
    project("c", "6000.0.1"), // missing (same version, count 2)
    project("d", "2021.3.20"), // missing
  ];
  const buckets = discoveryStore.missingVersionBuckets(projects);
  assert.equal(buckets.length, 2);
  // Sorted by count desc, then version asc.
  assert.equal(buckets[0].version, "6000.0.1");
  assert.equal(buckets[0].count, 2);
  assert.equal(buckets[1].version, "2021.3.20");
  assert.equal(buckets[1].count, 1);
});

test("missingVersionBuckets is empty when every project version is installed", () => {
  reset();
  discoveryStore.installations = [install("2022.3.10")];
  const projects = [project("a", "2022.3.10")];
  assert.deepEqual(discoveryStore.missingVersionBuckets(projects), []);
});

test("missingVersionBuckets sorts ties by version ascending", () => {
  reset();
  discoveryStore.installations = [];
  // Two missing versions, each with count 1 → sorted by version name.
  const projects = [
    project("a", "6000.0.5"),
    project("b", "2021.3.10"),
  ];
  const buckets = discoveryStore.missingVersionBuckets(projects);
  assert.equal(buckets[0].version, "2021.3.10");
  assert.equal(buckets[1].version, "6000.0.5");
});

// ---------------------------------------------------------------------------
// healthFor — classify a version against installs + projects
// ---------------------------------------------------------------------------

test('healthFor returns "missing" when no editor is installed', () => {
  reset();
  discoveryStore.installations = [];
  assert.equal(
    discoveryStore.healthFor("2022.3.10", [project("a", "2022.3.10")]),
    "missing",
  );
});

test('healthFor returns "warn" when an editor is installed but no project uses it', () => {
  reset();
  discoveryStore.installations = [install("6000.0.1")];
  assert.equal(discoveryStore.healthFor("6000.0.1", []), "warn");
  assert.equal(
    discoveryStore.healthFor("6000.0.1", [project("a", "2022.3.10")]),
    "warn",
  );
});

test('healthFor returns "ok" when an editor is installed AND a project uses it', () => {
  reset();
  discoveryStore.installations = [install("2022.3.10")];
  assert.equal(
    discoveryStore.healthFor("2022.3.10", [project("a", "2022.3.10")]),
    "ok",
  );
});
