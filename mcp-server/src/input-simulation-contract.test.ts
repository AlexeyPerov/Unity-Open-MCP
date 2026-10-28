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
test('label targeting (target_text) is shared by pointer, touch and gesture keyframes', () => {
  const keyframe = (property('inputsim_gesture', 'fingers') as Record<string, any>).items.properties.path.items;
  const shapes = [
    tool('inputsim_pointer').inputSchema.properties as Record<string, any>,
    tool('inputsim_touch').inputSchema.properties as Record<string, any>,
    keyframe.properties as Record<string, any>,
  ];
  for (const props of shapes) {
    assert.equal(props.target_text.type, 'string');
    assert.equal(props.target_text.minLength, 1);
    assert.deepEqual(props.text_match.enum, ['exact', 'contains', 'regex']);
    // No default: the MCP server fills scalar defaults, and an omitted value must stay "exact".
    assert.equal(props.text_match.default, undefined);
    assert.equal(props.index.type, 'integer');
    assert.equal(props.index.minimum, 0);
    assert.equal(props.under.type, 'string');
    assert.match(props.target_text.description, /normalisation/);
  }
  assert.equal(keyframe.additionalProperties, false);
  assert.match(tool('inputsim_pointer').description!, /conflicting_targets/);
  assert.match(tool('inputsim_touch').description!, /target_text/);
  assert.match(String(property('inputsim_gesture', 'coords').description), /target_text/);
});
test('ui_snapshot is a bounded, profile-shaped read in the playtest group', () => {
  const props = tool('ui_snapshot').inputSchema.properties as Record<string, any>;
  assert.deepEqual(props.profile.enum, ['compact', 'balanced', 'full']);
  assert.equal(props.profile.default, 'compact');
  assert.equal(props.page_size.default, 50);
  assert.equal(props.page_size.minimum, 1);
  assert.equal(props.page_size.maximum, 200);
  // visible_only defaults per profile on the bridge, so the schema must not fill one.
  assert.equal(props.visible_only.default, undefined);
  assert.equal(props.placeholder_pattern.default, undefined);
  assert.equal(props.min_touch_size.default, undefined);
  assert.equal(tool('ui_snapshot').inputSchema.required, undefined);
  const description = tool('ui_snapshot').description!;
  for (const word of ['on_screen', 'clipped', 'offscreen', 'transparent', 'zero_size', 'target_text', 'small_touch_targets'])
    assert.match(description, new RegExp(word));
});
