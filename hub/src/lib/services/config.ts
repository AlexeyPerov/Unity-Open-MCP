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
 * `useLocalCheckout` (added with the npm distribution milestone) is the
 * Step 2 toggle that selects the local-checkout onboarding path over the
 * default `npx` path. It auto-enables when `rootPath` is already set so
 * existing M4 onboarding (clone-based) keeps resolving to the local
 * launch command without a breaking change.
 *
 * All three fields default to empty/`false` so legacy `settings.json`
 * files (pre-M4) deserialize cleanly and the wizard Step 2 hard-blocks
 * downstream steps until `rootPath` is set and validated (local path).
 */
export interface AiToolkitSettings {
  rootPath: string;
  mcpIndexOverride: string;
  /** `true` to use the local toolkit checkout instead of `npx`. */
  useLocalCheckout?: boolean;
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
  /** Multi-type: when a project's on-disk size is below this threshold
   *  (in MiB), the git popup auto-calculates a line count and caches it
   *  so a passive stat is shown. Above it, the popup shows a hint and
   *  the user runs the manual counter from the settings popup. Default
   *  30. Legacy `settings.json` files load with 30. */
  lineCountAutoCalcThresholdMb?: number;
}

/**
 * Multi-type support: how the hub treats a tracked folder. Detection
 * precedence lives in the Rust `project_kind::detect_kind` helper:
 * Unity (Assets/ + ProjectSettings/) → OpenMcp (mcp-server/ +
 * package.json) → Package (package.json) → Custom (anything else).
 * Legacy entries (no `kind` field on disk) load as `"unity"`.
 */
export type ProjectKind = "unity" | "package" | "openMcp" | "custom";

/**
 * Cached line-counter output (§7). `details` is the same 4-section
 * report the LineWalker CLI emits, suitable for appending to the app
 * logs without re-formatting.
 */
export interface LineCountStats {
  totalLines: number;
  codeFiles: number;
  ignoredFiles: number;
  skippedDirs: number;
  /** ISO-8601 scan timestamp; shown in the settings popup so the user
   *  knows how stale the cached number is. */
  scannedAt: string;
  /** Human-readable report (extensions counted/ignored, skipped dirs,
   *  `.gitignore` respected). Surfaced to app logs by "Run line count". */
  details: string;
}

export interface ProjectEntry {
  id: string;
  name: string;
  path: string;
  /** Multi-type discriminator. Legacy entries (omitted on disk)
   *  deserialize as `"unity"` so the hub stays backwards compatible. */
  kind?: ProjectKind;
  /** For package/openMcp kinds: relative path to the manifest from the
   *  project root (`package.json`). `undefined` for unity/custom. */
  packageManifestPath?: string;
  /** Per-package saved source folder for the "Migrate" feature in the
   *  Package settings popup; persisted across sessions. */
  migrateSourceFolder?: string;
  /** Cached line-counter output (§7). `undefined` until first run. */
  lineCountStats?: LineCountStats;
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
  /**
   * M15 T6.4: cached render-pipeline label read from
   * `ProjectSettings/GraphicsSettings.asset`. One of `"URP"`,
   * `"HDRP"`, `"BIRP"` (Built-in Render Pipeline). `undefined` for
   * legacy entries and projects that have not been refreshed since
   * the field was added. The Projects tab renders a chip from this
   * label.
   */
  renderPipeline?: string;
  /**
   * M15 T6.4: cached default build target read from
   * `ProjectSettings/ProjectSettings.asset` (`m_BuildTarget` /
   * `m_BuildTargetGroup`). `undefined` for projects that have never
   * been opened by a Unity Editor (Unity writes the keys on first
   * save).
   */
  defaultBuildTarget?: string;
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
  /**
   * M15 T6.4: build targets the editor can produce, scanned from
   * `Data/PlaybackEngines/`. Friendly names (`Android`, `WebGL`,
   * `Win64`, `iOS`, …). Empty for installs that ship without a
   * PlaybackEngines folder (custom builds, source builds). Mirrors
   * the Rust `#[serde(default)]` so older cache payloads still load.
   */
  platforms?: string[];
  /**
   * M15 T6.4: release stream inferred from the version suffix —
   * `"LTS"`, `"TECH"`, `"Beta"`, `"Alpha"`, or `""` for unknown.
   * Mirrors the Rust `#[serde(default)]` so older cache payloads
   * still load.
   */
  releaseType?: string;
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

/**
 * M15 T6.4: candidate project surfaced by a live, read-only scan of
 * Unity Hub's `projects-v1.json`. Mirrors the Rust
 * `seed::HubProjectCandidate`. The frontend never persists these
 * directly — each candidate the user accepts goes through `addProject`,
 * which produces a real `ProjectEntry` and writes it to `projects.json`.
 */
export interface HubProjectCandidate {
  name: string;
  path: string;
  exists: boolean;
  unityVersion?: string;
  lastModifiedAt?: string;
  renderPipeline?: string;
  defaultBuildTarget?: string;
  /** `true` when the candidate's path already matches an entry in
   *  `projects.json` — the UI renders these as "tracked" and hides
   *  the import button. */
  alreadyTracked: boolean;
}

export interface HubCandidatesResult {
  candidates: HubProjectCandidate[];
  /** `undefined` when the scan succeeded (even with zero candidates).
   *  Present when Unity Hub's data directory or projects file could
   *  not be read — the UI surfaces the message inline. */
  error?: string;
}

/**
 * Live, read-only scan of Unity Hub's recent-projects list. Returns
 * the candidate list (Hub JSON entries merged with the current
 * `projects.json` so `alreadyTracked` is correct). Does not mutate
 * `projects.json` — the user picks which candidates to import via
 * `addProject`.
 */
export async function discoverHubProjects(): Promise<HubCandidatesResult> {
  return invoke<HubCandidatesResult>("discover_hub_projects");
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
 * M15 T6.4: render pipeline read from
 * `ProjectSettings/GraphicsSettings.asset`. Mirrors the Rust
 * `render_pipeline::RenderPipeline` enum (camelCase serialisation).
 * - `"builtIn"` — Built-in Render Pipeline (the Unity default before
 *   any SRP asset is assigned).
 * - `"urp"` — Universal Render Pipeline.
 * - `"hdrp"` — High Definition Render Pipeline.
 * - `"unknown"` — file present but no recognisable marker.
 */
export type RenderPipelineKind = "builtIn" | "urp" | "hdrp" | "unknown";

export async function getRenderPipeline(
  projectPath: string
): Promise<RenderPipelineKind> {
  return invoke<RenderPipelineKind>("get_render_pipeline", { projectPath });
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

/**
 * Multi-type: read-only git status (branch + ahead/behind + pending
 * file list) for the git popup. Shells out to the `git` CLI from
 * Rust; the popup never mutates the repo (no pull/commit/stage).
 */
export interface GitPendingFile {
  path: string;
  status: string;
  staged: boolean;
  renameFrom?: string;
}

export interface GitStatus {
  branch?: string;
  ahead: number;
  behind: number;
  noUpstream: boolean;
  pending: GitPendingFile[];
}

export type GitStatusError =
  | { type: "notARepo"; path: string }
  | { type: "gitMissingBinary" }
  | { type: "gitFailed"; message: string };

export async function gitStatus(projectPath: string): Promise<GitStatus> {
  return invoke<GitStatus>("git_status", { projectPath });
}

/**
 * Multi-type: line counter (port of LineWalker). `countLines` is the
 * manual "Run line count" button — always runs a full scan and
 * caches the result on the project entry. `countLinesCached` is the
 * git-popup auto-calc path — returns the cached result, or runs a
 * fresh scan only when the project is below the size threshold.
 */
export interface LineCountScanResult {
  totalLines: number;
  codeFiles: { relPath: string; ext: string; lines: number }[];
  ignoredFiles: { relPath: string; reason: string }[];
  skippedDirs: string[];
  readErrors: string[];
}

export interface CountLinesResult {
  scan: LineCountScanResult;
  report: string;
  stats: LineCountStats;
}

export async function countLines(
  projectId: string,
  useGitignore?: boolean
): Promise<CountLinesResult> {
  return invoke<CountLinesResult>("count_lines", {
    projectId,
    useGitignore: useGitignore ?? true,
  });
}

export async function countLinesCached(
  projectId: string
): Promise<LineCountStats | null> {
  return invoke<LineCountStats | null>("count_lines_cached", { projectId });
}

/**
 * Multi-type (Package): UPM manifest read/write + .meta operations +
 * migrate. Ports the relevant pieces of UPM-Template-Creator.
 */
export interface PackageManifestAuthor {
  name?: string;
  email?: string;
  url?: string;
}

export interface PackageManifestSample {
  displayName?: string;
  description?: string;
  path?: string;
}

export interface PackageManifest {
  name?: string;
  version?: string;
  displayName?: string;
  description?: string;
  unity?: string;
  unityRelease?: string;
  keywords?: string[];
  author?: PackageManifestAuthor;
  dependencies?: Record<string, string>;
  samples?: PackageManifestSample[];
  hideInEditor?: boolean;
  type?: string;
  documentationUrl?: string;
  changelogUrl?: string;
  licensesUrl?: string;
}

export type PackageManifestError =
  | { type: "notFound"; path: string }
  | { type: "parseFailed"; path: string; message: string }
  | { type: "writeFailed"; path: string; message: string }
  | { type: "projectNotFound"; projectId: string }
  | { type: "persistFailed"; message: string };

export async function readPackageManifest(
  projectId: string
): Promise<PackageManifest> {
  return invoke<PackageManifest>("read_package_manifest", { projectId });
}

export async function writePackageManifest(
  projectId: string,
  manifest: PackageManifest,
  previousVersion?: string,
  bumpChangelog?: boolean,
  changelogLabel?: string
): Promise<PackageManifest> {
  return invoke<PackageManifest>("write_package_manifest", {
    projectId,
    manifest,
    previousVersion,
    bumpChangelog,
    changelogLabel,
  });
}

export interface MetaOperationResult {
  regenerated: number;
  added: number;
  notes: string[];
}

export async function regeneratePackageMetaGuids(
  projectId: string
): Promise<MetaOperationResult> {
  return invoke<MetaOperationResult>("regenerate_package_meta_guids", { projectId });
}

export async function addMissingPackageMeta(
  projectId: string
): Promise<MetaOperationResult> {
  return invoke<MetaOperationResult>("add_missing_package_meta", { projectId });
}

export interface MigrateEntry {
  relPath: string;
  action: string;
}

export interface MigrateResult {
  entries: MigrateEntry[];
  copied: number;
  replaced: number;
  savedSourceFolder: string;
}

export type MigrateError =
  | { type: "projectNotFound"; projectId: string }
  | { type: "sourceNotADirectory"; path: string }
  | { type: "persistFailed"; message: string }
  | { type: "ioFailed"; message: string };

export async function migratePackageFiles(
  projectId: string,
  sourceFolder: string
): Promise<MigrateResult> {
  return invoke<MigrateResult>("migrate_package_files", { projectId, sourceFolder });
}

/**
 * Multi-type (Open-MCP): long-running command runner. Spawns an npm
 * process (build / test / version / publish / custom) in the npm-resolved
 * cwd (`mcp-server/` for Open-MCP projects, the project root otherwise),
 * streams stdout/stderr line-by-line via `cmd-log` events, and tracks the
 * PID so it can be stopped (process-group kill). Ports vibe-launcher's
 * process pattern.
 */
export type CommandRunnerError =
  | { type: "spawnFailed"; message: string }
  | { type: "alreadyRunning"; projectId: string; panel: string };

/**
 * Panels the command runner tracks per project. The npm-maintainer panels
 * (`version`, `publishDryRun`, `publish`) are surfaced in the Open-MCP
 * settings popup; build/test/custom predate them.
 */
export type CommandPanel =
  | "build"
  | "test"
  | "custom"
  | "version"
  | "publishDryRun"
  | "publish";

/**
 * Read-only package identity for the maintainer panel. Mirrors the Rust
 * `McpPackageInfo`. `name` + `version` come from the `package.json` at
 * the npm-resolved cwd; `manifestPath` is the absolute path read.
 */
export interface McpPackageInfo {
  name: string;
  version: string;
  manifestPath: string;
}

export type McpPackageInfoError =
  | { type: "notFound"; path: string }
  | { type: "parseFailed"; path: string; message: string };

/**
 * Best-effort npm registry query result. Mirrors the Rust
 * `NpmRegistryInfo`. Every field is optional — a missing value with a
 * populated `*Error` is a normal outcome (offline host, unpublished
 * name, or not-logged-in maintainer). The panel surfaces errors inline
 * rather than blocking the publish flow.
 */
export interface NpmRegistryInfo {
  publishedVersion?: string | null;
  whoami?: string | null;
  viewError?: string;
  whoamiError?: string;
}

export async function runProjectBuild(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
): Promise<void> {
  return invoke<void>("run_project_build", { projectId, projectPath, kind });
}

export async function runProjectTest(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
): Promise<void> {
  return invoke<void>("run_project_test", { projectId, projectPath, kind });
}

export async function runProjectCustom(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
  args: string[],
): Promise<void> {
  return invoke<void>("run_project_custom", { projectId, projectPath, kind, args });
}

/**
 * Bump the package version (`patch` | `minor` | `major`) in the
 * npm-resolved cwd. `--no-git-tag-version` keeps the bump local — the
 * Hub never creates git tags.
 */
export async function runProjectNpmVersion(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
  level: "patch" | "minor" | "major",
): Promise<void> {
  return invoke<void>("run_project_npm_version", {
    projectId,
    projectPath,
    kind,
    level,
  });
}

/** `npm publish --dry-run --access public` — preflight, safe without confirm. */
export async function runProjectNpmPublishDryRun(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
): Promise<void> {
  return invoke<void>("run_project_npm_publish_dry_run", {
    projectId,
    projectPath,
    kind,
  });
}

/** Real publish — mutating; the caller must confirm first. */
export async function runProjectNpmPublish(
  projectId: string,
  projectPath: string,
  kind: ProjectKind,
): Promise<void> {
  return invoke<void>("run_project_npm_publish", {
    projectId,
    projectPath,
    kind,
  });
}

/** Read `name` + `version` from the package.json at the npm-resolved cwd. */
export async function readMcpPackageInfo(
  projectPath: string,
  kind: ProjectKind,
): Promise<McpPackageInfo> {
  return invoke<McpPackageInfo>("read_mcp_package_info", { projectPath, kind });
}

/** Best-effort `npm view <name> version` + `npm whoami`. */
export async function queryNpmRegistry(
  projectPath: string,
  kind: ProjectKind,
): Promise<NpmRegistryInfo> {
  return invoke<NpmRegistryInfo>("query_npm_registry", { projectPath, kind });
}

export async function stopProjectCommand(
  projectId: string,
  panel: CommandPanel
): Promise<void> {
  return invoke<void>("stop_project_command", { projectId, panel });
}

export async function projectCommandRunning(
  projectId: string,
  panel: CommandPanel
): Promise<boolean> {
  return invoke<boolean>("project_command_running", { projectId, panel });
}

/**
 * Multi-type (New project flow): scaffolds a new UPM package on disk
 * and registers it as a tracked `Package` project.
 */
export interface CreatePackageParams {
  parent: string;
  name: string;
  version?: string;
  displayName?: string;
  description?: string;
  unity?: string;
  keywords?: string[];
  authorName?: string;
  authorUrl?: string;
  includeExtras?: boolean;
  overwrite?: boolean;
}

export interface CreatePackageResult {
  project: ProjectEntry;
  projects: ProjectsFile;
}

export type CreatePackageError =
  | { type: "parentNotADirectory"; path: string }
  | { type: "invalidName"; name: string; reason: string }
  | { type: "targetExists"; path: string }
  | { type: "scaffoldFailed"; message: string }
  | { type: "duplicate"; path: string }
  | { type: "persistFailed"; message: string };

export async function createPackage(
  params: CreatePackageParams
): Promise<CreatePackageResult> {
  return invoke<CreatePackageResult>("create_package", { params });
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

export type ReleaseStream = "lts" | "supported" | "tech" | "beta" | "alpha";

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
 * Unity Editor install: opens Unity Hub's install dialog for the given
 * release by firing its `unityhub://<version>/<changeset>` deep link.
 * The Hub runs the download itself (single instance, native progress
 * UI, module selection); this app does not track completion, so callers
 * should nudge the user to refresh the Installed list afterward.
 */
export async function openUnityHubInstall(
  version: string,
  changeset?: string
): Promise<void> {
  await invoke<void>("open_unity_hub_install", { version, changeset });
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
  zcodeGlobal: boolean;
  zcodeProject: boolean;
}

export interface ProjectState {
  path: string;
  name: string;
  isValidUnityProject: boolean;
  /** Raw `m_EditorVersion` from `ProjectSettings/ProjectVersion.txt`. */
  unityVersion: string | null;
  /** `true` when `unityVersion` parses to a `(major, minor)` ≥ 2022.3. */
  meetsMinUnityVersion: boolean;
  /** `true` when `unityVersion` parses to a major ≥ 6000 (Unity 6).
   * Projects that meet the minimum but not the recommended version
   * render an amber warning in the wizard — never a hard block. */
  meetsRecommendedUnityVersion: boolean;
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
  | "zcodeGlobal"
  | "zcodeProject"
  | "manual";

/**
 * How the MCP server is launched. Mirrors the Rust `McpLaunchMode`
 * (camelCase serialisation). The default (`npx`) resolves the published
 * npm package via `npx -y unity-open-mcp@latest`; `global` assumes a
 * `npm i -g unity-open-mcp` install; the two `local*` modes point at an
 * on-disk `mcp-server/dist/index.js` from a toolkit checkout. The Step 4
 * advanced override field promotes `local` to `localOverride`.
 */
export type McpLaunchModeWire = "npx" | "global" | "local" | "localOverride";

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
  /** Defaults to `"npx"`. See `McpLaunchModeWire`. */
  launchMode?: McpLaunchModeWire;
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
 * Wizard skill-copy plan / write. Mirrors the Rust
 * `mcp_config::{SkillCopyParams, SkillCopyPlan, …}` types. Targets
 * are derived from the selected MCP client via the single-source
 * manifest (`skills/client-paths.json`); the wire type carries the
 * MCP client id, not ad-hoc booleans.
 */
export type SkillCopyKind =
  /** Manifest client keys. */
  | "cursor"
  | "claude"
  | "opencode"
  | "agents";

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

/** Mirrors the Rust `McpClientId` (camelCase). */
export type McpClientWire =
  | "cursor"
  | "claudeDesktop"
  | "claudeCode"
  | "opencodeGlobal"
  | "opencodeProject"
  | "zcodeGlobal"
  | "zcodeProject"
  | "manual";

export interface SkillCopyParamsWire {
  projectPath: string;
  toolkitRoot: string;
  mcpClient: McpClientWire;
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
