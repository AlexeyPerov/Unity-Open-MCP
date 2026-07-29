import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import {
  getProjectSizes,
  streamProjectSizes,
  SIZES_DONE_EVENT,
  SIZES_PROGRESS_EVENT,
  type SizesDone,
  type SizesProgress,
} from "$lib/services/config";
import { S } from "$lib/state.svelte";

/**
 * Project directory sizes, streamed per-root from the backend.
 *
 * The Rust `stream_project_sizes` command sizes each root in parallel
 * and emits `sizes://progress` per root as it completes, closing with
 * `sizes://done`. This store mirrors the results into a keyed reactive
 * map (`sizeMap`) so each project row in `ProjectList.svelte` re-renders
 * the instant its own size lands — instead of the whole list waiting on
 * the slowest root (the previous `await getProjectSizes(all)` shape,
 * which froze the window ~20s on a 14-project list at boot).
 *
 * Mirrors the event/listen shape of `walkUpScanStore` (idempotent
 * listeners, teardown on tab destroy).
 */
class SizesStore {
  /** path → bytes. Keyed updates keep re-render scoped to one row. */
  sizeMap = $state<Record<string, number>>({});
  loading = $state(false);
  /** Total elapsed ms of the most recent run (for the boot drawer log). */
  lastElapsedMs = $state<number | null>(null);

  private progressUnlisten: UnlistenFn | null = null;
  private doneUnlisten: UnlistenFn | null = null;

  /** Read a single project's size, or `0` while unsized. */
  get(path: string): number {
    return this.sizeMap[path] ?? 0;
  }

  /**
   * Kick off a streamed sizing pass for `paths`. Fire-and-forget from
   * the caller's perspective: sizes arrive via events. Attaches
   * listeners idempotently (re-attaching if a previous run's teardown
   * ran) so repeated calls just start a new backend run.
   *
   * Safe to call with an empty list — no-op that leaves the map intact.
   */
  async load(paths: string[]): Promise<void> {
    if (paths.length === 0) return;
    await this.attach();
    this.loading = true;
    try {
      await streamProjectSizes(paths);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`size stream failed to start: ${msg}`);
      this.loading = false;
    }
  }

  /**
   * Merge a single-root (or few-root) batch result into the map. Used by
   * the add/import/relink sites where one project was just added — the
   * batch `get_project_sizes` is fast for one root and avoids spinning
   * up a whole streamed run just to size one new entry.
   */
  async mergeBatch(paths: string[]): Promise<void> {
    if (paths.length === 0) return;
    try {
      const result = await getProjectSizes(paths);
      // Merge key-by-key so existing entries aren't cleared and each
      // new key triggers a single-row re-render.
      for (const [path, size] of Object.entries(result)) {
        this.sizeMap[path] = size;
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`size check failed: ${msg}`);
    }
  }

  /**
   * Subscribe to the backend event stream. Idempotent: a no-op when
   * both listeners are already attached. Mirrors `walkUpScanStore.start`.
   */
  private async attach(): Promise<void> {
    if (this.progressUnlisten && this.doneUnlisten) return;
    // Detach any stale single-sided listeners first so we don't
    // double-fire progress into the map.
    await this.detach();
    const [a, b] = await Promise.all([
      listen<SizesProgress>(SIZES_PROGRESS_EVENT, (event) => {
        const { path, size } = event.payload;
        this.sizeMap[path] = size;
      }),
      listen<SizesDone>(SIZES_DONE_EVENT, (event) => {
        this.lastElapsedMs = event.payload.elapsedMs;
        this.loading = false;
        S.appendDrawerLog(
          `[boot] loadSizes (streamed): ${event.payload.elapsedMs}ms`,
        );
      }),
    ]);
    this.progressUnlisten = a;
    this.doneUnlisten = b;
  }

  private async detach(): Promise<void> {
    const a = this.progressUnlisten;
    const b = this.doneUnlisten;
    this.progressUnlisten = null;
    this.doneUnlisten = null;
    if (a) a();
    if (b) b();
  }

  /**
   * Tear down listeners. Called from the Projects tab `onDestroy` hook
   * so a navigating-away tab does not leak handlers.
   */
  teardown(): void {
    void this.detach();
    this.loading = false;
  }
}

export const sizesStore = new SizesStore();
