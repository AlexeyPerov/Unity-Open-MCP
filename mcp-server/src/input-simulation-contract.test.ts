import test from 'node:test';
import assert from 'node:assert/strict';
import {ALL_TOOLS} from './tools/index.js';
import {BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS} from './constants.js';

function tool(suffix: string) {
  const value = ALL_TOOLS.find(t => t.name === `unity_open_mcp_${suffix}` || t.name === `unity_senses_${suffix}`);
  assert(value, suffix);
  return value;
}
function property(suffix: string, name: string) {
  return tool(suffix).inputSchema.properties![name] as Record<string, unknown>;
}
test('input frame/gesture bounds are shared by the published schemas', () => {
  for (const [suffix, name, min, max] of [
    ['inputsim_step', 'frames', 0, 60],
    ['inputsim_key', 'advance_frames', 0, 60],
    ['inputsim_touch', 'advance_frames', 0, 60],
    ['inputsim_touch', 'steps', 1, 100],
    ['inputsim_pointer', 'drag_steps', 1, 100],
    ['inputsim_pointer3d', 'drag_steps', 1, 100],
  ] as const) {
    assert.equal(property(suffix, name).minimum, min);
    assert.equal(property(suffix, name).maximum, max);
  }
});
test('input schemas explain named release, touch resolution, and settle truth', () => {
  assert.match(tool('inputsim_key').description!, /preserving other held keys/);
  assert.doesNotMatch(tool('inputsim_key').description!, /releases ALL/);
  assert.match(tool('inputsim_touch').description!, /ambiguous_target/);
  assert.match(tool('inputsim_touch').description!, /no object_id parameter/);
  assert.equal(tool('inputsim_touch').inputSchema.properties!.object_id, undefined);
  assert.match(String(property('inputsim_step', 'settle_ms').description), /does not wait/);
});
test('live timeout schemas agree with the bridge host ceiling', () => {
  for (const suffix of ['editor_status', 'memory_snapshot_capture'])
    assert.equal(property(suffix, 'timeout_ms').maximum, BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS);
});
