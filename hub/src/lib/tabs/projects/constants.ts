/**
 * Pure constants and error-message formatters extracted from
 * ProjectsTab. No reactive or Tauri dependencies — fully testable.
 */
import type {
  AddProjectError,
  BundleStrategy,
  CreatePackageError,
  GitStatusError,
  KillUnityResult,
  LaunchError,
  NewProjectError,
  ProjectEntry,
  RelinkProjectError,
  RemoveProjectError,
  SetProjectFlagError,
  UpgradeUnityError,
} from "$lib/services/config";

export const LAUNCH_LOG_TAIL_LINES = 200;

export const BUILD_TARGETS: string[] = [
  "StandaloneWindows64",
  "StandaloneWindows",
  "StandaloneOSX",
  "StandaloneLinux64",
  "iOS",
  "Android",
  "WebGL",
  "WSAPlayer",
  "tvOS",
  "VisionOS",
];

export const BUILD_TARGET_LABELS: Record<string, string> = {
  Standalone: "Standalone (legacy)",
  StandaloneWindows64: "Windows",
  StandaloneWindows: "Windows (32-bit)",
  StandaloneOSX: "macOS",
  StandaloneOSXIntel: "macOS (Intel)",
  StandaloneLinux64: "Linux",
  iOS: "iOS",
  iPhone: "iOS",
  Android: "Android",
  WebGL: "WebGL",
  WSAPlayer: "UWP",
  MetroPlayer: "Windows Store",
  tvOS: "tvOS",
  VisionOS: "visionOS",
  Switch: "Nintendo Switch",
  PS4: "PlayStation 4",
  PS5: "PlayStation 5",
  XboxOne: "Xbox One",
  GameCoreXboxSeries: "Xbox Series X|S",
  GameCoreXboxOne: "Xbox One (GameCore)",
};

export function buildTargetLabel(target: string | null | undefined): string {
  if (!target) return "—";
  return BUILD_TARGET_LABELS[target] ?? target;
}

export function intentOptions(current: string): string[] {
  if (current && !BUILD_TARGETS.includes(current)) {
    return [current, ...BUILD_TARGETS];
  }
  return BUILD_TARGETS;
}

/** Sorted candidate Unity versions strictly higher than the project's. */
export function upgradeCandidatesFor(
  project: ProjectEntry,
  installedCandidates: string[],
): string[] {
  if (!project.unityVersion) return [];
  return installedCandidates.filter(
    (v) => v !== project.unityVersion && v > (project.unityVersion ?? ""),
  );
}

export type NewTemplateKind = "empty" | "hub-default" | "custom";
export type NewProjectMode = "project" | "package";

export const LAUNCH_ARGS_DOCS_URL =
  "https://docs.unity3d.com/Manual/CommandLineArguments.html";

export const LAUNCH_ARGS_EXAMPLES: { args: string; description: string }[] = [
  {
    args: "-batchmode -nographics -quit",
    description:
      "Run Unity headless in batch mode (no UI) and exit when done. Useful for CI / scripted builds.",
  },
  {
    args: "-logFile -",
    description:
      "Write the Editor log to stdout instead of the default log file. Handy for tailing logs in another tool.",
  },
  {
    args: "-username you@example.com -password **** -serial ****",
    description:
      "Sign in and activate a license on first launch. Only use in trusted environments — values are stored in plain text.",
  },
  {
    args: "-silent-crashes",
    description:
      "Skip the crash-recovery dialog after a hard exit. Useful for unattended runs.",
  },
];

// --- error formatters ---

export function formatLaunchError(err: LaunchError, project: ProjectEntry): string {
  switch (err.type) {
    case "projectNotFound":
      return `launch failed: project not found (${err.projectId})`;
    case "pathInvalid":
      return `launch failed: path invalid — ${err.path}`;
    case "versionMissing":
      return `launch failed: Unity version missing for ${project.name}`;
    case "installNotFound":
      return `launch failed: Unity ${err.version} is not installed`;
    case "launchFailed":
      return `launch failed: ${err.message}`;
    case "alreadyRunning":
      return `launch refused: Unity is already running for "${project.name}" (pid ${err.pid}). Terminate it first, or click "Terminate & relaunch" in the status drawer.`;
    default:
      return `launch failed: ${JSON.stringify(err)}`;
  }
}

export function formatAddProjectError(err: AddProjectError): string {
  switch (err.type) {
    case "notADirectory":
      return `not a directory — ${err.path}`;
    case "duplicate":
      return `already in list — ${err.path}`;
    case "persistFailed":
      return `failed to save: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatRelinkError(err: RelinkProjectError): string {
  switch (err.type) {
    case "projectNotFound":
      return `project not found (${err.projectId})`;
    case "notADirectory":
      return `not a directory — ${err.path}`;
    case "notAUnityProject":
      return `not a Unity project (${err.reason}) — ${err.path}`;
    case "duplicate":
      return `path already used by another project — ${err.path}`;
    case "persistFailed":
      return `failed to save: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatUpgradeError(err: UpgradeUnityError): string {
  switch (err.type) {
    case "projectNotFound":
      return `project not found (${err.projectId})`;
    case "pathInvalid":
      return `path invalid — ${err.path}`;
    case "versionNotInstalled":
      return `Unity ${err.version} is not installed on this machine`;
    case "projectVersionUnreadable":
      return `could not read or rewrite ${err.path}: ${err.reason}`;
    case "bundleVersionUnwritable":
      return `could not rewrite ${err.path}: ${err.reason}`;
    case "ioError":
      return err.message;
    case "persistFailed":
      return `Hub state update failed: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatNewProjectError(err: NewProjectError): string {
  switch (err.type) {
    case "parentNotDirectory":
      return `parent is not a folder: ${err.path}`;
    case "nameEmpty":
      return "project name cannot be empty";
    case "nameInvalid":
      return `invalid project name "${err.name}": ${err.reason}`;
    case "nameCollision":
      return `a ${err.isDirectory ? "folder" : "file"} already exists at ${err.path} — pick a new name or confirm overwrite`;
    case "versionUnknown":
      return `unknown Unity version: ${err.version}`;
    case "versionNotInstalled":
      return `Unity ${err.version} is not installed on this machine`;
    case "pipelineUnsupported":
      return `the ${err.pipeline} render pipeline is not supported by Unity ${err.version} (URP / HDRP need Unity 2019.3 or newer)`;
    case "templateNotFound":
      return `template folder not found: ${err.path}`;
    case "templateInvalid":
      return `template is not a Unity project root: ${err.reason} (${err.path})`;
    case "ioError":
      return `could not write project files: ${err.message}`;
    case "persistFailed":
      return `project was created on disk but Hub failed to register it: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatCreatePackageError(err: CreatePackageError): string {
  switch (err.type) {
    case "parentNotADirectory":
      return `parent is not a directory — ${err.path}`;
    case "invalidName":
      return `invalid package name: ${err.reason}`;
    case "targetExists":
      return `folder already exists — ${err.path}`;
    case "scaffoldFailed":
      return `scaffold failed: ${err.message}`;
    case "duplicate":
      return `already in list — ${err.path}`;
    case "persistFailed":
      return `failed to save: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatRemoveError(err: RemoveProjectError): string {
  switch (err.type) {
    case "projectNotFound":
      return `project not found (${err.projectId})`;
    case "persistFailed":
      return `failed to save: ${err.message}`;
    default:
      return `unknown error: ${JSON.stringify(err)}`;
  }
}

export function formatKillResult(result: KillUnityResult): string {
  switch (result.status) {
    case "killed":
      return `kill: terminated pid ${result.pid} — ${result.message}`;
    case "notFound":
      return `kill: pid ${result.pid} is not running (${result.message})`;
    case "accessDenied":
      return `kill: access denied for pid ${result.pid} — ${result.message}`;
    default:
      return `kill: ${JSON.stringify(result)}`;
  }
}

export function formatGitStatusError(err: GitStatusError): string {
  switch (err.type) {
    case "notARepo":
      return `not a git repository — ${err.path}`;
    case "gitMissingBinary":
      return "git is not installed or not on PATH";
    case "gitFailed":
      return `git failed: ${err.message}`;
    default:
      return `unknown git error: ${JSON.stringify(err)}`;
  }
}

/** Shared formatter for hide/unhide/mark-stale/unmark-stale errors. */
export function formatSetProjectFlagError(
  err: SetProjectFlagError,
  action: string,
): string {
  const message =
    err.type === "projectNotFound"
      ? `project not found (${err.projectId})`
      : err.type === "persistFailed"
        ? `failed to save: ${err.message}`
        : `unknown error: ${JSON.stringify(err)}`;
  return `${action} failed: ${message}`;
}

/** Validate the new-project (Unity) form. */
export function isNewProjectFormValid(
  parent: string,
  name: string,
  version: string,
  templateKind: NewTemplateKind,
  hubTemplatePath: string,
  customTemplatePath: string,
): boolean {
  if (!parent.trim()) return false;
  if (!name.trim()) return false;
  if (!version.trim()) return false;
  if (templateKind === "hub-default" && !hubTemplatePath) return false;
  if (templateKind === "custom" && !customTemplatePath) return false;
  return true;
}

/** Validate the new-package form (parent + valid UPM name). */
export function isPackageFormValid(parent: string, name: string): boolean {
  return parent.trim().length > 0 && /^[a-z0-9][a-z0-9.-]*$/.test(name.trim());
}

/**
 * URP / HDRP require Unity 2019.3 or newer (the version that shipped
 * the Scriptable Render Pipeline). Returns true when the selected
 * version supports URP/HDRP (or when no version is entered yet).
 */
export function pipelineSupportedForVersion(version: string): boolean {
  const v = version.trim();
  if (!v) return true;
  const match = v.match(/^(\d+)\.(\d+)/);
  if (!match) return true;
  const major = Number(match[1]);
  const minor = Number(match[2]);
  return major > 2019 || (major === 2019 && minor >= 3);
}

/** Resolve the selected template kind into a {@link TemplateRef}. */
export function resolveTemplate(
  kind: NewTemplateKind,
  hubTemplatePath: string,
  customTemplatePath: string,
): import("$lib/services/config").TemplateRef | null {
  if (kind === "empty") return null;
  if (kind === "hub-default") {
    if (!hubTemplatePath) return null;
    return { source: "hub-default", path: hubTemplatePath };
  }
  if (!customTemplatePath) return null;
  return { source: "custom", path: customTemplatePath };
}
