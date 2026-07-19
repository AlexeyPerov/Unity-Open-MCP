import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M20 Plan 5 / T20.5.2 — typed Assembly Definition create. Mutating:
// RestartThenSettle lifecycle — creating an asmdef forces a domain reload +
// recompile, and the gate waits for the settle window before the next
// mutation. The advantage over an ungated asmdef_create is the gate-integrated
// recompile: the active-scene dirty guard preflights it (a recompile can
// trigger Unity's native save modal) and a verify scan_paths can run after to
// catch broken references. Builds the .asmdef JSON from the typed params
// (name, references, platforms, define constraints, root namespace, unsafe /
// auto-ref / no-engine-ref flags) and writes it via File.WriteAllText + a
// forced AssetDatabase.ImportAsset so Unity recompiles immediately. `name`
// defaults to the filename when omitted.
export const asmdefCreate = makeTool(
  "unity_open_mcp_asmdef_create",
  "Create a new Assembly Definition (.asmdef) file. Mutating: runs the full gate " +
    "path with the RestartThenSettle lifecycle (creating an asmdef forces a domain " +
    "reload + recompile — the gate waits for the settle window, and the active-scene " +
    "dirty guard preflights it). `paths_hint` must be scoped to the new asset path. " +
    "Builds the .asmdef JSON from the typed params; `name` defaults to the filename " +
    "when omitted. After create, poll editor_status / compile_check to confirm the " +
    "recompile settled, then run scan_paths on the new asmdef to catch any broken " +
    "references. To edit an existing asmdef use asmdef_modify.",
  {
    required: ["asset_path", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "Destination asset path. Must start with 'Assets/' and end with '.asmdef'. " +
              "Must not already exist — use asmdef_modify to edit an existing one.",
          },
          name: {
            type: "string",
            description:
              "Assembly name. Defaults to the filename without extension when omitted. " +
              "Convention: 'Company.Product.Layer' (e.g. 'MyGame.Runtime').",
          },
          root_namespace: {
            type: "string",
            description: "Root namespace for scripts in this assembly (e.g. 'MyGame.Runtime').",
          },
          references: {
            type: "array",
            items: { type: "string" },
            description:
              "Assembly references — assembly names (e.g. 'Unity.TextMeshPro', 'MyGame.Core') " +
              "or GUID refs (e.g. 'GUID:...').",
          },
          include_platforms: {
            type: "array",
            items: { type: "string" },
            description:
              "Only compile for these platforms (e.g. ['Editor'] for editor-only code). " +
              "Common: 'Editor', 'Android', 'iOS', 'StandaloneWindows64', 'StandaloneOSX', " +
              "'StandaloneLinux64', 'WebGL'. Setting this clears exclude_platforms.",
          },
          exclude_platforms: {
            type: "array",
            items: { type: "string" },
            description: "Compile for all platforms EXCEPT these. Setting this clears include_platforms.",
          },
          define_constraints: {
            type: "array",
            items: { type: "string" },
            description:
              "Define constraints — the assembly only compiles when ALL symbols are defined " +
              "(e.g. ['UNITY_EDITOR', 'ENABLE_INPUT_SYSTEM']).",
          },
          precompiled_references: {
            type: "array",
            items: { type: "string" },
            description: "Precompiled DLL references (used with override_references: true).",
          },
          optional_unity_references: {
            type: "array",
            items: { type: "string" },
            description: "Optional Unity module references (e.g. ['TTS']).",
          },
          allow_unsafe: {
            type: "boolean",
            default: false,
            description: "Allow unsafe C# code blocks (default false).",
          },
          auto_referenced: {
            type: "boolean",
            default: true,
            description: "Automatically referenced by predefined assemblies (default true).",
          },
          no_engine_references: {
            type: "boolean",
            default: false,
            description: "Do not reference UnityEngine (for pure C# libraries, default false).",
          },
          override_references: {
            type: "boolean",
            default: false,
            description: "Override precompiled references (default false).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the new .asmdef asset path." },
          gate: { ...GATE_PROP },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default the tool refuses with " + "`scene_dirty` when any loaded scene is dirty (a recompile can trigger " + "Unity's native save modal). Set true to proceed and accept that risk." },
        },
  },
);
