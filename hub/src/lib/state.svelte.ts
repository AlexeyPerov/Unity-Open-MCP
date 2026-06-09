export type Tab = "projects" | "unityVersions" | "tools" | "settings";

class AppState {
  activeTab = $state<Tab>("projects");
  showConfirmationModal = $state(false);
  drawerExpanded = $state(false);
  drawerLogs = $state<string[]>([]);

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
}

export const S = new AppState();
