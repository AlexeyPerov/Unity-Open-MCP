// Supervision for batch-spawned Unity children.
//
// The MCP server spawns headless Unity processes for batch routes
// (compile_check, scan_all, baseline_create, …). Those runs hold the
// project's instance lock and its file descriptors for their whole lifetime.
// The spawn path in batch-spawn.ts already supervises the in-process cases
// (overall timeout → SIGTERM → 5s grace → SIGKILL, drained pipes, cleared
// timers) — but nothing covered the PARENT dying mid-run: an MCP host
// restarting, a CI runner timeout, or a Ctrl+C killed only the Node process
// and left the headless Unity orphaned, holding the project lock until its
// own 10-minute timeout (the next Editor launch then reports
// editor_instance_locked).
//
// This module keeps a process-wide registry of live batch children and
// installs SIGINT/SIGTERM/exit handlers (once per process) that tear them
// down: SIGTERM first (Unity flushes its log on a cooperative shutdown),
// SIGKILL after the grace window, and a synchronous SIGKILL sweep on "exit"
// as the unblockable last resort. SIGKILL of the parent itself remains
// unhandleable by design — that case is documented as out of scope.
//
// No runtime deps — `node:child_process` is imported as a TYPE only, so
// importing this module stays cheap enough for the CLI entry points.

import type { ChildProcess } from "node:child_process";

const activeChildren = new Set<ChildProcess>();

/**
 * Kill-signal grace before escalating SIGTERM → SIGKILL. Mirrors the spawn
 * path's own ladder in batch-spawn.ts (SIGKILL_GRACE_MS) so supervision and
 * timeout kill with the same rhythm.
 */
const SUPERVISION_KILL_GRACE_MS = 5_000;

/**
 * Register a batch child for signal-driven teardown. Retires it from the
 * registry when it closes (or fails to spawn) so the set holds only live
 * children. Safe to call for every spawn; supervision handlers are no-ops
 * when the set is empty.
 */
export function trackBatchChild(child: ChildProcess): void {
  activeChildren.add(child);
  const retire = (): void => {
    activeChildren.delete(child);
  };
  // Both events: a failed spawn may emit only 'error' on some Node versions,
  // and delete is idempotent on Set.
  child.on("close", retire);
  child.on("error", retire);
}

/** Number of currently tracked (live) batch children. Test/observability. */
export function supervisedChildCount(): number {
  return activeChildren.size;
}

/**
 * Send `signal` to every tracked child. Returns how many were actually
 * signaled. Never throws — dead PIDs are skipped.
 */
export function killActiveBatchChildren(
  signal: "SIGTERM" | "SIGKILL" = "SIGTERM",
): number {
  let signaled = 0;
  for (const child of activeChildren) {
    try {
      if (child.kill(signal)) signaled++;
    } catch {
      // Already dead — nothing to do.
    }
  }
  return signaled;
}

/**
 * Graceful teardown of every tracked child: SIGTERM now, SIGKILL after the
 * grace window (unref'd — the escalation must not keep the event loop alive
 * on the exit path).
 */
export function terminateActiveBatchChildren(): void {
  killActiveBatchChildren("SIGTERM");
  const escalation = setTimeout(() => {
    killActiveBatchChildren("SIGKILL");
  }, SUPERVISION_KILL_GRACE_MS);
  escalation.unref?.();
}

let supervisionInstalled = false;

/**
 * Install the process-wide signal handlers that terminate tracked batch
 * children when the MCP server (or CLI) is asked to die. Idempotent.
 *
 * Called from the two places that build the router stack — createServer
 * (stdio server) and runCli's stack build (CLI subcommands) — so both entry
 * points are covered without statically importing anything heavy into the
 * thin launcher (index.ts). Without a handler, Node's default disposition
 * kills the process instantly and orphans the children; with one, we kill
 * the children first (synchronous kill(2) syscalls) and exit with the
 * conventional 128+signal codes.
 */
export function installBatchChildSupervision(): void {
  if (supervisionInstalled) return;
  supervisionInstalled = true;

  process.on("SIGINT", () => {
    terminateActiveBatchChildren();
    process.exit(130); // 128 + SIGINT
  });
  process.on("SIGTERM", () => {
    terminateActiveBatchChildren();
    process.exit(143); // 128 + SIGTERM
  });
  // "exit" only allows synchronous work — no timers. SIGKILL directly so no
  // batch child can outlive the server holding the project's batch slot.
  process.on("exit", () => {
    killActiveBatchChildren("SIGKILL");
  });
}
