import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed asset folder creation. Wraps AssetDatabase.CreateFolder
// so an agent can organize the project without composing execute_csharp for
// this routine workflow. Mutating: runs the full gate path. `paths_hint`
// must enumerate the folders to be created (the gate scopes validation to
// those paths). Intermediate parent folders must already exist; per-entry
// errors are collected so a single bad input does not abort the batch.
export const assetsCreateFolder: Tool = {
  name: "unity_open_mcp_assets_create_folder",
  description:
    "Create one or more folders under Assets/. Each entry names a parent folder " +
    "(must already exist and be Assets/-rooted) and the new folder name. Cross-platform " +
    "invalid characters are rejected upfront. Refreshes AssetDatabase at the end and " +
    "returns the GUIDs of the created folders. Mutating: runs the full gate path " +
    "(checkpoint -> create -> validate -> delta). `paths_hint` must list each created " +
    "folder path (e.g. [\"Assets/NewFolder\", \"Assets/NewFolder/Sub\"]).",
  inputSchema: {
    type: "object",
    required: ["folders", "paths_hint"],
    properties: {
      folders: {
        type: "array",
        items: {
          type: "object",
          required: ["parent_folder_path", "new_folder_name"],
          properties: {
            parent_folder_path: {
              type: "string",
              description:
                "Existing parent folder. Must start with 'Assets/' and every segment must already exist.",
            },
            new_folder_name: {
              type: "string",
              description:
                "Name of the new folder to create under the parent. Must not contain /, \\, <, >, :, \", |, ?, * or control characters.",
            },
          },
          additionalProperties: false,
        },
        minItems: 1,
        description: "Folders to create. Per-entry errors are collected, not abortive.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Created folder paths (the gate's validation scope). There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. Default 'enforce' — fails the call if the create surfaces new errors.",
      },
    },
    additionalProperties: false,
  },
};
