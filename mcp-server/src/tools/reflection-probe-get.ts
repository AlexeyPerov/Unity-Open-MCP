import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.2 — Lighting domain read. Built-in lighting module.
// Read-only, gate-free.
export const reflectionProbeGet = makeTool(
  "unity_open_mcp_reflection_probe_get",
  "Read ReflectionProbe settings: mode (Baked | Realtime | Custom), " +
    "resolution, HDR, clear flags, importance, size, near/far clip, and the " +
    "baked cubemap path (if any). Read-only, gate-free. Address the host by " +
    "instance_id > path > name.",
  {
    properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
        },
  },
);
