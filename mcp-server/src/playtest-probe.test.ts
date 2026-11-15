import test from "node:test";
import assert from "node:assert/strict";
import { ALL_TOOLS } from "./tools/index.js";
import {
  PROBE_ACTIONS, PROBE_ID_PATTERN, PROBE_MAX_EVERY_FRAMES, PROBE_MAX_READ, PROBE_MAX_SAMPLES,
} from "./tools/playtest-probe.js";
import { CONDITION_LEAVES, PROBE_CONDITION_STATES } from "./tools/playtest-conditions.js";
import { validateSchema } from "./tool-contract.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { lifecycleFor, lifecycleForCall } from "./capabilities/lifecycle.js";
import { RECOMMENDED_TOOL_CHAINS } from "./capabilities/cost-hints.js";

const NAME = "unity_open_mcp_playtest_probe";

function tool(name = NAME) {
  const value = ALL_TOOLS.find((t) => t.name === name);
  assert(value, name);
  return value;
}
function props(name = NAME) {
  return tool(name).inputSchema.properties as Record<string, any>;
}

test("playtest_probe schema: actions, id pattern and the bridge's caps", () => {
  const p = props();
  assert.deepEqual(p.action.enum, [...PROBE_ACTIONS]);
  assert.deepEqual(PROBE_ACTIONS, ["start", "read", "stop", "list"]);
  assert.deepEqual(tool().inputSchema.required, ["action"]);
  const id = new RegExp(PROBE_ID_PATTERN);
  for (const ok of ["hp", "boss.hp", "wave_2-x", "A".repeat(64)]) assert.ok(id.test(ok), ok);
  for (const bad of ["", "-lead", ".hidden", "a b", "a/b", "A".repeat(65)]) assert.ok(!id.test(bad), bad);
  assert.equal(p.max_samples.maximum, PROBE_MAX_SAMPLES);
  assert.equal(PROBE_MAX_SAMPLES, 5000);
  assert.equal(p.every_frames.maximum, PROBE_MAX_EVERY_FRAMES);
  assert.equal(p.max.maximum, PROBE_MAX_READ);
  assert.equal(p.budget_ms.maximum, 100);
  assert.equal(tool().inputSchema.additionalProperties, false);
  // The GameObject target uses the canonical selector names.
  assert.ok(p.game_object_path && p.instance_id && p.component_type);
  assert.deepEqual(validateSchema({ action: "start", id: "hp", member: "x", max_samples: PROBE_MAX_SAMPLES + 1 }, tool().inputSchema as any),
    ["args.max_samples violates maximum"]);
  assert.ok(validateSchema({ action: "start", id: "hp", member: "x", game_object_path: "A", instance_id: "5" }, tool().inputSchema as any)
    .some((e) => e.includes("only one GameObject selector")));
});

test("playtest_probe until is the playtest_wait condition grammar, and waits can watch a probe", () => {
  const until = props().until;
  for (const leaf of CONDITION_LEAVES) assert.ok(until.properties[leaf], leaf);
  assert.deepEqual(until.properties.probe.properties.state.enum, [...PROBE_CONDITION_STATES]);
  const wait = props("unity_open_mcp_playtest_wait").condition;
  assert.deepEqual(until.properties, wait.properties, "same leaves and composites as playtest_wait");
  assert.ok(wait.properties.probe, "playtest_wait accepts {probe:{id}}");
  assert.deepEqual(validateSchema({ condition: { probe: { id: "hp" } } }, tool("unity_open_mcp_playtest_wait").inputSchema as any), []);
  assert.ok(validateSchema({ condition: { probe: { id: "hp", state: "gone" } } }, tool("unity_open_mcp_playtest_wait").inputSchema as any).length > 0);
});

test("playtest_probe description steers away from snippet hooks and states the lifecycle", () => {
  const d = tool().description!;
  for (const phrase of ["onBeforeRender", "AppDomain.SetData", "expression_not_read_only", "play_mode_required", "until_met",
    "max_samples_reached", "play_mode_exited", "over_budget", "probe_capacity", "next_cursor", "domain reloads"])
    assert.ok(d.includes(phrase), phrase);
});

test("playtest_probe is a gate-free playtest tool a tracing intent finds", () => {
  assert.equal(groupFor(NAME), "playtest");
  assert.equal(lifecycleFor(NAME).class, "none");
  assert.equal(lifecycleForCall(NAME, { action: "start", id: "hp", member: "x" }).class, "none");
  assert.equal(props().gate, undefined, "no gate parameter: never a project mutation");
  const ids = recommendGroups({ intent: "trace values every frame during the playtest" }).groups.map((g) => g.id);
  assert.ok(ids.includes("playtest"), JSON.stringify(ids));
  const chain = RECOMMENDED_TOOL_CHAINS.find((c) => c.name === "playtest-trace-values");
  assert.ok(chain, "playtest-trace-values chain");
  assert.ok(chain!.steps.some((s) => s.includes("next_cursor")));
});
