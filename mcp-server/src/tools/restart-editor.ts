import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M31 Plan 3 / T31.3 — Editor fd-exhaustion auto-recovery (kill half).
//
// Companion to `read_compile_errors` (which surfaces the
// `editor_fd_exhaustion` hang signature from Editor.log). Where
// read_compile_errors only *diagnoses* the Bee build-driver hang, this tool
// *acts* on it: with explicit confirmation it terminates the hung Unity
// process so the operator/Hub can relaunch a fresh one. The relaunch itself
// is deferred — the interactive Editor's launch recipe (the flags the Hub
// used) is not knowable from the server, so the response carries clear
// "relaunch via the Hub" guidance instead.
//
// Operator-only surface (sibling to bridge_status): no group assignment in
// `capabilities/tool-groups.ts`, so it sits in the always-visible meta-tool
// bucket. It is NOT a mutating project-asset tool — the gate validates asset
// fallout, which does not apply to an OS process kill — so it routes local
// with no gate hop.
//
// Safety contract (the plan's "explicit confirmation" requirement):
//   1. Refuses unless `confirm: true` is passed. A dry-run call (confirm false
//      or absent) returns the PID + diagnosis it would act on, no side effect.
//   2. Refuses when the `editor_fd_exhaustion` signature is ABSENT from the
//      Editor.log tail. Never restart on a plain `dead_bridge` (usually a
//      fixable compile failure) or a merely offline bridge.
//   3. Surfaces unsaved-scene risk in the response when the bridge is still
//      reachable enough to report dirty scenes. Killing the Editor can destroy
//      unsaved scene work and in-flight asset imports.
//   4. SIGTERM → grace period → SIGKILL fallback on macOS/Linux; `taskkill /T
//      /F` on Windows.
export const restartEditor = makeTool(
  "unity_open_mcp_restart_editor",
  "Auto-recover from Editor file-descriptor exhaustion by terminating the " +
    "hung Unity process. Use ONLY after `read_compile_errors` reports an " +
    "`editor_fd_exhaustion` issue (the Bee build-driver hang signature: " +
    "'Could not register to wait for file descriptor N'). The Editor is hung " +
    "mid-build in that state and will not recover on its own. Requires " +
    "EXPLICIT confirmation (`confirm: true`) — killing the Editor can destroy " +
    "unsaved scene work and in-flight asset imports. The tool refuses when " +
    "the fd-exhaustion signature is absent (no restart on a plain " +
    "`dead_bridge` / fixable compile failure), when no live Unity process " +
    "matches this project, or when the PID is invalid. Local-routed: it acts " +
    "on the OS process via process.kill (SIGTERM → SIGKILL on macOS/Linux) or " +
    "taskkill /T /F on Windows — no bridge round-trip, no Unity spawn. The " +
    "response carries the killed PID + kill method + clear 'Editor terminated, " +
    "relaunch required via the Hub' guidance; relaunch is NOT automatic. A " +
    "dry-run call (confirm false/absent) returns what WOULD happen without " +
    "any side effect. After relaunch, poll `unity_open_mcp_bridge_status` " +
    "until it returns `running` to confirm the bridge reconnected. The " +
    "active-scene-dirty signal is checked when the bridge is still reachable " +
    "and surfaced as a `dirtyScenesWarning` in the response (it does NOT " +
    "block the kill — the Editor is hung).",
  {
    properties: {
          confirm: {
            type: "boolean",
            default: false,
            description:
              "Must be `true` to actually terminate the Editor. When false or " +
              "absent the call is a dry-run: it returns the PID + diagnosis it " +
              "would act on without any side effect. Killing the Editor is " +
              "destructive (unsaved scene work, in-flight imports) — never pass " +
              "this without first confirming the fd-exhaustion signature via " +
              "`read_compile_errors`.",
          },
          kill_grace_ms: {
            type: "integer",
            default: 5000,
            minimum: 0,
            maximum: 15000,
            description:
              "SIGTERM→SIGKILL grace window in milliseconds on macOS/Linux. " +
              "Default 5000ms. Ignored on Windows (taskkill /F is forced). Raise " +
              "to give Unity more time to flush its log + release the per-project " +
              "lock on a cooperative shutdown; lower to fail faster when the " +
              "Editor is fully wedged.",
          },
        },
  },
);
