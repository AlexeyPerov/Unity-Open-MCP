import test from "node:test";
import assert from "node:assert/strict";
import { ALL_TOOLS } from "./tools/index.js";
import { PLAYER_STATE_ACTIONS, PLAYER_STATE_RESTORE_SCOPE } from "./tools/player-state.js";
import { withSchemaDefaults } from "./schema-defaults.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { lifecycleFor, lifecycleForCall } from "./capabilities/lifecycle.js";
import { RECOMMENDED_TOOL_CHAINS } from "./capabilities/cost-hints.js";

const NAME = "unity_open_mcp_player_state";

function tool() {
  const value = ALL_TOOLS.find((t) => t.name === NAME);
  assert(value, NAME);
  return value;
}
function props() {
  return tool().inputSchema.properties as Record<string, any>;
}

test("player_state schema: actions, name pattern, opt-in arguments without defaults", () => {
  const p = props();
  assert.deepEqual(p.action.enum, [...PLAYER_STATE_ACTIONS]);
  assert.deepEqual(PLAYER_STATE_ACTIONS, ["snapshot", "diff", "restore", "list", "delete"]);
  assert.deepEqual(tool().inputSchema.required, ["action"]);
  const pattern = new RegExp(p.name.pattern);
  for (const ok of ["before", "run-1", "a.b_c", "X".repeat(64)]) assert.ok(pattern.test(ok), ok);
  for (const bad of ["", "-lead", ".hidden", "a/b", "a b", "X".repeat(65)]) assert.ok(!pattern.test(bad), bad);
  assert.equal(p.against.pattern, p.name.pattern);
  // Nothing that changes what is captured or applied is filled in by the server.
  for (const key of ["editor_prefs_prefixes", "exclude", "exclude_keys", "max_files_mb", "overwrite", "company", "product", "limit"])
    assert.equal(p[key].default, undefined, key);
  assert.equal(p.editor_prefs_prefixes.items.minLength, 3);
  assert.equal(p.max_files_mb.maximum, 1024);
  assert.equal(p.limit.maximum, 1000);
  assert.equal(tool().inputSchema.additionalProperties, false);
});

test("player_state: restore is a dry run unless confirm:true, scoped to the identity asset", () => {
  const p = props();
  assert.equal(p.confirm.default, false);
  assert.deepEqual(p.paths_hint.default, [PLAYER_STATE_RESTORE_SCOPE]);
  assert.equal(PLAYER_STATE_RESTORE_SCOPE, "ProjectSettings/ProjectSettings.asset");
  // The server materializes the dry-run default and the audit scope.
  const forwarded = withSchemaDefaults(tool(), { action: "restore", name: "before" });
  assert.equal(forwarded.confirm, false);
  assert.deepEqual(forwarded.paths_hint, [PLAYER_STATE_RESTORE_SCOPE]);
  // A caller's own scope wins.
  const own = withSchemaDefaults(tool(), { action: "restore", name: "before", confirm: true, paths_hint: ["Assets/Save"] });
  assert.deepEqual(own.paths_hint, ["Assets/Save"]);
  assert.equal(p.gate.default, "enforce");
});

test("player_state description states the overwrite and the file removal plainly", () => {
  const d = tool().description!;
  for (const phrase of ["OVERWRITES", "DELETED", "EVERY removal", "EDIT MODE ONLY", "dry run", "edit_mode_required",
    "snapshot_project_mismatch", "editor_prefs_prefixes", "outside the project", "playtest.begin", "PlayerPrefs.Save"])
    assert.ok(d.includes(phrase), phrase);
});

test("player_state belongs to the playtest group and a save-state intent finds it", () => {
  assert.equal(groupFor(NAME), "playtest");
  for (const intent of [
    "snapshot the player state before the playtest and restore it after",
    "do not spend my save game during the playtest, restore persistentDataPath",
  ]) {
    const ids = recommendGroups({ intent }).groups.map((g) => g.id);
    assert.ok(ids.includes("playtest"), `${intent}: ${JSON.stringify(ids)}`);
  }
  const chain = RECOMMENDED_TOOL_CHAINS.find((c) => c.name === "playtest-state-sandbox");
  assert.ok(chain, "playtest-state-sandbox chain");
  assert.ok(chain!.steps.some((s) => s.includes("confirm true")));
});

test("player_state lifecycle: only an applied restore carries the gated class", () => {
  assert.equal(lifecycleFor(NAME).class, "scene-dirty");
  assert.equal(lifecycleForCall(NAME, { action: "restore", name: "x", confirm: true }).class, "scene-dirty");
  for (const args of [
    { action: "restore", name: "x" },
    { action: "restore", name: "x", confirm: false },
    { action: "snapshot", name: "x" },
    { action: "diff", name: "x" },
    { action: "list" },
    { action: "delete", name: "x" },
  ])
    assert.equal(lifecycleForCall(NAME, args).class, "none", JSON.stringify(args));
});
