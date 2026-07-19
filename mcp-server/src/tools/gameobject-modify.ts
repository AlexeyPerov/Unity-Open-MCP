import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject modify. Mutating: runs the full gate path.
// Covers name / tag / layer / active AND transform (pos/rot/scale).
//
// M22 T22.1.4 — three-surface RFC 7396 form (additive, backwards-compatible).
// In addition to the legacy flat fields, the caller may provide:
//   - gameObjectDiffs          : grouped root-target patches (same fields as flat).
//   - pathPatchesPerGameObject : {childPath: diffs} applied to descendants.
//   - jsonPatchesPerGameObject : {componentTypeName: mergePatch} applied to the
//                                root target's components via reflection.
// Apply order: jsonPatches → pathPatches → gameObjectDiffs/flat. When any path
// or json surface is present, the response carries a `surfaces` breakdown; a
// legacy (root-only) call keeps the original compact result shape.
export const gameobjectModify = makeTool(
  "unity_open_mcp_gameobject_modify",
  "Modify one or more GameObject fields in a single call: name, tag, layer, active, and " +
    "transform (position, rotation, scale). Only provided fields are touched; omitted fields are " +
    "preserved. Undo-recorded. Mutating: runs the full gate path; `paths_hint` is the scene path " +
    "that contains the target. Use local_space=true to interpret transform values in the parent's " +
    "local space (matches Inspector). Address the target by instance_id > path > name.\n\n" +
    "Three-surface form (RFC 7396 JSON Merge Patch, additive): beyond the legacy flat fields you " +
    "may pass gameObjectDiffs (root-target patches grouped in one object), " +
    "pathPatchesPerGameObject ({childPath: diffs} applied to descendants of the target), and " +
    "jsonPatchesPerGameObject ({componentTypeName: {field: value}} applied to the target's " +
    "components via reflection, reusing object_modify's value shape). Apply order is " +
    "jsonPatches → pathPatches → gameObjectDiffs/flat.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Target GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Target hierarchy path \"Root/Child\".",
          },
          name_target: {
            type: "string",
            description: "Target GameObject name (first match). Lowest priority resolver.",
          },
          name: {
            type: "string",
            description:
              "New name (legacy flat field). Omit to leave unchanged. Prefer gameObjectDiffs for new code.",
          },
          tag: {
            type: "string",
            description:
              "New tag. Must be a defined tag (use editor_get_tags from Plan 5 to enumerate). Omit to leave unchanged.",
          },
          layer: {
            type: "integer",
            minimum: 0,
            maximum: 31,
            description: "New layer index (0-31). Omit to leave unchanged.",
          },
          active: {
            type: "boolean",
            description: "Toggle active state. Omit to leave unchanged.",
          },
          position: {
            type: "string",
            description: "New position as \"x,y,z\". Omit to leave unchanged.",
          },
          rotation: {
            type: "string",
            description: "New Euler rotation in degrees as \"x,y,z\". Omit to leave unchanged.",
          },
          scale: {
            type: "string",
            description: "New scale as \"x,y,z\". Omit to leave unchanged.",
          },
          local_space: {
            type: "boolean",
            default: false,
            description:
              "When true, position/rotation are local-space (parent-relative). Default false = world " +
              "space. Unset fields inherit the same space as the previously-set value so they round-trip cleanly.",
          },
          gameObjectDiffs: {
            type: "object",
            description:
              "M22 T22.1.4 surface 1 — root-target patches grouped in one object: " +
              "{name, tag, layer, active, position, rotation, scale, local_space}. Same field shape as " +
              "the legacy flat fields; when present, takes precedence over them. Omit any field to leave it unchanged.",
            properties: {
              name: { type: "string" },
              tag: { type: "string" },
              layer: { type: "integer", minimum: 0, maximum: 31 },
              active: { type: "boolean" },
              position: { type: "string" },
              rotation: { type: "string" },
              scale: { type: "string" },
              local_space: { type: "boolean", default: false },
            },
            additionalProperties: false,
          },
          pathPatchesPerGameObject: {
            type: "object",
            description:
              "M22 T22.1.4 surface 2 — {childPath: diffs} applied to descendants of the target. Each " +
              "key is a slash-delimited path relative to the target (e.g. \"Body/Arm\"); the value is " +
              "the same diffs shape as gameObjectDiffs. Per-child errors accumulate and do not abort the batch.",
            additionalProperties: {
              type: "object",
              description: "Diffs for the descendant GameObject at this path.",
              properties: {
                name: { type: "string" },
                tag: { type: "string" },
                layer: { type: "integer", minimum: 0, maximum: 31 },
                active: { type: "boolean" },
                position: { type: "string" },
                rotation: { type: "string" },
                scale: { type: "string" },
                local_space: { type: "boolean", default: false },
              },
              additionalProperties: false,
            },
          },
          jsonPatchesPerGameObject: {
            type: "object",
            description:
              "M22 T22.1.4 surface 3 — {componentTypeName: mergePatch} applied to the root target's " +
              "components via reflection. The key names a component type on the target (class name " +
              "first, e.g. \"Rigidbody\", then full name); the value is a RFC 7396 merge patch " +
              "{field: value} reusing object_modify's value shape (scalars, [x,y,z] vectors, " +
              "{\"path\":...} refs). Per-component/per-field errors accumulate. Apply order: this " +
              "surface runs before pathPatches and the root diff.",
            additionalProperties: {
              type: "object",
              description: "RFC 7396 merge patch of {fieldName: value} for this component.",
            },
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the target." },
          gate: { ...GATE_PROP },
        },
  },
);
