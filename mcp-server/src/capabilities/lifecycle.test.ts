import test from "node:test";
import assert from "node:assert/strict";

import {
  TOOL_LIFECYCLE,
  LIFECYCLE_TAXONOMY,
  lifecycleFor,
  buildLifecycle,
  type LifecycleClass,
} from "./lifecycle.js";

// ---------------------------------------------------------------------------
// lifecycleFor — per-tool class resolution + the safe default.
// ---------------------------------------------------------------------------

test("lifecycleFor: unlisted tool defaults to none", () => {
  assert.deepEqual(lifecycleFor("unity_open_mcp_brand_new_unclassified"), {
    class: "none",
  });
  assert.deepEqual(lifecycleFor(""), { class: "none" });
});

test("lifecycleFor: read-only introspection tools are none", () => {
  const readOnly = [
    "unity_open_mcp_ping",
    "unity_open_mcp_find_members",
    "unity_open_mcp_editor_status",
    "unity_open_mcp_read_asset",
    "unity_open_mcp_search_assets",
    "unity_open_mcp_list_assets",
    "unity_open_mcp_find_references",
    "unity_open_mcp_dependencies",
    "unity_open_mcp_validate_edit",
    "unity_open_mcp_checkpoint_create",
    "unity_open_mcp_delta",
    "unity_open_mcp_scan_paths",
    "unity_open_mcp_read_compile_errors",
    "unity_open_mcp_bridge_status",
    "unity_open_mcp_capabilities",
    "unity_open_mcp_manage_tools",
    "unity_open_mcp_type_schema",
    "unity_open_mcp_object_get_data",
    "unity_open_mcp_script_read",
    "unity_senses_screenshot",
    "unity_senses_read_console",
    "unity_senses_spatial_query",
    "unity_senses_pull_events",
  ];
  for (const name of readOnly) {
    assert.equal(
      lifecycleFor(name).class,
      "none",
      `${name} should be none (read-only)`,
    );
  }
});

test("lifecycleFor: compile-reload tools — script/asmdef/package/menu/compile_check", () => {
  const expected: LifecycleClass = "compile-reload";
  const compileReload = [
    "unity_open_mcp_execute_csharp",
    "unity_open_mcp_invoke_method",
    "unity_open_mcp_execute_menu",
    "unity_open_mcp_asmdef_create",
    "unity_open_mcp_asmdef_modify",
    "unity_open_mcp_script_write",
    "unity_open_mcp_script_delete",
    "unity_open_mcp_build_set_target",
    "unity_open_mcp_build_set_defines",
    "unity_open_mcp_settings_set_player",
    "unity_open_mcp_scene_open",
    "unity_open_mcp_package_add",
    "unity_open_mcp_package_remove",
    "unity_open_mcp_compile_check",
  ];
  for (const name of compileReload) {
    assert.equal(
      lifecycleFor(name).class,
      expected,
      `${name} should be compile-reload`,
    );
  }
});

test("lifecycleFor: compile_check note carries the editor_instance_locked constraint", () => {
  const entry = lifecycleFor("unity_open_mcp_compile_check");
  assert.equal(entry.class, "compile-reload");
  assert.ok(entry.note, "compile_check must carry a lifecycleNote");
  assert.match(
    entry.note!,
    /editor_instance_locked/,
    "compile_check note must mention editor_instance_locked",
  );
  assert.match(
    entry.note!,
    /batch-only/i,
    "compile_check note must mention it is batch-only",
  );
});

test("lifecycleFor: compile_check note documents the packages/ stale-DLL caveat", () => {
  const note = lifecycleFor("unity_open_mcp_compile_check").note!;
  // The two compile-reload recovery constraints the plan pins.
  assert.match(note, /packages/i, "note must mention packages/ local source");
  assert.match(
    note,
    /ScriptAssemblies/,
    "note must point at Library/ScriptAssemblies DLL mtime verification",
  );
});

test("lifecycleFor: modal-dialog tools", () => {
  assert.equal(lifecycleFor("unity_open_mcp_build_start").class, "modal-dialog");
});

test("lifecycleFor: process-stale tools — async / long-running", () => {
  const processStale = [
    "unity_senses_run_tests",
    "unity_open_mcp_reflection_probe_bake",
    "unity_senses_memory_snapshot_capture",
  ];
  for (const name of processStale) {
    assert.equal(
      lifecycleFor(name).class,
      "process-stale",
      `${name} should be process-stale`,
    );
  }
});

test("lifecycleFor: scene-dirty tools — typed mutators + domain mutators", () => {
  const sceneDirty = [
    "unity_open_mcp_apply_fix",
    "unity_open_mcp_reserialize",
    "unity_open_mcp_material_create",
    "unity_open_mcp_material_set_property",
    "unity_open_mcp_gameobject_create",
    "unity_open_mcp_gameobject_modify",
    "unity_open_mcp_component_add",
    "unity_open_mcp_component_modify",
    "unity_open_mcp_prefab_instantiate",
    "unity_open_mcp_prefab_apply",
    "unity_open_mcp_scene_create",
    "unity_open_mcp_scene_save",
    "unity_open_mcp_editor_add_tag",
    "unity_open_mcp_console_clear",
    "unity_open_mcp_selection_set",
    "unity_open_mcp_navigation_surface_add",
    "unity_open_mcp_cinemachine_create_camera",
    "unity_open_mcp_terrain_create",
    "unity_open_mcp_light_add",
    "unity_open_mcp_ui_canvas_add",
    "unity_open_mcp_spriteatlas_create",
    "unity_open_mcp_texture_set_import",
    "unity_open_mcp_playerprefs_set",
    "unity_open_mcp_editorprefs_set",
    "unity_open_mcp_profiler_save_data",
    "unity_open_mcp_build_set_scenes",
  ];
  for (const name of sceneDirty) {
    assert.equal(
      lifecycleFor(name).class,
      "scene-dirty",
      `${name} should be scene-dirty`,
    );
  }
});

// ---------------------------------------------------------------------------
// Internal consistency — no tool appears in two conflicting classes.
// ---------------------------------------------------------------------------

test("TOOL_LIFECYCLE: every entry has a known class id", () => {
  const valid = new Set<LifecycleClass>([
    "none",
    "compile-reload",
    "modal-dialog",
    "scene-dirty",
    "process-stale",
  ]);
  for (const [name, entry] of Object.entries(TOOL_LIFECYCLE)) {
    assert.ok(
      valid.has(entry.class),
      `${name} has unknown class '${entry.class}'`,
    );
  }
});

test("TOOL_LIFECYCLE: no tool listed as 'none' (none is the implicit default)", () => {
  // The table only carries declarations that differ from the safe default.
  // A 'none' entry here would be dead data — lifecycleFor() already returns
  // none for unlisted tools.
  for (const [name, entry] of Object.entries(TOOL_LIFECYCLE)) {
    assert.notEqual(
      entry.class,
      "none",
      `${name} is listed as 'none' but none is the implicit default — remove the entry`,
    );
  }
});

// ---------------------------------------------------------------------------
// Taxonomy table — the agent-facing 5-class documentation.
// ---------------------------------------------------------------------------

test("LIFECYCLE_TAXONOMY: exactly the 5 classes in canonical order", () => {
  const ids = LIFECYCLE_TAXONOMY.map((e) => e.class);
  assert.deepEqual(ids, [
    "none",
    "compile-reload",
    "modal-dialog",
    "scene-dirty",
    "process-stale",
  ]);
});

test("LIFECYCLE_TAXONOMY: every entry has meaning / bridge / recovery text", () => {
  for (const entry of LIFECYCLE_TAXONOMY) {
    assert.ok(entry.meaning.trim(), `${entry.class} meaning empty`);
    assert.ok(entry.bridge.trim(), `${entry.class} bridge empty`);
    assert.ok(entry.recovery.trim(), `${entry.class} recovery empty`);
  }
});

test("LIFECYCLE_TAXONOMY: compile-reload recovery mentions editor_instance_locked", () => {
  const entry = LIFECYCLE_TAXONOMY.find((e) => e.class === "compile-reload")!;
  assert.match(entry.recovery, /editor_instance_locked/);
});

test("LIFECYCLE_TAXONOMY: no internal-ID leakage (clean of milestone/specs refs)", () => {
  const forbidden = [/M\d+/, /specs?\//, /execution-plan/i, /T\d+\.\d+/, /T-fix/];
  for (const entry of LIFECYCLE_TAXONOMY) {
    const text = `${entry.meaning} ${entry.bridge} ${entry.recovery}`;
    for (const re of forbidden) {
      assert.doesNotMatch(
        text,
        re,
        `${entry.class} leaks internal ID matching ${re}`,
      );
    }
  }
});

// ---------------------------------------------------------------------------
// buildLifecycle — the block attached to the capabilities response.
// ---------------------------------------------------------------------------

test("buildLifecycle: returns the 5-class taxonomy + non-empty guidance", () => {
  const block = buildLifecycle();
  assert.equal(block.classes.length, 5);
  assert.ok(block.guidance.trim().length > 0);
  assert.match(block.guidance, /lifecycle/i);
});
