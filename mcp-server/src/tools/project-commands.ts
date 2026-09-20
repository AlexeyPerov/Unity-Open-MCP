import { makeTool } from "./schema-fragments.js";

export const projectCommands = makeTool(
  "unity_open_mcp_project_commands",
  "Discover project-owned commands from the live Editor without a server release or tool-list refresh. list returns a bounded compact catalog; describe takes one exact id and returns its schema, safety declarations and registration diagnostics. Invocation is not supported yet. Async/cancellable are declarations only.",
  {
    required: ["action"],
    properties: {
      action: { type: "string", enum: ["list", "describe"] },
      id: { type: "string", description: "Exact catalog id, required for describe; aliases are metadata only." },
      query: { type: "string", maxLength: 256, description: "Case-insensitive substring in id, title or description." },
      tags: { type: "array", maxItems: 20, items: { type: "string", maxLength: 128 }, description: "Match all tags exactly." },
      group: { type: "string", maxLength: 128 },
      package: { type: "string", maxLength: 256 },
      offset: { type: "integer", minimum: 0, maximum: 2147483647 },
      limit: { type: "integer", minimum: 1, maximum: 100, description: "Page size, defaults to 20." },
    },
  },
);
