import { scanRunningUnity, type RunningUnity } from "$lib/services/config";
import { settingsStore } from "$lib/state/settings.svelte";

/**
 * Live snapshot of the running-Unity process scan. M1.5-10 uses this to
 * tag matching `projects.json` rows with a `running` chip.
 *
 * The state is intentionally a single map keyed by PID and a separate
 * set of project paths so the chip match in `ProjectsTab.svelte` is
 * O(1) per project:
 *  - `byPid` is used to match against `ProjectEntry.lastLaunchPid`.
 *  - `paths` is used to match against `ProjectEntry.path`.
 * Both shapes are derived from the same scan so a single `setInterval`
 * tick updates the UI in one pass.
 *
 * The poll cadence is driven by `settings.discovery.scanIntervalSeconds`
 * (default 30s, exposed in Settings → Discovery). We restart the timer
 * whenever the user changes the interval so the new cadence takes
 * effect on the next tick without a full app reload.
 */
class RunningUnityStore {
  byPid = $state<Record<number, RunningUnity>>({});
  paths = $state<Set<string>>(new Set());
  lastScanAt = $state<number | null>(null);
  scanning = $state(false);
  scanError = $state<string | null>(null);

  private timer: ReturnType<typeof setInterval> | null = null;
  private currentIntervalMs = 0;
  private inFlight = false;
  // Whether a consumer (the Projects tab) is currently mounted and wants
  // live polling. applyInterval uses this to avoid arming a setInterval
  // that spawns ps/PowerShell with nobody reading the results.
  private active = false;

  isRunningForPid(pid: number | undefined | null): boolean {
    if (pid === undefined || pid === null || pid === 0) return false;
    return Object.prototype.hasOwnProperty.call(this.byPid, pid);
  }

  isRunningForPath(path: string | undefined | null): boolean {
    if (!path) return false;
    return this.paths.has(path);
  }

  /**
   * Optionally surface the completion of the immediate scan back to the
   * caller. Used by the Projects-tab boot diagnostics to time the first
   * `scan_running_unity` invoke alongside the other launch-path calls.
   * The callback fires once (after the initial tick) and is not invoked
   * again for subsequent interval-driven ticks.
   */
  async start(onFirstScanDone?: () => void): Promise<void> {
    // L7 — mark that a consumer is mounted before arming the timer, so a
    // concurrent settings write (which also calls applyInterval) does not
    // leave a dangling timer once this start() arms it.
    this.active = true;
    this.applyInterval();
    // Run an immediate scan so the first render after Projects tab mount
    // already reflects the current process list, even if the user has
    // a 30s interval configured.
    void this.tick().then(() => onFirstScanDone?.());
  }

  stop(): void {
    this.active = false;
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /**
   * Recompute the timer whenever the scan interval changes. Called from
   * `applyInterval` after settings load/save, and also as a public
   * method so the Settings UI can call it after a write.
   *
   * Only arms a setInterval when a consumer is mounted (`active`); when the
   * Projects tab is unmounted (no consumer), this just records the desired
   * interval so the next `start()` picks it up. Otherwise every Settings
   * write would spawn a ps/PowerShell poll loop with nobody reading it.
   */
  applyInterval(): void {
    const seconds = this.resolveIntervalSeconds();
    const ms = Math.max(1, seconds) * 1000;
    if (ms === this.currentIntervalMs && this.timer !== null) {
      return;
    }
    this.currentIntervalMs = ms;
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
    // No consumer: remember the desired cadence but do not arm a timer. The
    // next start() will create one using the recorded currentIntervalMs.
    if (!this.active) return;
    this.timer = setInterval(() => {
      void this.tick();
    }, ms);
  }

  private resolveIntervalSeconds(): number {
    const stored = settingsStore.current?.unityDiscovery.scanIntervalSeconds;
    if (typeof stored === "number" && Number.isFinite(stored) && stored > 0) {
      return Math.min(600, Math.max(1, Math.round(stored)));
    }
    return 30;
  }

  /**
   * Pull a fresh scan from the backend. Re-entrant: if a previous scan
   * is still running we skip the tick (the next one will pick up any
   * changes) so the tab never stacks scans on top of each other. A scan
   * failure (ps / powershell error) is recorded but does not clear the
   * previous snapshot — the spec says the chip is best-effort, and a
   * transient failure should not flicker the UI.
   */
  async tick(): Promise<void> {
    if (this.inFlight) return;
    this.inFlight = true;
    this.scanning = true;
    try {
      const list = await scanRunningUnity();
      const byPid: Record<number, RunningUnity> = {};
      const paths = new Set<string>();
      for (const item of list) {
        byPid[item.pid] = item;
        if (item.projectPath) paths.add(item.projectPath);
      }
      this.byPid = byPid;
      this.paths = paths;
      this.lastScanAt = Date.now();
      this.scanError = null;
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.scanError = msg;
    } finally {
      this.scanning = false;
      this.inFlight = false;
    }
  }
}

export const runningUnityStore = new RunningUnityStore();
