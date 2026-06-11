import {
  loadSettings,
  saveSettings,
  type ProjectListSortBy,
  type Settings,
} from "$lib/services/config";
import { discoveryStore } from "$lib/state/discovery.svelte";

class SettingsStore {
  current = $state<Settings | null>(null);
  saving = $state(false);
  saveError = $state<string | null>(null);
  lastSavedAt = $state<number | null>(null);

  private lastDiscoveryFolders: string[] = [];

  async load(): Promise<void> {
    const settings = await loadSettings();
    this.current = settings;
    this.lastDiscoveryFolders = [...settings.unityDiscovery.parentFolders];
    this.saveError = null;
  }

  isLoaded(): boolean {
    return this.current !== null;
  }

  private clone(): Settings {
    const s = this.current;
    if (!s) throw new Error("settings not loaded");
    return JSON.parse(JSON.stringify(s)) as Settings;
  }

  private async persist(next: Settings): Promise<void> {
    this.saving = true;
    this.saveError = null;
    try {
      await saveSettings(next);
      this.current = next;
      this.lastSavedAt = Date.now();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.saveError = msg;
      throw e;
    } finally {
      this.saving = false;
    }
  }

  private foldersChanged(prev: string[], next: string[]): boolean {
    if (prev.length !== next.length) return true;
    for (let i = 0; i < prev.length; i++) {
      if (prev[i] !== next[i]) return true;
    }
    return false;
  }

  private async applyDiscoveryRefresh(
    prevFolders: string[],
    nextFolders: string[]
  ): Promise<void> {
    if (!this.foldersChanged(prevFolders, nextFolders)) return;
    try {
      await discoveryStore.refresh();
    } catch {
      // Discovery errors are surfaced via the discovery store; settings save
      // is independent and has already completed.
    }
  }

  async setLaunchMode(mode: "openProject" | "openEditor"): Promise<void> {
    if (!this.current) return;
    if (this.current.launch.mode === mode) return;
    const next = this.clone();
    next.launch.mode = mode;
    await this.persist(next);
  }

  async setRememberLastSelection(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.launch.rememberLastSelection === value) return;
    const next = this.clone();
    next.launch.rememberLastSelection = value;
    await this.persist(next);
  }

  async setShowPathColumn(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.projectList.showPathColumn === value) return;
    const next = this.clone();
    next.projectList.showPathColumn = value;
    await this.persist(next);
  }

  async setShowModifiedColumn(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.projectList.showModifiedColumn === value) return;
    const next = this.clone();
    next.projectList.showModifiedColumn = value;
    await this.persist(next);
  }

  async setShowGitBranchColumn(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.projectList.showGitBranchColumn === value) return;
    const next = this.clone();
    next.projectList.showGitBranchColumn = value;
    await this.persist(next);
  }

  async setProjectListSortBy(value: ProjectListSortBy): Promise<void> {
    if (!this.current) return;
    if (this.current.projectList.sortBy === value) return;
    const next = this.clone();
    next.projectList.sortBy = value;
    await this.persist(next);
  }

  async setSearchIncludesPath(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.projectList.searchIncludesPath === value) return;
    const next = this.clone();
    next.projectList.searchIncludesPath = value;
    await this.persist(next);
  }

  async setConfirmKillUnity(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.safety.confirmKillUnity === value) return;
    const next = this.clone();
    next.safety.confirmKillUnity = value;
    await this.persist(next);
  }

  async setConfirmRemoveProject(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.safety.confirmRemoveProject === value) return;
    const next = this.clone();
    next.safety.confirmRemoveProject = value;
    await this.persist(next);
  }

  async setAutoOpenDrawerOnLaunchFailure(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.diagnostics.autoOpenDrawerOnLaunchFailure === value) return;
    const next = this.clone();
    next.diagnostics.autoOpenDrawerOnLaunchFailure = value;
    await this.persist(next);
  }

  async addDiscoveryFolder(folder: string): Promise<void> {
    if (!this.current) return;
    const trimmed = folder.trim();
    if (!trimmed) return;
    const next = this.clone();
    if (next.unityDiscovery.parentFolders.includes(trimmed)) return;
    next.unityDiscovery.parentFolders = [
      ...next.unityDiscovery.parentFolders,
      trimmed,
    ];
    const prev = this.lastDiscoveryFolders;
    await this.persist(next);
    this.lastDiscoveryFolders = [...next.unityDiscovery.parentFolders];
    await this.applyDiscoveryRefresh(prev, this.lastDiscoveryFolders);
  }

  async removeDiscoveryFolder(index: number): Promise<void> {
    if (!this.current) return;
    if (index < 0 || index >= this.current.unityDiscovery.parentFolders.length) return;
    const next = this.clone();
    next.unityDiscovery.parentFolders = next.unityDiscovery.parentFolders.filter(
      (_, i) => i !== index
    );
    const prev = this.lastDiscoveryFolders;
    await this.persist(next);
    this.lastDiscoveryFolders = [...next.unityDiscovery.parentFolders];
    await this.applyDiscoveryRefresh(prev, this.lastDiscoveryFolders);
  }
}

export const settingsStore = new SettingsStore();
