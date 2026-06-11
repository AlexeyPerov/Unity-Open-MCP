import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import {
  cancelWalkUpScan,
  startWalkUpScan,
  type WalkUpDone,
  type WalkUpError,
  type WalkUpProgress,
  type WalkUpStart,
  type WalkUpStartParams,
} from "$lib/services/config";
import { projectsStore } from "$lib/state/projects.svelte";
import { S } from "$lib/state.svelte";

/**
 * M1.5-11 — walk-up directory scan progress and cancellation.
 *
 * The Rust scan runs on a background thread and streams progress
 * via Tauri events (`walk-up://progress`, `walk-up://done`). The
 * store mirrors that state into Svelte runes so the modal in
 * `ProjectsTab.svelte` can render the live counter, the current
 * root / depth, and the Cancel button. Cancellation is
 * best-effort: the scanner checks the cancellation flag at every
 * directory boundary.
 */
class WalkUpScanStore {
  scanning = $state(false);
  scanId = $state<string | null>(null);
  currentRoot = $state<string | null>(null);
  currentDepth = $state<number | null>(null);
  maxDepth = $state<number | null>(null);
  foundSoFar = $state(0);
  visitedDirs = $state(0);
  startError = $state<string | null>(null);
  lastResult = $state<WalkUpDone | null>(null);

  private progressUnlisten: UnlistenFn | null = null;
  private doneUnlisten: UnlistenFn | null = null;
  private teardownPromise: Promise<void> | null = null;

  isScanning(): boolean {
    return this.scanning;
  }

  /**
   * Subscribe to the backend event stream. Idempotent: repeated
   * calls are no-ops when the listeners are already attached. The
   * listeners are detached on `stop()` (called from the Projects
   * tab `onDestroy` hook) so a navigating-away tab does not leak
   * handlers.
   */
  async start(): Promise<void> {
    if (this.progressUnlisten && this.doneUnlisten) return;
    const [a, b] = await Promise.all([
      listen<WalkUpProgress>("walk-up://progress", (event) => {
        this.applyProgress(event.payload);
      }),
      listen<WalkUpDone>("walk-up://done", (event) => {
        this.applyDone(event.payload);
      }),
    ]);
    this.progressUnlisten = a;
    this.doneUnlisten = b;
  }

  async stop(): Promise<void> {
    const a = this.progressUnlisten;
    const b = this.doneUnlisten;
    this.progressUnlisten = null;
    this.doneUnlisten = null;
    if (a) a();
    if (b) b();
  }

  /**
   * Kick off a scan. Resolves with the `scan_id` on success and
   * the modal can read `scanning = true` to render the spinner;
   * the `done` event is what closes the modal. On a synchronous
   * backend error (e.g. another scan already in progress) the
   * error is surfaced via `startError` and the modal can render
   * the inline message.
   */
  async begin(params: WalkUpStartParams): Promise<WalkUpStart | null> {
    if (this.scanning) {
      this.startError = "a walk-up scan is already running";
      return null;
    }
    this.startError = null;
    this.lastResult = null;
    try {
      const start = await startWalkUpScan(params);
      this.scanId = start.scanId;
      this.maxDepth = start.maxDepth;
      this.scanning = true;
      this.foundSoFar = 0;
      this.visitedDirs = 0;
      this.currentRoot = start.roots[0] ?? null;
      this.currentDepth = 0;
      return start;
    } catch (e) {
      const msg = formatStartError(e);
      this.startError = msg;
      S.appendErrorLog(`walk-up scan failed to start: ${msg}`);
      return null;
    }
  }

  async cancel(): Promise<void> {
    if (!this.scanning || !this.scanId) return;
    const id = this.scanId;
    try {
      await cancelWalkUpScan(id);
      S.appendDrawerLog(`walk-up scan ${id}: cancel requested`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`walk-up scan cancel failed: ${msg}`);
    }
  }

  private applyProgress(payload: WalkUpProgress): void {
    if (!this.scanId || payload.scanId !== this.scanId) return;
    this.currentRoot = payload.currentRoot;
    this.currentDepth = payload.currentDepth;
    this.maxDepth = payload.maxDepth;
    this.foundSoFar = payload.foundSoFar;
    this.visitedDirs = payload.visitedDirs;
  }

  private applyDone(payload: WalkUpDone): void {
    if (!this.scanId || payload.scanId !== this.scanId) return;
    this.scanning = false;
    this.foundSoFar = payload.added.length;
    this.lastResult = payload;
    this.scanId = null;
    this.currentRoot = null;
    this.currentDepth = null;

    // Replace the in-memory projects list with the backend's
    // authoritative copy: the scanner may have appended entries
    // and the persistence layer is the source of truth. Avoid
    // double-counting by passing the whole list through
    // `replaceAll` (the Projects store also fires its own dedup
    // re-checks so an entry that pre-existed is preserved).
    projectsStore.replaceAll(payload.projects.projects);

    const added = payload.added.length;
    const skipped = payload.skippedExisting.length;
    const status = payload.status;
    const summary =
      status === "cancelled"
        ? `walk-up scan cancelled — kept ${added}, skipped ${skipped}`
        : status === "failed"
          ? `walk-up scan failed: ${payload.error ?? "unknown error"}`
          : `walk-up scan complete — added ${added}, skipped ${skipped}`;

    if (status === "failed" && payload.error) {
      S.appendErrorLog(summary);
    } else {
      S.appendDrawerLog(summary);
    }
  }
}

function formatStartError(e: unknown): string {
  // Tauri serialises the Rust `WalkUpError` enum with a `type`
  // discriminator, so we can map the common shapes inline.
  if (e && typeof e === "object" && "type" in e) {
    const err = e as WalkUpError;
    switch (err.type) {
      case "anotherScanInProgress":
        return `another walk-up scan is already in progress (${err.currentScanId})`;
      case "noRoots":
        return "no walk-up roots are configured — add at least one in Settings";
      case "invalidRoot":
        return `invalid walk-up root: ${err.reason} (${err.path})`;
    }
  }
  return e instanceof Error ? e.message : String(e);
}

export const walkUpScanStore = new WalkUpScanStore();
