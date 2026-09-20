import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";
import { toolHintReference } from "../tool-hint.js";

// specs/feedback.md 2026-08-14 — the description and the runtime hints must
// prescribe the SAME recovery path and must both carry the activation call for
// a tool outside the default surface. Both come from toolHintReference so the
// two can no longer drift.
const RECOMPILE_SCRIPTS_HINT = toolHintReference("unity_open_mcp_recompile_scripts");
const COMPILE_CHECK_HINT = toolHintReference("unity_open_mcp_compile_check");

// A bounded live compile snapshot is preferred; the filesystem fallback stays
// available when the bridge itself cannot compile, without launching Unity.
export const readCompileErrors = makeTool(
  "unity_open_mcp_read_compile_errors",
  "Read current compiler state from the live CompilationPipeline when available, with a bounded " +
    "read-only probe and offline Editor.log fallback (never spawns Unity). Live status distinguishes " +
    "currently_compiling, assembly_stale, compile_failed, and no_errors_found. A completed compile " +
    "generation with matching source content outranks historical log errors; generation and before/after " +
    "assembly mtimes identify the evidence. historicalLogErrors and historicalLogIssues preserve older " +
    "log evidence separately. Without confirmed live state, returns log diagnostics with source/authorship " +
    "and staleAssembly/staleLogSuspected caveats; a clean rotated log is indeterminate. Log issues include " +
    "assembly_resolution, package_deprecated, package_manager_error, and editor_fd_exhaustion. " +
    `For stale sources call ${RECOMPILE_SCRIPTS_HINT}, then re-read. ` +
    `With the Editor closed, ${COMPILE_CHECK_HINT} can verify headlessly; never launch a second Unity ` +
    "while a live Editor owns the project. Check unhealthy, status, headline, errors, and issues; " +
    "offline partialCompileLikely means a failing assembly may hide downstream errors.",
  {
    properties: {
          tail_bytes: {
            type: "integer",
            default: 262144,
            minimum: 4096,
            maximum: 1048576,
            description:
              "Maximum number of bytes to read from the END of Editor.log " +
              "(default 256KB). Compiler errors AND assembly-resolution failures " +
              "are written in contiguous blocks near the end, so a modest tail " +
              "is ample. Increase only if errors or issues are reported missing.",
          },
        },
  },
);
