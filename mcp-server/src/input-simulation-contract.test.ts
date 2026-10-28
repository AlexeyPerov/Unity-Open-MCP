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
test('touch, gesture and raycast_real pointer share the backend contract', () => {
  for (const suffix of ['inputsim_touch', 'inputsim_gesture', 'inputsim_pointer'])
    assert.deepEqual(property(suffix, 'backend').enum, ['auto', 'legacy_touch', 'input_system']);
  assert.deepEqual(property('inputsim_pointer', 'routing').enum, ['dispatch', 'raycast_real']);
  assert.equal(property('inputsim_pointer', 'routing').default, 'dispatch');
  for (const suffix of ['inputsim_touch', 'inputsim_gesture']) {
    const description = tool(suffix).description!;
    assert.match(description, /input_backend_unavailable/);
    assert.match(description, /legacy_multitouch_unsupported/);
    assert.match(description, /unsupported_editor_version/);
  }
  assert.doesNotMatch(tool('inputsim_touch').description!, /Requires com\.unity\.inputsystem/);
});
test('gesture schema bounds fingers and keyframes and validates the operation id handle', () => {
  const fingers = property('inputsim_gesture', 'fingers') as Record<string, any>;
  assert.equal(fingers.minItems, 1);
  assert.equal(fingers.maxItems, 10);
  assert.equal(fingers.items.properties.path.minItems, 2);
  assert.equal(fingers.items.properties.path.maxItems, 256);
  assert.deepEqual(fingers.items.properties.path.items.properties.phase.enum, ['down', 'move', 'hold', 'up']);
  assert.equal(fingers.items.properties.path.items.additionalProperties, false);
  assert.deepEqual(property('inputsim_gesture', 'coords').enum, ['screen', 'normalized', 'target']);
  assert.deepEqual(property('inputsim_gesture', 'time').enum, ['realtime', 'gameplay']);
  assert.equal(property('inputsim_gesture', 'operation_id').pattern, '^[A-Za-z0-9-]{8,64}$');
  assert.match(tool('inputsim_gesture').description!, /job_id/);
  assert.match(tool('inputsim_gesture').description!, /batch_execute/);
});
