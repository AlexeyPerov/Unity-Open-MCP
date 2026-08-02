import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// feedback-01-08-glm §5 — a first-class "force a real recompile and wait for
// it" primitive. Fills the gap left by assets_refresh / execute_csharp, which
// frequently no-op on edited C# (Unity's incremental compiler sees no import
// change), leaving the running assembly stale while read_compile_errors reports
// the old healthy state.
//
// Mutating: calls CompilationPipeline.RequestScriptCompilation and blocks on
// the post-request compile via its restart_then_settle lifecycle. The response
// reports dllMtimeBefore / dllMtimeAfter (newest Library/ScriptAssemblies/*.dll
// mtime, in UTC ticks) and a recompiled boolean so a no-op recompile is
// detectable; on a no-op or an already-in-flight compile, agentNextSteps points
// at the right fallback (assets_refresh / reimport_package / poll isCompiling).
export const recompileScripts = makeTool(
  "unity_open_mcp_recompile_scripts",
  "Force Unity to recompile ALL scripts and block until the compile settles. Use this when " +
    "unity_open_mcp_assets_refresh or unity_open_mcp_execute_csharp fail to recompile after a C# edit " +
    "(Unity's incremental compiler frequently no-ops on unchanged-import views, leaving the running " +
    "assembly stale while read_compile_errors reports the old healthy state). Mutating: calls " +
    "CompilationPipeline.RequestScriptCompilation and blocks on the compile via its restart_then_settle " +
    "lifecycle (a domain reload may follow). The response reports dllMtimeBefore / dllMtimeAfter (newest " +
    "Library/ScriptAssemblies/*.dll mtime, in UTC ticks) and a recompiled boolean so a no-op recompile is " +
    "detectable; agentNextSteps branches on the outcome. paths_hint is the set of script assets you edited " +
    "(the gate validates the post-recompile state of those paths). Prefer this over assets_refresh when you " +
    "need a DETERMINISTIC recompile of edited C#.",
  {
    required: ["paths_hint"],
        properties: {
          paths_hint: {
            ...PATHS_HINT_TYPE,
            description:
              "Mutation scope — the script asset(s) you edited (e.g. [\"Assets/Scripts/MyClass.cs\"]). " +
              "The gate validates the post-recompile state of these paths. The recompile itself is " +
              "project-wide; paths_hint exists to give the gate a non-empty, scoped hint.",
          },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the recompile surfaces new errors." },
        },
  },
);
