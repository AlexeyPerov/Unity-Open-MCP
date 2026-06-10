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

export type LaunchError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "pathInvalid"; projectId: string; path: string }
  | { type: "versionMissing"; projectId: string }
  | { type: "installNotFound"; projectId: string; version: string }
  | { type: "launchFailed"; projectId: string; message: string };

export interface LaunchResult {
  projectId: string;
  pid: number;
  unityVersion?: string;
  executablePath: string;
}

export interface VersionRefreshResult {
  projectId: string;
  unityVersion?: string;
}

export type AddProjectError =
  | { type: "notADirectory"; path: string }
  | { type: "notAUnityProject"; path: string; reason: string }
  | { type: "duplicate"; path: string }
  | { type: "persistFailed"; message: string };

export interface AddProjectResult {
  project: ProjectEntry;
  projects: ProjectsFile;
}

export interface RefreshAllResult {
  projects: ProjectsFile;
  updated: string[];
  skipped: string[];
}

export type RemoveProjectError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "persistFailed"; message: string };

export interface RemoveProjectResult {
  projectId: string;
  removedName: string;
  removedPath: string;
  projects: ProjectsFile;
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

export async function launchProject(projectId: string): Promise<LaunchResult> {
  return invoke<LaunchResult>("launch_project", { projectId });
}

export async function refreshProjectVersion(
  projectId: string
): Promise<VersionRefreshResult> {
  return invoke<VersionRefreshResult>("refresh_project_version", { projectId });
}

export async function checkPathsExists(
  paths: string[]
): Promise<Record<string, boolean>> {
  return invoke<Record<string, boolean>>("check_paths_exists", { paths });
}

export async function addProject(path: string): Promise<AddProjectResult> {
  return invoke<AddProjectResult>("add_project", { path });
}

export async function refreshAllProjects(): Promise<RefreshAllResult> {
  return invoke<RefreshAllResult>("refresh_all_projects");
}

export async function removeProject(projectId: string): Promise<RemoveProjectResult> {
  return invoke<RemoveProjectResult>("remove_project", { projectId });
}
