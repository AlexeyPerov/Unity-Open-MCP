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

class ProjectsStore {
  projects = $state<ProjectEntry[]>([]);
  selectedProjectId = $state<string | null>(null);
  loading = $state(false);
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
    const next = this.projects.map((p) =>
      p.id === updated.id ? updated : p
    );
    this.projects = next;
    await this.persist(next);
  }

  /**
   * Update only the `aiSetupWizard` draft field on a single project,
   * without replacing the `projects` array identity.
   *
   * The wizard debounces its draft-save on every form field change; routing
   * that through `update()` reassigns `this.projects` (a new array), which
   * re-runs every `$derived`/row in ProjectsTab and re-passes a fresh
   * `project` prop to the wizard mid-interaction — destabilizing its event
   * bindings (clicks stop having an effect right after preset selection).
   * No grid row or derived consumes `aiSetupWizard`, so mutating the field
   * in place persists the draft for crash-recovery without churning the
   * rest of the UI. `undefined` clears the field (omit from `projects.json`).
   */
  async updateDraftOnly(
    id: string,
    draft: AiSetupWizardDraft | undefined,
  ): Promise<void> {
    const entry = this.projects.find((p) => p.id === id);
    if (!entry) return;
    entry.aiSetupWizard = draft;
    await this.persist();
  }

  add(entry: ProjectEntry): void {
    this.projects = [...this.projects, entry];
    this.selectedProjectId = entry.id;
  }

  async remove(id: string): Promise<void> {
    const next = this.projects.filter((p) => p.id !== id);
    this.projects = next;
    if (this.selectedProjectId === id) this.selectedProjectId = null;
    await this.persist(next);
  }

  replaceAll(list: ProjectEntry[]): void {
    this.projects = list;
  }

  async persist(list: ProjectEntry[] = this.projects): Promise<void> {
    const payload: ProjectsFile = {
      version: 1,
      projects: list,
    };
    await saveProjects(payload);
  }
}

export const projectsStore = new ProjectsStore();
