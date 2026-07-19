import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.1 — Cinemachine camera_list. Reflection-gated. Read-only,
// gate-free — agents use it to discover valid camera instance_ids before
// mutating.
export const cinemachineCameraList = makeTool(
  "unity_open_mcp_cinemachine_camera_list",
  "List every CinemachineCamera in loaded scenes. Returns each camera's " +
    "instance id, path, priority, Follow / Look At targets, and Body/Aim/Noise " +
    "component names. Read-only, gate-free. Requires Cinemachine 3.x.",
  {
    properties: {},
  },
);
