/**
 * Node:test coverage for the projects store's synchronous state transitions:
 * find / select / add / replaceAll / updateDraftOnly. The async methods
 * (load / update / remove / persist) route through Tauri invoke and are
 * integration-tested elsewhere; the pure list mutations are the regression-
 * prone surface (selection clearing, draft-in-place).
 *
 * The store uses Svelte 5 `$state` runes; the `test:state` npm script injects
 * a shim (see _test-shim.mjs) so the class constructs.
 *
 * Run with: `npm test`.
 */
import test from "node:test";
import assert from "node:assert/strict";

import { projectsStore } from "./projects.svelte.ts";
import type { ProjectEntry } from "$lib/services/config.ts";

function project(id: string): ProjectEntry {
  return { id, name: id, path: `/proj/${id}` };
}

function reset() {
  projectsStore.projects = [];
  projectsStore.selectedProjectId = null;
}

// ---------------------------------------------------------------------------
// find
// ---------------------------------------------------------------------------

test("find returns the entry by id", () => {
  reset();
  projectsStore.projects = [project("a"), project("b")];
  assert.equal(projectsStore.find("b")?.id, "b");
});

test("find returns undefined for an unknown id", () => {
  reset();
  projectsStore.projects = [project("a")];
  assert.equal(projectsStore.find("zzz"), undefined);
});

// ---------------------------------------------------------------------------
// select — selection clears when the id is absent
// ---------------------------------------------------------------------------

test("select sets the selected id for a known project", () => {
  reset();
  projectsStore.projects = [project("a"), project("b")];
  projectsStore.select("b");
  assert.equal(projectsStore.selectedProjectId, "b");
});

test("select(null) clears the selection", () => {
  reset();
  projectsStore.projects = [project("a")];
  projectsStore.select("a");
  projectsStore.select(null);
  assert.equal(projectsStore.selectedProjectId, null);
});

test("select clears the selection when the id is not in the list", () => {
  reset();
  projectsStore.projects = [project("a")];
  projectsStore.select("a");
  // Selecting an id that is not present nulls the selection (defensive —
  // a stale id after a remove must not leave a dangling selection).
  projectsStore.select("gone");
  assert.equal(projectsStore.selectedProjectId, null);
});

// ---------------------------------------------------------------------------
// add — appends and selects
// ---------------------------------------------------------------------------

test("add appends the entry and selects it", () => {
  reset();
  projectsStore.projects = [project("a")];
  projectsStore.add(project("b"));
  assert.equal(projectsStore.projects.length, 2);
  assert.equal(projectsStore.projects[1].id, "b");
  assert.equal(projectsStore.selectedProjectId, "b");
});

test("add on an empty list seeds it", () => {
  reset();
  projectsStore.add(project("solo"));
  assert.equal(projectsStore.projects.length, 1);
  assert.equal(projectsStore.selectedProjectId, "solo");
});

// ---------------------------------------------------------------------------
// replaceAll — wholesale swap
// ---------------------------------------------------------------------------

test("replaceAll swaps the whole list", () => {
  reset();
  projectsStore.projects = [project("a"), project("b")];
  projectsStore.replaceAll([project("x"), project("y"), project("z")]);
  assert.deepEqual(
    projectsStore.projects.map((p) => p.id),
    ["x", "y", "z"],
  );
});

test("replaceAll with an empty list clears it", () => {
  reset();
  projectsStore.projects = [project("a")];
  projectsStore.replaceAll([]);
  assert.equal(projectsStore.projects.length, 0);
});
