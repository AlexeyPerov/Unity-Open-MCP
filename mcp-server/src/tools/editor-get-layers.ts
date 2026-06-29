import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 5 — typed editor layers read. Read-only: lists every non-empty
// layer slot (index 0–31) from the TagManager. Gate-free. Pair with
// editor_add_layer for the mutating side, and gameobject_modify (layer) for
// consumers.
export const editorGetLayers: Tool = {
  name: "unity_open_mcp_editor_get_layers",
  description:
    "List every non-empty layer slot configured in the project's TagManager " +
    "(the Layers list under Edit → Project Settings → Tags and Layers). " +
    "Returns each populated slot's index (0–31) and name. Built-in layers " +
    "(0 Default, 1 TransparentFX, 2 IgnoreRaycast, 4 Water, 5 UI) are " +
    "included; empty slots are omitted. Read-only and gate-free. Use this " +
    "before gameobject_modify (layer) to discover valid layer indices, and " +
    "before editor_add_layer to find the first free slot. Prefer this over " +
    "raw execute_csharp InternalEditorUtility.layers — schema-validated and " +
    "includes the slot indices the modify tool expects.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
