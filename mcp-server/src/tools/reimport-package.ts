import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// Force a reimport of a LOCAL (file:-linked) package so Unity recompiles its
// assembly. Fills the gap left by assets_refresh / execute_csharp, which no-op
// on local package source that lives outside Assets/ (Unity's incremental
// compiler sees no import change). See specs/feedback.md.
//
// Mutating: force-reimports the package's .cs/.asmdef via AssetDatabase and
// nudges a script recompile (restart_then_settle). The package id IS the
// scope — the bridge defaults paths_hint to "Packages/<package_id>" so the
// caller does not need to pass one. Critically, the response reports
// dllMtimeBefore / dllMtimeAfter so an agent can DETECT when the recompile
// was a no-op and fall back to a standalone Roslyn compile (documented in the
// agentNextSteps on a no-op).
export const reimportPackage = makeTool(
  "unity_open_mcp_reimport_package",
  "Force-reimport a LOCAL (file:-linked) UPM package's source so Unity recompiles its assembly. " +
    "Use this when unity_open_mcp_assets_refresh or unity_open_mcp_execute_csharp(RequestScriptCompilation) " +
    "fail to recompile a local package whose source lives outside Assets/ (Unity's incremental compiler " +
    "no-ops on it). Mutating: force-reimports every .cs + .asmdef under the package's resolved source root " +
    "and nudges a script recompile; the bridge blocks on the post-reimport compile via its " +
    "restart_then_settle lifecycle. The response reports dllMtimeBefore / dllMtimeAfter (newest matching " +
    "Library/ScriptAssemblies/*.dll mtime, in UTC ticks) and a recompiled boolean so a no-op recompile is " +
    "detectable; on a no-op, agentNextSteps points at a standalone Roslyn-compile fallback. Returns " +
    "not_local_package for registry/git/embedded packages (they have nothing outside Assets/ to reimport — " +
    "use unity_open_mcp_assets_refresh instead). The package id IS the scope: paths_hint is optional and " +
    "defaults to [\"Packages/<package_id>\"].",
  {
    required: ["package_id"],
        properties: {
          package_id: {
            type: "string",
            description:
              "Package name, packageId, or displayName to reimport, e.g. 'com.alexeyperov.unity-open-mcp-bridge'. " +
              "A trailing '@<version>' is stripped. Must resolve to an installed LOCAL (file:-linked) package; " +
              "non-local packages return not_local_package.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Optional mutation scope. Defaults to [\"Packages/<package_id>\"] — the bridge fills it in from " + "package_id, so callers usually omit it. The package's real source lives outside Assets/, so " + "there is no Assets/ path for the gate to validate; the default scope exists only to give the " + "gate a non-empty hint." },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the reimport surfaces new errors." },
        },
  },
);
