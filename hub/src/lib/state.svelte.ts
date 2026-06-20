export type Tab = "projects" | "unityVersions" | "tools" | "settings";
export type ProjectsFilter =
  | "all"
  | "launchable"
  | "missingVersion"
  | "missingPath"
  | "missingOrStale"
  | "running";

export interface LastLaunchFailure {
  projectId: string;
  projectName: string;
  projectPath: string;
  timestamp: string;
  /** `true` when the failure is a spawn-level error (likely Unity crash). */
  isLikelyCrash: boolean;
  /** Absolute path to the per-launch log file (for the "Reveal" link). */
  launchLogPath: string;
  /** Absolute path to the platform crash-log folder (only set when isLikelyCrash). */
  crashLogPath: string | null;
  /**
   * When set, the failure was a double-launch guard: a Unity process is
   * already running for this project and the conflict was identified by
   * the given PID. The status drawer offers a "Terminate & relaunch"
   * quick action that targets this PID. `null` for non-conflict
   * failures.
   */
  conflictPid: number | null;
}

/**
 * Calm, dismissible, non-alarming notice for the "Unity is already
 * running for this project" launch precondition. Distinct from
 * `LastLaunchFailure` because the condition is informational (an
 * expected state), not an error — it must not be rendered inside the
 * red failure card, must not force the drawer open, and must not pull
 * in the on-disk launch log tail. The status drawer renders it as a
 * blue info card with an optional "Terminate & relaunch" quick action.
 */
export interface LaunchInfoNotice {
  projectId: string;
  projectName: string;
  /** Friendly, non-scary one-line summary shown in the card body. */
  message: string;
  /**
   * PID of the running Unity that blocked the launch, when known. The
   * drawer offers a "Terminate & relaunch" quick action targeting it.
   * `null` when the conflict was detected but no PID was identified.
   */
  conflictPid: number | null;
  timestamp: string;
}

class AppState {
  activeTab = $state<Tab>("projects");
  showConfirmationModal = $state(false);
  drawerExpanded = $state(false);
  drawerLogs = $state<string[]>([]);

  pendingProjectsFilter = $state<ProjectsFilter | null>(null);

  confirmationTitle = $state("");
  confirmationMessage = $state("");
  private confirmationResolve: ((value: boolean) => void) | null = null;

  lastLaunchFailure = $state<LastLaunchFailure | null>(null);

  /**
   * Calm notice for the "already running" launch precondition. Set by
   * `handleLaunch` (frontend pre-check) and by the backend
   * `alreadyRunning` error branch instead of routing through
   * `lastLaunchFailure`, so the user sees a friendly info card rather
   * than the red launch-failed chrome.
   */
  launchInfoNotice = $state<LaunchInfoNotice | null>(null);

  async confirm(title: string, message: string): Promise<boolean> {
    this.confirmationTitle = title;
    this.confirmationMessage = message;
    this.showConfirmationModal = true;
    try {
      return await new Promise<boolean>((resolve) => {
        this.confirmationResolve = resolve;
      });
    } catch {
      // A defensive catch so a throw from a derived effect (e.g. a
      // misbehaving Svelte 5 subscriber) cannot leave the modal open
      // with an unresolvable promise — the user would otherwise be
      // "stuck forever" with a non-dismissable overlay.
      return false;
    } finally {
      // Best-effort: if the promise was neither resolved nor rejected,
      // make sure the modal state is cleared so the next `confirm()` is
      // not stacked on top of a hidden one. `resolveConfirmation`
      // already clears it, so this is a no-op in the normal path.
      if (this.showConfirmationModal) {
        this.showConfirmationModal = false;
        this.confirmationResolve = null;
      }
    }
  }

  resolveConfirmation(result: boolean) {
    this.showConfirmationModal = false;
    this.confirmationResolve?.(result);
    this.confirmationResolve = null;
  }

  appendDrawerLog(line: string) {
    // Prefix every line with a wall-clock timestamp. Critical for
    // launch-path diagnostics: the gap between two boot phases (or two
    // component mounts) shows whether a remount is automatic (sub-second)
    // or user/HMR-driven (multi-second). `HH:MM:SS.mmm` keeps lines
    // greppable and sortable in the ring buffer.
    const now = new Date();
    const ts =
      `${String(now.getHours()).padStart(2, "0")}:` +
      `${String(now.getMinutes()).padStart(2, "0")}:` +
      `${String(now.getSeconds()).padStart(2, "0")}.` +
      `${String(now.getMilliseconds()).padStart(3, "0")}`;
    this.drawerLogs = [...this.drawerLogs, `${ts} ${line}`].slice(-500);
  }

  appendErrorLog(line: string) {
    this.appendDrawerLog(line);
    this.drawerExpanded = true;
  }

  /**
   * Append a launch-related log line. When `autoOpen` is true the drawer is
   * expanded so the user sees the failure without an extra click. Pass
   * `false` when the user has disabled `autoOpenDrawerOnLaunchFailure` in
   * Settings → Diagnostics.
   */
  appendLaunchLog(line: string, autoOpen: boolean) {
    this.appendDrawerLog(line);
    if (autoOpen) {
      this.drawerExpanded = true;
    }
  }

  clearDrawerLogs() {
    this.drawerLogs = [];
  }

  clearLastLaunchFailure() {
    this.lastLaunchFailure = null;
  }

  setLastLaunchFailure(failure: LastLaunchFailure) {
    this.lastLaunchFailure = failure;
  }

  setLaunchInfoNotice(notice: LaunchInfoNotice) {
    this.launchInfoNotice = notice;
  }

  clearLaunchInfoNotice() {
    this.launchInfoNotice = null;
  }

  requestProjectsFilter(filter: ProjectsFilter) {
    this.pendingProjectsFilter = filter;
    this.activeTab = "projects";
  }

  consumeProjectsFilter(): ProjectsFilter | null {
    const f = this.pendingProjectsFilter;
    this.pendingProjectsFilter = null;
    return f;
  }

  // Callback registered by ProjectsTab so the status drawer can request
  // a "terminate & relaunch" without importing the ProjectsTab module
  // (the drawer is mounted at the app root and outlives tab switches).
  // Signature: (projectId: string) => Promise<void> | void.
  terminateAndRelaunchHandler:
    | ((projectId: string) => Promise<void> | void)
    | null = null;

  setTerminateAndRelaunchHandler(
    handler: ((projectId: string) => Promise<void> | void) | null,
  ): void {
    this.terminateAndRelaunchHandler = handler;
  }

  async requestTerminateAndRelaunch(projectId: string): Promise<void> {
    if (!this.terminateAndRelaunchHandler) return;
    await this.terminateAndRelaunchHandler(projectId);
  }
}

export const S = new AppState();
