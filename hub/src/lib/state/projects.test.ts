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

test("project edits send their base snapshot and reconcile independent backend fields", async () => {
  const { ProjectsStore } = await import("./projects.svelte.ts");
  const store = new ProjectsStore();
  const original = project("a");
  store.projects = [original];
  const previous = Object.getOwnPropertyDescriptor(globalThis, "window");
  Object.defineProperty(globalThis, "window", { configurable: true, value: {
    __TAURI_INTERNALS__: { async invoke(command: string, args: { projects: { projects: ProjectEntry[] }; base: { projects: ProjectEntry[] } }) {
      assert.equal(command, "save_projects");
      assert.equal(args.base.projects[0].launchArgs, undefined);
      assert.equal(args.projects.projects[0].launchArgs, "-batchmode");
      return { version: 1, projects: [{ ...args.projects.projects[0], frecency: 42 }, project("b")] };
    } },
  } });
  try {
    await store.update({ ...original, launchArgs: "-batchmode" });
    assert.equal(store.projects[0], original, "existing row identity is preserved");
    assert.equal(store.projects[0].frecency, 42);
    assert.equal(store.projects[1].id, "b");
  } finally {
    if (previous) Object.defineProperty(globalThis, "window", previous);
    else Reflect.deleteProperty(globalThis, "window");
  }
});

test("queued draft saves keep the newest local draft until its response arrives", async () => {
  const { ProjectsStore } = await import("./projects.svelte.ts");
  const store = new ProjectsStore();
  store.projects = [project("a")];
  const writes: Array<{ payload: { version: number; projects: ProjectEntry[] }; resolve(value: unknown): void }> = [];
  const previous = Object.getOwnPropertyDescriptor(globalThis, "window");
  Object.defineProperty(globalThis, "window", { configurable: true, value: {
    __TAURI_INTERNALS__: { invoke(_command: string, args: { projects: { version: number; projects: ProjectEntry[] } }) {
      return new Promise(resolve => writes.push({ payload: structuredClone(args.projects), resolve }));
    } },
  } });
  const firstDraft = { presetId: "first" } as unknown as NonNullable<ProjectEntry["aiSetupWizard"]>;
  const secondDraft = { presetId: "second" } as unknown as NonNullable<ProjectEntry["aiSetupWizard"]>;
  const tick = () => new Promise<void>(resolve => setImmediate(resolve));
  try {
    const first = store.updateDraftOnly("a", firstDraft);
    const second = store.updateDraftOnly("a", secondDraft);
    await tick();
    assert.equal(writes.length, 1);
    assert.deepEqual(writes[0].payload.projects[0].aiSetupWizard, firstDraft);
    writes[0].resolve(writes[0].payload); await first; await tick();
    assert.deepEqual(store.projects[0].aiSetupWizard, secondDraft);
    writes[1].resolve(writes[1].payload); await second;
    assert.deepEqual(store.projects[0].aiSetupWizard, secondDraft);
  } finally {
    if (previous) Object.defineProperty(globalThis, "window", previous);
    else Reflect.deleteProperty(globalThis, "window");
  }
});
