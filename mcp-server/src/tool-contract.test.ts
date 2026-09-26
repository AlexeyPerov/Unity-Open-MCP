import test from "node:test";
import assert from "node:assert/strict";
import { ALL_TOOLS } from "./tools/index.js";
import { validateSchema, wireArguments } from "./tool-contract.js";
import { exampleFor, discoverTools } from "./capabilities/discovery.js";
import { ToolSessionState } from "./tool-session-state.js";
const tool = (suffix: string) => ALL_TOOLS.find(t => t.name === `unity_open_mcp_${suffix}`)!;
test("canonical component locators and patch paths map without touching values", () => {
  const args = { game_object_path: "Main Camera", component_type: "UnityEngine.Transform", fields: [{ property_path: "m_LocalPosition", value: { path: "opaque" } }], paths_hint: ["Assets/Scenes/Main.unity"] };
  assert.deepEqual(validateSchema(args, tool("component_modify").inputSchema), []);
  const wire = wireArguments(args, tool("component_modify").inputSchema);
  assert.equal(wire.path, "Main Camera");
  assert.equal(wire.fields[0].path, "m_LocalPosition");
  assert.deepEqual(wire.fields[0].value, { path: "opaque" });
});
test("historical aliases emit canonical guidance and conflicting/unknown keys fail", () => {
  for (const suffix of ["find_references", "dependencies"]) {
    const schema = tool(suffix).inputSchema;
    const notes: string[] = [];
    assert.deepEqual(validateSchema({ path: "Assets/A.prefab" }, schema), []);
    assert.equal(wireArguments({ path: "Assets/A.prefab" }, schema, notes).asset_path, "Assets/A.prefab");
    assert.match(notes.join(), /asset_path/);
    assert.ok(validateSchema({ path: "a", asset_path: "b" }, schema).length);
  }
  assert.match(validateSchema({ modifications: [] }, tool("component_modify").inputSchema).join(), /canonical keys:.*game_object_path/);
});
test("all locator contracts have schema-valid minimal examples", () => {
  for (const t of ALL_TOOLS) {
    if (!["asset_path", "game_object_path", "component_type", "property_path"].some(k => t.inputSchema.properties?.[k])) continue;
    assert.deepEqual(validateSchema(exampleFor(t.inputSchema), t.inputSchema), [], t.name);
  }
});
test("exact discovery returns one schema regardless of compact profile", () => {
  const result = discoverTools(ALL_TOOLS, new Set(), new ToolSessionState(), undefined, { tool_name: tool("component_modify").name, cursor: "tool-discovery:100" });
  assert.equal(result.tools.length, 1);
  assert.ok("inputSchema" in result.tools[0]);
  assert.equal(result.tools[0].active, false);
  assert.equal(result.tools[0].available, null);
  assert.equal(result.tools[0].mutating, true);
});
test("discovery filters and deterministic pages compose", () => {
  const session = new ToolSessionState();
  const args = { group: "typed-editor", mutating: true, page_size: 2 };
  const first = discoverTools(ALL_TOOLS, new Set(), session, new Set(), args);
  const next = discoverTools(ALL_TOOLS, new Set(), session, new Set(), { ...args, cursor: first.pagination.next_cursor });
  assert.equal(first.tools.length, 2);
  assert.equal(next.tools.length, 2);
  assert.ok(first.tools[1].name < next.tools[0].name);
  assert.ok(first.tools.every(t => t.mutating && t.group === "typed-editor" && t.available === false));
  assert.deepEqual(first, discoverTools(ALL_TOOLS, new Set(), session, new Set(), args));
});

test("null property values remain valid; shadowed selectors are rejected", () => {
  const schema = tool("component_modify").inputSchema;
  const args = { game_object_path: "A", component_type: "UnityEngine.Transform", fields: [{ property_path: "m_Reference", value: null }], paths_hint: ["Assets/A.unity"] };
  assert.deepEqual(validateSchema(args, schema), []);
  assert.match(validateSchema({ ...args, instance_id: 42 }, schema).join(), /only one GameObject selector/);
  assert.match(validateSchema({ ...args, component_instance_id: 42 }, schema).join(), /alternatives/);
});

test("gameobject_modify renames through name; name_target is its by-name selector", () => {
  const schema = tool("gameobject_modify").inputSchema;
  const base = { paths_hint: ["Assets/Scenes/Main.unity"] };
  assert.deepEqual(validateSchema({ ...base, game_object_path: "Root/Player", name: "Hero" }, schema), []);
  assert.deepEqual(validateSchema({ ...base, instance_id: 42, name: "Hero" }, schema), []);
  assert.deepEqual(validateSchema({ ...base, name_target: "Player", name: "Hero" }, schema), []);
  assert.match(validateSchema({ ...base, game_object_path: "Root/Player", name_target: "Player" }, schema).join(),
    /only one GameObject selector.*game_object_path, name_target/);
  const wire = wireArguments({ ...base, game_object_path: "Root/Player", name: "Hero" }, schema);
  assert.equal(wire.path, "Root/Player");
  assert.equal(wire.name, "Hero");
  // Every other GameObject host keeps `name` as a selector.
  const host = { ...base, game_object_path: "Root/Player", name: "Player", component_type: "UnityEngine.BoxCollider" };
  assert.match(validateSchema(host, tool("component_add").inputSchema).join(), /only one GameObject selector.*game_object_path, name/);
});

test("registry forbids unknown keys and every alias names an existing canonical property", () => {
  for (const t of ALL_TOOLS) {
    assert.match(validateSchema({ __unknown_locator: "x" }, t.inputSchema).join(), /__unknown_locator is unknown/, t.name);
    for (const [key, p] of Object.entries(t.inputSchema.properties ?? {})) {
      const prop = p as Record<string, any>;
      if (!prop["x-alias-for"]) continue;
      assert.ok(t.inputSchema.properties?.[prop["x-alias-for"]], `${t.name}.${key}`);
      assert.equal(prop.deprecated, true);
    }
  }
  assert.equal((tool("read_asset").inputSchema.properties?.path as any)["x-alias-for"], undefined, "asset subtree path is not a scene GameObject locator");
});

test("availability, activation, route and tag filters remain independent", () => {
  const session = new ToolSessionState();
  const tools = ALL_TOOLS;
  const local = discoverTools(tools, new Set(), session, undefined, { route: "local", available: true, active: true, page_size: 1000 });
  assert.ok(local.tools.some(t => t.name === "unity_open_mcp_read_compile_errors"));
  const offline = discoverTools(tools, new Set(), session, undefined, { group: "typed-editor", available: false });
  assert.deepEqual(offline.tools, [], "unknown availability is not false");
  const name = "unity_open_mcp_component_get";
  const active = discoverTools(tools, new Set(), session, new Set([name]), { group: "typed-editor", tag: "scene", route: "live", available: true, active: false });
  assert.deepEqual(active.tools.map(t => t.name), [name]);
  session.activate("typed-editor");
  const after = discoverTools(tools, new Set(), session, new Set([name]), { group: "typed-editor", tag: "scene", route: "live", available: true, active: true });
  assert.deepEqual(after.tools.map(t => t.name), [name]);
});

test("jobs discovery remains available locally and reports potential mutation", () => {
  const result = discoverTools(ALL_TOOLS, new Set(), new ToolSessionState(), undefined, {
    tool_name: "unity_open_mcp_jobs",
  });

  assert.equal(result.tools.length, 1);
  assert.equal(result.tools[0].routePolicy, "local");
  assert.equal(result.tools[0].available, true);
  assert.equal(result.tools[0].mutating, true);
});

test("verify filters enumerate implemented rules, excluding planned entries", () => {
  for (const t of ALL_TOOLS) for (const key of ["include_rules", "exclude_rules"]) {
    const p = t.inputSchema.properties?.[key] as any;
    if (!p) continue;
    assert.ok(p.items.enum.includes("missing_references"), t.name);
    assert.ok(!p.items.enum.includes("textures"), t.name);
    assert.ok(!p.items.enum.includes("offline_integrity"), t.name);
  }
});
