import {
  discoverInstallations,
  refreshDiscovery,
  type DiscoveryError,
  type ProjectEntry,
  type UnityInstallation,
} from "$lib/services/config";

export type VersionHealth = "ok" | "warn" | "missing";

interface VersionBucket {
  version: string;
  count: number;
}

class DiscoveryStore {
  installations = $state<UnityInstallation[]>([]);
  errors = $state<DiscoveryError[]>([]);
  loading = $state(false);
  error = $state<string | null>(null);
  lastLoaded = $state<number | null>(null);

  async load(force = false): Promise<void> {
    if (this.loading) return;
    this.loading = true;
    this.error = null;
    try {
      const result = force
        ? await refreshDiscovery()
        : await discoverInstallations();
      this.installations = result.installations;
      this.errors = result.errors;
      this.lastLoaded = Date.now();
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e);
      this.installations = [];
      this.errors = [];
    } finally {
      this.loading = false;
    }
  }

  async refresh(): Promise<void> {
    await this.load(true);
  }
  versions(): string[] {
    return this.installations.map((i) => i.version);
  }

  installedSet(): Set<string> {
    return new Set(this.versions());
  }

  bucketCounts(projects: ProjectEntry[]): Map<string, number> {
    const counts = new Map<string, number>();
    for (const project of projects) {
      if (!project.unityVersion) continue;
      counts.set(project.unityVersion, (counts.get(project.unityVersion) ?? 0) + 1);
    }
    return counts;
  }

  missingVersionBuckets(projects: ProjectEntry[]): VersionBucket[] {
    const installed = this.installedSet();
    const counts = new Map<string, number>();
    for (const project of projects) {
      if (!project.unityVersion) continue;
      if (installed.has(project.unityVersion)) continue;
      counts.set(project.unityVersion, (counts.get(project.unityVersion) ?? 0) + 1);
    }
    return Array.from(counts.entries())
      .map(([version, count]) => ({ version, count }))
      .sort((a, b) => b.count - a.count || a.version.localeCompare(b.version));
  }

  healthFor(version: string, projects: ProjectEntry[]): VersionHealth {
    const installed = this.installations.find((i) => i.version === version);
    if (!installed) return "missing";
    const counts = this.bucketCounts(projects);
    const projectCount = counts.get(version) ?? 0;
    if (projectCount === 0) return "warn";
    return "ok";
  }
}

export const discoveryStore = new DiscoveryStore();
