// Input simulation — type text into a uGUI InputField / TextMeshPro input field.
//
// The bridge types character by character through the field's own public key
// path (ProcessEvent), so the field's validation, content type, character limit,
// read-only flag and line type decide what goes in and onValueChanged fires per
// accepted character; then it ends the edit (end_edit / submit / none). No
// selection, no focus emulation. Password / PIN fields answer lengths only.
// Play-mode only; gate-free. See TextEntryTools.cs.
import { makeTool } from "./schema-fragments.js";
import { textTargetProps } from "./text-target.js";

export const INPUTSIM_TEXT_TOOL = "unity_open_mcp_inputsim_text";
export const TEXT_ENTRY_MODES = ["replace", "append", "clear"] as const;
export const TEXT_ENTRY_ENDS = ["end_edit", "submit", "none"] as const;

export const inputsimText = makeTool(
  INPUTSIM_TEXT_TOOL,
  "Type text into a uGUI InputField or a TextMeshPro input field during play mode, as a player with a keyboard " +
    "would: character by character through the field's own key path, so ITS validation decides — onValidateInput, " +
    "content type (integer, email, name, password…), characterLimit, read-only, single- vs multi-line — and " +
    "onValueChanged fires per accepted character. mode: replace (default; empties the field first — one " +
    "onValueChanged(\"\") — then types), append (types at the end), clear (empties, no text). end: end_edit (default; " +
    "onEndEdit, as tapping away does), submit (onSubmit then onEndEdit, as Enter does in a single-line field), none " +
    "(left mid-edit). Name the field by target_text (its placeholder or current text as ui_snapshot shows it, role " +
    "input), object_id or target; a label / placeholder child resolves to its field. The answer is honest: field kind " +
    "(ugui | tmp), content / line type, limit, focused; wasFocused (it had focus before typing); textBefore / textAfter; typed.requested / accepted / " +
    "transformed / rejected (rejectedAt with reason limit | validation); events.value_changed count, end_edit, submit; " +
    "an exception a listener threw (also logged to the console, like the game's own). Password / PIN fields (and " +
    "secret:true) answer lengths only, never the text. Focus is not emulated: tap the field with inputsim_pointer first " +
    "when the game reacts to selecting it. The on-screen keyboard of a phone never appears in the Editor. Refuses: " +
    "play_mode_required, not_a_text_field (find fields with ui_snapshot role=input), field_not_interactable, " +
    "field_read_only, field_inactive. Gate-free. Requires com.unity.ugui (TextMeshPro fields when TextMeshPro is " +
    "installed).",
  {
    properties: {
      text: {
        type: "string",
        maxLength: 1000,
        description: "What to type (≤ 1000 characters; \\n starts a new line in a multi-line field and is dropped in a single-line one). Required unless mode is clear.",
      },
      mode: {
        type: "string",
        enum: [...TEXT_ENTRY_MODES],
        description: "replace (default) = empty the field, then type; append = type at the end; clear = only empty it.",
      },
      end: {
        type: "string",
        enum: [...TEXT_ENTRY_ENDS],
        description: "end_edit (default) = onEndEdit; submit = onSubmit, then onEndEdit; none = leave the edit open.",
      },
      object_id: { type: "string", description: "The field (or its label / placeholder) by instance id, e.g. a ui_snapshot node id." },
      target: { type: "string", description: "The field by GameObject name or slash-path (duplicates refuse with ambiguous_target)." },
      ...textTargetProps("The field"),
      secret: {
        type: "boolean",
        description: "The text is a secret (a password, a code): the answer gives lengths only and a playtest session records the text as [redacted]. Password / PIN fields are treated so without it.",
      },
    },
  },
);
