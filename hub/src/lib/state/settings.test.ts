import test from "node:test";
import assert from "node:assert/strict";
import { SettingsStore } from "./settings.svelte.ts";
import type { Settings } from "../services/config.ts";

function setup() {
  const writes: Array<{ settings: Settings; resolve(): void; reject(error: Error): void }> = [];
  const previous = Object.getOwnPropertyDescriptor(globalThis, "window");
  Object.defineProperty(globalThis, "window", { configurable: true, value: {
    __TAURI_INTERNALS__: { invoke(command: string, args: { settings: Settings }) {
      assert.equal(command, "save_settings");
      return new Promise<void>((resolve, reject) => writes.push({ settings: structuredClone(args.settings), resolve, reject }));
    } },
  } });
  const store = new SettingsStore();
  store.current = { launch: { mode: "openProject", rememberLastSelection: true }, projectList: { showPathColumn: false } } as Settings;
  return { store, writes, restore() {
    if (previous) Object.defineProperty(globalThis, "window", previous);
    else Reflect.deleteProperty(globalThis, "window");
  } };
}

const tick = () => new Promise<void>(resolve => setImmediate(resolve));

test("rapid settings edits are cumulative and save in order", async () => {
  const { store, writes, restore } = setup();
  try {
    const first = store.setShowPathColumn(true);
    const second = store.setRememberLastSelection(false);
    await tick();
    assert.equal(writes.length, 1, "second write must wait for the first");
    writes[0].resolve(); await first; await tick();
    assert.equal(store.saving, true, "saving remains true while the second write is pending");
    assert.equal(writes[1].settings.projectList.showPathColumn, true);
    assert.equal(writes[1].settings.launch.rememberLastSelection, false);
    writes[1].resolve(); await second;
    assert.equal(store.current?.projectList.showPathColumn, true);
    assert.equal(store.current?.launch.rememberLastSelection, false);
    assert.equal(store.saving, false);
  } finally { restore(); }
});

test("failed settings save restores confirmed state and does not block later edits", async () => {
  const { store, writes, restore } = setup();
  try {
    const first = store.setShowPathColumn(true);
    const rejection = assert.rejects(first, /disk full/);
    await tick(); writes[0].reject(new Error("disk full")); await rejection;
    assert.equal(store.current?.projectList.showPathColumn, false);
    const second = store.setRememberLastSelection(false);
    await tick(); writes[1].resolve(); await second;
    assert.equal(store.current?.launch.rememberLastSelection, false);
    assert.equal(store.saveError, null);
  } finally { restore(); }
});
