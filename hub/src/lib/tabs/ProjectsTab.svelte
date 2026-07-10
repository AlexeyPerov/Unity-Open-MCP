<script lang="ts">
  import "./projects/projects.css";
  import { onMount, onDestroy } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { runningUnityStore } from "$lib/state/running_unity.svelte";
  import { walkUpScanStore } from "$lib/state/walk_up_scan.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import { discoveryStore } from "$lib/state/discovery.svelte";
  import { activePalette } from "$lib/theme.svelte";
  import {
    addProject,
    checkPathsExists,
    createNewProject,
    detectProjectState,
    discoverHubProjects,
    envVarCollisions,
    getCrashLogPath,
    getDefaultBuildTarget,
    getGitBranches,
    getLaunchLogTail,
    getLogPaths,
    getProjectSizes,
    isPidAlive,
    killUnity,
    launchProject,
    listHubTemplates,
    refreshAllProjects,
    refreshProjectVersion,
    relinkProject,
    removeProject,
    saveProjects,
    setProjectHidden,
    setProjectStale,
    upgradeCandidates,
    upgradeUnity,
    gitStatus,
    countLinesCached,
    createPackage,
    DEFAULT_WALK_UP_KINDS,
    type AddProjectError,
    type BundleStrategy,
    type CreatePackageError,
    type GitStatus,
    type GitStatusError,
    type HubCandidatesResult,
    type HubProjectCandidate,
    type HubTemplateEntry,
    type HubTemplatesResult,
    type KillUnityResult,
    type LaunchError,
    type LineCountStats,
    type LogPaths,
    type NewProjectError,
    type ProjectEntry,
    type ProjectKind,
    type ProjectState,
    type RelinkProjectError,
    type RemoveProjectError,
    type RenderPipeline,
    type SetProjectFlagError,
    type TemplateRef,
    type UpgradeUnityError,
    type WalkUpKinds,
  } from "$lib/services/config";
  import {
    compareFrecency,
    compareLastModified,
  } from "$lib/frecency";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, openUrl } from "@tauri-apps/plugin-opener";
  import { getCurrentWebview } from "@tauri-apps/api/webview";
  import AiSetupWizard from "$lib/components/AiSetupWizard.svelte";
  import { AI_SETUP_ENABLED, MULTI_PROJECT_TYPES_ENABLED } from "$lib/features";
  import WalkUpScanModal from "./projects/WalkUpScanModal.svelte";
  import ProjectList from "./projects/ProjectList.svelte";
  import NewProjectModal from "./projects/NewProjectModal.svelte";
  import HubImportModal from "./projects/HubImportModal.svelte";
  import UpgradeModal from "./projects/UpgradeModal.svelte";
  import ContextMenu from "./projects/ContextMenu.svelte";
  import SettingsPopup from "./projects/SettingsPopup.svelte";
  import GitPopup from "./projects/GitPopup.svelte";
  import LaunchArgsInfoModal from "./projects/LaunchArgsInfoModal.svelte";
  import {
    projectKindOf as projectKindOfHelper,
    kindLabel,
    statusFor as statusForHelper,
    previewBundleFor as previewBundleForHelper,
    validateArgs as validateArgsHelper,
    isValidEnvVarDraft as isValidEnvVarDraftImported,
    type EnvVarDraft,
    type RowStatus,
  } from "./projects/helpers.ts";
  import {
    LAUNCH_LOG_TAIL_LINES,
    BUILD_TARGETS,
    BUILD_TARGET_LABELS,
    buildTargetLabel as buildTargetLabelImported,
    formatLaunchError as formatLaunchErrorImported,
    formatAddProjectError as formatAddProjectErrorImported,
    formatRelinkError as formatRelinkErrorImported,
    formatUpgradeError as formatUpgradeErrorImported,
    formatNewProjectError as formatNewProjectErrorImported,
    formatCreatePackageError as formatCreatePackageErrorImported,
    formatRemoveError as formatRemoveErrorImported,
    formatKillResult as formatKillResultImported,
    formatGitStatusError as formatGitStatusErrorImported,
    formatSetProjectFlagError,
    LAUNCH_ARGS_DOCS_URL,
    isNewProjectFormValid as isNewProjectFormValidHelper,
    isPackageFormValid as isPackageFormValidHelper,
    pipelineSupportedForVersion as pipelineSupportedForVersionHelper,
    resolveTemplate,
    upgradeCandidatesFor as upgradeCandidatesForHelper,
    intentOptions as intentOptionsImported,
    type NewTemplateKind,
    type NewProjectMode,
  } from "./projects/constants.ts";
  import {
    formatSize as formatSizeImported,
  } from "./projects/helpers.ts";
  import type { ProjectsHandlers, ProjectsState } from "./projects/state.ts";

  /**
   * Normalizes a project's `kind` to the four-value union. Legacy
   * entries (added before multi-type support) have no `kind` field on
   * disk and deserialize as `undefined`; they are always Unity
   * projects, matching the Rust default in `schemas::ProjectKind`.
   * When `MULTI_PROJECT_TYPES_ENABLED` is `false` the frontend forces
   * every row to look like Unity so the type chip stays hidden and the
   * launch/AI affordances behave as before.
   */
  function projectKindOf(project: ProjectEntry): ProjectKind {
    return projectKindOfHelper(project, MULTI_PROJECT_TYPES_ENABLED);
  }

  /**
   * Short human label for the type chip in the projects list. Kept
   * compact so the chip fits the existing column width alongside the
   * project name.
   */
  function kindLabelLocal(kind: ProjectKind): string {
    return kindLabel(kind);
  }

  type FilterPreset = "all" | "launchable" | "missingVersion" | "missingPath" | "missingOrStale" | "running";

  let search = $state("");
  let filterPreset = $state<FilterPreset>("all");
  let pathExistsMap = $state<Record<string, boolean>>({});
  let checkingPaths = $state(false);
  // AI-setup detection cache keyed by project path. Populated lazily
  // (after the first paint, one project at a time) so the per-row AI
  // button can reflect "installed + configured" without blocking the
  // list load. Not persisted — mirrors `pathExistsMap`.
  let aiDetectMap = $state<Record<string, ProjectState>>({});
  let aiDetectLoading = $state(false);
  let launching = $state<string | null>(null);
  let contextMenu = $state<{ x: number; y: number; projectId: string } | null>(null);
  let moreMenuOpenFor = $state<string | null>(null);
  let addingProject = $state(false);
  let refreshing = $state(false);
  let refreshingId = $state<string | null>(null);
  let removingId = $state<string | null>(null);
  let killingId = $state<string | null>(null);
  let addError = $state<string | null>(null);
  let actionError = $state<string | null>(null);
  let sizeMap = $state<Record<string, number>>({});
  let loadingSizes = $state(false);
  let logPathsMap = $state<Record<string, LogPaths>>({});
  let defaultBuildTargetMap = $state<Record<string, string | null>>({});
  let isDragOver = $state(false);
  let relinkingId = $state<string | null>(null);
  let walkUpModalOpen = $state(false);
  let pickingWalkUpFolder = $state(false);
  // M15 T6.4: "Import from Hub" modal — live, read-only scan of Unity
  // Hub's `projects-v1.json`. The candidate list is merged with the
  // current `projects.json` so already-tracked paths show as such;
  // the user picks which untracked candidates to import via the
  // normal `addProject` flow.
  let hubImportModalOpen = $state(false);
  let hubImportLoading = $state(false);
  let hubImportError = $state<string | null>(null);
  let hubImportCandidates = $state<HubProjectCandidate[]>([]);
  let hubImportAddingPath = $state<string | null>(null);
  // M1.5-14: Unity upgrade modal.
  let upgradeModalProjectId = $state<string | null>(null);
  let upgradeCandidatesList = $state<string[]>([]);
  let upgradeTargetVersion = $state<string>("");
  let upgradeStrategy = $state<BundleStrategy>("patch");
  let upgradePreviewBundle = $state<string>("");
  let upgradePreviewPrevBundle = $state<string>("");
  let upgradeLoading = $state(false);
  let upgradeError = $state<string | null>(null);
  // M1.5-15: Hide / Mark stale actions.
  let hidingId = $state<string | null>(null);
  let markingStaleId = $state<string | null>(null);
  // M1.5-15: when true, hidden rows are also shown in the list.
  // Defaults to true so the list shows ALL projects by default;
  // the toggle chip only appears in the toolbar once at least one
  // hidden project exists, letting the user collapse them out of
  // view. The toggle is a chip, not a setting, so session-only
  // state is sufficient.
  let showHidden = $state(true);
  // M1.5-15: row-level cache of the user's confirmed
  // "I manually upgraded" state. When the user clicks "Upgrade
  // Unity…" and the modal reports the project has already been
  // bumped past the discovered candidates, we set this flag so the
  // action is hidden until the next Refresh re-reads the version.
  // (The modal itself still updates the entry; this is purely for
  // the action visibility.)
  let newProjectModalOpen = $state(false);
  let newProjectParent = $state<string>("");
  let newProjectName = $state<string>("");
  let newProjectVersion = $state<string>("");
  let newProjectPipeline = $state<RenderPipeline>("none");
  let newProjectBundleVersion = $state<string>("0.1.0");
  type NewTemplateKind = "empty" | "hub-default" | "custom";
  let newProjectTemplateKind = $state<NewTemplateKind>("empty");
  let newProjectHubTemplatePath = $state<string>("");
  let newProjectCustomTemplatePath = $state<string>("");
  let newProjectHubTemplates = $state<HubTemplateEntry[]>([]);
  let newProjectHubTemplatesAvailable = $state<boolean>(false);
  let newProjectHubTemplatesFolder = $state<string | null>(null);
  let newProjectError = $state<string | null>(null);
  let newProjectCreating = $state(false);
  let newProjectOverwriteConfirm = $state<string | null>(null);

  // Multi-type: the New Project modal has a Project | Package tab.
  // The Project tab is the existing Unity scaffold flow; the Package
  // tab scaffolds a UPM package (see create_package backend).
  type NewProjectMode = "project" | "package";
  let newProjectMode = $state<NewProjectMode>("project");
  // Package-tab form fields.
  let pkgName = $state("");
  let pkgVersion = $state("1.0.0");
  let pkgDisplayName = $state("");
  let pkgDescription = $state("");
  let pkgUnity = $state("2022.3");
  let pkgKeywords = $state("");
  let pkgAuthorName = $state("");
  let pkgAuthorUrl = $state("");
  let pkgIncludeExtras = $state(true);
  // M4 Plan 2 (M4-4 / M4-5): AI Setup wizard. The wizard's own state
  // lives inside `AiSetupWizard.svelte`; the Projects tab only owns
  // the "open / close" handle and the live project pointer. User-edited
  // wizard form fields persist per project in `projects.json`
  // (`aiSetupWizard`); step navigation always restarts at Step 1.
  let aiSetupWizardProjectId = $state<string | null>(null);


  // Boot-path diagnostics. Each launch invoke logs a `start` line
  // immediately and a `done` line on completion. The data points to
  // whichever invoke is slow on a given machine — the most likely
  // culprit for an intermittent ~30s post-launch "freeze" is a
  // `get_project_sizes` or `check_paths_exists` against a spun-down
  // external drive or stale network mount, where the OS filesystem
  // timeout is 15-60s. Emitting a `start` line up front means a hung
  // phase shows up as `start` with no matching `done` — unambiguous,
  // since only-on-completion logging left the slow phase invisible.
  // The drawer is collapsed by default, so this is invisible unless
  // the user opens it.
  function bootSpan(label: string): (result?: unknown) => void {
    const start = performance.now();
    S.appendDrawerLog(`[boot] ${label}: start`);
    return (result) => {
      const ms = Math.round(performance.now() - start);
      // Surface the project count where relevant so the line is useful
      // without cross-referencing the list.
      const suffix =
        result && typeof result === "object" && "length" in (result as object)
          ? ` (${(result as { length: number }).length})`
          : "";
      S.appendDrawerLog(`[boot] ${label}: ${ms}ms${suffix}`);
    };
  }

  // Lifecycle diagnostics. `mounted`/`unmounted` lines let us see if
  // ProjectsTab is destroyed and recreated during a single app session
  // (the drawer is an in-memory ring buffer, so it survives component
  // remounts but NOT a page reload — seeing both lines proves a
  // remount, not a reload). `cancelled` is hoisted so the onDestroy
  // teardown can signal the boot IIFE to bail out instead of
  // continuing to mutate store state after unmount.
  let cancelled = false;
  S.appendDrawerLog("[lifecycle] ProjectsTab mounted");
  onMount(() => {
    (async () => {
      const bootStart = performance.now();
      const doneLoad = bootSpan("projectsStore.load");
      await projectsStore.load();
      doneLoad(projectsStore.projects);
      if (cancelled) return;
      // Path existence, sizes, and git branches are independent of each
      // other — run them concurrently. The backing Tauri commands are
      // `async` + `spawn_blocking`, so they no longer serialize on the
      // webview thread and the window stays responsive while they run.
      const donePaths = bootSpan("refreshPathExistence");
      const doneSizes = bootSpan("loadSizes");
      const doneBranches = bootSpan("loadGitBranches");
      await Promise.all([
        refreshPathExistence().then((r) => donePaths(r)),
        loadSizes().then((r) => doneSizes(r)),
        loadGitBranches().then((r) => doneBranches(r)),
      ]);
      S.appendDrawerLog(
        `[boot] total onMount: ${Math.round(performance.now() - bootStart)}ms`,
      );
      // AI-setup detection is best-effort and per-project; kick it off
      // after the blocking boot so rows paint first, then each AI
      // button flips to green as its snapshot arrives.
      void refreshAiDetection();
    })();
    // Start the running-Unity polling loop. The cadence is read from
    // `settings.discovery.scanIntervalSeconds` (default 30s, M1.5-10);
    // the store internally restarts the timer when the user edits the
    // setting. The polling stops on teardown so we don't leak the
    // interval while the user is on another tab.
    // `bootSpan` logs a `start` line now and returns a `done` callback;
    // the running-Unity store invokes it once the immediate scan
    // completes, so the drawer shows the real scan duration (not ~0ms).
    const doneScanRunning = bootSpan("scanRunningUnity");
    void runningUnityStore.start(() => doneScanRunning());
    // M1.5-11: subscribe to walk-up scan progress / done events so the
    // modal can render the live counter. The store handles listener
    // re-registration safely; we only need to call `stop()` on
    // teardown so a navigating tab does not leak event handlers.
    void walkUpScanStore.start();
    window.addEventListener("click", closeContextMenu, true);
    window.addEventListener("keydown", handleGlobalKeydown, true);
    // Register the terminate-and-relaunch callback so the status drawer
    // (mounted at the app root) can request a conflict resolution
    // without importing the ProjectsTab module. Cleared on teardown.
    S.setTerminateAndRelaunchHandler((id) => terminateAndRelaunch(id));

    // Drag-and-drop a folder onto the Projects tab. Tauri's webview
    // emits a single `DragDropEvent` stream covering enter/over/leave/drop
    // for the whole window; we toggle `isDragOver` for the visual
    // affordance and process the first valid Unity project folder on
    // drop. The listener is registered only while the Projects tab is
    // mounted, so dropping files while on the Settings tab is a no-op
    // (the user lands back here via the add-folder flow or the Relink
    // action on a missing-path row).
    let unlistenDrop: (() => void) | null = null;
    (async () => {
      try {
        unlistenDrop = await getCurrentWebview().onDragDropEvent((event) => {
          if (cancelled) return;
          switch (event.payload.type) {
            case "enter":
            case "over":
              isDragOver = true;
              break;
            case "leave":
              isDragOver = false;
              break;
            case "drop":
              isDragOver = false;
              void handleDroppedPaths(event.payload.paths);
              break;
          }
        });
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`drag-drop listener failed: ${msg}`);
      }
    })();

    return () => {
      cancelled = true;
      S.appendDrawerLog("[lifecycle] ProjectsTab unmounted");
      window.removeEventListener("click", closeContextMenu, true);
      window.removeEventListener("keydown", handleGlobalKeydown, true);
      if (unlistenDrop) unlistenDrop();
      runningUnityStore.stop();
      void walkUpScanStore.stop();
      walkUpModalOpen = false;
      // M1.5-12: force the New Project modal closed so a pending
      // submit / overwrite prompt does not leak into the next time
      // the user lands on the tab.
      newProjectModalOpen = false;
      newProjectCreating = false;
      newProjectError = null;
      newProjectOverwriteConfirm = null;
      // M4 Plan 2: close the AI Setup wizard on tab unmount so a
      // pending Step 5 verification does not leak into another tab
      // session. The wizard's internal state is local; closing the
      // modal re-mounts it next time with a fresh Step 1 (Task 3).
      aiSetupWizardProjectId = null;
      // Detach the terminate-and-relaunch callback so a stale
      // handler is not invoked after the tab is unmounted (e.g. the
      // status drawer is mounted at the app root and outlives us).
      S.setTerminateAndRelaunchHandler(null);
    };
  });

  /**
   * Bulk-resolve git branches for every project whose stored value is
   * missing (e.g. projects imported from a legacy `projects.json` that
   * pre-dates the column). The Rust resolver is bounded and async, so
   * this does not block the Projects tab. Results are written through
   * the store with a single bulk update; non-git projects resolve to
   * `null` and we store that explicitly so the column never re-probes
   * them. Per the task spec, "do not block the Projects tab on a slow
   * disk" — the read is fast (`read_to_string` on `.git/HEAD`) but the
   * UI never blocks on its completion.
   */
  async function loadGitBranches() {
    const list = projectsStore.projects;
    const pending = list.filter((p) => p.gitBranch === undefined);
    if (pending.length === 0) return;
    try {
      const paths = pending.map((p) => p.path);
      const map = await getGitBranches(paths);
      // One bulk replace keeps the sort stable; doing per-row
      // `update` calls would re-render the list once per row.
      const next = list.map((p) => {
        if (p.gitBranch !== undefined) return p;
        const resolved = map[p.path];
        // Treat `undefined` (resolver didn't return) as `null` so we
        // don't keep re-probing on every mount.
        return { ...p, gitBranch: resolved === undefined ? null : resolved };
      });
      projectsStore.replaceAll(next);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`git branch read failed: ${msg}`);
    }
  }

  $effect(() => {
    const pending = S.pendingProjectsFilter;
    if (pending) {
      filterPreset = pending;
      S.pendingProjectsFilter = null;
    }
  });

  // M1.5-15: when the user has `hideMissingByDefault` enabled in
  // Settings, default the filter to "Missing or stale" on the first
  // load. The effect runs whenever the settings object changes, so a
  // user who toggles the setting and refreshes sees the new default
  // immediately. We only apply the default when the user has not
  // picked another filter in this session — re-running on every
  // change would clobber the user's explicit selection.
  let didApplyHideMissingDefault = false;
  $effect(() => {
    if (didApplyHideMissingDefault) return;
    const hideDefault = projectsStore.settings?.projectList.hideMissingByDefault;
    if (hideDefault === true) {
      filterPreset = "missingOrStale";
      didApplyHideMissingDefault = true;
    } else if (projectsStore.settings) {
      // Mark as "applied" even when the setting is off so we do not
      // re-evaluate on every settings change.
      didApplyHideMissingDefault = true;
    }
  });

  async function loadSizes() {
    const list = projectsStore.projects;
    if (list.length === 0) return;
    loadingSizes = true;
    try {
      const paths = list.map((p) => p.path);
      sizeMap = await getProjectSizes(paths);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`size check failed: ${msg}`);
    } finally {
      loadingSizes = false;
    }
  }

  async function refreshPathExistence() {
    const list = projectsStore.projects;
    if (list.length === 0) {
      pathExistsMap = {};
      return;
    }
    checkingPaths = true;
    try {
      const paths = list.map((p) => p.path);
      pathExistsMap = await checkPathsExists(paths);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`path check failed: ${msg}`);
    } finally {
      checkingPaths = false;
    }
  }

  // `true` when the cached detection snapshot says the agent is fully
  // installed + configured for this project: valid Unity ≥ 2022.3, a
  // writable manifest, bridge + verify present, and at least one MCP
  // client configured. Step 5 bridge-verify and the optional skill are
  // runtime/optional and intentionally not required for the green state.
  function aiReadyFor(path: string): boolean {
    const d = aiDetectMap[path];
    if (!d) return false;
    if (!d.isValidUnityProject || !d.meetsMinUnityVersion || !d.manifestWritable) return false;
    if (!d.bridgeInstalled || !d.verifyInstalled) return false;
    const h = d.mcpConfigured;
    return (
      h.cursor ||
      h.claudeDesktop ||
      h.opencodeGlobal ||
      h.opencodeProject ||
      h.zcodeGlobal ||
      h.zcodeProject
    );
  }

  // Refresh AI detection for every tracked Unity project. Runs lazily
  // (yielding between calls) so the list renders first and each row's
  // button flips to green as its snapshot lands.
  async function refreshAiDetection() {
    if (aiDetectLoading) return;
    const list = projectsStore.projects.filter((p) => p.kind === "unity");
    if (list.length === 0) {
      aiDetectMap = {};
      return;
    }
    aiDetectLoading = true;
    try {
      for (const p of list) {
        try {
          const d = await detectProjectState(p.path);
          aiDetectMap = { ...aiDetectMap, [p.path]: d };
        } catch {
          // Per-project failures are non-fatal; leave whatever was cached.
        }
      }
    } finally {
      aiDetectLoading = false;
    }
  }

  async function refreshAiDetectionFor(path: string) {
    try {
      const d = await detectProjectState(path);
      aiDetectMap = { ...aiDetectMap, [path]: d };
    } catch {
      // ignore — keep prior cache entry.
    }
  }

  async function loadLogPaths(project: ProjectEntry) {
    try {
      const paths = await getLogPaths(project.path);
      logPathsMap = { ...logPathsMap, [project.id]: paths };
    } catch (_e) {
      // silently skip
    }
  }

  async function loadDefaultBuildTarget(project: ProjectEntry) {
    try {
      const info = await getDefaultBuildTarget(project.path);
      defaultBuildTargetMap = {
        ...defaultBuildTargetMap,
        [project.id]: info.target,
      };
    } catch (_e) {
      // silently skip
    }
  }

  function isRunningFor(project: ProjectEntry): boolean {
    // Match either by the `-projectPath` argument that the running
    // Unity process was launched with, or — as a fallback — by the PID
    // the row recorded for its own previous launch. The PID fallback
    // covers Unity launched with no parseable `-projectPath` (e.g. via
    // the Hub's "Open Editor" button) and any future command-line
    // changes that would defeat the path parser. See M1.5-10 acceptance
    // checklist: "A row with `lastLaunchPid === scannedPid` is
    // `running` even if the `-projectPath` argument cannot be parsed".
    if (runningUnityStore.isRunningForPath(project.path)) return true;
    if (
      project.lastLaunchPid !== undefined &&
      project.lastLaunchPid !== null &&
      project.lastLaunchPid !== 0
    ) {
      return runningUnityStore.isRunningForPid(project.lastLaunchPid);
    }
    return false;
  }

  function statusFor(project: ProjectEntry): RowStatus {
    return statusForHelper({
      project,
      pathExists: pathExistsMap[project.path],
      running: isRunningFor(project),
      kind: projectKindOf(project),
    });
  }

  let filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    const includePath = projectsStore.settings?.projectList.searchIncludesPath ?? true;
    const sortBy = projectsStore.settings?.projectList.sortBy ?? "frecency";
    // The `running` chip and the "running" filter are reactive through
    // `statusFor` → `isRunningForPath`/`isRunningForPid`, which read the
    // running-Unity store's `paths` / `byPid` (`$state`). We deliberately
    // do NOT touch `runningUnityStore.lastScanAt` here: doing so forced a
    // full filter + re-sort of every row on each 30s poll tick even when
    // the running set was unchanged. The derive now only recomputes when
    // something visible changes — a project starts/stops running (which
    // mutates `byPid`/`paths`), or a project field / filter / sort changes.
    const list = projectsStore.projects.filter((p) => {
      // M1.5-15: hidden rows are removed from the default view but
      // re-surface when the "Show hidden" toolbar chip is active. The
      // "Missing or stale" filter shows hidden rows implicitly so the
      // user can clean up: a hidden row that is also missing its
      // path still warrants a Relink, but we still drop pure-hidden
      // rows from the default `all` view.
      if (p.hidden && !showHidden && filterPreset !== "missingOrStale") {
        return false;
      }
      if (q) {
        const nameMatch = p.name.toLowerCase().includes(q);
        const pathMatch = includePath && p.path.toLowerCase().includes(q);
        if (!nameMatch && !pathMatch) return false;
      }
      const s = statusFor(p);
      switch (filterPreset) {
        case "all":
          return true;
        case "launchable":
          return s.launchable;
        case "missingVersion":
          return s.pathExists === true && !s.hasVersion && !s.stale;
        case "missingPath":
          return s.pathExists === false && !s.stale;
        case "missingOrStale":
          // Per the task spec: a single preset that surfaces
          // anything the user should clean up — missing-path rows,
          // stale rows, and hidden rows (so "Show hidden" is
          // implicit in this filter). Running / launchable rows
          // are dropped.
          return (
            s.pathExists === false ||
            s.stale ||
            !!p.hidden
          );
        case "running":
          return s.running;
        default:
          return true;
      }
    });
    // Sort the filtered list before returning. The two comparators are
    // pure (no side effects) and only depend on project fields, so the
    // `$derived` is safe to recompute on every store/settings update.
    const sorted = [...list];
    if (sortBy === "lastModified") {
      sorted.sort(compareLastModified);
    } else {
      sorted.sort(compareFrecency);
    }
    return sorted;
  });

  function closeContextMenu() {
    contextMenu = null;
  }

  function openContextMenu(e: MouseEvent, projectId: string) {
    e.preventDefault();
    contextMenu = { x: e.clientX, y: e.clientY, projectId };
    projectsStore.select(projectId);
  }

  function handleGlobalKeydown(e: KeyboardEvent) {
    if (S.activeTab !== "projects") return;
    // The confirmation modal owns Escape (and the overlay-click cancel)
    // while it is open; its own `onkeydown` on the overlay calls
    // `e.stopPropagation()` but the global handler is registered in the
    // capture phase via `addEventListener(..., true)` so the modal's
    // stopPropagation does not actually suppress us. Bail out explicitly
    // so this handler cannot close anything underneath the modal.
    if (S.showConfirmationModal) return;
    const target = e.target as HTMLElement | null;
    if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable)) {
      if (e.key !== "Escape") return;
    }
    if (contextMenu && e.key === "Escape") {
      closeContextMenu();
      return;
    }
    if (settingsPopupFor && e.key === "Escape") {
      closeSettingsPopup();
      return;
    }
    if (launchArgsInfoOpen && e.key === "Escape") {
      launchArgsInfoOpen = false;
      return;
    }
  }

  async function handleLaunch(id: string) {
    if (launching) return;
    const project = projectsStore.find(id);
    if (!project) return;
    // Multi-type: only Unity projects are launchable. The row for other
    // kinds is not wired to call this handler, but the early-return
    // here is the authoritative guard so a stray click (e.g. keyboard
    // activation) cannot try to spawn a Unity editor against a package
    // or arbitrary folder.
    if (projectKindOf(project) !== "unity") return;
    const status = statusFor(project);
    if (status.pathExists === false) {
      S.appendErrorLog(`cannot launch: path missing — ${project.path}`);
      return;
    }
    // M1.5-10 follow-up: refuse to spawn a second Unity for a project
    // whose editor is already running. The backend has the authoritative
    // check (`is_already_running` in `config::launch`), but we also
    // consult the running-Unity store here for a snappy UX (no
    // round-trip) and to surface the running PID in the error message.
    // The store polls every `scanIntervalSeconds`; if the snapshot is
    // stale the backend will still catch it.
    if (isRunningFor(project)) {
      const pid = project.lastLaunchPid ?? 0;
      // Treat the "already running" precondition as an informational
      // notice, not an error: surface a calm blue card (no red launch-
      // failed chrome, no on-disk launch log tail dump) with an optional
      // "Terminate & relaunch" quick action. Append a single calm log
      // line via `appendDrawerLog` (not `appendErrorLog`) so the drawer
      // is not force-expanded.
      const notice = pid > 0
        ? `Unity is already running for "${project.name}" (pid ${pid}).`
        : `Unity is already running for "${project.name}".`;
      S.appendDrawerLog(notice);
      S.setLaunchInfoNotice({
        projectId: project.id,
        projectName: project.name,
        message: notice,
        conflictPid: pid > 0 ? pid : null,
        timestamp: new Date().toISOString(),
      });
      return;
    }
    // M1.5-17: when the project has env vars and the safety toggle is
    // on, list the keys that would override a parent-process variable
    // and ask the user to confirm. The toggle defaults to on; the
    // backend command is a pure non-mutating lookup so the prompt is
    // cheap and synchronous from the user's perspective.
    const confirmEnvVars =
      projectsStore.settings?.safety.confirmEnvVarOverride ?? true;
    const hasEnvVars =
      !!project.envVars && Object.keys(project.envVars).length > 0;
    if (confirmEnvVars && hasEnvVars) {
      try {
        const collisions = await envVarCollisions(project.id);
        if (collisions.length > 0) {
          const ok = await S.confirm(
            "Override environment variables?",
            `${project.name} defines env vars that will override variables in the parent process:\n\n${collisions.map((k) => `  • ${k}`).join("\n")}\n\nThe spawned Unity will use the project-level values for these keys. Continue?`,
          );
          if (!ok) return;
        }
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(
          `failed to check env-var collisions: ${msg} (proceeding without confirmation)`,
        );
      }
    }
    launching = id;
    try {
      const result = await launchProject(id, activePalette());
      const updated: ProjectEntry = {
        ...project,
        lastLaunchPid: result.pid,
        unityVersion: result.unityVersion ?? project.unityVersion,
      };
      await projectsStore.update(updated);
      S.appendDrawerLog(
        `launched ${project.name} (pid ${result.pid}, ${result.unityVersion ?? "version unknown"})`
      );
      // A successful launch must never auto-open the drawer.
      S.clearLastLaunchFailure();
      S.clearLaunchInfoNotice();
    } catch (e) {
      const err = e as LaunchError;
      // The backend double-launch guard returns `alreadyRunning` when a
      // Unity process for this project is already alive. Like the
      // frontend pre-check, treat this as an informational notice, not a
      // launch failure: route to the calm blue card and skip
      // `handleLaunchFailure` entirely so the on-disk launch log tail is
      // not dumped into the drawer.
      if (err.type === "alreadyRunning") {
        const notice = `Unity is already running for "${project.name}" (pid ${err.pid}).`;
        S.appendDrawerLog(notice);
        S.setLaunchInfoNotice({
          projectId: project.id,
          projectName: project.name,
          message: notice,
          conflictPid: err.pid,
          timestamp: new Date().toISOString(),
        });
      } else {
        const message = formatLaunchError(err, project);
        const autoOpen =
          projectsStore.settings?.diagnostics.autoOpenDrawerOnLaunchFailure ?? true;
        S.appendLaunchLog(message, autoOpen);
        await handleLaunchFailure(project, err, message, autoOpen, null);
      }
    } finally {
      launching = null;
    }
  }

  async function handleLaunchFailure(
    project: ProjectEntry,
    err: LaunchError,
    message: string,
    autoOpen: boolean,
    conflictPid: number | null = null,
  ): Promise<void> {
    // Tail the on-disk launch log and push its lines into the in-memory
    // drawer so the user sees the persistent record without clicking
    // anything. We do this regardless of `autoOpen` so the data is in the
    // stream either way.
    let tailPath = "";
    try {
      const tail = await getLaunchLogTail(LAUNCH_LOG_TAIL_LINES);
      tailPath = tail.path;
      if (tail.content && tail.content.length > 0) {
        const lines = tail.content.split("\n");
        S.appendLaunchLog(
          `--- last ${lines.length} launch log record(s) for ${project.name} ---`,
          autoOpen,
        );
        for (const line of lines) {
          S.appendLaunchLog(line, autoOpen);
        }
        S.appendLaunchLog("--- end launch log tail ---", autoOpen);
      } else {
        S.appendLaunchLog(
          `launch log not yet written for ${project.name} (file: ${tail.path || "<unknown>"})`,
          autoOpen,
        );
      }
    } catch (tailErr) {
      const msg = tailErr instanceof Error ? tailErr.message : String(tailErr);
      S.appendErrorLog(`failed to read launch log: ${msg}`);
    }

    // If the failure is a Unity spawn failure, surface a quick-action that
    // opens the platform crash-log folder.
    let crashLogPath: string | null = null;
    if (err.type === "launchFailed") {
      try {
        crashLogPath = await getCrashLogPath();
      } catch (crashErr) {
        // Non-fatal: just skip the crash-button.
        crashLogPath = null;
        void crashErr;
      }
    }

    S.setLastLaunchFailure({
      projectId: project.id,
      projectName: project.name,
      projectPath: project.path,
      timestamp: new Date().toISOString(),
      isLikelyCrash: err.type === "launchFailed",
      launchLogPath: tailPath,
      crashLogPath,
      conflictPid,
    });

    // The original `message` was already added via `appendLaunchLog`; this
    // is a no-op in normal flow but keeps the helper self-contained for
    // future extension.
    void message;
  }

  /**
   * Terminate the Unity process that blocked a launch (the PID recorded
   * on the latest `LastLaunchFailure.conflictPid` or
   * `LaunchInfoNotice.conflictPid`), wait for the OS to actually reap
   * it, then retry the launch. Used by the status drawer's
   * "Terminate & relaunch" quick action — invoked from both the red
   * failure card (real spawn errors that happened to carry a conflict
   * pid) and the calm blue "already running" notice.
   *
   * The kill reuses the existing `killUnity` Rust command (which
   * handles the SIGTERM → SIGKILL escalation) and polls the running-
   * Unity store for a cleared snapshot before re-launching. The backend
   * double-launch guard is the final safety net — if the polling misses
   * the process exit, the second `launchProject` call will return
   * `alreadyRunning` and the user gets the same calm notice again.
   */
  async function terminateAndRelaunch(projectId: string): Promise<void> {
    // The conflict PID may live on either surface: the calm
    // `launchInfoNotice` (the normal "already running" path) or the red
    // `lastLaunchFailure` (kept for any non-alreadyRunning failure that
    // still happens to carry a conflict pid). Prefer the notice.
    const notice = S.launchInfoNotice;
    const failure = S.lastLaunchFailure;
    const source = notice && notice.projectId === projectId
      ? notice
      : failure && failure.projectId === projectId
        ? failure
        : null;
    if (!source) return;
    const pid = source.conflictPid ?? 0;
    const project = projectsStore.find(projectId);
    if (!project) return;
    if (pid <= 0) {
      // No PID to terminate (the conflict came from a different tool
      // that the PID-fallback could not match). Fall back to refreshing
      // the running-Unity snapshot and asking the user to terminate
      // manually if the process is still there.
      await runningUnityStore.tick();
      S.appendDrawerLog(
        `cannot auto-terminate: no PID recorded for ${project.name}; open the project menu to terminate manually.`,
      );
      return;
    }
    S.appendDrawerLog(`terminating pid ${pid} for ${project.name}…`);
    try {
      const result = await killUnity(pid);
      if (result.status === "accessDenied") {
        S.appendErrorLog(`kill: access denied for pid ${pid} — ${result.message}`);
        return;
      }
      if (result.status === "notFound") {
        // The process already exited between the guard and the kill —
        // not an error, just clear the banner and proceed to launch.
        S.appendDrawerLog(`pid ${pid} is not running (${result.message}); proceeding to relaunch.`);
      } else {
        S.appendDrawerLog(`terminated pid ${pid} — ${result.message}`);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`terminate failed: ${msg}`);
      return;
    }
    // Wait briefly for the OS to actually reap the process and for the
    // running-Unity store to reflect it on the next tick. A short
    // explicit tick is the fastest path; cap the wait so a stuck OS
    // reaper does not hang the UI.
    let cleared = false;
    for (let attempt = 0; attempt < 6; attempt++) {
      await runningUnityStore.tick();
      if (!runningUnityStore.isRunningForPid(pid)) {
        cleared = true;
        break;
      }
      await new Promise((r) => setTimeout(r, 250));
    }
    if (!cleared) {
      S.appendDrawerLog(
        `pid ${pid} may still be alive; the backend guard will refuse the relaunch if so.`,
      );
    }
    S.clearLastLaunchFailure();
    S.clearLaunchInfoNotice();
    await handleLaunch(projectId);
  }

  function formatLaunchError(err: LaunchError, project: ProjectEntry): string {
    return formatLaunchErrorImported(err, project);
  }

  async function handleAddProject() {
    if (addingProject) return;
    addingProject = true;
    addError = null;
    try {
      const selected = await openDialog({
        directory: true,
        multiple: false,
        title: "Select folder",
      });
      if (!selected || typeof selected !== "string") {
        return;
      }
      try {
        const result = await addProject(selected);
        projectsStore.add(result.project);
        await refreshPathExistence();
        await loadSizes();
        // Multi-type: log the detected kind so the drawer confirms
        // how the folder was classified (Unity / Package / Open-MCP /
        // Custom). Unity rows keep the version line; other kinds
        // surface the kind label instead.
        const kind = projectKindOf(result.project);
        if (kind === "unity") {
          S.appendDrawerLog(
            `added project ${result.project.name} (${result.project.unityVersion ?? "version unknown"})`
          );
        } else {
          S.appendDrawerLog(
            `added ${kindLabel(kind).toLowerCase()} ${result.project.name}`
          );
        }
      } catch (e) {
        const err = e as AddProjectError;
        addError = formatAddProjectError(err);
        S.appendErrorLog(`add project failed: ${addError}`);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`folder picker failed: ${msg}`);
    } finally {
      addingProject = false;
    }
  }

  function formatAddProjectError(err: AddProjectError): string {
    return formatAddProjectErrorImported(err);
  }

  function formatRelinkError(err: RelinkProjectError): string {
    return formatRelinkErrorImported(err);
  }

  /**
   * Relink a `pathMissing` row to a new folder. The folder picker is
   * shown with `directory: true` and `multiple: false`; on selection we
   * call the Rust `relink_project` command, which validates the
   * directory, refreshes the Unity version, and bumps `lastModifiedAt`
   * to now. The frontend then refreshes the in-memory path-existence
   * map so the missing-path chip disappears on the next render.
   *
   * Cancel (no folder selected) returns the row unchanged. Failed
   * relinks (invalid folder) do not modify the project entry; the
   * inline error keeps the user on the row.
   */
  async function handleRelink(project: ProjectEntry) {
    if (relinkingId) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    let selected: string | string[] | null = null;
    try {
      selected = await openDialog({
        directory: true,
        multiple: false,
        title: `Relink "${project.name}" to a Unity project folder`,
      });
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `folder picker failed: ${msg}`;
      S.appendErrorLog(actionError);
      return;
    }
    if (!selected || typeof selected !== "string") {
      return;
    }
    relinkingId = project.id;
    actionError = null;
    try {
      const updated = await relinkProject(project.id, selected);
      // Replace the in-memory entry; the store's `update` persists
      // through to the same `projects.json` the Rust command already
      // wrote, but keeping the two paths in sync prevents the UI from
      // showing stale fields if the persistence layer ever drifts.
      await projectsStore.update(updated);
      // Re-check existence for the new path and the (now stale) old
      // path so the missing chip clears on the next render.
      try {
        const map = await checkPathsExists([updated.path, project.path]);
        pathExistsMap = { ...pathExistsMap, ...map };
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`path recheck failed: ${msg}`);
      }
      S.appendDrawerLog(
        `relinked ${project.name} → ${updated.path} (${updated.unityVersion ?? "version unknown"})`
      );
    } catch (e) {
      const err = e as RelinkProjectError;
      const message = formatRelinkError(err);
      actionError = `relink failed: ${message}`;
      S.appendErrorLog(`relink failed: ${message}`);
    } finally {
      relinkingId = null;
    }
  }

  /**
   * M1.5-14 — open the Unity upgrade modal. We pull the candidate
   * version list from the Rust cache (the same source the launch
   * resolver uses) so the modal is consistent with what the launch
   * button would actually pick. The Rust helper already filters to
   * strictly-higher versions per the lexicographic comparator the
   * discovery service uses.
   */
  async function openUpgradeModal(project: ProjectEntry) {
    if (upgradeLoading) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    upgradeModalProjectId = project.id;
    upgradeError = null;
    upgradeStrategy = "patch";
    upgradeTargetVersion = "";
    upgradePreviewBundle = "";
    upgradePreviewPrevBundle = "";
    // Refresh the discovery cache so the modal sees the latest set
    // of installed versions (the cache is small and the call is
    // idempotent — repeated clicks are cheap).
    void discoveryStore.refresh();
    upgradeLoading = true;
    try {
      const candidates = await upgradeCandidates(project.id);
      upgradeCandidatesList = candidates;
      if (candidates.length > 0) {
        upgradeTargetVersion = candidates[0];
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      upgradeError = `could not load upgrade candidates: ${msg}`;
      S.appendErrorLog(`upgrade candidates failed: ${msg}`);
    } finally {
      upgradeLoading = false;
    }
  }

  function closeUpgradeModal() {
    if (upgradeLoading) return;
    upgradeModalProjectId = null;
    upgradeError = null;
  }

  function formatUpgradeError(err: UpgradeUnityError): string {
    return formatUpgradeErrorImported(err);
  }

  async function submitUpgrade() {
    const projectId = upgradeModalProjectId;
    if (!projectId) return;
    if (upgradeLoading) return;
    const project = projectsStore.find(projectId);
    if (!project) return;
    if (!upgradeTargetVersion) {
      upgradeError = "pick a target Unity version";
      return;
    }
    upgradeError = null;
    upgradeLoading = true;
    try {
      const result = await upgradeUnity({
        projectId,
        targetVersion: upgradeTargetVersion,
        bundleStrategy: upgradeStrategy,
      });
      projectsStore.replaceAll(
        projectsStore.projects.map((p) => (p.id === result.project.id ? result.project : p))
      );
      // Force a re-probe of the path so the row's `exists` state is
      // current (the upgrade may have refreshed a project whose path
      // we hadn't probed since the last refresh).
      try {
        const map = await checkPathsExists([result.project.path]);
        pathExistsMap = { ...pathExistsMap, ...map };
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`path recheck failed: ${msg}`);
      }
      S.appendDrawerLog(
        `upgraded ${result.project.name}: Unity ${result.previousUnityVersion || "?"} → ${result.unityVersion} (bundleVersion ${result.previousBundleVersion} → ${result.bundleVersion}, ${result.bundleStrategy})`,
      );
      upgradeModalProjectId = null;
    } catch (e) {
      const err = e as UpgradeUnityError;
      const message = formatUpgradeError(err);
      upgradeError = message;
      S.appendErrorLog(`upgrade failed: ${message}`);
    } finally {
      upgradeLoading = false;
    }
  }

  /**
   * M1.5-15: soft-delete a project row. The entry stays in
   * `projects.json` with `hidden: true`; the toolbar's "Show hidden"
   * chip reveals it again. No folder operations — the project folder
   * on disk is untouched.
   */
  async function handleHide(project: ProjectEntry) {
    if (hidingId) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    hidingId = project.id;
    actionError = null;
    try {
      const updated = await setProjectHidden(project.id, true);
      await projectsStore.update(updated);
      S.appendDrawerLog(`hid ${project.name} (entry kept; use "Show hidden" in the toolbar to reveal)`);
    } catch (e) {
      const err = e as SetProjectFlagError;
      const message =
        err.type === "projectNotFound"
          ? `project not found (${err.projectId})`
          : err.type === "persistFailed"
            ? `failed to save: ${err.message}`
            : `unknown error: ${JSON.stringify(err)}`;
      actionError = `hide failed: ${message}`;
      S.appendErrorLog(`hide failed: ${message}`);
    } finally {
      hidingId = null;
    }
  }

  /**
   * M1.5-15: un-hide a previously hidden row. Reachable from the
   * context menu (the row is visible when `showHidden` is on) and
   * from the "Missing or stale" filter (which also surfaces hidden
   * rows so the user can clean up).
   */
  async function handleUnhide(project: ProjectEntry) {
    if (hidingId) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    hidingId = project.id;
    actionError = null;
    try {
      const updated = await setProjectHidden(project.id, false);
      await projectsStore.update(updated);
      S.appendDrawerLog(`unhid ${project.name}`);
    } catch (e) {
      const err = e as SetProjectFlagError;
      const message =
        err.type === "projectNotFound"
          ? `project not found (${err.projectId})`
          : err.type === "persistFailed"
            ? `failed to save: ${err.message}`
            : `unknown error: ${JSON.stringify(err)}`;
      actionError = `unhide failed: ${message}`;
      S.appendErrorLog(`unhide failed: ${message}`);
    } finally {
      hidingId = null;
    }
  }

  /**
   * M1.5-15: mark a missing-path row as `stale`. The row stays
   * visible with a `stale` chip (distinct from `missing path`),
   * Launch is disabled, and the Relink action remains reachable.
   * Re-running on a row that is already stale clears the flag
   * (toggle-style; the context menu shows "Unmark stale" in that
   * case).
   */
  async function handleMarkStale(project: ProjectEntry) {
    if (markingStaleId) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    markingStaleId = project.id;
    actionError = null;
    try {
      const updated = await setProjectStale(project.id, true);
      await projectsStore.update(updated);
      S.appendDrawerLog(`marked ${project.name} as stale (kept in list; relink to clear)`);
    } catch (e) {
      const err = e as SetProjectFlagError;
      const message =
        err.type === "projectNotFound"
          ? `project not found (${err.projectId})`
          : err.type === "persistFailed"
            ? `failed to save: ${err.message}`
            : `unknown error: ${JSON.stringify(err)}`;
      actionError = `mark stale failed: ${message}`;
      S.appendErrorLog(`mark stale failed: ${message}`);
    } finally {
      markingStaleId = null;
    }
  }

  async function handleUnmarkStale(project: ProjectEntry) {
    if (markingStaleId) return;
    closeContextMenu();
    moreMenuOpenFor = null;
    if (settingsPopupFor === project.id) closeSettingsPopup();
    markingStaleId = project.id;
    actionError = null;
    try {
      const updated = await setProjectStale(project.id, false);
      await projectsStore.update(updated);
      S.appendDrawerLog(`unmarked ${project.name} as stale`);
    } catch (e) {
      const err = e as SetProjectFlagError;
      const message =
        err.type === "projectNotFound"
          ? `project not found (${err.projectId})`
          : err.type === "persistFailed"
            ? `failed to save: ${err.message}`
            : `unknown error: ${JSON.stringify(err)}`;
      actionError = `unmark stale failed: ${message}`;
      S.appendErrorLog(`unmark stale failed: ${message}`);
    } finally {
      markingStaleId = null;
    }
  }

  /**
   * M1.5-15: a project is eligible for the "Upgrade Unity…" action
   * when its path exists on disk, it has a stored version, and at
   * least one installed version is strictly higher than the
   * project's version (per the Rust comparator the upgrade flow
   * uses). The candidates list is populated on modal open, so the
   * boolean check here is a synchronous read of the cached value
   * — the modal itself will refresh the cache if it is empty.
   */
  function upgradeCandidatesFor(project: ProjectEntry): string[] {
    return upgradeCandidatesForHelper(project, upgradeCandidatesList);
  }

  function hasUpgradeAvailable(project: ProjectEntry): boolean {
    return upgradeCandidatesForHelper(project, upgradeCandidatesList).length > 0;
  }

  /**
   * M1.5-14 (continued): compute the preview bundle version the
   * modal shows alongside the radio group. We re-derive the next
   * value on every input change so the user sees the result of
   * their strategy choice live. The Rust bump math is mirrored
   * client-side so a pure-CLI user can pick the strategy without
   * round-tripping every keystroke.
   */
  function previewBundleFor(current: string, strategy: BundleStrategy): { previous: string; next: string } {
    return previewBundleForHelper(current, strategy);
  }

  /**
   * M1.5-15: hide / mark-stale affordance visibility. A row is
   * reachable when it is missing its path (the chip in the row
   * itself is the entry point per the spec's "Missing project
   * handling UX parity"). Hidden and stale rows can also be
   * un-hidden / un-marked via the same context menu.
   */
  function canHide(project: ProjectEntry): boolean {
    const s = statusFor(project);
    return !project.hidden && s.pathExists === false;
  }
  function canMarkStale(project: ProjectEntry): boolean {
    const s = statusFor(project);
    return !project.stale && s.pathExists === false;
  }
  function canUnhide(project: ProjectEntry): boolean {
    return project.hidden === true;
  }
  function canUnmarkStale(project: ProjectEntry): boolean {
    return project.stale === true;
  }
  function canUpgrade(project: ProjectEntry): boolean {
    const s = statusFor(project);
    if (s.pathExists !== true) return false;
    if (!project.unityVersion) return false;
    if (s.stale) return false;
    // `upgradeCandidatesList` is populated when the modal opens;
    // before that, default to the discovery store's known installs
    // (same comparator as the Rust side) so the entry shows up
    // immediately after the cache has been populated.
    if (upgradeCandidatesList.length > 0) {
      return hasUpgradeAvailable(project);
    }
    const current = project.unityVersion ?? "";
    return discoveryStore.installations.some(
      (i) => i.version !== current && i.version > current,
    );
  }

  /**
   * Process a drag-and-drop payload. The Tauri webview gives us the
   * platform-resolved paths of the dragged items; the spec is:
   *
   *   - Files are rejected with an inline message (we only accept
   *     folders, matching the Add Project button).
   *   - Exactly one valid Unity project folder is added.
   *   - Multiple folders: process the first valid one and surface a
   *     short note that the rest were ignored.
   *   - Empty folder / non-Unity folder: same inline error as the
   *     Add Project button so users get consistent feedback.
   *   - Duplicate path: a brief inline message; the existing entry is
   *     preserved.
   */
  async function handleDroppedPaths(paths: string[]) {
    addError = null;
    if (!paths || paths.length === 0) return;
    // Tauri delivers the paths in the order the OS reported them; we
    // simply pick the first item and call it a "drop" for the single-
    // folder case. Files are detected by the absence of a directory
    // check — `addProject` returns a typed `notADirectory` error which
    // we surface in the dedicated file message.
    const [first, ...rest] = paths;
    try {
      const result = await addProject(first);
      projectsStore.add(result.project);
      await refreshPathExistence();
      await loadSizes();
      S.appendDrawerLog(
        `added project ${result.project.name} (${result.project.unityVersion ?? "version unknown"})`
      );
      if (rest.length > 0) {
        S.appendDrawerLog(
          `dropped ${paths.length} items; only the first valid one was added`
        );
      }
    } catch (e) {
      const err = e as AddProjectError;
      // Files (vs. folders) come back as `notADirectory` from the
      // backend; surface a friendlier message so the user knows why
      // their drag was rejected.
      if (err.type === "notADirectory") {
        addError = `only folders can be added — dropped a file: ${err.path}`;
        S.appendErrorLog(`drop ignored: only folders are accepted (${err.path})`);
        return;
      }
      const message = formatAddProjectError(err);
      addError = message;
      S.appendErrorLog(`drop failed: ${message}`);
    }
  }

  async function handleRefresh() {
    if (refreshing) return;
    refreshing = true;
    try {
      const result = await refreshAllProjects();
      projectsStore.replaceAll(result.projects.projects);
      await refreshPathExistence();
      await loadSizes();
      const updatedCount = result.updated.length;
      const skippedCount = result.skipped.length;
      S.appendDrawerLog(
        `refreshed projects (${updatedCount} updated${skippedCount ? `, ${skippedCount} skipped` : ""})`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`refresh failed: ${msg}`);
    } finally {
      refreshing = false;
    }
  }

  function formatKillResult(result: KillUnityResult): string {
    return formatKillResultImported(result);
  }

  /**
   * M1.5-11: open the walk-up scan modal. The modal reads the
   * current `settings.unityDiscovery.walkUp*` configuration and
   * lets the user start a scan against the configured roots. The
   * actual scan runs on the Rust side; we just open the modal and
   * let the user click Start.
   */
  function openWalkUpModal() {
    if (walkUpModalOpen) return;
    walkUpModalOpen = true;
  }

  function closeWalkUpModal() {
    // Close the modal only when no scan is in flight; if a scan is
    // running the user has to cancel it first (the X button is
    // hidden / disabled in that case).
    if (walkUpScanStore.scanning) return;
    walkUpModalOpen = false;
  }

  /**
   * M15 T6.4: open the "Import from Hub" modal. The modal opens
   * immediately and then fetches the live candidate list from Unity
   * Hub's `projects-v1.json`. The scan is non-mutating — the user
   * picks which untracked candidates to import via `addProject`.
   */
  async function openHubImportModal() {
    if (hubImportModalOpen) return;
    hubImportModalOpen = true;
    hubImportError = null;
    hubImportCandidates = [];
    await loadHubCandidates();
  }

  function closeHubImportModal() {
    // Block close while an `addProject` is mid-flight so the user
    // does not lose the visible "Adding…" state on the row.
    if (hubImportAddingPath) return;
    hubImportModalOpen = false;
  }

  async function loadHubCandidates() {
    hubImportLoading = true;
    hubImportError = null;
    try {
      const result: HubCandidatesResult = await discoverHubProjects();
      hubImportCandidates = result.candidates;
      if (result.error) {
        hubImportError = result.error;
      }
    } catch (e) {
      hubImportError = e instanceof Error ? e.message : String(e);
      hubImportCandidates = [];
    } finally {
      hubImportLoading = false;
    }
  }

  /**
   * Import a single Hub candidate via the normal `addProject` flow so
   * the new row gets a real `ProjectEntry` (with id, frecency,
   * renderPipeline, etc.) and is persisted to `projects.json`.
   * Re-throws nothing — errors are surfaced inline next to the row.
   */
  async function importHubCandidate(candidate: HubProjectCandidate) {
    if (hubImportAddingPath) return;
    hubImportAddingPath = candidate.path;
    try {
      await addProject(candidate.path);
      // Mark this candidate as tracked in-place so the UI updates
      // without a full reload.
      candidate.alreadyTracked = true;
      hubImportCandidates = [...hubImportCandidates];
      S.appendDrawerLog(`imported from Hub: ${candidate.name} (${candidate.path})`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`Hub import failed for ${candidate.path}: ${msg}`);
    } finally {
      hubImportAddingPath = null;
    }
  }

  async function startWalkUpFromModal() {
    const settings = settingsStore.current;
    if (!settings) {
      addError = "settings not loaded yet — try again in a moment";
      return;
    }
    const roots = settings.unityDiscovery.walkUpRoots;
    if (roots.length === 0) {
      addError =
        "no folder selected — click “Select folder” to pick a parent directory";
      return;
    }
    addError = null;
    const kinds = settings.unityDiscovery.walkUpKinds ?? DEFAULT_WALK_UP_KINDS;
    if (!kinds.unity && !kinds.package && !kinds.openMcp && !kinds.custom) {
      addError =
        "no project types selected — enable at least one type toggle above";
      return;
    }
    const result = await walkUpScanStore.begin({
      roots,
      maxDepth: settings.unityDiscovery.walkUpMaxDepth,
      followSymlinks: settings.unityDiscovery.walkUpFollowSymlinks,
      keepPartial: settings.unityDiscovery.walkUpKeepPartial,
      kinds,
    });
    if (result) {
      // Scan is running — modal stays open with the live progress.
      // The done event in the store clears `scanning` so the user
      // can close the modal.
      S.appendDrawerLog(
        `walk-up scan ${result.scanId} started (${result.roots.length} root(s), max depth ${result.maxDepth})`
      );
    } else {
      const msg = walkUpScanStore.startError;
      if (msg) {
        addError = msg;
        S.appendErrorLog(`walk-up scan failed to start: ${msg}`);
      }
    }
  }

  async function cancelWalkUpFromModal() {
    await walkUpScanStore.cancel();
  }

  /**
   * Open a folder picker and store the picked path as the single
   * walk-up scan root. Replaces any previously selected root so the
   * "Selected Folder" section always shows at most one entry.
   */
  async function handleWalkUpSelectFolder() {
    if (pickingWalkUpFolder) return;
    pickingWalkUpFolder = true;
    addError = null;
    try {
      const picked = await openDialog({
        directory: true,
        multiple: false,
        title: "Select a parent folder to scan for Unity projects",
      });
      if (!picked || typeof picked !== "string") {
        return;
      }
      const normalized = picked.replace(/[\\/]+$/, "");
      try {
        await settingsStore.setWalkUpRoots([normalized]);
        S.appendDrawerLog(
          `walk-up scan root set to ${normalized} — click “Start scan” to discover projects`
        );
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        addError = `set walk-up folder failed: ${msg}`;
        S.appendErrorLog(addError);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      addError = `folder picker failed: ${msg}`;
      S.appendErrorLog(addError);
    } finally {
      pickingWalkUpFolder = false;
    }
  }

  /**
   * M1.5-12 / M1.5-13: New project creation. The modal flow:
   *
   *   1. user clicks "New project…" in the toolbar
   *   2. we open the modal, load Hub templates + discovery (so the
   *      version dropdown is populated), and reset the form
   *   3. user fills in parent / name / version / pipeline / template
   *   4. submit calls `create_new_project`; on success we close the
   *      modal and the new project appears at the top of the list
   *      (frecency bumped to 1 so the frecency sort surfaces it
   *      immediately, per the Task 4 acceptance checklist)
   *   5. errors are surfaced inline; a name collision is non-fatal
   *      and offers an "Overwrite" affordance after confirmation
   */
  async function openNewProjectModal() {
    if (newProjectModalOpen) return;
    newProjectError = null;
    newProjectOverwriteConfirm = null;
    newProjectName = "";
    // Reuse the last-used parent if the user has picked one before;
    // otherwise leave empty so the picker is the only way to fill it.
    newProjectParent = newProjectParent || "";
    newProjectPipeline = "none";
    newProjectBundleVersion = "0.1.0";
    newProjectTemplateKind = "empty";
    newProjectHubTemplatePath = "";
    newProjectCustomTemplatePath = "";
    newProjectModalOpen = true;
    // Make sure the Unity version dropdown is populated even if the
    // user has never opened the Unity Versions tab.
    void discoveryStore.load();
    try {
      const result: HubTemplatesResult = await listHubTemplates();
      newProjectHubTemplates = result.templates;
      newProjectHubTemplatesAvailable = result.available;
      newProjectHubTemplatesFolder = result.folder ?? null;
      // Preselect the first Hub template if any are available so the
      // dropdown has a sensible default.
      if (newProjectHubTemplates.length > 0) {
        newProjectHubTemplatePath = newProjectHubTemplates[0].path;
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`list Hub templates failed: ${msg}`);
      newProjectHubTemplates = [];
      newProjectHubTemplatesAvailable = false;
      newProjectHubTemplatesFolder = null;
    }
  }

  function closeNewProjectModal() {
    if (newProjectCreating) return;
    newProjectModalOpen = false;
    newProjectError = null;
    newProjectOverwriteConfirm = null;
  }

  async function pickNewProjectParent() {
    try {
      const selected = await openDialog({
        directory: true,
        multiple: false,
        title: "Select parent folder for the new project",
      });
      if (typeof selected === "string") {
        newProjectParent = selected;
        newProjectError = null;
        newProjectOverwriteConfirm = null;
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`folder picker failed: ${msg}`);
    }
  }

  async function pickNewProjectCustomTemplate() {
    try {
      const selected = await openDialog({
        directory: true,
        multiple: false,
        title: "Select Unity project folder to use as template",
      });
      if (typeof selected === "string") {
        newProjectCustomTemplatePath = selected;
        newProjectError = null;
        newProjectOverwriteConfirm = null;
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`folder picker failed: ${msg}`);
    }
  }

  /**
   * Save a path picked from a Custom template picker into the
   * settings list. Errors are logged to the drawer; the Settings tab
   * also surfaces the inline error.
   */
  async function saveCustomTemplateToSettings(path: string) {
    try {
      await settingsStore.addCustomTemplateFolder(path);
      S.appendDrawerLog(`added custom template folder: ${path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save custom template failed: ${msg}`);
    }
  }

  function formatNewProjectError(err: NewProjectError): string {
    return formatNewProjectErrorImported(err);
  }

  function resolveNewProjectTemplate(): TemplateRef | null {
    return resolveTemplate(newProjectTemplateKind, newProjectHubTemplatePath, newProjectCustomTemplatePath);
  }

  /**
   * URP / HDRP require Unity 2019.3 or newer (the version that
   * shipped the Scriptable Render Pipeline). We grey the dropdown
   * options out so the user gets the same hint at click-time as
   * they would after submitting; the backend enforces the same rule
   * and returns `pipelineUnsupported` if the user somehow picks
   * an older version anyway.
   */
  $effect(() => {
    if (!pipelineSupportedForVersionHelper(newProjectVersion) && newProjectPipeline !== "none") {
      newProjectPipeline = "none";
    }
  });

  function isNewProjectFormValid(): boolean {
    return isNewProjectFormValidHelper(newProjectParent, newProjectName, newProjectVersion, newProjectTemplateKind, newProjectHubTemplatePath, newProjectCustomTemplatePath);
  }

  async function submitNewProject() {
    if (newProjectCreating) return;
    newProjectError = null;
    if (!isNewProjectFormValid()) {
      newProjectError = "Please fill in parent, name, and Unity version.";
      return;
    }
    newProjectCreating = true;
    try {
      const result = await createNewProject({
        parent: newProjectParent.trim(),
        name: newProjectName.trim(),
        version: newProjectVersion.trim(),
        pipeline: newProjectPipeline,
        bundleVersion: newProjectBundleVersion.trim() || "0.1.0",
        template: resolveNewProjectTemplate(),
        overwrite: false,
      });
      // Replace the in-memory list with the backend's authoritative
      // copy so a `frecency=1` re-sort and any other side effects
      // (de-duplication of a stale entry with the same path) take
      // effect immediately.
      projectsStore.replaceAll(result.projects.projects);
      projectsStore.select(result.project.id);
      S.appendDrawerLog(
        `created project ${result.project.name} (${result.project.unityVersion ?? "version unknown"}) at ${result.project.path}`
      );
      newProjectModalOpen = false;
      newProjectOverwriteConfirm = null;
      // Best-effort: refresh the row-level state the rest of the UI
      // reads (path existence / sizes / branch) so the new row
      // renders correctly without a manual Refresh.
      await refreshPathExistence();
      await loadSizes();
    } catch (e) {
      const err = e as NewProjectError;
      const message = formatNewProjectError(err);
      newProjectError = message;
      if (err.type === "nameCollision") {
        newProjectOverwriteConfirm = err.path;
      }
      S.appendErrorLog(`new project failed: ${message}`);
    } finally {
      newProjectCreating = false;
    }
  }

  async function submitNewProjectOverwrite() {
    if (newProjectCreating) return;
    newProjectError = null;
    if (!isNewProjectFormValid()) {
      newProjectError = "Please fill in parent, name, and Unity version.";
      return;
    }
    newProjectCreating = true;
    try {
      const result = await createNewProject({
        parent: newProjectParent.trim(),
        name: newProjectName.trim(),
        version: newProjectVersion.trim(),
        pipeline: newProjectPipeline,
        bundleVersion: newProjectBundleVersion.trim() || "0.1.0",
        template: resolveNewProjectTemplate(),
        overwrite: true,
      });
      projectsStore.replaceAll(result.projects.projects);
      projectsStore.select(result.project.id);
      S.appendDrawerLog(
        `replaced existing folder with ${result.project.name} (${result.project.unityVersion ?? "version unknown"}) at ${result.project.path}`
      );
      newProjectModalOpen = false;
      newProjectOverwriteConfirm = null;
      await refreshPathExistence();
      await loadSizes();
    } catch (e) {
      const err = e as NewProjectError;
      const message = formatNewProjectError(err);
      newProjectError = message;
      S.appendErrorLog(`new project (overwrite) failed: ${message}`);
    } finally {
      newProjectCreating = false;
    }
  }

  /**
   * Multi-type: scaffolds a new UPM package on disk and registers it
   * as a tracked Package project. Mirrors submitNewProject's error /
   * overwrite-confirm flow but for the Package tab.
   */
  function isPackageFormValid(): boolean {
    return isPackageFormValidHelper(newProjectParent, pkgName);
  }

  function formatCreatePackageError(err: CreatePackageError): string {
    return formatCreatePackageErrorImported(err);
  }

  async function submitNewPackage() {
    if (!isPackageFormValid() || newProjectCreating) return;
    newProjectCreating = true;
    newProjectError = null;
    newProjectOverwriteConfirm = null;
    try {
      const result = await createPackage({
        parent: newProjectParent.trim(),
        name: pkgName.trim(),
        version: pkgVersion.trim() || undefined,
        displayName: pkgDisplayName.trim() || undefined,
        description: pkgDescription.trim() || undefined,
        unity: pkgUnity.trim() || undefined,
        keywords: pkgKeywords.split(",").map((k) => k.trim()).filter((k) => k.length > 0),
        authorName: pkgAuthorName.trim() || undefined,
        authorUrl: pkgAuthorUrl.trim() || undefined,
        includeExtras: pkgIncludeExtras,
      });
      projectsStore.replaceAll(result.projects.projects);
      projectsStore.select(result.project.id);
      await refreshPathExistence();
      newProjectModalOpen = false;
      S.appendDrawerLog(`created package ${result.project.name}`);
    } catch (e) {
      const err = e as CreatePackageError;
      if (err.type === "targetExists") {
        newProjectOverwriteConfirm = `${newProjectParent.trim()}/${pkgName.trim()}`;
      }
      const message = formatCreatePackageError(err);
      newProjectError = message;
      S.appendErrorLog(`new package failed: ${message}`);
    } finally {
      newProjectCreating = false;
    }
  }

  async function submitNewPackageOverwrite() {
    if (!isPackageFormValid() || newProjectCreating) return;
    newProjectCreating = true;
    try {
      const result = await createPackage({
        parent: newProjectParent.trim(),
        name: pkgName.trim(),
        version: pkgVersion.trim() || undefined,
        displayName: pkgDisplayName.trim() || undefined,
        description: pkgDescription.trim() || undefined,
        unity: pkgUnity.trim() || undefined,
        keywords: pkgKeywords.split(",").map((k) => k.trim()).filter((k) => k.length > 0),
        authorName: pkgAuthorName.trim() || undefined,
        authorUrl: pkgAuthorUrl.trim() || undefined,
        includeExtras: pkgIncludeExtras,
        overwrite: true,
      });
      projectsStore.replaceAll(result.projects.projects);
      projectsStore.select(result.project.id);
      await refreshPathExistence();
      newProjectModalOpen = false;
      newProjectOverwriteConfirm = null;
      S.appendDrawerLog(`replaced package ${result.project.name}`);
    } catch (e) {
      const err = e as CreatePackageError;
      const message = formatCreatePackageError(err);
      newProjectError = message;
      S.appendErrorLog(`new package (overwrite) failed: ${message}`);
    } finally {
      newProjectCreating = false;
    }
  }

  /**
   * M1.5-11: summary line for the modal's "done" panel. Reads the
   * store's `lastResult` so the message survives a tab switch and
   * is available after the scan closes out. Returns null when no
   * scan has been run in this session.
   */
  function lastScanSummary(): { added: number; skipped: number; status: string } | null {
    const r = walkUpScanStore.lastResult;
    if (!r) return null;
    return {
      added: r.added.length,
      skipped: r.skippedExisting.length,
      status: r.status,
    };
  }

  async function performKill(project: ProjectEntry, pid: number) {
    if (killingId) return;
    killingId = project.id;
    actionError = null;
    try {
      const result = await killUnity(pid);
      const killMessage = formatKillResult(result);
      if (result.status === "accessDenied") {
        S.appendErrorLog(killMessage);
      } else {
        S.appendDrawerLog(killMessage);
      }
      if (result.status === "killed" || result.status === "notFound") {
        const cleared: ProjectEntry = { ...project, lastLaunchPid: undefined };
        await projectsStore.update(cleared);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `kill failed: ${msg}`;
      S.appendErrorLog(actionError);
    } finally {
      killingId = null;
    }
  }

  async function handleRefreshProject(project: ProjectEntry) {
    if (refreshingId) return;
    const status = statusFor(project);
    if (status.pathExists === false) {
      return;
    }
    refreshingId = project.id;
    actionError = null;
    try {
      const result = await refreshProjectVersion(project.id);
      try {
        const exists = await checkPathsExists([project.path]);
        pathExistsMap = { ...pathExistsMap, ...exists };
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`path recheck failed: ${msg}`);
      }
      const updated: ProjectEntry = {
        ...project,
        unityVersion: result.unityVersion ?? project.unityVersion,
        lastModifiedAt: result.lastModifiedAt ?? project.lastModifiedAt,
        gitBranch: result.gitBranch !== undefined ? result.gitBranch : project.gitBranch,
      };
      await projectsStore.update(updated);
      S.appendDrawerLog(
        `refreshed ${project.name} (${result.unityVersion ?? "version unknown"})`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `refresh failed: ${msg}`;
      S.appendErrorLog(actionError);
    } finally {
      refreshingId = null;
    }
  }

  async function handleKillUnity(project: ProjectEntry) {
    closeContextMenu();
    moreMenuOpenFor = null;
    const pid = project.lastLaunchPid;
    if (!pid) {
      actionError = `no recent Unity launch recorded for ${project.name}`;
      S.appendErrorLog(actionError);
      return;
    }
    const confirmKill = projectsStore.settings?.safety.confirmKillUnity ?? true;
    if (confirmKill) {
      const ok = await S.confirm(
        "Kill Unity for this project?",
        `Send a terminate signal to pid ${pid} (last launched from "${project.name}"). Other Unity instances on this machine are not affected.`
      );
      if (!ok) return;
    }
    await performKill(project, pid);
  }

  /**
   * Open the AI Setup wizard for a specific project (called from
   * the per-row AI button and the row context menu — "Configure
   * Agent Bridge"). The context-menu entry always operates on the
   * right-clicked row, so the two paths are explicit.
   */
  function openAiSetupFor(project: ProjectEntry) {
    if (!AI_SETUP_ENABLED) return;
    if (!project) return;
    projectsStore.select(project.id);
    aiSetupWizardProjectId = project.id;
  }

  function closeAiSetup() {
    // Capture the project before clearing the id so we can refresh its
    // AI status — the wizard may have written configs or run "Clear".
    const proj = aiSetupWizardProjectId
      ? projectsStore.find(aiSetupWizardProjectId)
      : null;
    aiSetupWizardProjectId = null;
    if (proj?.path) void refreshAiDetectionFor(proj.path);
  }

  function formatRemoveError(err: RemoveProjectError): string {
    return formatRemoveErrorImported(err);
  }

  async function performRemove(id: string) {
    if (removingId) return;
    const project = projectsStore.find(id);
    if (!project) return;
    removingId = id;
    actionError = null;
    try {
      const result = await removeProject(id);
      await projectsStore.remove(id);
      S.appendDrawerLog(
        `removed ${result.removedName} from list (folder left intact: ${result.removedPath})`
      );
    } catch (e) {
      const err = e as RemoveProjectError;
      const message = formatRemoveError(err);
      actionError = `remove failed: ${message}`;
      S.appendErrorLog(`remove failed: ${message}`);
    } finally {
      removingId = null;
    }
  }

  async function handleRemove(id: string) {
    const project = projectsStore.find(id);
    if (!project) return;
    const confirmRemove = projectsStore.settings?.safety.confirmRemoveProject ?? true;
    if (confirmRemove) {
      const ok = await S.confirm(
        "Remove project from list?",
        `"${project.name}" will be removed from the Hub project list. The project folder on disk and Unity Hub registry will not be touched.`
      );
      if (!ok) return;
    }
    closeContextMenu();
    moreMenuOpenFor = null;
    await performRemove(id);
  }

  function handleCopyPath(project: ProjectEntry) {
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(project.path).then(
        () => S.appendDrawerLog(`copied path: ${project.path}`),
        () => S.appendErrorLog("copy failed: clipboard unavailable")
      );
    }
    closeContextMenu();
  }

  async function handleOpenFolder(project: ProjectEntry) {
    closeContextMenu();
    moreMenuOpenFor = null;
    const status = statusFor(project);
    if (status.pathExists === false) {
      actionError = `cannot open folder: path missing — ${project.path}`;
      S.appendErrorLog(actionError);
      return;
    }
    try {
      await openPath(project.path);
      S.appendDrawerLog(`opened folder: ${project.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open folder failed: ${msg}`;
      S.appendErrorLog(actionError);
    }
  }

  function rowStatus(p: ProjectEntry) {
    return statusFor(p);
  }

  let showModified = $derived(projectsStore.settings?.projectList.showModifiedColumn ?? true);
  let showGitBranch = $derived(projectsStore.settings?.projectList.showGitBranchColumn ?? true);

  let gridTemplate = $derived.by(() => {
    // Name column widened ~50% (was `minmax(8rem, 1.1fr)`) so longer
    // project names fit on one line and the type chip has room to wrap
    // underneath when it still does not fit (see `.name-text` /
    // `.source-tag` CSS for the wrapping behavior).
    const name = "minmax(12rem, 1.65fr)";
    const version = "minmax(6rem, 0.9fr)";
    const modified = "minmax(5rem, 0.7fr)";
    const gitBranch = "minmax(5rem, 0.7fr)";
    const size = "minmax(4rem, 0.6fr)";
    const status = "minmax(10rem, 1.4fr)";
    const settings = AI_SETUP_ENABLED ? "5.2rem" : "2.6rem";
    if (showModified && showGitBranch) {
      return `${name} ${version} ${modified} ${gitBranch} ${size} ${status} ${settings}`;
    }
    if (showModified) {
      return `${name} ${version} ${modified} ${size} ${status} ${settings}`;
    }
    if (showGitBranch) {
      return `${name} ${version} ${gitBranch} ${size} ${status} ${settings}`;
    }
    return `${name} ${version} ${size} ${status} ${settings}`;
  });

  const filterOptions: { id: FilterPreset; label: string }[] = [
    { id: "all", label: "All" },
    { id: "launchable", label: "Launchable" },
    { id: "running", label: "Running" },
    { id: "missingVersion", label: "Missing version" },
    { id: "missingOrStale", label: "Missing or stale" },
  ];

  function formatSize(bytes: number): string {
    return formatSizeImported(bytes);
  }

  function toggleMoreMenu(id: string) {
    moreMenuOpenFor = moreMenuOpenFor === id ? null : id;
  }

  let settingsPopupFor = $state<string | null>(null);

  let popupProject = $derived(
    settingsPopupFor ? projectsStore.find(settingsPopupFor) ?? null : null
  );

  /**
   * Effective walk-up kind filter for the "Add Multiple Projects"
   * modal, with defaults filled in for legacy settings files that have
   * no `walkUpKinds` field. Defaults: Unity + Package on, Open-MCP +
   * Custom off (see `DEFAULT_WALK_UP_KINDS`).
   */
  let walkUpKinds = $derived<WalkUpKinds>({
    ...DEFAULT_WALK_UP_KINDS,
    ...settingsStore.current?.unityDiscovery.walkUpKinds,
  });

  // Multi-type: git popup state. Loaded on-demand when the branch chip
  // is clicked; the cheap `.git/HEAD` branch read still drives the
  // list-row paint. `gitPopupFor` holds the project id, `gitStatus`
  // holds the parsed result (or null while loading / on error).
  let gitPopupFor = $state<string | null>(null);
  let gitStatusData = $state<GitStatus | null>(null);
  let gitStatusLoading = $state(false);
  let gitStatusError = $state<string | null>(null);
  // Cached line-count stat for the git popup's passive display.
  let gitPopupLineStats = $state<LineCountStats | null>(null);

  let gitPopupProject = $derived(
    gitPopupFor ? projectsStore.find(gitPopupFor) ?? null : null
  );

  let popupDefaultBuildTarget = $derived(
    popupProject ? defaultBuildTargetMap[popupProject.id] : undefined
  );

  // Per-project environment variables editor — moved out of the Tools
  // tab so the user always edits the project whose settings popup is
  // open. Drafts mirror `project.envVars` and save through the same
  // atomic `saveProjects` path the rest of the project mutations use.
  type EnvVarDraft = {
    uid: string;
    key: string;
    value: string;
  };
  let envVarsDraft = $state<EnvVarDraft[]>([]);
  let envVarsRevealed = $state<Record<string, boolean>>({});
  let envVarsSaving = $state(false);
  let envVarsError = $state<string | null>(null);
  let envVarsInfo = $state<string | null>(null);
  let nextEnvDraftUid = 1;
  let envVarsSeededFor = $state<string | null>(null);

  function newEnvVarDraft(): EnvVarDraft {
    return { uid: `draft-${nextEnvDraftUid++}`, key: "", value: "" };
  }

  $effect(() => {
    const project = popupProject;
    if (!project) {
      envVarsDraft = [];
      envVarsError = null;
      envVarsInfo = null;
      envVarsSeededFor = null;
      return;
    }
    if (envVarsSeededFor === project.id) return;
    envVarsDraft = Object.entries(project.envVars ?? {})
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, value]) => ({
        uid: `seed-${nextEnvDraftUid++}`,
        key,
        value,
      }));
    envVarsError = null;
    envVarsInfo = null;
    envVarsSeededFor = project.id;
  });

  function addEnvVarRow() {
    if (!popupProject) return;
    envVarsDraft = [...envVarsDraft, newEnvVarDraft()];
  }

  function removeEnvVarRow(uid: string) {
    envVarsDraft = envVarsDraft.filter((r) => r.uid !== uid);
  }

  function toggleEnvReveal(uid: string) {
    envVarsRevealed = { ...envVarsRevealed, [uid]: !envVarsRevealed[uid] };
  }

  function isValidEnvVarDraft(rows: EnvVarDraft[]): { ok: true; map: Record<string, string> } | { ok: false; error: string } {
    return isValidEnvVarDraftImported(rows);
  }

  async function saveEnvVars() {
    const project = popupProject;
    if (!project) return;
    const validation = isValidEnvVarDraft(envVarsDraft);
    if (!validation.ok) {
      envVarsError = validation.error;
      return;
    }
    envVarsError = null;
    envVarsInfo = null;
    envVarsSaving = true;
    try {
      const updated: ProjectEntry = { ...project, envVars: validation.map };
      const nextList = projectsStore.projects.map((p) =>
        p.id === project.id ? updated : p,
      );
      await saveProjects({ version: 1, projects: nextList });
      projectsStore.replaceAll(nextList);
      envVarsSeededFor = project.id;
      envVarsInfo = `saved ${Object.keys(validation.map).length} env var${Object.keys(validation.map).length === 1 ? "" : "s"}`;
      S.appendDrawerLog(
        `${envVarsInfo} for ${project.name}`,
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      envVarsError = `save env vars failed: ${msg}`;
      S.appendErrorLog(envVarsError);
    } finally {
      envVarsSaving = false;
    }
  }

  function openSettingsPopup(id: string) {
    const project = projectsStore.find(id);
    if (project) {
      loadLogPaths(project);
      loadDefaultBuildTarget(project);
    }
    settingsPopupFor = id;
    moreMenuOpenFor = null;
  }

  function closeSettingsPopup() {
    settingsPopupFor = null;
  }

  /**
   * Multi-type: child settings components (Package / Open-MCP / Custom)
   * call this when they mutate a project's persisted fields (manifest
   * edits, migrate source folder, line-count cache, etc.) so the
   * ProjectsTab store and the open popup both reflect the new state.
   * The child is responsible for calling `saveProjects` itself before
   * invoking this; we just refresh the in-memory list.
   */
  function handlePopupProjectMutated(updated: ProjectEntry) {
    const nextList = projectsStore.projects.map((p) =>
      p.id === updated.id ? updated : p,
    );
    projectsStore.replaceAll(nextList);
  }

  /**
   * Multi-type: opens the read-only git popup for a project and loads
   * the full status (branch + ahead/behind + pending file list). The
   * line-count auto-calc stat is also probed so a small project shows
   * its total lines under the branch line without the user having to
   * open the settings popup.
   */
  async function openGitPopup(id: string) {
    gitPopupFor = id;
    gitStatusData = null;
    gitStatusError = null;
    gitPopupLineStats = null;
    gitStatusLoading = true;
    const project = projectsStore.find(id);
    if (!project) {
      gitStatusLoading = false;
      return;
    }
    // Load status + cached line stats in parallel.
    const [statusResult, statsResult] = await Promise.allSettled([
      gitStatus(project.path),
      countLinesCached(project.id),
    ]);
    gitStatusLoading = false;
    if (statusResult.status === "fulfilled") {
      gitStatusData = statusResult.value;
    } else {
      const err = statusResult.reason as GitStatusError;
      gitStatusError = formatGitStatusError(err);
    }
    if (statsResult.status === "fulfilled") {
      gitPopupLineStats = statsResult.value;
    }
  }

  function closeGitPopup() {
    gitPopupFor = null;
    gitStatusData = null;
    gitStatusError = null;
    gitPopupLineStats = null;
  }

  async function refreshGitStatus() {
    const project = gitPopupProject;
    if (!project) return;
    gitStatusLoading = true;
    gitStatusError = null;
    try {
      gitStatusData = await gitStatus(project.path);
    } catch (e) {
      const err = e as GitStatusError;
      gitStatusError = formatGitStatusError(err);
    } finally {
      gitStatusLoading = false;
    }
  }

  function formatGitStatusError(err: GitStatusError): string {
    return formatGitStatusErrorImported(err);
  }

  async function handlePopupLaunch() {
    if (!settingsPopupFor) return;
    const id = settingsPopupFor;
    closeSettingsPopup();
    await handleLaunch(id);
  }

  // --- Expanded panel: launch args helpers ---
  let argsDrafts = $state<Record<string, string>>({});
  let argsErrors = $state<Record<string, string | null>>({});
  let savingArgsFor = $state<string | null>(null);
  let intentDrafts = $state<Record<string, string>>({});
  let savingIntentFor = $state<string | null>(null);

  function getArgsDraft(id: string): string {
    if (id in argsDrafts) return argsDrafts[id];
    return projectsStore.find(id)?.launchArgs ?? "";
  }

  function getIntentDraft(id: string): string {
    if (id in intentDrafts) return intentDrafts[id];
    return projectsStore.find(id)?.platformIntent ?? "";
  }

  function handleArgsInput(id: string, value: string) {
    argsDrafts = { ...argsDrafts, [id]: value };
    const err = validateArgs(value);
    if (err) argsErrors = { ...argsErrors, [id]: err };
    else if (argsErrors[id]) argsErrors = { ...argsErrors, [id]: null };
  }

  function validateArgs(value: string): string | null {
    return validateArgsHelper(value);
  }

  async function handleSaveArgs(project: ProjectEntry) {
    const draft = getArgsDraft(project.id);
    if (draft.trim().length === 0) return;
    const err = validateArgs(draft);
    if (err) {
      argsErrors = { ...argsErrors, [project.id]: err };
      return;
    }
    savingArgsFor = project.id;
    try {
      const updated: ProjectEntry = { ...project, launchArgs: draft };
      await projectsStore.update(updated);
      S.appendDrawerLog(`saved launch args for ${project.name}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save launch args failed: ${msg}`);
    } finally {
      savingArgsFor = null;
    }
  }

  async function handleResetArgs(project: ProjectEntry) {
    savingArgsFor = project.id;
    try {
      const updated: ProjectEntry = { ...project, launchArgs: "" };
      await projectsStore.update(updated);
      argsDrafts = { ...argsDrafts, [project.id]: "" };
      argsErrors = { ...argsErrors, [project.id]: null };
      S.appendDrawerLog(`cleared launch args for ${project.name}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`reset launch args failed: ${msg}`);
    } finally {
      savingArgsFor = null;
    }
  }

  function handleIntentChange(id: string, value: string) {
    intentDrafts = { ...intentDrafts, [id]: value };
  }

  async function handleSaveIntent(project: ProjectEntry) {
    const next = getIntentDraft(project.id).trim();
    const previous = project.platformIntent ?? "";
    // Task 5 (M1.5-5): when the recorded launch PID is still alive, the
    // new intent will only apply on the next launch. Surface the nudge
    // before saving so the user can opt out.
    let proceed = true;
    if (next !== previous && project.lastLaunchPid) {
      const alive = await probePidAlive(project.lastLaunchPid);
      if (alive) {
        proceed = await S.confirm(
          "Unity is running for this project",
          "Unity is currently running for this project. The new platform intent applies to the next launch; live switch is not supported in v1.\n\nSave anyway?",
        );
      }
    }
    if (!proceed) {
      return;
    }
    savingIntentFor = project.id;
    try {
      const updated: ProjectEntry = { ...project, platformIntent: next };
      await projectsStore.update(updated);
      S.appendDrawerLog(
        next
          ? `set platform intent for ${project.name} to ${next}`
          : `cleared platform intent for ${project.name}`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save platform intent failed: ${msg}`);
    } finally {
      savingIntentFor = null;
    }
  }

  // Probe whether a recorded launch PID is still alive. The OS may have
  // already reaped the process — the previous launch was the most
  // recent; if Unity exited in the meantime the nudge is moot and the
  // save proceeds silently. Errors are non-fatal: a failed probe
  // (e.g. permission issue) is treated as "not alive" so the user
  // never gets a false-positive warning.
  async function probePidAlive(pid: number): Promise<boolean> {
    try {
      return await isPidAlive(pid);
    } catch {
      return false;
    }
  }

  function intentOptions(current: string): string[] {
    return intentOptionsImported(current);
  }

  let launchArgsInfoOpen = $state(false);

  function toggleLaunchArgsInfo() {
    launchArgsInfoOpen = !launchArgsInfoOpen;
  }

  async function openLaunchArgsDocs() {
    try {
      await openUrl(LAUNCH_ARGS_DOCS_URL);
      S.appendDrawerLog(`opened launch args docs: ${LAUNCH_ARGS_DOCS_URL}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`open launch args docs failed: ${msg}`);
    }
    launchArgsInfoOpen = false;
  }

  function setNewProjectField<K extends keyof NewProjectFieldArgs>(key: K, value: NewProjectFieldArgs[K]): void {
    // Assign each form field. Each branch is a simple setter so the
    // sub-component's `oninput`/`onchange` can drive the form fields
    // owned by the orchestrator without exposing raw `$state`.
    switch (key) {
      case "newProjectParent": newProjectParent = value as string; break;
      case "newProjectName": newProjectName = value as string; break;
      case "newProjectVersion": newProjectVersion = value as string; break;
      case "newProjectPipeline": newProjectPipeline = value as RenderPipeline; break;
      case "newProjectBundleVersion": newProjectBundleVersion = value as string; break;
      case "newProjectTemplateKind": newProjectTemplateKind = value as NewTemplateKind; break;
      case "newProjectHubTemplatePath": newProjectHubTemplatePath = value as string; break;
      case "newProjectCustomTemplatePath": newProjectCustomTemplatePath = value as string; break;
      case "pkgName": pkgName = value as string; break;
      case "pkgVersion": pkgVersion = value as string; break;
      case "pkgDisplayName": pkgDisplayName = value as string; break;
      case "pkgDescription": pkgDescription = value as string; break;
      case "pkgUnity": pkgUnity = value as string; break;
      case "pkgKeywords": pkgKeywords = value as string; break;
      case "pkgAuthorName": pkgAuthorName = value as string; break;
      case "pkgAuthorUrl": pkgAuthorUrl = value as string; break;
      case "pkgIncludeExtras": pkgIncludeExtras = value as boolean; break;
    }
  }

  function setEnvVarDraft(uid: string, field: "key" | "value", value: string): void {
    envVarsDraft = envVarsDraft.map((r) => (r.uid === uid ? { ...r, [field]: value } : r));
  }

  // --- state + handler bags passed to every sub-component ---
  import type { NewProjectFieldArgs } from "./projects/state.ts";

  let projectsState = $derived<ProjectsState>({
    search,
    filterPreset,
    showHidden,
    filtered,
    pathExistsMap,
    sizeMap,
    loadingSizes,
    checkingPaths,
    logPathsMap,
    defaultBuildTargetMap,
    aiDetectMap,
    launching,
    refreshingId,
    killingId,
    actionError,
    addError,
    isDragOver,
    contextMenu,
    moreMenuOpenFor,
    addingProject,
    refreshing,
    removingId,
    relinkingId,
    hidingId,
    markingStaleId,
    walkUpModalOpen,
    pickingWalkUpFolder,
    walkUpKinds,
    hubImportModalOpen,
    hubImportLoading,
    hubImportError,
    hubImportCandidates,
    hubImportAddingPath,
    upgradeModalProjectId,
    upgradeCandidatesList,
    upgradeTargetVersion,
    upgradeStrategy,
    upgradePreviewBundle,
    upgradePreviewPrevBundle,
    upgradeLoading,
    upgradeError,
    newProjectModalOpen,
    newProjectParent,
    newProjectName,
    newProjectVersion,
    newProjectPipeline,
    newProjectBundleVersion,
    newProjectTemplateKind,
    newProjectHubTemplatePath,
    newProjectCustomTemplatePath,
    newProjectHubTemplates,
    newProjectHubTemplatesAvailable,
    newProjectHubTemplatesFolder,
    newProjectError,
    newProjectCreating,
    newProjectOverwriteConfirm,
    newProjectMode,
    pkgName,
    pkgVersion,
    pkgDisplayName,
    pkgDescription,
    pkgUnity,
    pkgKeywords,
    pkgAuthorName,
    pkgAuthorUrl,
    pkgIncludeExtras,
    aiSetupWizardProjectId,
    settingsPopupFor,
    popupProject,
    popupDefaultBuildTarget,
    gitPopupFor,
    gitPopupProject,
    gitStatusData,
    gitStatusLoading,
    gitStatusError,
    gitPopupLineStats,
    envVarsDraft,
    envVarsRevealed,
    envVarsSaving,
    envVarsError,
    envVarsInfo,
    argsDrafts,
    argsErrors,
    savingArgsFor,
    intentDrafts,
    savingIntentFor,
    launchArgsInfoOpen,
    statusFor,
    isRunningFor,
    projectKindOf,
    aiReadyFor,
    gridTemplate,
    showModified,
    showGitBranch,
    aiSetupEnabled: AI_SETUP_ENABLED,
  });

  let projectsHandlers = $derived<ProjectsHandlers>({
    setSearch: (v) => (search = v),
    setFilterPreset: (v) => (filterPreset = v as FilterPreset),
    toggleShowHidden: () => (showHidden = !showHidden),
    dismissAddError: () => (addError = null),
    dismissActionError: () => (actionError = null),
    handleAddProject,
    handleRefresh,
    openNewProjectModal,
    openWalkUpModal,
    openHubImportModal,
    handleLaunch,
    openContextMenu,
    openSettingsPopup,
    openAiSetupFor,
    openGitPopup,
    toggleMoreMenu,
    closeContextMenu,
    setMoreMenuOpen: (id) => (moreMenuOpenFor = id),
    handleOpenFolder,
    handleCopyPath,
    handleKillUnity,
    handleRelink,
    handleRefreshProject,
    handleRemove,
    handleHide,
    handleUnhide,
    handleMarkStale,
    handleUnmarkStale,
    openUpgradeModal,
    canHide,
    canMarkStale,
    canUnhide,
    canUnmarkStale,
    canUpgrade,
    closeWalkUpModal,
    startWalkUpFromModal,
    cancelWalkUpFromModal,
    handleWalkUpSelectFolder,
    lastScanSummary,
    closeNewProjectModal,
    pickNewProjectParent,
    pickNewProjectCustomTemplate,
    saveCustomTemplateToSettings,
    submitNewProject,
    submitNewProjectOverwrite,
    submitNewPackage,
    submitNewPackageOverwrite,
    setNewProjectMode: (mode) => (newProjectMode = mode),
    setNewProjectField,
    closeHubImportModal,
    loadHubCandidates,
    importHubCandidate,
    closeUpgradeModal,
    submitUpgrade,
    setUpgradeTargetVersion: (v) => (upgradeTargetVersion = v),
    setUpgradeStrategy: (s) => (upgradeStrategy = s),
    closeSettingsPopup,
    handlePopupLaunch,
    handlePopupProjectMutated,
    closeGitPopup,
    refreshGitStatus,
    addEnvVarRow,
    removeEnvVarRow,
    toggleEnvReveal,
    setEnvVarDraft,
    saveEnvVars,
    getArgsDraft,
    getIntentDraft,
    handleArgsInput,
    handleSaveArgs,
    handleResetArgs,
    handleIntentChange,
    handleSaveIntent,
    toggleLaunchArgsInfo,
    openLaunchArgsDocs,
    closeAiSetup,
    openPath: async (p) => { await openPath(p); },
  });
</script>

<ProjectList state={projectsState} handlers={projectsHandlers} />


<WalkUpScanModal state={projectsState} handlers={projectsHandlers} />
<NewProjectModal state={projectsState} handlers={projectsHandlers} />
<HubImportModal state={projectsState} handlers={projectsHandlers} />
<UpgradeModal state={projectsState} handlers={projectsHandlers} />
<ContextMenu state={projectsState} handlers={projectsHandlers} />

{#if AI_SETUP_ENABLED && aiSetupWizardProjectId}
  {@const aiSetupProject = projectsStore.find(aiSetupWizardProjectId)}
  {#if aiSetupProject}
    <AiSetupWizard
      project={{
        id: aiSetupProject.id,
        name: aiSetupProject.name,
        path: aiSetupProject.path,
        unityVersion: aiSetupProject.unityVersion,
        aiSetupWizard: aiSetupProject.aiSetupWizard,
      }}
      onClose={closeAiSetup}
    />
  {/if}
{/if}

<SettingsPopup state={projectsState} handlers={projectsHandlers} />
<GitPopup state={projectsState} handlers={projectsHandlers} />
<LaunchArgsInfoModal state={projectsState} handlers={projectsHandlers} />
