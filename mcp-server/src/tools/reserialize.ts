import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const reserialize: Tool = {
  name: "unity_open_mcp_reserialize",
  description:
    "Round-trip text-serialized assets through Unity's native serializer (AssetDatabase.ForceReserializeAssets). " +
    "Use after directly editing a .prefab/.unity/.asset/.mat/.controller/.anim as YAML to normalize formatting and surface missing fields, wrong indents, or stale fileIDs. " +
    "Mutating: runs the full gate path (checkpoint -> reserialize -> validate -> delta). The `paths` array doubles as the gate's paths_hint scope.",
  inputSchema: {
    type: "object",
    required: ["paths"],
    properties: {
      paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Asset paths to reserialize (e.g. [\"Assets/Prefabs/Player.prefab\"]). Must be non-empty. Supported extensions: .prefab, .unity, .asset, .mat, .controller, .anim. Whole-project reserialize is not supported — enumerate explicitly.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. Default 'enforce' — fails the call if the round-trip surfaces new errors.",
      },
    },
    additionalProperties: false,
  },
};
