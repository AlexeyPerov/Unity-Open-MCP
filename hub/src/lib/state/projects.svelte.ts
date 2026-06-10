import {
  loadProjects,
  loadSettings,
  saveProjects,
  type ProjectEntry,
  type ProjectsFile,
  type Settings,
} from "$lib/services/config";

class ProjectsStore {
  projects = $state<ProjectEntry[]>([]);
  settings = $state<Settings | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);

  async load(): Promise<void> {
    this.loading = true;
    this.error = null;
    try {
      const [projectsFile, settings] = await Promise.all([
        loadProjects(),
        loadSettings(),
      ]);
      this.projects = projectsFile.projects;
      this.settings = settings;
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

  async update(updated: ProjectEntry): Promise<void> {
    const next = this.projects.map((p) =>
      p.id === updated.id ? updated : p
    );
    this.projects = next;
    await this.persist(next);
  }

  add(entry: ProjectEntry): void {
    this.projects = [...this.projects, entry];
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
