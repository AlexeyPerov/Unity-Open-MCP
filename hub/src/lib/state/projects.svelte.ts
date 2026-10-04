import {
  loadProjects,
  saveProjects,
  type AiSetupWizardDraft,
  type ProjectEntry,
  type ProjectsFile,
  type Settings,
} from "$lib/services/config";
import { S } from "$lib/state.svelte";
import { settingsStore } from "$lib/state/settings.svelte";

/**
 * Boot diagnostic: run an `invoke`-backed async fn, logging a `start`
 * line up front and a duration line on completion. A phase that hangs
 * appears as `start` with no matching duration line — unambiguous,
 * since only-on-completion logging left the slow phase invisible. Used
 * to split `projectsStore.load` into its two underlying invokes so a
 * freeze can be attributed to `loadProjects` or `loadSettings`.
 */
async function timedInvoke<T>(label: string, fn: () => Promise<T>): Promise<T> {
  const start = performance.now();
  S.appendDrawerLog(`[boot] ${label}: start`);
  try {
    const result = await fn();
    S.appendDrawerLog(
      `[boot] ${label}: ${Math.round(performance.now() - start)}ms`,
    );
    return result;
  } catch (e) {
    S.appendDrawerLog(
      `[boot] ${label}: FAILED after ${Math.round(performance.now() - start)}ms`,
    );
    throw e;
  }
}

export class ProjectsStore {
  private saveQueue: Promise<void> = Promise.resolve();
  private saveRevision = 0;
  projects = $state<ProjectEntry[]>([]);
  selectedProjectId = $state<string | null>(null);
  loading = $state(false);
  /**
   * True once the first `load()` has completed (success or error).
   * `loading` alone can't distinguish "still bootstrapping on first
   * boot" from "loaded and genuinely has zero projects" — both look
   * like `projects.length === 0`. The list uses this flag to show a
   * spinner instead of the "No projects yet." empty state until the
   * initial load resolves, so a slow `load_projects` invoke no longer
   * flashes the empty state.
   */
  loaded = $state(false);
  error = $state<string | null>(null);

  get settings(): Settings | null {
    return settingsStore.current;
  }

  async load(): Promise<void> {
    // Re-entrancy guard. ProjectsTab mounts behind an `{#if}` on
    // `activeTab`, so it can be destroyed and recreated during a single
    // session; without this guard each mount fires a fresh pair of
    // `load_projects` + `load_settings` invokes while a prior pair may
    // still be resolving. Mirrors the dedup `discoveryStore.load` uses.
    // If this branch fires during a launch freeze, it confirms a
    // double-mount was the trigger.
    if (this.loading) {
      S.appendDrawerLog("[boot] projectsStore.load: skipped (already loading)");
      return;
    }
    this.loading = true;
    this.error = null;
    try {
      // Per-arm timing: on a freeze, this shows which of the two
      // invokes never returns (a `start` with no matching duration line).
      const [projectsFile] = await Promise.all([
        timedInvoke("loadProjects", loadProjects),
        // `settingsStore.load` is `Promise<void>`; time it without
        // surfacing its (absent) return value.
        timedInvoke("loadSettings", () => settingsStore.load()),
      ]);
      this.projects = projectsFile.projects;
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e);
      this.projects = [];
    } finally {
      this.loading = false;
      this.loaded = true;
    }
  }

  find(id: string): ProjectEntry | undefined {
    return this.projects.find((p) => p.id === id);
  }

  select(id: string | null): void {
    if (id !== null && !this.projects.some((p) => p.id === id)) {
      this.selectedProjectId = null;
      return;
    }
    this.selectedProjectId = id;
  }

  async update(updated: ProjectEntry): Promise<void> {
    const base = this.snapshot();
    const next = this.projects.map((p) =>
      p.id === updated.id ? updated : p
    );
    await this.persist(next, base);
  }

  /**
   * Merge an entry into the in-memory list WITHOUT persisting to disk.
   *
   * Exists for the `persistFailed` launch path (A-R5/B-N16): the backend
   * already failed to write `projects.json`, so calling `update()` (which
   * re-persists) would write to the very volume whose write just failed —
   * and its rejection would escape the caller's catch block, killing the
   * success-with-warning notice. The backend's in-memory state already
   * holds the merged entry; this keeps the frontend consistent with it.
   */
  mergeInMemory(updated: ProjectEntry): void {
    this.projects = this.projects.map((p) =>
      p.id === updated.id ? updated : p
    );
  }

  /**
   * Update only the `aiSetupWizard` draft field on a single project,
   * without replacing the `projects` array identity.
   *
   * The wizard debounces saves on every field change. Keep its row identity
   * stable while the save is pending and while reconciling the backend result,
   * so draft persistence does not replace the form's project prop mid-edit.
   * `undefined` clears the field (omit from `projects.json`).
   */
  async updateDraftOnly(
    id: string,
    draft: AiSetupWizardDraft | undefined,
  ): Promise<void> {
    const entry = this.projects.find((p) => p.id === id);
    if (!entry) return;
    const base = this.snapshot();
    const revision = this.saveRevision + 1;
    entry.aiSetupWizard = draft;
    try {
      await this.persist(this.projects, base);
    } catch (error) {
      if (revision === this.saveRevision) {
        entry.aiSetupWizard = base.projects.find((p) => p.id === id)?.aiSetupWizard;
      }
      throw error;
    }
  }

  add(entry: ProjectEntry): void {
    this.projects = [...this.projects, entry];
    this.selectedProjectId = entry.id;
  }

  async remove(id: string): Promise<void> {
    const base = this.snapshot();
    const next = this.projects.filter((p) => p.id !== id);
    await this.persist(next, base);
    if (this.selectedProjectId === id) this.selectedProjectId = null;
  }

  replaceAll(list: ProjectEntry[]): void {
    this.projects = list;
  }

  private snapshot(): ProjectsFile {
    return JSON.parse(JSON.stringify({ version: 1, projects: this.projects })) as ProjectsFile;
  }

  async persist(list: ProjectEntry[], base: ProjectsFile): Promise<void> {
    const payload = JSON.parse(JSON.stringify({
      version: 1,
      projects: list,
    })) as ProjectsFile;
    const revision = ++this.saveRevision;
    const write = this.saveQueue.then(() => saveProjects(payload, base));
    this.saveQueue = write.then(() => {}, () => {});
    const saved = await write;
    // A pending edit owns newer local values; let its response reconcile the list.
    if (revision !== this.saveRevision) return;
    // Preserve existing row identities while reconciling authoritative fields.
    const rows = saved.projects.map((savedEntry) => {
      const existing = this.find(savedEntry.id);
      if (!existing) return savedEntry;
      for (const key of Object.keys(existing) as Array<keyof ProjectEntry>) {
        if (!(key in savedEntry)) delete existing[key];
      }
      Object.assign(existing, savedEntry);
      return existing;
    });
    if (rows.length !== this.projects.length || rows.some((row, i) => row !== this.projects[i])) {
      this.projects.splice(0, this.projects.length, ...rows);
    }
  }
}

export const projectsStore = new ProjectsStore();
