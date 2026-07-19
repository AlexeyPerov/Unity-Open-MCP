import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, makeTool } from "./schema-fragments.js";

export const reserialize = makeTool(
  "unity_open_mcp_reserialize",
  "Round-trip text-serialized assets through Unity's native serializer (AssetDatabase.ForceReserializeAssets). " +
    "Use after directly editing a .prefab/.unity/.asset/.mat/.controller/.anim as YAML to normalize formatting and surface missing fields, wrong indents, or stale fileIDs. " +
    "By default round-trips asset YAML only — the companion .meta is left untouched, so direct body edits produce no meta noise. " +
    "Pass include_meta: true to also re-serialize importer metadata (use only for upgrade/importer-change workflows). " +
    "Mutating: runs the full gate path (checkpoint -> reserialize -> validate -> delta). The `paths` array doubles as the gate's paths_hint scope.",
  {
    required: ["paths"],
        properties: {
          paths: {
            type: "array",
            items: { type: "string" },
            description:
              "Asset paths to reserialize (e.g. [\"Assets/Prefabs/Player.prefab\"]). Must be non-empty. Supported extensions: .prefab, .unity, .asset, .mat, .controller, .anim. Whole-project reserialize is not supported — enumerate explicitly.",
          },
          include_meta: {
            type: "boolean",
            default: false,
            description:
              "When true, also round-trip each asset's companion .meta (ReserializeAssetsAndMetadata). Default false round-trips asset YAML only — this avoids spurious .meta churn (e.g. userData: vs userData: ) when only the asset body was edited. Set true only for upgrade or importer-change workflows that intentionally re-serialize importer metadata.",
          },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the round-trip surfaces new errors." },
        },
  },
);
