import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 5 / T20.5.2 — typed Assembly Definition read. Read-only and gate-
// free. Parses one .asmdef into a full model (name, rootNamespace, references,
// include/exclude platforms, allowUnsafeCode, overrideReferences,
// precompiledReferences, autoReferenced, defineConstraints, versionDefines,
// noEngineReferences, optionalUnityReferences, plus any unknown keys preserved
// verbatim). Offline-routeable in principle — .asmdef is plain JSON and the
// offline index can parse it without a live Editor; the live path here is the
// authoritative reader. Unknown keys round-trip so an inspect never silently
// drops user-authored fields (e.g. versionDefines entries).
export const asmdefGet = makeTool(
  "unity_open_mcp_asmdef_get",
  "Read one Assembly Definition (.asmdef) by asset path and return its full parsed " +
    "model (name, rootNamespace, references, include/exclude platforms, " +
    "allowUnsafeCode, overrideReferences, precompiledReferences, autoReferenced, " +
    "defineConstraints, versionDefines, noEngineReferences, optionalUnityReferences). " +
    "Read-only and gate-free. Offline-routeable in principle — .asmdef is plain JSON "    +
    "and the offline index can parse it without a live Editor. Unknown keys are " +
    "preserved verbatim so a round-trip never drops user-authored fields.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description: "Asset path of the .asmdef to read (must start with 'Assets/' and end with '.asmdef').",
          },
        },
  },
);
