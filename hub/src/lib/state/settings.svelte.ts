import {
  loadSettings,
  saveSettings,
  type AiToolkitSettings,
  type ProjectListSortBy,
  type Settings,
  type Theme,
} from "$lib/services/config";
import { discoveryStore } from "$lib/state/discovery.svelte";
import { runningUnityStore } from "$lib/state/running_unity.svelte";
import { applyTheme } from "$lib/theme.svelte";

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
    // M1.5-10: re-arm the running-Unity polling timer so the cadence
    // the user picked in Settings actually drives the scan (the store
    // reads the interval off the live settings object on every
    // `applyInterval` call).
    runningUnityStore.applyInterval();
    // M1.5-18: apply the persisted theme to the document so the
    // first paint is already in the right palette. The
    // `applyTheme` helper is a no-op when the document attribute
    // is already correct, so the cost is one DOM read on startup.
    applyTheme(settings.theme ?? "system");
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

  /**
   * M1.5-15: when true, the Projects tab starts with the "Missing or
   * stale" filter preset selected on load. The toolbar chips and the
   * "Show hidden" toggle stay reachable — this only changes the
   * initial selection. Default false in the Rust layer.
   */
  async setHideMissingByDefault(value: boolean): Promise<void> {
    if (!this.current) return;
    const current = this.current.projectList.hideMissingByDefault ?? false;
    if (current === value) return;
    const next = this.clone();
    next.projectList.hideMissingByDefault = value;
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

  /**
   * M1.5-17: when `true` (default), the Launch button on a project with
   * colliding env vars opens a confirmation modal listing the keys
   * that would override a parent-process variable. Off = the modal is
   * skipped and the env vars are applied silently.
   */
  async setConfirmEnvVarOverride(value: boolean): Promise<void> {
    if (!this.current) return;
    const current = this.current.safety.confirmEnvVarOverride ?? true;
    if (current === value) return;
    const next = this.clone();
    next.safety.confirmEnvVarOverride = value;
    await this.persist(next);
  }

  /**
   * M1.5-18: switch the active theme. The change is applied to the
   * document *before* the on-disk write so the user sees the new
   * palette immediately; the persist call updates `settings.json`
   * so the choice survives a relaunch.
   */
  async setTheme(theme: Theme): Promise<void> {
    if (!this.current) return;
    const current = this.current.theme ?? "system";
    if (current === theme) {
      // Even on a no-op, re-apply so the OS color-scheme listener
      // is in sync with the latest media-query state.
      applyTheme(theme);
      return;
    }
    applyTheme(theme);
    const next = this.clone();
    next.theme = theme;
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

  /**
   * M1.5-10: the running-Unity scan cadence. The Rust layer defaults
   * to 5s and tolerates legacy `settings.json` files via
   * `#[serde(default)]`, so we mirror that on the TS side: the field is
   * optional in `UnityDiscoverySettings`, and we resolve the effective
   * interval inside the running-Unity store. Persisting here
   * additionally tells the store to re-arm its `setInterval` so the
   * new cadence takes effect on the very next tick.
   */
  async setScanIntervalSeconds(value: number): Promise<void> {
    if (!this.current) return;
    const sanitized = Math.max(1, Math.min(600, Math.round(value)));
    if (this.current.unityDiscovery.scanIntervalSeconds === sanitized) {
      // Even on a no-op write, re-apply so the store picks up a
      // refreshed settings object after the initial load.
      runningUnityStore.applyInterval();
      return;
    }
    const next = this.clone();
    next.unityDiscovery.scanIntervalSeconds = sanitized;
    await this.persist(next);
    runningUnityStore.applyInterval();
  }

  /**
   * M1.5-11: walk-up scan roots. Adding a folder here is purely a
   * settings change — it does **not** trigger a scan. The user starts
   * the scan explicitly from the Projects tab; the walk-up section on
   * the Settings tab only manages the configured list.
   */
  async addWalkUpRoot(folder: string): Promise<void> {
    if (!this.current) return;
    const trimmed = folder.trim();
    if (!trimmed) return;
    const next = this.clone();
    if (next.unityDiscovery.walkUpRoots.includes(trimmed)) return;
    next.unityDiscovery.walkUpRoots = [
      ...next.unityDiscovery.walkUpRoots,
      trimmed,
    ];
    await this.persist(next);
  }

  async removeWalkUpRoot(index: number): Promise<void> {
    if (!this.current) return;
    if (index < 0 || index >= this.current.unityDiscovery.walkUpRoots.length) return;
    const next = this.clone();
    next.unityDiscovery.walkUpRoots = next.unityDiscovery.walkUpRoots.filter(
      (_, i) => i !== index
    );
    await this.persist(next);
  }

  /**
   * Replace the entire walk-up roots list with `folders`. Used by the
   * "Add Multiple Projects" modal: the "Select folder" picker always
   * sets the single selected folder, so a previous pick is replaced
   * rather than appended. Empty input clears the list.
   */
  async setWalkUpRoots(folders: string[]): Promise<void> {
    if (!this.current) return;
    const cleaned = folders
      .map((f) => f.trim())
      .filter((f) => f.length > 0);
    const current = this.current.unityDiscovery.walkUpRoots;
    if (
      current.length === cleaned.length &&
      current.every((v, i) => v === cleaned[i])
    ) {
      return;
    }
    const next = this.clone();
    next.unityDiscovery.walkUpRoots = cleaned;
    await this.persist(next);
  }

  /**
   * M1.5-11: walk-up max depth (clamped to 1..=8 by the UI; the Rust
   * mutator also clamps so a stale value cannot panic the backend).
   */
  async setWalkUpMaxDepth(value: number): Promise<void> {
    if (!this.current) return;
    const sanitized = Math.max(1, Math.min(8, Math.round(value)));
    if (this.current.unityDiscovery.walkUpMaxDepth === sanitized) return;
    const next = this.clone();
    next.unityDiscovery.walkUpMaxDepth = sanitized;
    await this.persist(next);
  }

  async setWalkUpFollowSymlinks(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.unityDiscovery.walkUpFollowSymlinks === value) return;
    const next = this.clone();
    next.unityDiscovery.walkUpFollowSymlinks = value;
    await this.persist(next);
  }

  async setWalkUpKeepPartial(value: boolean): Promise<void> {
    if (!this.current) return;
    if (this.current.unityDiscovery.walkUpKeepPartial === value) return;
    const next = this.clone();
    next.unityDiscovery.walkUpKeepPartial = value;
    await this.persist(next);
  }

  /**
   * M1.5-13: custom template folders. Adding a path here is purely
   * a settings change — the path is validated as a directory on save
   * and the New Project modal validates it again as a Unity root at
   * use-time (so a stale entry cannot crash a project create). The
   * Settings tab rejects the entry on save with an inline error if
   * the path does not resolve to a directory.
   */
  async addCustomTemplateFolder(folder: string): Promise<void> {
    if (!this.current) return;
    const trimmed = folder.trim();
    if (!trimmed) return;
    const next = this.clone();
    if (next.unityDiscovery.customTemplateFolders.includes(trimmed)) return;
    next.unityDiscovery.customTemplateFolders = [
      ...next.unityDiscovery.customTemplateFolders,
      trimmed,
    ];
    await this.persist(next);
  }

  async removeCustomTemplateFolder(index: number): Promise<void> {
    if (!this.current) return;
    if (index < 0 || index >= this.current.unityDiscovery.customTemplateFolders.length) return;
    const next = this.clone();
    next.unityDiscovery.customTemplateFolders = next.unityDiscovery.customTemplateFolders.filter(
      (_, i) => i !== index
    );
    await this.persist(next);
  }

  /**
   * M4: resolved view of `aiToolkit` from the current settings.
   * Defaults to an empty record so legacy `settings.json` files
   * (pre-M4) keep working without a migration step. Wizard Step 2
   * is the only writer of these fields.
   */
  get aiToolkit(): AiToolkitSettings {
    return (
      this.current?.aiToolkit ?? {
        rootPath: "",
        mcpIndexOverride: "",
      }
    );
  }

  /**
   * M4: persist the AI toolkit root after Step 2 fingerprint
   * validation succeeds. The caller is expected to have called
   * `validateToolkitRoot` first; this method does not re-validate.
   * An empty `rootPath` clears the persisted value (used by the
   * Step 2 "clear" affordance).
   */
  async setAiToolkitRoot(rootPath: string): Promise<void> {
    if (!this.current) return;
    const trimmed = rootPath.trim();
    const next = this.clone();
    const current = next.aiToolkit ?? { rootPath: "", mcpIndexOverride: "" };
    if (current.rootPath === trimmed) return;
    next.aiToolkit = { ...current, rootPath: trimmed };
    await this.persist(next);
  }

  /**
   * M4 Step 4 advanced override: custom `mcp-server/dist/index.js`
   * path. An empty string clears the override and falls back to
   * the path derived from `aiToolkit.rootPath`. Packages and
   * skills always use `rootPath` regardless of this value.
   */
  async setAiToolkitMcpIndexOverride(override: string): Promise<void> {
    if (!this.current) return;
    const trimmed = override.trim();
    const next = this.clone();
    const current = next.aiToolkit ?? { rootPath: "", mcpIndexOverride: "" };
    if (current.mcpIndexOverride === trimmed) return;
    next.aiToolkit = { ...current, mcpIndexOverride: trimmed };
    await this.persist(next);
  }
}

export const settingsStore = new SettingsStore();
