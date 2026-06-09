import { invoke } from "@tauri-apps/api/core";

export interface LaunchSettings {
  mode: string;
  rememberLastSelection: boolean;
}

export interface ProjectListSettings {
  showPathColumn: boolean;
  showModifiedColumn: boolean;
  searchIncludesPath: boolean;
}

export interface SafetySettings {
  confirmKillUnity: boolean;
  confirmRemoveProject: boolean;
}

export interface UnityDiscoverySettings {
  parentFolders: string[];
}

export interface Settings {
  version: number;
  launch: LaunchSettings;
  projectList: ProjectListSettings;
  safety: SafetySettings;
  unityDiscovery: UnityDiscoverySettings;
}

export interface ProjectEntry {
  id: string;
  name: string;
  path: string;
  unityVersion?: string;
  lastOpenedAt?: string;
  lastModifiedAt?: string;
  launchArgs?: string;
  platformIntent?: string;
  lastLaunchPid?: number;
  lastLaunchAt?: string;
}

export interface ProjectsFile {
  version: number;
  projects: ProjectEntry[];
}

export interface UnityInstallation {
  version: string;
  path: string;
  source: string;
  installDate?: string;
}

export interface DiscoveryError {
  parentPath: string;
  message: string;
}

export interface DiscoveryResult {
  installations: UnityInstallation[];
  errors: DiscoveryError[];
}

export interface SeedResult {
  projects: ProjectsFile;
  seededCount: number;
  skippedPaths: string[];
  error?: string;
}

export async function loadSettings(): Promise<Settings> {
  return invoke<Settings>("load_settings");
}

export async function saveSettings(settings: Settings): Promise<void> {
  return invoke("save_settings", { settings });
}

export async function loadProjects(): Promise<ProjectsFile> {
  return invoke<ProjectsFile>("load_projects");
}

export async function saveProjects(projects: ProjectsFile): Promise<void> {
  return invoke("save_projects", { projects });
}

export async function seedFromUnityHub(): Promise<SeedResult> {
  return invoke<SeedResult>("seed_from_unity_hub");
}

export async function discoverInstallations(): Promise<DiscoveryResult> {
  return invoke<DiscoveryResult>("discover_installations");
}

export async function refreshDiscovery(): Promise<DiscoveryResult> {
  return invoke<DiscoveryResult>("refresh_discovery");
}
