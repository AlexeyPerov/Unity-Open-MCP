import { invoke } from "@tauri-apps/api/core";

export interface LaunchSettings {
  mode: string;
  rememberLastSelection: boolean;
}

export interface ProjectListSettings {
  showPathColumn: boolean;
  showModifiedColumn: boolean;
  showGitBranchColumn: boolean;
  searchIncludesPath: boolean;
  /**
   * `"frecency"` (default) or `"lastModified"`. Unknown values fall back to
   * `"frecency"` so a future field addition never bricks the list sort.
   */
  sortBy: ProjectListSortBy;
}

export type ProjectListSortBy = "frecency" | "lastModified";

export interface SafetySettings {
  confirmKillUnity: boolean;
  confirmRemoveProject: boolean;
}

export interface UnityDiscoverySettings {
  parentFolders: string[];
  /**
   * Polling interval (in seconds) for the running-Unity process scan that
   * powers the `running` chip on the Projects tab. Added in M1.5-10. The
   * Rust `serde(default)` keeps legacy settings.json files loadable; this
   * field is optional in TypeScript for the same reason.
   */
  scanIntervalSeconds?: number;
  /**
   * M1.5-11: folder roots the user wants the walk-up directory scan to
   * recurse into when looking for Unity project roots (a folder that
   * contains both `Assets/` and `ProjectSettings/`). Empty by default;
   * the scan button on the Projects tab is a no-op when this is empty.
   */
  walkUpRoots: string[];
  /**
   * M1.5-11: maximum directory depth the walk-up scan descends from
   * each root. Default 4, hard cap 8. The Rust mutator clamps incoming
   * values to `[1, 8]` and the UI input is range-pinned to the same.
   */
  walkUpMaxDepth: number;
  /**
   * M1.5-11: when true, the walk-up scan follows symbolic links. Off
   * by default to avoid loops and unintended home-dir traversal.
   */
  walkUpFollowSymlinks: boolean;
  /**
   * M1.5-11: when true (default), a cancelled walk-up scan keeps the
   * projects it had already discovered and appended to `projects.json`;
   * when false, partial results are discarded.
   */
  walkUpKeepPartial: boolean;
  /**
   * M1.5-13: absolute paths to user-curated Unity project roots that
   * can be used as a template when creating a new project (the
   * "Custom folder…" option in the New Project modal). Each entry is
   * validated as a Unity project root at create-time. Mirrors the
   * Rust `#[serde(default)]` so legacy `settings.json` files load.
   */
  customTemplateFolders: string[];
}

export interface DiagnosticsSettings {
  autoOpenDrawerOnLaunchFailure: boolean;
}

export interface Settings {
  version: number;
  launch: LaunchSettings;
  projectList: ProjectListSettings;
  safety: SafetySettings;
  unityDiscovery: UnityDiscoverySettings;
  diagnostics: DiagnosticsSettings;
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
  /**
   * Frecency counter. Incremented on every successful launch; the sort
   * score combines this counter with `lastLaunchAt` (14-day half-life).
   * Defaulted to 0 for legacy entries.
   */
  frecency?: number;
  /**
   * Cached git branch name (short form, e.g. `feature/frecency`) or
   * `detached:<sha>` for detached HEAD. `null`/omitted for non-git
   * projects or pending reads.
   */
  gitBranch?: string | null;
  /**
   * M1.5-11: where this entry came from. One of:
   *  - `"hub-seed"` — from the M1 first-run Unity Hub import
   *  - `"manual"` — user added via the Add Project button / drag-drop / CLI
   *  - `"walk-up"` — added by the walk-up directory scan
   * Legacy entries deserialize as `"manual"` so the on-disk file stays
   * compact; the walk-up chip / filter never matches legacy rows.
   */
  source?: string;
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

export type RunUnityError =
  | { type: "versionMissing" }
  | { type: "installNotFound"; version: string }
  | { type: "executableMissing"; version: string; installPath: string }
  | { type: "launchFailed"; version: string; message: string };

export interface RunUnityResult {
  version: string;
  pid: number;
  executablePath: string;
}

export interface LogPaths {
  editorLogsFolder?: string;
  editorLogFile?: string;
  playerLogsFolder?: string;
  crashLogsFolder?: string;
}

export interface AssetStorePaths {
  folder?: string;
  versioned: boolean;
  missingMessage?: string;
}

export type KillUnityStatus = "killed" | "notFound" | "accessDenied";

export interface KillUnityResult {
  pid: number;
  status: KillUnityStatus;
  message: string;
}

export interface LaunchResult {
  projectId: string;
  pid: number;
  unityVersion?: string;
  executablePath: string;
}

export interface VersionRefreshResult {
  projectId: string;
  unityVersion?: string;
  lastModifiedAt?: string;
  gitBranch?: string | null;
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

export type RelinkProjectError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "notADirectory"; path: string }
  | { type: "notAUnityProject"; path: string; reason: string }
  | { type: "duplicate"; path: string }
  | { type: "persistFailed"; message: string };

export interface DiagnosticsPaths {
  configDir: string;
  settingsFile: string;
  projectsFile: string;
}

export interface ExportDiagnosticsResult {
  path: string;
  settingsCopied: boolean;
  projectsCopied: boolean;
  logIncluded: boolean;
}

export interface ExportDiagnosticsError {
  kind: string;
  message: string;
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

export async function runUnityInstall(version: string): Promise<RunUnityResult> {
  return invoke<RunUnityResult>("run_unity_install", { version });
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

export async function relinkProject(
  projectId: string,
  newPath: string
): Promise<ProjectEntry> {
  return invoke<ProjectEntry>("relink_project", { projectId, newPath });
}

export async function getLogPaths(projectPath: string): Promise<LogPaths> {
  return invoke<LogPaths>("log_paths", { projectPath });
}

export async function getAssetStorePaths(): Promise<AssetStorePaths> {
  return invoke<AssetStorePaths>("asset_store_paths");
}

export async function getCrashLogPath(): Promise<string | null> {
  return invoke<string | null>("crash_log_path");
}

export interface LaunchLogTail {
  path: string;
  content: string;
  lineCount: number;
}

export async function getLaunchLogTail(lineCount: number): Promise<LaunchLogTail> {
  return invoke<LaunchLogTail>("get_launch_log_tail", { lineCount });
}

export type DefaultBuildTargetSource = "projectSettings" | "notRecorded";

export interface DefaultBuildTarget {
  target: string | null;
  source: DefaultBuildTargetSource;
}

export async function getDefaultBuildTarget(
  projectPath: string
): Promise<DefaultBuildTarget> {
  return invoke<DefaultBuildTarget>("get_default_build_target", { projectPath });
}

/**
 * Bulk-resolve git branches for the given project paths. Returns a map
 * keyed by the input path so the caller can correlate results back to
 * each project entry. Values are `null` for non-git projects and
 * `detached:<sha>` for detached HEAD; see Rust `git_branch::read_git_branch`.
 */
export async function getGitBranches(
  paths: string[]
): Promise<Record<string, string | null>> {
  return invoke<Record<string, string | null>>("get_git_branches", { paths });
}

export async function isPidAlive(pid: number): Promise<boolean> {
  return invoke<boolean>("is_pid_alive", { pid });
}

export interface RunningUnity {
  pid: number;
  projectPath?: string | null;
}

export async function scanRunningUnity(): Promise<RunningUnity[]> {
  return invoke<RunningUnity[]>("scan_running_unity");
}

export async function killUnity(pid: number): Promise<KillUnityResult> {
  return invoke<KillUnityResult>("kill_unity", { pid });
}

export async function getDiagnosticsPaths(): Promise<DiagnosticsPaths> {
  return invoke<DiagnosticsPaths>("get_diagnostics_paths");
}

export async function exportDiagnostics(
  targetDir: string,
  logTail: string | null
): Promise<ExportDiagnosticsResult> {
  return invoke<ExportDiagnosticsResult>("export_diagnostics", {
    targetDir,
    logTail,
  });
}

export async function getProjectSizes(
  paths: string[]
): Promise<Record<string, number>> {
  return invoke<Record<string, number>>("get_project_sizes", { paths });
}

/**
 * M1.5-11 — walk-up directory scan.
 */

export interface WalkUpStartParams {
  roots: string[];
  maxDepth: number;
  followSymlinks: boolean;
  keepPartial: boolean;
}

export interface WalkUpStart {
  scanId: string;
  maxDepth: number;
  followSymlinks: boolean;
  keepPartial: boolean;
  roots: string[];
}

export type WalkUpStatus = "running" | "cancelled" | "completed" | "failed";

export interface WalkUpProgress {
  scanId: string;
  currentRoot: string;
  currentDepth: number;
  maxDepth: number;
  foundSoFar: number;
  visitedDirs: number;
  status: WalkUpStatus;
}

export interface WalkUpDone {
  scanId: string;
  status: WalkUpStatus;
  added: ProjectEntry[];
  skippedExisting: string[];
  skippedNotUnity: number;
  skippedInvalidRoot: string[];
  projects: ProjectsFile;
  error?: string | null;
}

export type WalkUpError =
  | { type: "anotherScanInProgress"; currentScanId: string }
  | { type: "noRoots" }
  | { type: "invalidRoot"; path: string; reason: string };

export async function startWalkUpScan(
  params: WalkUpStartParams
): Promise<WalkUpStart> {
  return invoke<WalkUpStart>("start_walk_up_scan", { params });
}

export async function cancelWalkUpScan(scanId: string): Promise<boolean> {
  return invoke<boolean>("cancel_walk_up_scan", { scanId });
}

/**
 * M1.5-12 / M1.5-13 — New project creation.
 */

export type RenderPipeline = "none" | "urp" | "hdrp";

/**
 * Resolved template reference for the New Project modal. The backend
 * does not branch on `source`; it is the label shown next to the
 * picker ("hub-default" or "custom") so the user can see where the
 * template came from.
 */
export interface TemplateRef {
  /** `"hub-default"` (Unity Hub's own templates) or `"custom"`. */
  source: "hub-default" | "custom";
  /** Absolute path to the template folder on disk. */
  path: string;
}

export interface NewProjectParams {
  /** Absolute path to the parent folder; the new project is created
   *  as `<parent>/<name>`. */
  parent: string;
  /** Project name; must not contain path separators. */
  name: string;
  /** Unity version string (e.g. `"6000.0.1f1"`). Must be installed. */
  version: string;
  /** Render pipeline to scaffold. */
  pipeline: RenderPipeline;
  /** `bundleVersion` written into ProjectManager.asset / ProjectSettings.asset. */
  bundleVersion: string;
  /** `undefined` ⇒ Empty template; a value selects Hub default or Custom. */
  template?: TemplateRef | null;
  /** When `true`, an existing directory at the target path is replaced. */
  overwrite?: boolean;
}

export interface NewProjectResult {
  project: ProjectEntry;
  projects: ProjectsFile;
}

export type NewProjectError =
  | { type: "parentNotDirectory"; path: string }
  | { type: "nameEmpty" }
  | { type: "nameInvalid"; name: string; reason: string }
  | { type: "nameCollision"; path: string; isDirectory: boolean }
  | { type: "versionUnknown"; version: string }
  | { type: "versionNotInstalled"; version: string }
  | { type: "pipelineUnsupported"; version: string; pipeline: string }
  | { type: "templateNotFound"; path: string }
  | { type: "templateInvalid"; path: string; reason: string }
  | { type: "ioError"; message: string }
  | { type: "persistFailed"; message: string };

export interface HubTemplateEntry {
  name: string;
  path: string;
  /** Version read from the template's `ProjectVersion.txt`, if present. */
  unityVersion?: string | null;
}

export interface HubTemplatesResult {
  /** `true` when Unity Hub is installed and at least one template is available. */
  available: boolean;
  /** Absolute path to the Hub templates folder, when present. */
  folder?: string | null;
  templates: HubTemplateEntry[];
}

export async function createNewProject(
  params: NewProjectParams
): Promise<NewProjectResult> {
  return invoke<NewProjectResult>("create_new_project", { params });
}

export async function listHubTemplates(): Promise<HubTemplatesResult> {
  return invoke<HubTemplatesResult>("list_hub_templates");
}
