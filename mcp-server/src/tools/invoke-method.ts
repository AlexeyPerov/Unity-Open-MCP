import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS } from "../constants.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M16 Plan 6 — invoke_method enhanced in place with better overload + generic-
// arg resolution. Two new inputs:
//   - generic_arg_types: type-name strings substituted for the method's
//     generic parameters when calling a generic method (e.g. GetComponent<T>).
//     Without this, generic methods could not be invoked.
//   - arg_type_names: optional explicit parameter types, used to disambiguate
//     overloads when multiple methods share a name (avoids the
//     AmbiguousMatchException the previous single-GetMethod path raised).
// When neither is supplied, the legacy single-GetMethod resolution runs as
// before so existing callers are unaffected.
export const invokeMethod = makeTool(
  "unity_open_mcp_invoke_method",
  "Call a method via reflection. Supports generic methods via `generic_arg_types` and " +
    "overload disambiguation via `arg_type_names` (when multiple methods share a name, " +
    "supply the parameter type names to pick one; without it the first overload is used). " +
    "Otherwise identical to the previous behavior: pass type_name + method_name, optional " +
    "args, is_static for static methods, object_id for instance methods on a live Object.",
  {
    required: ["type_name", "method_name"],
        properties: {
          type_name: {
            type: "string",
            description: "Fully qualified type name",
          },
          method_name: {
            type: "string",
          },
          args: {
            type: "array",
            description: "JSON-serializable arguments",
            items: {},
          },
          arg_type_names: {
            type: "array",
            items: { type: "string" },
            description:
              "Optional explicit parameter type names (full or simple, e.g. " +
              "[\"UnityEngine.GameObject\", \"Int32\"]) used to disambiguate overloads when " +
              "multiple methods share `method_name`. Length must match the overload's parameter " +
              "count. When omitted, the first overload with the right name is used (legacy " +
              "behavior).",
          },
          generic_arg_types: {
            type: "array",
            items: { type: "string" },
            description:
              "Type-name strings substituted for the method's generic parameters when invoking a " +
              "generic method (e.g. [\"UnityEngine.Rigidbody\"] for GetComponent<Rigidbody>). " +
              "Length must match the method's generic parameter count. Omit for non-generic methods.",
          },
          is_static: {
            type: "boolean",
            default: false,
          },
          assembly_name: {
            type: "string",
            description:
              "Optional assembly simple name if type is ambiguous",
          },
          object_id: {
            type: "integer",
            default: 0,
            description:
              "Instance ID of a live UnityEngine.Object to use as the target " +
              "for instance methods (instead of creating a new instance via " +
              "Activator). 0 = not set (create new instance). The instance ID " +
              "comes from the 'objectId' field of object handles returned by " +
              "other tools (screenshot, spatial_query, scene_snapshot, etc.). " +
              "Instance IDs change on domain reload.",
          },
          paths_hint: { ...PATHS_HINT_TYPE },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default a disruptive op " + "(recompile / scene switch) is refused with scene_dirty when any " + "loaded scene has unsaved changes, so Unity's native save modal " + "never interrupts the flow. Set true to proceed and accept the risk." },
          gate: { ...GATE_PROP },
          timeout_ms: {
            type: "integer",
            default: BRIDGE_DEFAULT_TIMEOUT_MS,
          },
          max_depth: {
            type: "integer",
            default: 4,
            minimum: 0,
            description:
              "Max recursion depth when serializing the returned object graph (default 4).",
          },
          max_items: {
            type: "integer",
            default: 100,
            minimum: 0,
            description:
              "Max items emitted per list/enumerable in the returned object graph (default 100). Truncated lists report a `truncated` count.",
          },
        },
  },
);
