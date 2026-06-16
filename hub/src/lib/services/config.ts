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
  /**
   * M1.5-15: when true, the Projects tab opens with the
   * "Missing or stale" filter preset selected so a freshly-installed
   * Hub does not flash every missing-path row at the user. The
   * toolbar's filter chips and the "Show hidden" toggle remain
   * reachable — this only changes the default selection on load.
   */
  hideMissingByDefault?: boolean;
}

export type ProjectListSortBy = "frecency" | "lastModified";

export interface SafetySettings {
  confirmKillUnity: boolean;
  confirmRemoveProject: boolean;
  /**
   * M1.5-17: when `true` (default), a confirmation modal lists
   * colliding env-var keys before a launch so the user is warned
   * that the spawned Unity will override a parent-process variable.
   */
  confirmEnvVarOverride?: boolean;
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

/**
 * M4: AI toolkit root + advanced MCP override (questions-4 Q2 = B).
 * `rootPath` is the absolute path to the cloned unity-open-mcp
 * monorepo; the wizard Step 2 collects it and persists it here on
 * successful fingerprint validation. `mcpIndexOverride` is the Step 4
 * advanced escape hatch for a custom-built `mcp-server/dist/index.js`
 * only — packages and skills always use `rootPath`.
 *
 * Both fields default to `""` so legacy `settings.json` files
 * (pre-M4) deserialize cleanly and the wizard Step 2 hard-blocks
 * downstream steps until `rootPath` is set and validated.
 */
export interface AiToolkitSettings {
  rootPath: string;
  mcpIndexOverride: string;
}

/** M1.5-18: three-way theme switch. */
export type Theme = "dark" | "light" | "system";

export interface Settings {
  version: number;
  launch: LaunchSettings;
  projectList: ProjectListSettings;
  safety: SafetySettings;
  unityDiscovery: UnityDiscoverySettings;
  diagnostics: DiagnosticsSettings;
  /** M4: AI toolkit root + advanced MCP override. See
   *  `AiToolkitSettings` for field semantics. Defaulted to an empty
   *  record in the Rust layer so legacy `settings.json` files load. */
  aiToolkit?: AiToolkitSettings;
  /** M1.5-18: active theme. Defaults to `"system"`; legacy
   *  `settings.json` files (pre-M1.5-18) load with `"system"`. */
  theme?: Theme;
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
  /**
   * M1.5-15: when `true`, the row is soft-deleted from the default
   * list view but the entry is kept on disk; the toolbar's "Show
   * hidden" toggle reveals it again. Defaulted to `false` for
   * legacy entries.
   */
  hidden?: boolean;
  /**
   * M1.5-15: when `true`, the row is kept visible with a `stale`
   * chip (distinct from `missing path`) and is excluded from
   * launch / running-Unity actions. A "Mark stale" toggle on the
   * missing-path chip is the only way to set this field; relinking
   * to a real project root clears it.
   */
  stale?: boolean;
  /**
   * M1.5-17: per-project environment variables merged into the
   * spawned Unity process's environment (the child overrides the
   * parent on key collision). Empty map for legacy entries.
   */
  envVars?: Record<string, string>;
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
  | { type: "launchFailed"; projectId: string; message: string }
  | {
      type: "alreadyRunning";
      projectId: string;
      pid: number;
      projectPath: string;
    };

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
  /** M1.5-16: path to `Editor-prev.log` (the previous session's
   *  Editor.log, rotated by Unity on every fresh launch). */
  editorPrevLogFile?: string;
  playerLogsFolder?: string;
  /** M1.5-16: per-project `Player.log` (the editor preview player's
   *  log; same folder as the Editor.log). */
  playerLogFile?: string;
  /** M1.5-16: per-user global `Player.log` written by standalone
   *  Unity Player builds. `undefined` when no standalone build has
   *  been run on this machine yet (the file does not exist). */
  unityPlayerLogFile?: string;
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

export async function launchProject(
  projectId: string,
  /**
   * M1.5-18: the active theme at the time of the launch
   * (`"dark" | "light"` — the frontend has already resolved
   * `system` to a concrete palette). The backend stamps the
   * value on the persistent per-launch log record.
   */
  theme: "dark" | "light"
): Promise<LaunchResult> {
  return invoke<LaunchResult>("launch_project", { projectId, theme });
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

export async function getOsDefaultHubPaths(): Promise<string[]> {
  return invoke<string[]>("get_os_default_hub_paths");
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

/**
 * M1.5-14 — Unity upgrade assistant.
 */

export type BundleStrategy = "none" | "patch" | "minor" | "major";

export interface UpgradeUnityParams {
  projectId: string;
  /** Target Unity version; must be present in the in-memory discovery cache. */
  targetVersion: string;
  /**
   * `bundleVersion` bump strategy. The Rust `#[serde(default)]` makes
   * this field optional; the backend defaults to `"patch"`.
   */
  bundleStrategy?: BundleStrategy;
}

export type UpgradeUnityError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "pathInvalid"; projectId: string; path: string }
  | { type: "versionNotInstalled"; projectId: string; version: string }
  | {
      type: "projectVersionUnreadable";
      projectId: string;
      path: string;
      reason: string;
    }
  | {
      type: "bundleVersionUnwritable";
      projectId: string;
      path: string;
      reason: string;
    }
  | { type: "ioError"; projectId: string; message: string }
  | { type: "persistFailed"; projectId: string; message: string };

export interface UpgradeUnityResult {
  project: ProjectEntry;
  /** The new Unity version written to `ProjectVersion.txt`. */
  unityVersion: string;
  /** The new `bundleVersion` written to `ProjectManager.asset`. */
  bundleVersion: string;
  /** The `bundleVersion` value on disk before the upgrade. */
  previousBundleVersion: string;
  /** The Unity version on disk before the upgrade. */
  previousUnityVersion: string;
  bundleStrategy: BundleStrategy;
}

export async function upgradeUnity(
  params: UpgradeUnityParams
): Promise<UpgradeUnityResult> {
  return invoke<UpgradeUnityResult>("upgrade_unity", { params });
}

export async function upgradeCandidates(projectId: string): Promise<string[]> {
  return invoke<string[]>("upgrade_candidates", { projectId });
}

/**
 * M1.5-15 — Hide / Mark-stale.
 */

export type SetProjectFlagError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "persistFailed"; message: string };

export async function setProjectHidden(
  projectId: string,
  hidden: boolean
): Promise<ProjectEntry> {
  return invoke<ProjectEntry>("set_project_hidden", { projectId, hidden });
}

export async function setProjectStale(
  projectId: string,
  stale: boolean
): Promise<ProjectEntry> {
  return invoke<ProjectEntry>("set_project_stale", { projectId, stale });
}

/**
 * M1.5-17: project-level env vars. Returns the sorted list of keys
 * that would override a parent-process variable. Empty when the
 * project is missing, has no env vars, or no env var collides.
 */
export async function envVarCollisions(projectId: string): Promise<string[]> {
  return invoke<string[]>("env_var_collisions", { projectId });
}

/**
 * M1.5-19: Unity releases / updates viewer.
 */

export type ReleaseStream = "lts" | "tech" | "beta" | "alpha";

export interface ReleaseEntry {
  version: string;
  stream: ReleaseStream;
  releaseDate?: string;
  releaseNotesUrl: string;
  changeset?: string;
}

export interface ReleasesResult {
  entries: ReleaseEntry[];
  /** `true` when the cache on disk is older than the TTL (1h). */
  stale: boolean;
  /** Unix-epoch seconds of the cache write. */
  fetchedAtEpoch: number;
  /** Path to the on-disk cache file. */
  cachePath: string;
}

export async function fetchReleases(): Promise<ReleasesResult> {
  return invoke<ReleasesResult>("fetch_releases");
}

export async function refreshReleases(): Promise<ReleasesResult> {
  return invoke<ReleasesResult>("refresh_releases_command");
}

/**
 * M1.5-20: Unity Editor install via Unity Hub CLI.
 */

export type InstallError =
  | { type: "hubNotFound" }
  | { type: "installInProgress" }
  | { type: "versionEmpty" }
  | { type: "installFailed"; message: string };

export interface InstallResult {
  version: string;
}

export async function installUnityVersion(
  version: string,
  changeset?: string
): Promise<InstallResult> {
  return invoke<InstallResult>("install_unity_version", { version, changeset });
}

export async function checkInstallInProgress(): Promise<boolean> {
  return invoke<boolean>("check_install_in_progress");
}

/**
 * M4: AI toolkit root validation. The wizard Step 2 calls this
 * whenever the user picks a folder or edits the path. Pure
 * function — does not mutate `settings.json`; the wizard's Step 2
 * save handler is responsible for persisting `aiToolkit.rootPath`
 * on a successful `ToolkitValidation.ok`.
 */
export type ToolkitFingerprintKind = "file" | "dir";

export interface ToolkitFingerprintResult {
  relativePath: string;
  kind: ToolkitFingerprintKind;
  required: boolean;
  exists: boolean;
  /**
   * `true` when the entry exists and matches the expected kind.
   * `false` when it exists but is the wrong kind (a file where a
   * directory is required, or vice versa). `null` when the entry
   * does not exist at all.
   */
  kindOk: boolean | null;
  resolved: string | null;
}

export interface ToolkitValidation {
  ok: boolean;
  root: string | null;
  fingerprints: ToolkitFingerprintResult[];
  /** `true` when `mcp-server/dist/index.js` is missing while every
   *  other required fingerprint is fine — the UI surfaces a focused
   *  "run `npm run build`" remediation in that case. */
  mcpDistMissing: boolean;
}

export interface NodeProbe {
  ok: boolean;
  version: string | null;
  major: number | null;
  requiredMajor: number;
  error: string | null;
}

export async function validateToolkitRoot(
  path: string
): Promise<ToolkitValidation> {
  return invoke<ToolkitValidation>("validate_toolkit_root", { path });
}

export async function checkNodeVersion(): Promise<NodeProbe> {
  return invoke<NodeProbe>("check_node_version");
}

/**
 * M4 Plan 3: wizard Step 1 + Done screen detection snapshot.
 * Mirrors the Rust `wizard::ProjectState` struct.
 */
export type BridgeStatusKind =
  | { kind: "notChecked" }
  | {
      kind: "ok";
      connected: boolean;
      projectPath?: string | null;
      compiling: boolean;
      isPlaying: boolean;
    }
  | { kind: "failed"; message: string };

export interface McpConfigHeuristic {
  cursor: boolean;
  claudeDesktop: boolean;
  opencodeGlobal: boolean;
  opencodeProject: boolean;
}

export interface ProjectState {
  path: string;
  name: string;
  isValidUnityProject: boolean;
  /** Raw `m_EditorVersion` from `ProjectSettings/ProjectVersion.txt`. */
  unityVersion: string | null;
  /** `true` when `unityVersion` parses to a major ≥ 6000 (Unity 6). */
  meetsMinUnityVersion: boolean;
  manifestPresent: boolean;
  bridgeInstalled: boolean;
  verifyInstalled: boolean;
  mcpConfigured: McpConfigHeuristic;
  /** `true` when `Packages/manifest.json` (or its parent) is writable. */
  manifestWritable: boolean;
  /** `true` when any path segment contains a space — Step 2 warning. */
  hasSpacesInPath: boolean;
  /** Always `notChecked` in M4; Step 5 `/ping` would rewrite this. */
  bridgeStatus: BridgeStatusKind;
}

export async function detectProjectState(
  projectPath: string
): Promise<ProjectState> {
  return invoke<ProjectState>("detect_project_state", { projectPath });
}

/**
 * M4 Plan 3: wizard Step 3 manifest read / merge.
 * Mirrors the Rust `wizard::{ManifestRead, ManifestMergePlan, …}` types.
 */

export type ChangeKind = "add" | "upgrade" | "unchanged";

export interface ManifestRead {
  projectPath: string;
  present: boolean;
  readable: boolean;
  parseError: string | null;
  raw: unknown | null;
  dependencies: Record<string, string>;
}

export interface PackageInstallEntry {
  id: string;
  url: string;
  tag: string;
  packagePath: string;
}

export interface DerivedPackageUrls {
  toolkitRoot: string;
  gitRemote: string;
  bridge: PackageInstallEntry;
  verify: PackageInstallEntry;
}

export interface PackageChange {
  id: string;
  before: string | null;
  after: string;
  kind: ChangeKind;
}

export interface ManifestMergeParams {
  projectPath: string;
  toolkitRoot: string;
  installBridge: boolean;
  installVerify: boolean;
  versionPin: string;
  customUrl: string;
  confirmUpgrades: boolean;
  useLocalPackages: boolean;
}

export interface ManifestMergePlan {
  projectPath: string;
  derivedUrls: DerivedPackageUrls;
  changes: PackageChange[];
  proposedDependencies: Record<string, string>;
  manifestRead: ManifestRead;
  hasUpgrades: boolean;
  useLocalPackages: boolean;
  manifestUsesLocalPackages: boolean;
}

export interface ManifestWriteResult {
  projectPath: string;
  manifestPath: string;
  backupPath: string;
  changes: PackageChange[];
  dependencies: Record<string, string>;
}

export interface ManifestError {
  kind: string;
  message: string;
}

export async function readManifest(projectPath: string): Promise<ManifestRead> {
  return invoke<ManifestRead>("read_manifest", { projectPath });
}

export async function planManifestMerge(
  params: ManifestMergeParams
): Promise<ManifestMergePlan> {
  return invoke<ManifestMergePlan>("plan_manifest_merge", { params });
}

export async function writeManifestMerge(
  params: ManifestMergeParams
): Promise<ManifestWriteResult> {
  return invoke<ManifestWriteResult>("write_manifest_merge", { params });
}

/**
 * M4 Plan 4: wizard Step 4 MCP client config plan / write.
 * Mirrors the Rust `wizard::{McpConfigParams, McpConfigPlan, …}` types.
 */
export type McpClientIdWire =
  | "cursor"
  | "claudeDesktop"
  | "claudeCode"
  | "opencodeGlobal"
  | "opencodeProject"
  | "manual";

export interface McpConfigParamsWire {
  projectPath: string;
  toolkitRoot: string;
  mcpIndexOverride: string;
  unityProjectPath: string;
  bridgePort: string;
  includeUnityPath: boolean;
  unityPath: string;
  client: McpClientIdWire;
  cursorProjectScope: boolean;
}

export interface McpConfigPlan {
  client: McpClientIdWire;
  targetPath: string | null;
  fileExists: boolean;
  wouldWrite: boolean;
  preservedKeys: string[];
  proposedJson: string | null;
  command: string | null;
  resolvedMcpIndex: string;
}

export interface McpConfigWriteResult {
  client: McpClientIdWire;
  targetPath: string;
  backupPath: string;
  wouldWrite: boolean;
  proposedJson: string;
}

export interface McpConfigError {
  kind: string;
  message: string;
}

/**
 * Tauri command: compute a Step 4 MCP config merge plan
 * without writing anything. The wizard Step 4 calls this on
 * every form-state change so the diff preview is always live.
 */
export async function planMcpConfig(
  params: McpConfigParamsWire
): Promise<McpConfigPlan> {
  return invoke<McpConfigPlan>("plan_mcp_config", { params });
}

/**
 * Tauri command: apply the Step 4 MCP config merge. Refuses
 * when the MCP index path is missing, when the target client
 * is CLI-only (`claude-code`) or clipboard-only (`manual`),
 * and when the parent folder is not writable.
 */
export async function writeMcpConfig(
  params: McpConfigParamsWire
): Promise<McpConfigWriteResult> {
  return invoke<McpConfigWriteResult>("write_mcp_config", { params });
}

/**
 * M4 Plan 4: wizard Done-time skill copy plan / write.
 * Mirrors the Rust `mcp_config::{SkillCopyParams, SkillCopyPlan, …}` types.
 */
export type SkillCopyKind = "claude" | "opencode";

export interface SkillCopyTarget {
  kind: SkillCopyKind;
  targetPath: string;
  relativePath: string;
  sourcePath: string | null;
  exists: boolean;
}

export interface SkillCopyPlan {
  projectPath: string;
  toolkitRoot: string;
  sourcePath: string | null;
  targets: SkillCopyTarget[];
}

export interface SkillCopyParamsWire {
  projectPath: string;
  toolkitRoot: string;
  opencodeSelected: boolean;
}

export interface SkillCopyResult {
  projectPath: string;
  copied: SkillCopyTarget[];
  /** Targets that existed and were skipped (no overwrite). */
  skipped: SkillCopyTarget[];
  /** Targets the writer replaced after the user confirmed. */
  overwritten: SkillCopyTarget[];
}

export interface SkillCopyError {
  kind: string;
  message: string;
}

export async function planSkillCopy(
  params: SkillCopyParamsWire
): Promise<SkillCopyPlan> {
  return invoke<SkillCopyPlan>("plan_skill_copy", { params });
}

export async function copySkillFiles(
  params: SkillCopyParamsWire,
  overwriteExisting: boolean
): Promise<SkillCopyResult> {
  return invoke<SkillCopyResult>("copy_skill_files", {
    params,
    overwriteExisting,
  });
}

/**
 * M4 Plan 5: wizard Step 5 launch + HTTP `/ping` verification.
 * Mirrors the Rust `launch_verify::{BridgePingResult, LaunchForVerifyParams, …}`
 * types.
 */

export interface LaunchForVerifyParamsWire {
  projectId: string;
  bridgePort: number;
  theme?: "dark" | "light" | "system";
}

export interface LaunchForVerifyResult {
  projectId: string;
  pid: number;
  unityVersion?: string | null;
  executablePath: string;
  bridgePort: number;
}

export type LaunchForVerifyError =
  | { kind: "projectNotFound"; projectId: string }
  | { kind: "pathInvalid"; projectId: string; path: string }
  | { kind: "versionMissing"; projectId: string }
  | { kind: "installNotFound"; projectId: string; version: string }
  | { kind: "launchFailed"; projectId: string; message: string }
  | { kind: "portInvalid"; port: number };

export interface BridgePingResult {
  port: number;
  durationMs: number;
  ok: boolean;
  connected: boolean;
  compiling: boolean;
  isPlaying: boolean;
  projectPath?: string | null;
  unityVersion?: string | null;
  raw?: string | null;
  errorKind: string;
  errorMessage: string;
}

/**
 * Default bridge HTTP port (numeric form). Mirrors
 * `DEFAULT_BRIDGE_PORT` in `launch_verify.rs` and the default
 * documented in `packages/bridge.md` §HTTP API. The Step 4
 * env-var builder (`ai_toolkit.ts`) uses the string form
 * (`"19120"`) for the MCP config; the Step 5 verifier needs
 * the numeric form for `poll_bridge_ping` and
 * `launch_for_verify`.
 */
export const DEFAULT_BRIDGE_PORT_NUM = 19120;

/**
 * Parse the Step 4 bridge port input (a free-text string) into
 * the numeric value the Rust commands consume. Returns the
 * `DEFAULT_BRIDGE_PORT_NUM` when the input is blank or
 * unparsable so the wizard always has a usable port to poll
 * and pass to Unity.
 */
export function bridgePortFromString(raw: string): number {
  const trimmed = raw.trim();
  if (trimmed.length === 0) return DEFAULT_BRIDGE_PORT_NUM;
  const n = Number.parseInt(trimmed, 10);
  if (!Number.isFinite(n) || n <= 0 || n > 65535) {
    return DEFAULT_BRIDGE_PORT_NUM;
  }
  return n;
}

/**
 * Tauri command: launch Unity with the bridge port pinned via
 * `-UNITY_OPEN_MCP_BRIDGE_PORT` and the `UNITY_OPEN_MCP_BRIDGE_PORT`
 * env var, so the in-Editor bridge listens on the port the
 * wizard Step 5 is about to poll. Reuses the regular launch
 * pipeline (install resolution, version refresh, env-var
 * layering, `last_launch_pid` bookkeeping) and writes the
 * same `LaunchOutcome::Ok | LaunchOutcome::Error` record to
 * the per-launch log.
 */
export async function launchForVerify(
  params: LaunchForVerifyParamsWire
): Promise<LaunchForVerifyResult> {
  return invoke<LaunchForVerifyResult>("launch_for_verify", { params });
}

/**
 * Tauri command: GET `127.0.0.1:{port}/ping` with a per-request
 * timeout and return the parsed bridge body. The wizard Step 5
 * calls this on a 2-3 s cadence until the bridge responds 200
 * with a parseable body or until the 120 s overall budget
 * elapses. The ping call never spawns a separate
 * `unity-open-mcp` subprocess (questions-4 Q8 = B).
 */
export async function pollBridgePing(
  port: number,
  timeoutMs: number
): Promise<BridgePingResult> {
  return invoke<BridgePingResult>("poll_bridge_ping", { port, timeoutMs });
}
