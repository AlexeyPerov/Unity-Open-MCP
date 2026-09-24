import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M27 Plan 4 — live `batch_execute`. One HTTP round trip runs many typed tools
// sequentially inside the already-open Editor. NOT the headless batch fallback
// (`batchCapable: false`; not in BATCH_TOOL_NAMES). The bridge wraps the whole
// sequence in a single checkpoint → N steps → one validate/delta gate cycle
// (one undo group for the whole batch), so a partial failure is scoped,
// validated, and undoable as one unit.
//
// Default cap is 25 commands (hard max 100), configurable in
// `.unity-open-mcp/settings.json` (`batchExecuteMaxCommands`).
export const batchExecute = makeTool(
  "unity_open_mcp_batch_execute",
  "Run many typed tools sequentially inside the already-open Editor in a single HTTP round trip — " +
    "cuts agent↔Unity latency and token cost for multi-object setup (e.g. spawn N cubes + create " +
    "materials + assign in one call). Each entry in `commands` carries a full tool id " +
    "(`unity_open_mcp_*` / `unity_senses_*`) plus its `params` object. Lives in the `core` group " +
    "(always visible). Live-only — NOT headless `batchCapable`; there is no batch spawn fallback " +
    "when the bridge is down.\n\n" +
    "NOTE on `batchCapable`: that per-tool capability flag answers \"does this tool have a HEADLESS " +
    "batch-spawn fallback when the bridge is down?\" — NOT \"can this tool be a nested step here\". " +
    "The two are independent axes; nestability is decided by the pre-flight refusals below.\n\n" +
    "Every step is schema/lifecycle preflighted before dispatch. All-read batches need no paths_hint, " +
    "checkpoint, gate, or undo group. Mixed/mutating batches require the explicit union paths_hint. " +
    "Safety: a mutating batch shares ONE gate cycle (one checkpoint → all steps → one validate/" +
    "delta) and ONE undo group. `fail_fast: true` (the default) stops on the first step failure " +
    "and marks later entries `skipped`. With `fail_fast: false`, every step runs and per-step " +
    "errors are collected. Partial failure semantics: a successful step is NOT rolled back when a " +
    "later step fails. A partial batch (at least one step committed) propagates " +
    "`mutation.success: false` with error code `batch_partial_failure`; the gate STILL runs its " +
    "validate/delta on the committed work and waits for the asset/compile settle. Gate outcome describes " +
    "validation independently of mutation.success; read `batch.results[]` for the breakdown and undo " +
    "with a single `editor_undo` if needed. A total failure (no step committed) skips the " +
    "validate/delta with gate.outcome: skipped and skippedReason: mutation_failed. Run `unity_open_mcp_validate_edit` on the touched paths to confirm " +
    "health after a partial run.\n\n" +
    "v1 limits: 25 commands default / 100 hard max (`batchExecuteMaxCommands`). `parallel: true` " +
    "is accepted but ignored with a note — Unity's API is main-thread; sequential execution only. " +
    "`batch_execute` cannot be nested inside itself; `compile_check` is headless-only. Any nested " +
    "tool with the `restart_then_settle` lifecycle (package_add / package_remove / reimport_package / upgrade, " +
    "scene_open, asmdef_create / asmdef_modify, build_set_target / build_set_defines, " +
    "settings_set_player, mutating execute_csharp / invoke_method / disruptive execute_menu) is refused up-front with a " +
    "`batch_nested_reload_unsafe` error — a domain reload or scene switch mid-batch would silently " +
    "abort the remaining steps. `scene_create` is also refused unless `mode: \"additive\"` (its " +
    "default Single mode replaces the active scene stack and can discard unsaved changes in open " +
    "scenes). Script writes/deletes, undo/redo, play transitions, and non-preview fixes also require " +
    "their top-level routes. Use those tools as single top-level calls instead.\n\n" +
    "A step whose terminal result is produced by the SERVER rather than by the Editor dispatch is " +
    "refused with `batch_step_requires_server_poll`: `unity_senses_run_tests` returns " +
    "`{status:\"started\", runId}` immediately and the server polls a results file to turn that " +
    "into a real result, which only happens on the top-level route. Inside a batch nothing polls, " +
    "so the step would be recorded `success` carrying the non-terminal \"started\" body and the " +
    "run's outcome would never reach the caller. Call it top-level. Every pre-flight refusal also " +
    "names a reachable alternative where one exists (e.g. the `execute_csharp` equivalent), for " +
    "clients that cannot see a newly activated tool because they ignore `tools/list_changed`.",
  {
    required: ["commands"],
        properties: {
          commands: {
            type: "array",
            minItems: 1,
            items: {
              type: "object",
              required: ["tool"],
              properties: {
                tool: {
                  type: "string",
                  description:
                    "Full MCP tool id (`unity_open_mcp_gameobject_create`, etc.). Must be a " +
                    "live-bridge typed tool; not `batch_execute`, `compile_check`, or a meta-tool.",
                },
                params: {
                  type: "object",
                  description:
                    "The tool's input object (its normal `args`); omitted means an empty object. Omit the outer `paths_hint` / " +
                    "`gate` — the batch supplies those for the whole sequence.",
                },
              },
              additionalProperties: false,
            },
            description:
              "Ordered list of tool calls to run sequentially. Default cap 25, hard max 100 " +
              "(`batchExecuteMaxCommands`).",
          },
          fail_fast: {
            type: "boolean",
            default: true,
            description:
              "Stop on the first step failure and mark later entries `skipped` (default true). " +
              "Set false to run every step and collect per-step errors.",
          },
          gate: { ...GATE_PROP, description: "Gate mode applied to the WHOLE batch (one checkpoint → N steps → one validate/delta). " + "Default `enforce`." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope for the whole batch — the union of all paths the nested commands may " + "touch (scene paths, asset paths). Required when any nested command is mutating." },
          parallel: {
            type: "boolean",
            default: false,
            description:
              "Accepted but ignored in v1 — Unity's API is main-thread, so execution is always " +
              "sequential. The response reports that sequential execution was used.",
          },
        },
  },
);
