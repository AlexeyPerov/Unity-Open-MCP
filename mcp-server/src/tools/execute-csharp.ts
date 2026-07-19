import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS, BRIDGE_MIN_TIMEOUT_MS } from "../constants.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, CONFIRM_BYPASS_BASE, makeTool } from "./schema-fragments.js";

export const executeCsharp = makeTool(
  "unity_open_mcp_execute_csharp",
  "Compile and run a C# snippet in the Editor (Roslyn). Primary escape hatch — covers most Editor APIs without typed tools.",
  {
    required: ["code"],
        properties: {
          code: {
            type: "string",
            description: "C# source. Use return x; to produce output.",
          },
          usings: {
            type: "array",
            items: { type: "string" },
            description:
              "Extra using directives beyond defaults (UnityEngine, UnityEditor, etc.)",
          },
          object_ids: {
            type: "array",
            items: { type: "string" },
            description:
              "Instance IDs (or full handle JSON) of live UnityEngine.Objects " +
              "to inject into the snippet. Access them via Refs[index] or " +
              "Ref<T>(index) in the code body. Instance IDs come from the " +
              "'objectId' field of object handles returned by other tools. " +
              "Instance IDs change on domain reload.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Asset paths likely touched; drives scoped gate validation" },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default a disruptive op " + "(recompile / scene switch) is refused with scene_dirty when any " + "loaded scene has unsaved changes, so Unity's native save modal " + "never interrupts the flow. Set true to proceed and accept the risk " + "of a native save prompt." },
          confirm_bypass: { ...CONFIRM_BYPASS_BASE, description: "Bypass the deny heuristic for destructive patterns " + "(EditorApplication.Exit, AssetDatabase.DeleteAsset, " + "BuildPipeline.BuildPlayer, etc.). Requires gate: \"off\" as well — " + "both flags must be set. The bypass is audited." },
          gate: { ...GATE_PROP },
          timeout_ms: {
            type: "integer",
            default: BRIDGE_DEFAULT_TIMEOUT_MS,
            minimum: BRIDGE_MIN_TIMEOUT_MS,
            maximum: 300000,
          },
          max_depth: {
            type: "integer",
            default: 4,
            minimum: 0,
            description:
              "Max recursion depth when serializing the returned object graph (default 4). Composite nodes deeper than this are stringified to bound payload size.",
          },
          max_items: {
            type: "integer",
            default: 100,
            minimum: 0,
            description:
              "Max items emitted per list/enumerable in the returned object graph (default 100). Truncated lists report a `truncated` count of the elided items.",
          },
        },
  },
);
