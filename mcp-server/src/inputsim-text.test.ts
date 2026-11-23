// inputsim_text (type into uGUI / TextMeshPro input fields): schema and catalog
// contract, and how a playtest session records it — an input call whose typed
// text is redacted for a secret call or a password-like field.
import { test } from "node:test";
import assert from "node:assert/strict";
import { ALL_TOOLS, INPUTSIM_TOOLS } from "./tools/index.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { INPUTSIM_TEXT_TOOL, TEXT_ENTRY_ENDS, TEXT_ENTRY_MODES } from "./tools/inputsim-text.js";
import { classifyCall, redactTypedText } from "./playtest/recorder.js";

const tool = () => ALL_TOOLS.find(t => t.name === INPUTSIM_TEXT_TOOL)!;

test("inputsim_text: schema, input-simulation group, intents", () => {
  assert.ok(tool());
  assert.ok(INPUTSIM_TOOLS.includes(tool()));
  assert.equal(groupFor(INPUTSIM_TEXT_TOOL), "input-simulation");
  const props = tool().inputSchema.properties as Record<string, any>;
  assert.deepEqual(props.mode.enum, [...TEXT_ENTRY_MODES]);
  assert.deepEqual([...TEXT_ENTRY_MODES], ["replace", "append", "clear"]);
  assert.deepEqual(props.end.enum, [...TEXT_ENTRY_ENDS]);
  assert.deepEqual([...TEXT_ENTRY_ENDS], ["end_edit", "submit", "none"]);
  assert.equal(props.text.maxLength, 1000);
  assert.equal(props.object_id.type, "string", "EntityId-safe");
  for (const key of ["target_text", "under", "index", "text_match", "secret", "target"]) assert.ok(props[key], key);
  for (const [key, value] of Object.entries(props)) assert.equal((value as any).default, undefined, key);
  assert.equal(tool().inputSchema.additionalProperties, false);
  for (const phrase of ["character by character", "onValidateInput", "characterLimit", "end_edit", "submit", "rejectedAt",
    "lengths only", "play_mode_required", "not_a_text_field", "field_read_only", "Focus is not emulated"])
    assert.ok(tool().description!.includes(phrase), phrase);
  for (const intent of ["type a nickname into the input field", "fill the login form text field"]) {
    const ids = recommendGroups({ intent }).groups.map(g => g.id);
    assert.ok(ids.includes("input-simulation"), `${intent}: ${JSON.stringify(ids)}`);
  }
});

test("inputsim_text in a playtest session: an input call, secret text redacted", () => {
  assert.equal(classifyCall(INPUTSIM_TEXT_TOOL, { text: "Alex", target_text: "Nickname" })?.kind, "input");
  assert.deepEqual(redactTypedText(INPUTSIM_TEXT_TOOL, { text: "Alex", target_text: "Nickname" }), { text: "Alex", target_text: "Nickname" });
  for (const args of [
    { text: "hunter2", target_text: "Password" },
    { text: "1234", target: "Canvas/Login/PinField" },
    { text: "987654", target_text: "Verification code" },
    { text: "anything", target_text: "Nickname", secret: true },
  ]) {
    const out = redactTypedText(INPUTSIM_TEXT_TOOL, args);
    assert.match(String(out.text), /^\[redacted \d+ chars\]$/, JSON.stringify(args));
    assert.ok(!JSON.stringify(out).includes(args.text), "the secret never stays");
  }
  assert.deepEqual(redactTypedText("unity_open_mcp_inputsim_pointer", { text: "x", target_text: "Password" }), { text: "x", target_text: "Password" });
  assert.equal(redactTypedText(INPUTSIM_TEXT_TOOL, { mode: "clear", target_text: "Password" }).text, undefined);
});
