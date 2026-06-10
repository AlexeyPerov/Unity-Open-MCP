export type Tab = "projects" | "unityVersions" | "tools" | "settings";
export type ProjectsFilter = "all" | "launchable" | "missingVersion" | "missingPath";

class AppState {
  activeTab = $state<Tab>("projects");
  showConfirmationModal = $state(false);
  drawerExpanded = $state(false);
  drawerLogs = $state<string[]>([]);

  pendingProjectsFilter = $state<ProjectsFilter | null>(null);

  confirmationTitle = $state("");
  confirmationMessage = $state("");
  private confirmationResolve: ((value: boolean) => void) | null = null;

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
    this.drawerExpanded = true;
  }

  clearDrawerLogs() {
    this.drawerLogs = [];
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
