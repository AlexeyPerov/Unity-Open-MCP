import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 5 / T20.5.2 — typed Assembly Definition modify. Mutating:
// RestartThenSettle lifecycle — editing references / platforms / define
// constraints can force a domain reload + recompile, and the gate waits for the
// settle window. Loads the current .asmdef, applies the supplied params, writes
// it back via File.WriteAllText + a forced AssetDatabase.ImportAsset. Only
// supplied params mutate — omitted params keep their current value. References
// are additive by default (add_references / remove_references); pass
// `references` for a full replacement. Setting include_platforms clears
// exclude_platforms and vice versa (mirrors AnkleBreaker set_platforms
// semantics). The advantage over AnkleBreaker's ungated asmdef mutators is the
// gate-integrated recompile + the active-scene dirty guard preflight.
export const asmdefModify: Tool = {
  name: "unity_open_mcp_asmdef_modify",
  description:
    "Modify an existing Assembly Definition (.asmdef). Mutating: runs the full gate " +
    "path with the RestartThenSettle lifecycle (editing references / platforms / " +
    "define constraints can force a domain reload + recompile — the gate waits for the " +
    "settle window, and the active-scene dirty guard preflights it). `paths_hint` must " +
    "be scoped to the asset path. Only supplied params mutate — omitted params keep " +
    "their current value. References are additive by default (add_references / " +
    "remove_references); pass `references` for a full replacement. Setting " +
    "include_platforms clears exclude_platforms and vice versa. After modify, poll " +
    "editor_status / compile_check to confirm the recompile settled, then run " +
    "scan_paths to catch any broken references.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Asset path of the .asmdef to modify (must start with 'Assets/' and end with '.asmdef').",
      },
      name: {
        type: "string",
        description: "New assembly name. Omit to keep the current value.",
      },
      add_references: {
        type: "array",
        items: { type: "string" },
        description:
          "Assembly references to add (deduped, order preserved). e.g. " +
          "['Unity.TextMeshPro', 'MyGame.Core'].",
      },
      remove_references: {
        type: "array",
        items: { type: "string" },
        description: "Assembly references to remove.",
      },
      references: {
        type: "array",
        items: { type: "string" },
        description:
          "Full reference-list replacement. When set, this becomes the complete " +
          "references array (overrides add/remove).",
      },
      include_platforms: {
        type: "array",
        items: { type: "string" },
        description: "Only compile for these platforms. Setting this clears exclude_platforms.",
      },
      exclude_platforms: {
        type: "array",
        items: { type: "string" },
        description: "Compile for all platforms EXCEPT these. Setting this clears include_platforms.",
      },
      define_constraints: {
        type: "array",
        items: { type: "string" },
        description: "Full define-constraint replacement. Symbols required for compilation.",
      },
      root_namespace: {
        type: "string",
        description: "New root namespace. Omit to keep the current value.",
      },
      allow_unsafe: {
        type: "boolean",
        description: "Allow unsafe C# code blocks. Omit to keep the current value.",
      },
      auto_referenced: {
        type: "boolean",
        description: "Auto-referenced by predefined assemblies. Omit to keep the current value.",
      },
      no_engine_references: {
        type: "boolean",
        description: "Do not reference UnityEngine. Omit to keep the current value.",
      },
      override_references: {
        type: "boolean",
        description: "Override precompiled references. Omit to keep the current value.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .asmdef asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
      ignore_scene_dirty: {
        type: "boolean",
        default: false,
        description:
          "Bypass the active-scene dirty guard. By default the tool refuses with " +
          "`scene_dirty` when any loaded scene is dirty (a recompile can trigger " +
          "Unity's native save modal). Set true to proceed and accept that risk.",
      },
    },
    additionalProperties: false,
  },
};
