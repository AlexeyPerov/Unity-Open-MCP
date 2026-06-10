import {
  loadProjects,
  saveProjects,
  type ProjectEntry,
  type ProjectsFile,
  type Settings,
} from "$lib/services/config";
import { settingsStore } from "$lib/state/settings.svelte";

class ProjectsStore {
  projects = $state<ProjectEntry[]>([]);
  selectedProjectId = $state<string | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);

  get settings(): Settings | null {
    return settingsStore.current;
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = null;
    try {
      const [projectsFile] = await Promise.all([
        loadProjects(),
        settingsStore.load(),
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
