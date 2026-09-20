import { makeTool, GATE_REQUEST_PROP } from "./schema-fragments.js";

export const projectCommands = makeTool(
  "unity_open_mcp_project_commands",
  "Discover project-owned commands from the live Editor without a server release or tool-list refresh. list returns a bounded compact catalog; describe takes one exact id and returns its schema, safety declarations and registration diagnostics. invoke validates args against a fresh schema and uses the normal scope, gate and lifecycle pipeline. Async/cancellable remain declarations only.",
  {
    required: ["action"],
    properties: {
      action: { type: "string", enum: ["list", "describe", "invoke"] },
      id: { type: "string", description: "Exact catalog id, required for describe; aliases are metadata only." },
      command_id: { type: "string", description: "Exact command id, required for invoke." },
      args: { type: "object", description: "Command arguments using canonical keys from describe." },
      schema_version: { type: "string", description: "Optional version from describe; a changed contract is rejected." },
      paths_hint: { type: "array", items: { type: "string" }, description: "Explicit mutation scope when the command declares no paths." },
      gate: { ...GATE_REQUEST_PROP },
      ignore_scene_dirty: { type: "boolean" },
      confirm_bypass: { type: "boolean" },
      timeout_ms: { type: "integer", minimum: 1000, maximum: 55000 },
      query: { type: "string", maxLength: 256, description: "Case-insensitive substring in id, title or description." },
      tags: { type: "array", maxItems: 20, items: { type: "string", maxLength: 128 }, description: "Match all tags exactly." },
      group: { type: "string", maxLength: 128 },
      package: { type: "string", maxLength: 256 },
      offset: { type: "integer", minimum: 0, maximum: 2147483647 },
      limit: { type: "integer", minimum: 1, maximum: 100, description: "Page size, defaults to 20." },
    },
  },
);
