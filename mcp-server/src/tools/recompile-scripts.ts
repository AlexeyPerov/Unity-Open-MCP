import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { IGNORE_SCENE_DIRTY_BASE, GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// feedback-01-08-glm §5 — a first-class "force a real recompile and wait for
// it" primitive. Fills the gap left by assets_refresh / execute_csharp, which
// frequently no-op on edited C# (Unity's incremental compiler sees no import
// change), leaving the running assembly stale while read_compile_errors reports
// the old healthy state.
//
// Mutating: imports script files Unity has not seen yet (new or deleted .cs /
// .asmdef / .asmref), calls CompilationPipeline.RequestScriptCompilation, and
// the router waits for that compile (compile-result.ts): its status, generation
// and error count are the call's answer, and a failed compile fails the call.
export const recompileScripts = makeTool(
  "unity_open_mcp_recompile_scripts",
  "Force Unity to recompile ALL scripts and return the compile result. Use it after editing or adding C# " +
    "(it is deterministic where unity_open_mcp_assets_refresh / execute_csharp can leave the running assembly " +
    "stale). First imports script files Unity has not seen yet — new or deleted .cs / .asmdef / .asmref written " +
    "outside Unity (`scriptImport` lists them) — then calls CompilationPipeline.RequestScriptCompilation and " +
    "waits for that compile and its domain reload. The answer is the compile: `status` `compiled` | " +
    "`compile_failed` | `no_compile_ran` | `compiling` (past ~40 s the wait continues as a unity_open_mcp_jobs " +
    "job — wait on its job_id) and `compile: {generationAtStart, generation, status, errorCount, errors}` with " +
    "the first errors inline. A failed compile is a failed call (isError, mutation.error.code `compile_failed`); " +
    "fix the errors and call it again. dllMtimeBefore / dllMtimeAfter / recompiled in mutation.output show " +
    "whether the assemblies were rewritten. Disruptive: refused with `scene_dirty` while a loaded scene has " +
    "unsaved changes. The project refresh a deleted script needs treats open scenes that changed on disk " +
    "like assets_refresh (reloaded in place, or `open_scene_changed_on_disk` and nothing compiled). " +
    "paths_hint is optional (default [\"Assets\"]).",
  {
        properties: {
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Opt out of the active-scene dirty guard. When false (default), the call is refused with code `scene_dirty` if any loaded scene has unsaved changes (the reload this call triggers could raise Unity's native save modal). Set true to proceed and accept the risk." },
          paths_hint: {
            ...PATHS_HINT_TYPE,
            description:
              "Optional gate scope — the script asset(s) you edited (e.g. [\"Assets/Scripts/MyClass.cs\"]). " +
              "Defaults to [\"Assets\"]: the recompile itself is project-wide, so the gate validates the " +
              "whole Assets tree unless you narrow it.",
          },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the recompile surfaces new asset-validation errors. The compile result is reported either way." },
        },
  },
);
