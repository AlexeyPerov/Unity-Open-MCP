import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.2 — VFX Graph block_edit. Compile-gated + auto-activating
// (see vfx-list.ts). Mutating: patches a single property on a block in a .vfx
// — paths_hint is the .vfx asset path, EditorSettle lifecycle. VFX Graph's
// editor graph model is internal and requires the VFX Graph window to be open
// (no stable public headless entry point); when the model cannot be reached,
// the tool returns a structured vfx_block_edit_requires_editor_window error and
// the agent falls back to manual editing.
export const vfxBlockEdit = makeTool(
  "unity_open_mcp_vfx_block_edit",
  "Patch a single property on a block in a VisualEffectGraph (.vfx). " +
    "block_selector names the target block (by type-name fragment, e.g. " +
    "'SetVelocity', 'SetColor'); property is the field name; value_json is the " +
    "new value. Mutating: runs the full gate path; paths_hint is the .vfx asset " +
    "path. Requires com.unity.visualeffectgraph. VFX Graph's editor graph model " +
    "is internal and requires the VFX Graph window to be open; when it is not, " +
    "the tool returns a structured vfx_block_edit_requires_editor_window error " +
    "— open the graph in the VFX Graph window (unity_open_mcp_vfx_open) and " +
    "retry, or edit the block manually in the window.",
  {
    required: ["asset_path", "block_selector", "property", "value_json", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "VisualEffectGraph asset path ('Assets/.../*.vfx').",
          },
          block_selector: {
            type: "string",
            description:
              "Target block selector — a type-name fragment (e.g. 'SetVelocity', " +
              "'SetColor'). The first block whose type name contains the fragment " +
              "(case-insensitive) is patched.",
          },
          property: {
            type: "string",
            description: "The block field to patch.",
          },
          value_json: {
            type: "string",
            description:
              "The new value as a JSON literal (string / number / bool / object).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — must include the .vfx asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
