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

  async confirm(title: string, message: string): Promise<boolean> {
    this.confirmationTitle = title;
    this.confirmationMessage = message;
    this.showConfirmationModal = true;
    return new Promise((resolve) => {
      this.confirmationResolve = resolve;
    });
  }

  resolveConfirmation(result: boolean) {
    this.showConfirmationModal = false;
    this.confirmationResolve?.(result);
    this.confirmationResolve = null;
  }

  appendDrawerLog(line: string) {
    this.drawerLogs = [...this.drawerLogs, line].slice(-500);
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

  requestProjectsFilter(filter: ProjectsFilter) {
    this.pendingProjectsFilter = filter;
    this.activeTab = "projects";
  }

  consumeProjectsFilter(): ProjectsFilter | null {
    const f = this.pendingProjectsFilter;
    this.pendingProjectsFilter = null;
    return f;
  }
}

export const S = new AppState();
