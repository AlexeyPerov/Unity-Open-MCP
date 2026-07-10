/**
 * The single contract between the ProjectsTab orchestrator and its
 * sub-components. The orchestrator builds a `ProjectsState` bag (a
 * `$derived` view-model) and a `ProjectsHandlers` bag (stable
 * closures) and passes both to every sub-component. This mirrors the
 * proven wizard pattern (`wizard/state.ts`).
 *
 * Re-exports the service/store types so sub-components import from
 * one place.
 */
export type {
  BundleStrategy,
  CreatePackageError,
  GitStatus,
  GitStatusError,
  HubCandidatesResult,
  HubProjectCandidate,
  HubTemplateEntry,
  HubTemplatesResult,
  KillUnityResult,
  LaunchError,
  LineCountStats,
  LogPaths,
  NewProjectError,
  ProjectEntry,
  ProjectKind,
  ProjectState,
  RelinkProjectError,
  RemoveProjectError,
  RenderPipeline,
  SetProjectFlagError,
  TemplateRef,
  UpgradeUnityError,
  WalkUpKinds,
} from "$lib/services/config";

export type {
  EnvVarDraft,
  RowStatus,
  StatusKind,
} from "./helpers.ts";

export type {
  NewProjectMode,
  NewTemplateKind,
} from "./constants.ts";

import type { ProjectEntry } from "$lib/services/config";
import type { RowStatus } from "./helpers.ts";
import type { NewProjectMode, NewTemplateKind } from "./constants.ts";

/**
 * Read-only reactive view-model. Every field is read-only from the
 * sub-component's perspective — mutations go through `handlers`.
 *
 * Kept as a flat bag of primitives/arrays/objects so a sub-component
 * only needs to read the fields it cares about. Fields are grouped
 * by concern via comments.
 */
export interface ProjectsState {
  // --- filters / search ---
  search: string;
  filterPreset: string;
  showHidden: boolean;
  filtered: ProjectEntry[];

  // --- path / size / git-branch loading ---
  pathExistsMap: Record<string, boolean>;
  sizeMap: Record<string, number>;
  loadingSizes: boolean;
  checkingPaths: boolean;
  logPathsMap: Record<string, import("$lib/services/config").LogPaths>;
  defaultBuildTargetMap: Record<string, string | null>;

  // --- AI detection ---
  aiDetectMap: Record<string, import("$lib/services/config").ProjectState>;

  // --- launch / running / kill ---
  launching: string | null;
  refreshingId: string | null;
  killingId: string | null;
  actionError: string | null;
  addError: string | null;
  /** True while a folder is dragged over the window (visual affordance). */
  isDragOver: boolean;

  // --- context menu / more menu ---
  contextMenu: { x: number; y: number; projectId: string } | null;
  moreMenuOpenFor: string | null;

  // --- add / remove / relink ---
  addingProject: boolean;
  refreshing: boolean;
  removingId: string | null;
  relinkingId: string | null;

  // --- hide / stale ---
  hidingId: string | null;
  markingStaleId: string | null;

  // --- walk-up scan ---
  walkUpModalOpen: boolean;
  pickingWalkUpFolder: boolean;
  walkUpKinds: import("$lib/services/config").WalkUpKinds;

  // --- hub import ---
  hubImportModalOpen: boolean;
  hubImportLoading: boolean;
  hubImportError: string | null;
  hubImportCandidates: import("$lib/services/config").HubProjectCandidate[];
  hubImportAddingPath: string | null;

  // --- upgrade modal ---
  upgradeModalProjectId: string | null;
  upgradeCandidatesList: string[];
  upgradeTargetVersion: string;
  upgradeStrategy: import("$lib/services/config").BundleStrategy;
  upgradePreviewBundle: string;
  upgradePreviewPrevBundle: string;
  upgradeLoading: boolean;
  upgradeError: string | null;

  // --- new project modal ---
  newProjectModalOpen: boolean;
  newProjectParent: string;
  newProjectName: string;
  newProjectVersion: string;
  newProjectPipeline: import("$lib/services/config").RenderPipeline;
  newProjectBundleVersion: string;
  newProjectTemplateKind: NewTemplateKind;
  newProjectHubTemplatePath: string;
  newProjectCustomTemplatePath: string;
  newProjectHubTemplates: import("$lib/services/config").HubTemplateEntry[];
  newProjectHubTemplatesAvailable: boolean;
  newProjectHubTemplatesFolder: string | null;
  newProjectError: string | null;
  newProjectCreating: boolean;
  newProjectOverwriteConfirm: string | null;
  newProjectMode: NewProjectMode;

  // --- package form fields ---
  pkgName: string;
  pkgVersion: string;
  pkgDisplayName: string;
  pkgDescription: string;
  pkgUnity: string;
  pkgKeywords: string;
  pkgAuthorName: string;
  pkgAuthorUrl: string;
  pkgIncludeExtras: boolean;

  // --- AI setup wizard ---
  aiSetupWizardProjectId: string | null;

  // --- settings popup ---
  settingsPopupFor: string | null;
  popupProject: ProjectEntry | null;
  popupDefaultBuildTarget: string | null | undefined;

  // --- git popup ---
  gitPopupFor: string | null;
  gitPopupProject: ProjectEntry | null;
  gitStatusData: import("$lib/services/config").GitStatus | null;
  gitStatusLoading: boolean;
  gitStatusError: string | null;
  gitPopupLineStats: import("$lib/services/config").LineCountStats | null;

  // --- env-var editor ---
  envVarsDraft: import("./helpers.ts").EnvVarDraft[];
  envVarsRevealed: Record<string, boolean>;
  envVarsSaving: boolean;
  envVarsError: string | null;
  envVarsInfo: string | null;

  // --- launch args / intent editor ---
  argsDrafts: Record<string, string>;
  argsErrors: Record<string, string | null>;
  savingArgsFor: string | null;
  intentDrafts: Record<string, string>;
  savingIntentFor: string | null;
  launchArgsInfoOpen: boolean;

  // --- row helpers (functions exposed as fields) ---
  /** Compute row status for a project. */
  statusFor: (project: ProjectEntry) => RowStatus;
  /** Whether a project is currently running. */
  isRunningFor: (project: ProjectEntry) => boolean;
  /** Normalize a project's kind (legacy → unity, etc.). */
  projectKindOf: (project: ProjectEntry) => import("$lib/services/config").ProjectKind;
  /** AI-ready predicate. */
  aiReadyFor: (path: string) => boolean;

  // --- misc constants exposed to markup ---
  gridTemplate: string;
  showModified: boolean;
  showGitBranch: boolean;
  aiSetupEnabled: boolean;
}

/**
 * Stable callback closures owned by the orchestrator. Sub-components
 * call these to mutate state; the orchestrator holds the authoritative
 * `$state` and performs the actual async work.
 */
export interface ProjectsHandlers {
  // filters / search
  setSearch: (v: string) => void;
  setFilterPreset: (v: string) => void;
  toggleShowHidden: () => void;
  dismissAddError: () => void;
  dismissActionError: () => void;

  // toolbar actions
  handleAddProject: () => void;
  handleRefresh: () => void;
  openNewProjectModal: () => void;
  openWalkUpModal: () => void;
  openHubImportModal: () => void;

  // project list row actions
  handleLaunch: (id: string) => void;
  openContextMenu: (e: MouseEvent, projectId: string) => void;
  openSettingsPopup: (id: string) => void;
  openAiSetupFor: (project: ProjectEntry) => void;
  openGitPopup: (id: string) => void;
  toggleMoreMenu: (id: string) => void;

  // context menu
  closeContextMenu: () => void;
  setMoreMenuOpen: (id: string | null) => void;
  handleOpenFolder: (project: ProjectEntry) => void;
  handleCopyPath: (project: ProjectEntry) => void;
  handleKillUnity: (project: ProjectEntry) => void;
  handleRelink: (project: ProjectEntry) => void;
  handleRefreshProject: (project: ProjectEntry) => void;
  handleRemove: (id: string) => void;
  handleHide: (project: ProjectEntry) => void;
  handleUnhide: (project: ProjectEntry) => void;
  handleMarkStale: (project: ProjectEntry) => void;
  handleUnmarkStale: (project: ProjectEntry) => void;
  openUpgradeModal: (project: ProjectEntry) => void;

  // predicate helpers (read state, return bool)
  canHide: (project: ProjectEntry) => boolean;
  canMarkStale: (project: ProjectEntry) => boolean;
  canUnhide: (project: ProjectEntry) => boolean;
  canUnmarkStale: (project: ProjectEntry) => boolean;
  canUpgrade: (project: ProjectEntry) => boolean;

  // walk-up modal
  closeWalkUpModal: () => void;
  startWalkUpFromModal: () => void;
  cancelWalkUpFromModal: () => void;
  handleWalkUpSelectFolder: () => void;
  lastScanSummary: () => { added: number; skipped: number; status: string } | null;

  // new project modal
  closeNewProjectModal: () => void;
  pickNewProjectParent: () => void;
  pickNewProjectCustomTemplate: () => void;
  saveCustomTemplateToSettings: (path: string) => void;
  submitNewProject: () => void;
  submitNewProjectOverwrite: () => void;
  submitNewPackage: () => void;
  submitNewPackageOverwrite: () => void;
  setNewProjectMode: (mode: NewProjectMode) => void;
  setNewProjectField: <K extends keyof NewProjectFieldArgs>(key: K, value: NewProjectFieldArgs[K]) => void;

  // hub import modal
  closeHubImportModal: () => void;
  loadHubCandidates: () => void;
  importHubCandidate: (candidate: import("$lib/services/config").HubProjectCandidate) => void;

  // upgrade modal
  closeUpgradeModal: () => void;
  submitUpgrade: () => void;
  setUpgradeTargetVersion: (v: string) => void;
  setUpgradeStrategy: (s: import("$lib/services/config").BundleStrategy) => void;

  // settings popup
  closeSettingsPopup: () => void;
  handlePopupLaunch: () => void;
  handlePopupProjectMutated: (updated: ProjectEntry) => void;

  // git popup
  closeGitPopup: () => void;
  refreshGitStatus: () => void;

  // env-var editor
  addEnvVarRow: () => void;
  removeEnvVarRow: (uid: string) => void;
  toggleEnvReveal: (uid: string) => void;
  setEnvVarDraft: (uid: string, field: "key" | "value", value: string) => void;
  saveEnvVars: () => void;

  // launch args / intent editor
  getArgsDraft: (id: string) => string;
  getIntentDraft: (id: string) => string;
  handleArgsInput: (id: string, value: string) => void;
  handleSaveArgs: (project: ProjectEntry) => void;
  handleResetArgs: (project: ProjectEntry) => void;
  handleIntentChange: (id: string, value: string) => void;
  handleSaveIntent: (project: ProjectEntry) => void;
  toggleLaunchArgsInfo: () => void;
  openLaunchArgsDocs: () => void;

  // AI setup wizard
  closeAiSetup: () => void;

  // misc
  openPath: (path: string) => Promise<void>;
}

/**
 * New-project form fields that sub-components can edit directly via
 * `setNewProjectField`. Kept narrow so the orchestrator's update
 * logic stays centralized.
 */
export interface NewProjectFieldArgs {
  newProjectParent: string;
  newProjectName: string;
  newProjectVersion: string;
  newProjectPipeline: import("$lib/services/config").RenderPipeline;
  newProjectBundleVersion: string;
  newProjectTemplateKind: NewTemplateKind;
  newProjectHubTemplatePath: string;
  newProjectCustomTemplatePath: string;
  pkgName: string;
  pkgVersion: string;
  pkgDisplayName: string;
  pkgDescription: string;
  pkgUnity: string;
  pkgKeywords: string;
  pkgAuthorName: string;
  pkgAuthorUrl: string;
  pkgIncludeExtras: boolean;
}
