<script lang="ts">
  import { onMount } from "svelte";
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
    type AddProjectError,
    type BundleStrategy,
    type HubTemplateEntry,
    type HubTemplatesResult,
    type KillUnityResult,
    type LaunchError,
    type LogPaths,
    type NewProjectError,
    type ProjectEntry,
    type RelinkProjectError,
    type RemoveProjectError,
    type RenderPipeline,
    type SetProjectFlagError,
    type TemplateRef,
    type UpgradeUnityError,
  } from "$lib/services/config";
  import {
    compareFrecency,
    compareLastModified,
  } from "$lib/frecency";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
  import { getCurrentWebview } from "@tauri-apps/api/webview";
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import RelativeTime from "$lib/components/RelativeTime.svelte";
  import AiSetupWizard from "$lib/components/AiSetupWizard.svelte";
  import { AI_SETUP_ENABLED } from "$lib/features";

  type FilterPreset = "all" | "launchable" | "missingVersion" | "missingPath" | "missingOrStale" | "running";
  type StatusKind =
    | "ok"
    | "warn"
    | "missing"
    | "missingVersion"
    | "missingPath"
    | "stale"
    | "running"
    | "loading"
    | "unknown";

  interface RowStatus {
    pathExists: boolean | null;
    hasVersion: boolean;
    running: boolean;
    /** True when the row is tagged as `stale` (M1.5-15). Stale rows
     *  are kept visible with a `stale` chip and excluded from
     *  launch / running-Unity actions. */
    stale: boolean;
    chips: { tone: "ok" | "warn" | "missing" | "running" | "stale" | "info" | "muted"; label: string; title: string }[];
    kind: StatusKind;
    launchable: boolean;
  }

  let search = $state("");
  let filterPreset = $state<FilterPreset>("all");
  let pathExistsMap = $state<Record<string, boolean>>({});
  let checkingPaths = $state(false);
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
  // M1.5-15: when true, hidden rows are also shown in the default
  // list. The toggle is a chip in the toolbar, not a setting, so
  // session-only state is sufficient.
  let showHidden = $state(false);
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
  // M4 Plan 2 (M4-4 / M4-5): AI Setup wizard. The wizard's own state
  // lives entirely inside `AiSetupWizard.svelte`; the Projects tab
  // only owns the "open / close" handle and the live project
  // pointer. Wizard progress is never written to `projects.json`
  // (questions-4 Q11 = A) — reopening the modal always restarts at
  // Step 1 by way of the wizard's local `$state`.
  let aiSetupWizardProjectId = $state<string | null>(null);

  const UNSAFE_RE = /[\n\r\0`$|&;<>]/;

  const LAUNCH_LOG_TAIL_LINES = 200;

  const BUILD_TARGETS: string[] = [
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

  const BUILD_TARGET_LABELS: Record<string, string> = {
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

  function buildTargetLabel(target: string | null): string {
    if (!target) return "—";
    return BUILD_TARGET_LABELS[target] ?? target;
  }

    onMount(() => {
    let cancelled = false;
    (async () => {
      await projectsStore.load();
      if (cancelled) return;
      // Path existence, sizes, and git branches are independent of each
      // other — run them concurrently. The backing Tauri commands are
      // `async` + `spawn_blocking`, so they no longer serialize on the
      // webview thread and the window stays responsive while they run.
      await Promise.all([refreshPathExistence(), loadSizes(), loadGitBranches()]);
    })();
    // Start the running-Unity polling loop. The cadence is read from
    // `settings.discovery.scanIntervalSeconds` (default 30s, M1.5-10);
    // the store internally restarts the timer when the user edits the
    // setting. The polling stops on teardown so we don't leak the
    // interval while the user is on another tab.
    void runningUnityStore.start();
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
    const hasVersion = !!project.unityVersion && project.unityVersion.length > 0;
    const exists = pathExistsMap[project.path];
    const running = isRunningFor(project);
    const stale = !!project.stale;

    if (exists === undefined) {
      return {
        pathExists: null,
        hasVersion,
        running,
        stale,
        chips: [{ tone: "muted", label: "checking…", title: "Checking path" }],
        kind: "loading",
        launchable: false,
      };
    }

    // M1.5-15: stale rows are kept visible but never launchable. A
    // stale row whose path also went missing shows both chips so
    // the user can decide whether to relink or to keep the entry
    // around for record-keeping.
    if (!exists) {
      const chips: { tone: "ok" | "warn" | "missing" | "running" | "stale" | "info" | "muted"; label: string; title: string }[] = [
        { tone: "missing", label: "missing path", title: project.path },
      ];
      if (stale) {
        chips.push({
          tone: "stale",
          label: "stale",
          title: "Marked stale — keep the entry but exclude from launch",
        });
      }
      return {
        pathExists: false,
        hasVersion,
        running: false,
        stale,
        chips,
        kind: "missingPath",
        launchable: false,
      };
    }

    if (stale) {
      return {
        pathExists: true,
        hasVersion,
        running: false,
        stale,
        chips: [
          {
            tone: "stale",
            label: "stale",
            title: "Marked stale — relink to a Unity project root to clear",
          },
          { tone: "info", label: "launchable", title: "Project will try to launch" },
        ],
        kind: "stale",
        launchable: false,
      };
    }

    if (!hasVersion) {
      return {
        pathExists: true,
        hasVersion: false,
        running,
        stale,
        chips: [
          { tone: "warn", label: "version missing", title: "No Unity version detected" },
          { tone: "info", label: "launchable", title: "Project will try to launch" },
        ],
        kind: "missingVersion",
        launchable: false,
      };
    }

    const baseChips: { tone: "ok" | "warn" | "missing" | "running" | "stale" | "info" | "muted"; label: string; title: string }[] = [
      { tone: "ok", label: "ok", title: "Detected" },
      { tone: "info", label: "launchable", title: "Ready to launch" },
    ];
    if (running) {
      baseChips.push({
        tone: "running",
        label: "running",
        title: "Unity is currently running for this project",
      });
    }
    return {
      pathExists: true,
      hasVersion: true,
      running,
      stale,
      chips: baseChips,
      kind: running ? "running" : "ok",
      launchable: true,
    };
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
      const message = pid > 0
        ? `launch refused: Unity is already running for "${project.name}" (pid ${pid}). Terminate it first, or click "Terminate & relaunch" in the status drawer.`
        : `launch refused: Unity is already running for "${project.name}". Terminate it from the project menu before launching again.`;
      S.appendErrorLog(message);
      S.setLastLaunchFailure({
        projectId: project.id,
        projectName: project.name,
        projectPath: project.path,
        timestamp: new Date().toISOString(),
        isLikelyCrash: false,
        launchLogPath: "",
        crashLogPath: null,
        conflictPid: pid > 0 ? pid : null,
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
    } catch (e) {
      const err = e as LaunchError;
      const message = formatLaunchError(err, project);
      const autoOpen =
        projectsStore.settings?.diagnostics.autoOpenDrawerOnLaunchFailure ?? true;
      S.appendLaunchLog(message, autoOpen);
      // For an `alreadyRunning` error coming back from the backend, lift
      // the conflicting PID into the failure banner so the relaunch
      // action targets the right process. A frontend pre-check
      // (handleLaunch) sets the same field on the local rejection path.
      const conflictPid = err.type === "alreadyRunning" ? err.pid : null;
      await handleLaunchFailure(project, err, message, autoOpen, conflictPid);
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
   * on the latest `LastLaunchFailure.conflictPid`), wait for the OS to
   * actually reap it, then retry the launch. Used by the status drawer's
   * "Terminate & relaunch" quick action so a user who accidentally
   * double-clicks Launch on a running project does not have to find the
   * running row, open the popup, and click through the More menu.
   *
   * The kill reuses the existing `killUnity` Rust command (which
   * handles the SIGTERM → SIGKILL escalation) and polls the running-
   * Unity store for a cleared snapshot before re-launching. The backend
   * double-launch guard is the final safety net — if the polling misses
   * the process exit, the second `launchProject` call will return
   * `alreadyRunning` and the user gets the same clear error message.
   */
  async function terminateAndRelaunch(projectId: string): Promise<void> {
    const failure = S.lastLaunchFailure;
    if (!failure || failure.projectId !== projectId) return;
    const pid = failure.conflictPid ?? 0;
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
    await handleLaunch(projectId);
  }

  function formatLaunchError(err: LaunchError, project: ProjectEntry): string {
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

  async function handleAddProject() {
    if (addingProject) return;
    addingProject = true;
    addError = null;
    try {
      const selected = await openDialog({
        directory: true,
        multiple: false,
        title: "Select Unity project root",
      });
      if (!selected || typeof selected !== "string") {
        return;
      }
      try {
        const result = await addProject(selected);
        projectsStore.add(result.project);
        await refreshPathExistence();
        await loadSizes();
        S.appendDrawerLog(
          `added project ${result.project.name} (${result.project.unityVersion ?? "version unknown"})`
        );
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
    switch (err.type) {
      case "notADirectory":
        return `not a directory — ${err.path}`;
      case "notAUnityProject":
        return `not a Unity project (${err.reason}) — ${err.path}`;
      case "duplicate":
        return `already in list — ${err.path}`;
      case "persistFailed":
        return `failed to save: ${err.message}`;
      default:
        return `unknown error: ${JSON.stringify(err)}`;
    }
  }

  function formatRelinkError(err: RelinkProjectError): string {
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
    if (!project.unityVersion) return [];
    return upgradeCandidatesList.filter((v) => v !== project.unityVersion && v > (project.unityVersion ?? ""));
  }

  function hasUpgradeAvailable(project: ProjectEntry): boolean {
    return upgradeCandidatesFor(project).length > 0;
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
    const trimmed = (current || "0.0.0").trim();
    if (strategy === "none") return { previous: trimmed, next: trimmed };
    const match = trimmed.match(/^(\d+)\.(\d+)\.(\d+)$/);
    if (!match) return { previous: trimmed, next: trimmed };
    const major = Number(match[1]);
    const minor = Number(match[2]);
    const patch = Number(match[3]);
    if (strategy === "patch") return { previous: trimmed, next: `${major}.${minor}.${patch + 1}` };
    if (strategy === "minor") return { previous: trimmed, next: `${major}.${minor + 1}.0` };
    return { previous: trimmed, next: `${major + 1}.0.0` };
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
    const result = await walkUpScanStore.begin({
      roots,
      maxDepth: settings.unityDiscovery.walkUpMaxDepth,
      followSymlinks: settings.unityDiscovery.walkUpFollowSymlinks,
      keepPartial: settings.unityDiscovery.walkUpKeepPartial,
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

  function resolveNewProjectTemplate(): TemplateRef | null {
    if (newProjectTemplateKind === "empty") return null;
    if (newProjectTemplateKind === "hub-default") {
      if (!newProjectHubTemplatePath) return null;
      return { source: "hub-default", path: newProjectHubTemplatePath };
    }
    if (!newProjectCustomTemplatePath) return null;
    return { source: "custom", path: newProjectCustomTemplatePath };
  }

  /**
   * URP / HDRP require Unity 2019.3 or newer (the version that
   * shipped the Scriptable Render Pipeline). We grey the dropdown
   * options out so the user gets the same hint at click-time as
   * they would after submitting; the backend enforces the same rule
   * and returns `pipelineUnsupported` if the user somehow picks
   * an older version anyway.
   */
  let pipelineSupportedForVersion = $derived.by(() => {
    const v = newProjectVersion.trim();
    if (!v) return true;
    const match = v.match(/^(\d+)\.(\d+)/);
    if (!match) return true;
    const major = Number(match[1]);
    const minor = Number(match[2]);
    return major > 2019 || (major === 2019 && minor >= 3);
  });

  $effect(() => {
    if (!pipelineSupportedForVersion && newProjectPipeline !== "none") {
      newProjectPipeline = "none";
    }
  });

  function isNewProjectFormValid(): boolean {
    if (!newProjectParent.trim()) return false;
    if (!newProjectName.trim()) return false;
    if (!newProjectVersion.trim()) return false;
    if (newProjectTemplateKind === "hub-default" && !newProjectHubTemplatePath) return false;
    if (newProjectTemplateKind === "custom" && !newProjectCustomTemplatePath) return false;
    return true;
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
   * M4 Plan 2 (M4-4): AI Setup toolbar button entry point. The
   * button is bound to the currently selected project; when no row
   * is selected the button is disabled (state = "disabled"), when
   * the selected row is launchable it lights up (state = "ready"),
   * and a project whose last detected state was incomplete (e.g.
   * a previous run that skipped Step 5) shows the "incomplete"
   * state. The wizard itself is a modal opened by
   * `AiSetupWizard.svelte`; this handler just kicks the open.
   */
  function openAiSetup() {
    if (!AI_SETUP_ENABLED) return;
    const id = projectsStore.selectedProjectId;
    if (!id) return;
    const project = projectsStore.find(id);
    if (!project) return;
    aiSetupWizardProjectId = project.id;
  }

  /**
   * Open the AI Setup wizard for a specific project (called from
   * the row context menu — "Configure Agent Bridge"). The button
   * in the toolbar uses the currently selected row; the context
   * menu entry always operates on the right-clicked row, so the
   * two paths are explicit.
   */
  function openAiSetupFor(project: ProjectEntry) {
    if (!AI_SETUP_ENABLED) return;
    if (!project) return;
    projectsStore.select(project.id);
    aiSetupWizardProjectId = project.id;
  }

  function closeAiSetup() {
    aiSetupWizardProjectId = null;
  }

  /**
   * The AI Setup button has three visual states per hub-ui.md
   * §Projects toolbar:
   *   - disabled: no row selected, OR the selected row is not a
   *     valid Unity project (missing path / unknown version).
   *   - ready: selected row is launchable.
   *   - incomplete: selected row previously went through a partial
   *     run (we currently treat any selected row whose path exists
   *     but whose AI Setup is unknown as "incomplete" — Step 1's
   *     live detection in Plan 3 will tighten this once we have
   *     a stored detect snapshot).
   * Selection is mirrored in real time so right-click → context
   * menu / keyboard nav all share the same underlying button
   * state without duplicate state.
   */
  type AiSetupButtonState = "disabled" | "ready" | "incomplete";
  function aiSetupButtonState(): AiSetupButtonState {
    if (!AI_SETUP_ENABLED) return "disabled";
    const id = projectsStore.selectedProjectId;
    if (!id) return "disabled";
    const project = projectsStore.find(id);
    if (!project) return "disabled";
    const s = statusFor(project);
    if (s.pathExists !== true) return "disabled";
    if (!s.hasVersion) return "disabled";
    if (s.stale) return "disabled";
    if (s.launchable) return "ready";
    return "incomplete";
  }

  function aiSetupButtonTitle(state: AiSetupButtonState): string {
    switch (state) {
      case "disabled":
        return "AI Setup — select a project first";
      case "ready":
        return "AI Setup — install / configure the Unity AI agent for this project";
      case "incomplete":
        return "AI Setup — previous run was incomplete; resume from Step 1";
    }
  }

  function formatRemoveError(err: RemoveProjectError): string {
    switch (err.type) {
      case "projectNotFound":
        return `project not found (${err.projectId})`;
      case "persistFailed":
        return `failed to save: ${err.message}`;
      default:
        return `unknown error: ${JSON.stringify(err)}`;
    }
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
    const name = "minmax(8rem, 1.1fr)";
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
    if (bytes === 0) return "—";
    const units = ["B", "KB", "MB", "GB"];
    let i = 0;
    let size = bytes;
    while (size >= 1024 && i < units.length - 1) {
      size /= 1024;
      i++;
    }
    return `${size.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
  }

  function toggleMoreMenu(id: string) {
    moreMenuOpenFor = moreMenuOpenFor === id ? null : id;
  }

  let settingsPopupFor = $state<string | null>(null);

  let popupProject = $derived(
    settingsPopupFor ? projectsStore.find(settingsPopupFor) ?? null : null
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
    const map: Record<string, string> = {};
    for (const row of rows) {
      const key = row.key.trim();
      if (key === "") {
        return { ok: false, error: "env-var keys cannot be empty" };
      }
      if (key.includes("=")) {
        return { ok: false, error: `env-var key cannot contain '=': ${key}` };
      }
      if (Object.prototype.hasOwnProperty.call(map, key)) {
        return { ok: false, error: `duplicate env-var key: ${key}` };
      }
      map[key] = row.value;
    }
    return { ok: true, map };
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
    const match = value.match(UNSAFE_RE);
    if (match) {
      return `unsafe character "${match[0]}"`;
    }
    return null;
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
    if (current && !BUILD_TARGETS.includes(current)) {
      return [current, ...BUILD_TARGETS];
    }
    return BUILD_TARGETS;
  }

  let launchArgsInfoOpen = $state(false);

  function toggleLaunchArgsInfo() {
    launchArgsInfoOpen = !launchArgsInfoOpen;
  }

  const LAUNCH_ARGS_DOCS_URL =
    "https://docs.unity3d.com/Manual/CommandLineArguments.html";

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

  const LAUNCH_ARGS_EXAMPLES: { args: string; description: string }[] = [
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
</script>

<div class="projects" class:drag-over={isDragOver}>
  <div class="toolbar">
    <div class="toolbar-row">
      <input
        type="search"
        class="search"
        placeholder="Search projects…"
        bind:value={search}
        aria-label="Search projects"
      />

      <Select
        options={filterOptions.map((o) => ({ value: o.id, label: o.label }))}
        value={filterPreset}
        onchange={(v) => (filterPreset = v as FilterPreset)}
        aria-label="Filter projects"
        title="Filter projects"
      />

      <button
        type="button"
        class="filter-btn show-hidden-btn"
        class:filter-active={showHidden}
        onclick={() => (showHidden = !showHidden)}
        aria-pressed={showHidden}
        title={showHidden
          ? "Hide soft-deleted projects from the list"
          : "Show soft-deleted projects (entries kept in projects.json; use Hide from the row menu to soft-delete)"}
      >
        {showHidden ? "✓ " : ""}Show hidden
      </button>

      <div class="toolbar-spacer"></div>

      {#if AI_SETUP_ENABLED}
        {@const aiState = aiSetupButtonState()}
        <Button
          variant="secondary"
          class="ai-setup-btn ai-setup-{aiState}"
          onclick={openAiSetup}
          disabled={aiState === "disabled"}
          title={aiSetupButtonTitle(aiState)}
          data-state={aiState}
        >
          AI Setup
        </Button>
      {/if}
    </div>
    <div class="toolbar-row">
      <div class="toolbar-spacer"></div>
      <Button
        variant="secondary"
        onclick={openNewProjectModal}
        disabled={newProjectCreating}
        title="New project — scaffold a fresh Unity project from a template"
      >
        New project…
      </Button>
      <Button variant="primary" onclick={handleAddProject} disabled={addingProject}>
        {addingProject ? "Adding…" : "Add Project"}
      </Button>
      <Button
        variant="secondary"
        onclick={openWalkUpModal}
        disabled={walkUpScanStore.scanning}
        title="Add Multiple Projects — pick a parent folder and discover Unity projects underneath"
      >
        {walkUpScanStore.scanning ? "Scanning…" : "Add Multiple Projects"}
      </Button>
      <button
        type="button"
        class="icon-btn"
        onclick={handleRefresh}
        disabled={refreshing}
        title={refreshing ? "Refreshing…" : "Refresh"}
        aria-label={refreshing ? "Refreshing…" : "Refresh"}
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class:icon-spin={refreshing}
          aria-hidden="true"
        >
          <polyline points="23 4 23 10 17 10"/>
          <polyline points="1 20 1 14 7 14"/>
          <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/>
        </svg>
      </button>
      {#if removingId}
        <span class="toolbar-status" aria-live="polite">Removing…</span>
      {/if}
    </div>
  </div>

  {#if addError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{addError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={() => (addError = null)}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  {#if actionError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{actionError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={() => (actionError = null)}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  <div class="table" role="grid">
    <div class="table-head" role="row" style="grid-template-columns: {gridTemplate};">
      <div class="th th-name" role="columnheader">Name</div>
      <div class="th" role="columnheader">Editor Version</div>
      {#if showModified}
        <div class="th" role="columnheader">Modified</div>
      {/if}
      {#if showGitBranch}
        <div class="th" role="columnheader" title="Current git branch (detached HEAD shows the SHA on hover)">Branch</div>
      {/if}
      <div class="th" role="columnheader" title="Folder size excluding Library, Temp, Logs, UserSettings and gitignored directories">Size</div>
      <div class="th" role="columnheader">Status</div>
      <div class="th th-settings" role="columnheader"></div>
    </div>

    <div class="table-body">
      {#if filtered.length === 0}
        <div class="empty-state">
          {#if projectsStore.projects.length === 0}
            <p>No projects yet.</p>
            <p class="empty-hint">Use <strong>Add Project</strong> to register a Unity project folder.</p>
          {:else}
            <p>No projects match the current filter.</p>
          {/if}
        </div>
      {:else}
        {#each filtered as project, index (project.id)}
          {@const s = rowStatus(project)}
          <div class="row-wrapper">
            <!-- svelte-ignore a11y_interactive_supports_focus -->
            <!-- svelte-ignore a11y_click_events_have_key_events -->
            <div
              class="row"
              class:row-missing={s.kind === "missingPath"}
              class:row-stale={s.kind === "stale"}
              class:row-hidden={project.hidden === true}
              class:row-selected={projectsStore.selectedProjectId === project.id}
              role="row"
              aria-selected={projectsStore.selectedProjectId === project.id}
              style="grid-template-columns: {gridTemplate};"
              onclick={() => handleLaunch(project.id)}
              oncontextmenu={(e) => openContextMenu(e, project.id)}
            >
              <div class="cell cell-name" role="gridcell">
                <div class="name-path">
                  <span class="name-text">
                    {project.name}
                    {#if project.source === "walk-up"}
                      <span
                        class="source-tag source-walkup"
                        title="Added by walk-up directory scan"
                        >walk-up</span
                      >
                    {:else if project.source === "hub-seed"}
                      <span
                        class="source-tag source-hubseed"
                        title="Imported from Unity Hub on first run"
                        >hub</span
                      >
                    {/if}
                    {#if project.hidden === true}
                      <span
                        class="source-tag source-hidden"
                        title="Hidden from the default view — toggle 'Show hidden' in the toolbar to reveal"
                        >hidden</span
                      >
                    {/if}
                  </span>
                  <span class="path-text" title={project.path}>{project.path}</span>
                </div>
              </div>
              <div class="cell cell-version" role="gridcell">
                {#if project.unityVersion}
                  <span class="version-text">{project.unityVersion}</span>
                {:else}
                  <span class="muted">—</span>
                {/if}
              </div>
              {#if showModified}
                <div class="cell cell-modified" role="gridcell">
                  <RelativeTime iso={project.lastOpenedAt ?? project.lastModifiedAt} />
                </div>
              {/if}
              {#if showGitBranch}
                <div class="cell cell-branch" role="gridcell">
                  {#if project.gitBranch}
                    {#if project.gitBranch.startsWith("detached:")}
                      <span
                        class="branch-chip branch-detached"
                        title={project.gitBranch}
                      >detached</span>
                    {:else}
                      <span
                        class="branch-chip"
                        title={project.gitBranch}
                      >{project.gitBranch}</span>
                    {/if}
                  {/if}
                </div>
              {/if}
              <div class="cell cell-size" role="gridcell">
                <span class="size-text">{formatSize(sizeMap[project.path] ?? 0)}</span>
              </div>
              <div class="cell cell-status" role="gridcell">
                <div class="chips">
                  {#each s.chips as chip}
                    <StatusChip tone={chip.tone} label={chip.label} title={chip.title} />
                  {/each}
                </div>
              </div>
              <div class="cell cell-settings" role="gridcell">
                {#if AI_SETUP_ENABLED && s.pathExists === true && s.hasVersion && !s.stale}
                  <button
                    type="button"
                    class="row-action-btn ai-row-btn ai-setup-btn ai-setup-{s.launchable ? 'ready' : 'incomplete'}"
                    onclick={(e: MouseEvent) => { e.stopPropagation(); openAiSetupFor(project); }}
                    aria-label="AI setup"
                    title="AI — install / configure the Unity AI agent for this project"
                  >
                    AI
                  </button>
                {/if}
                <button
                  type="button"
                  class="row-action-btn settings-btn"
                  onclick={(e: MouseEvent) => { e.stopPropagation(); openSettingsPopup(project.id); }}
                  aria-label="Project settings"
                  title="Project settings"
                >
                  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" style="pointer-events: none;">
                    <circle cx="12" cy="12" r="3"/>
                    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
                  </svg>
                </button>
              </div>
            </div>
          </div>
        {/each}
      {/if}
    </div>
  </div>
</div>

{#if walkUpModalOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="walkup-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="walkup-modal-title"
    onclick={(e) => { if (e.target === e.currentTarget) closeWalkUpModal(); }}
    onkeydown={(e) => { if (e.key === "Escape" && !walkUpScanStore.scanning) closeWalkUpModal(); }}
  >
    <div class="walkup-modal">
      <header class="walkup-header">
        <h2 id="walkup-modal-title" class="walkup-title">Add Multiple Projects</h2>
        {#if !walkUpScanStore.scanning}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close add multiple projects"
            onclick={closeWalkUpModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="walkup-body">
        <p class="walkup-desc">
          Hub will recurse into the selected folder and append every
          folder that contains both <code>Assets/</code> and
          <code>ProjectSettings/</code> to the project list as
          <code>source: walk-up</code>.
        </p>

        <section class="walkup-config">
          <h3 class="walkup-section-title">Selected Folder</h3>
          {#if settingsStore.current && settingsStore.current.unityDiscovery.walkUpRoots.length > 0}
            <ul class="walkup-roots">
              {#each settingsStore.current.unityDiscovery.walkUpRoots as root (root)}
                <li class="walkup-root" title={root}>{root}</li>
              {/each}
            </ul>
          {:else}
            <p class="walkup-empty">No folder selected</p>
          {/if}
          <dl class="walkup-config-list">
            <div>
              <dt>Max depth</dt>
              <dd>{settingsStore.current?.unityDiscovery.walkUpMaxDepth ?? 4}</dd>
            </div>
            <div>
              <dt>Follow symlinks</dt>
              <dd>
                {settingsStore.current?.unityDiscovery.walkUpFollowSymlinks ? "yes" : "no"}
              </dd>
            </div>
            <div>
              <dt>Keep partial on cancel</dt>
              <dd>
                {settingsStore.current?.unityDiscovery.walkUpKeepPartial ? "yes" : "no"}
              </dd>
            </div>
          </dl>
        </section>

        {#if walkUpScanStore.scanning}
          <section class="walkup-progress" aria-live="polite">
            <h3 class="walkup-section-title">Scanning…</h3>
            <dl class="walkup-progress-list">
              <div>
                <dt>Current root</dt>
                <dd>{walkUpScanStore.currentRoot ?? "—"}</dd>
              </div>
              <div>
                <dt>Current depth</dt>
                <dd>
                  {walkUpScanStore.currentDepth ?? 0} / {walkUpScanStore.maxDepth ?? 0}
                </dd>
              </div>
              <div>
                <dt>Found so far</dt>
                <dd>{walkUpScanStore.foundSoFar}</dd>
              </div>
              <div>
                <dt>Visited dirs</dt>
                <dd>{walkUpScanStore.visitedDirs}</dd>
              </div>
            </dl>
          </section>
        {:else if walkUpScanStore.lastResult}
          <section class="walkup-done" aria-live="polite">
            <h3 class="walkup-section-title">
              {walkUpScanStore.lastResult.status === "cancelled"
                ? "Cancelled"
                : walkUpScanStore.lastResult.status === "failed"
                  ? "Failed"
                  : "Done"}
            </h3>
            {#if lastScanSummary()}
              {@const s = lastScanSummary()}
              <p class="walkup-done-line">
                Added <strong>{s?.added}</strong>
                {#if s && s.skipped > 0}
                  , skipped <strong>{s?.skipped}</strong> already in list
                {/if}.
              </p>
              {#if walkUpScanStore.lastResult.error}
                <p class="walkup-error">{walkUpScanStore.lastResult.error}</p>
              {/if}
            {/if}
          </section>
        {/if}

        {#if addError && walkUpModalOpen}
          <p class="walkup-error" role="alert">{addError}</p>
        {/if}
      </div>

      <footer class="walkup-footer">
        {#if walkUpScanStore.scanning}
          <Button variant="destructive" onclick={cancelWalkUpFromModal}>
            Cancel scan
          </Button>
          <span class="walkup-footer-hint">
            The scan checks the cancel flag at every directory
            boundary — it will stop within a few milliseconds.
          </span>
        {:else}
          <Button variant="secondary" onclick={closeWalkUpModal}>
            Close
          </Button>
          <Button
            variant="secondary"
            onclick={handleWalkUpSelectFolder}
            disabled={pickingWalkUpFolder}
          >
            {pickingWalkUpFolder ? "Selecting…" : "Select folder"}
          </Button>
          <Button
            variant="primary"
            onclick={startWalkUpFromModal}
            disabled={
              !settingsStore.current ||
              settingsStore.current.unityDiscovery.walkUpRoots.length === 0
            }
          >
            {walkUpScanStore.lastResult ? "Run again" : "Start scan"}
          </Button>
        {/if}
      </footer>
    </div>
  </div>
{/if}

{#if newProjectModalOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="newproj-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="newproj-modal-title"
    onclick={(e) => { if (e.target === e.currentTarget) closeNewProjectModal(); }}
    onkeydown={(e) => { if (e.key === "Escape" && !newProjectCreating) closeNewProjectModal(); }}
  >
    <div class="newproj-modal">
      <header class="newproj-header">
        <h2 id="newproj-modal-title" class="newproj-title">New project</h2>
        {#if !newProjectCreating}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close new project"
            onclick={closeNewProjectModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="newproj-body">
        <p class="newproj-desc">
          Scaffold a fresh Unity project on disk and register it in
          Hub. The project will appear at the top of the list once
          the modal closes.
        </p>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-parent">Parent folder</label>
          <div class="newproj-input-row">
            <input
              id="newproj-parent"
              type="text"
              class="newproj-input"
              placeholder="/Users/you/Projects"
              bind:value={newProjectParent}
              disabled={newProjectCreating}
            />
            <Button variant="secondary" onclick={pickNewProjectParent} disabled={newProjectCreating}>
              Browse…
            </Button>
          </div>
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-name">Project name</label>
          <input
            id="newproj-name"
            type="text"
            class="newproj-input"
            placeholder="MyGame"
            bind:value={newProjectName}
            disabled={newProjectCreating}
          />
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-version">Unity version</label>
          {#if discoveryStore.installations.length > 0}
          <Select
            id="newproj-version"
            options={[
              { value: "", label: "Select an installed version", disabled: true },
              ...discoveryStore.installations.map((i) => ({ value: i.version, label: i.version })),
            ]}
            value={newProjectVersion}
            onchange={(v) => (newProjectVersion = v)}
            disabled={newProjectCreating}
            placeholder="Select an installed version"
          />
          {:else}
            <input
              id="newproj-version"
              type="text"
              class="newproj-input"
              placeholder="2022.3.48f1"
              bind:value={newProjectVersion}
              disabled={newProjectCreating}
            />
            <p class="newproj-hint">
              No Unity installations discovered — open the Unity Versions tab to scan,
              or type a version manually.
            </p>
          {/if}
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-pipeline">Render pipeline</label>
          <Select
            id="newproj-pipeline"
            options={[
              { value: "none", label: "None (Built-in)" },
              { value: "urp", label: "URP (Universal Render Pipeline)" + (!pipelineSupportedForVersion ? " — requires Unity 2019.3+" : ""), disabled: !pipelineSupportedForVersion },
              { value: "hdrp", label: "HDRP (High Definition Render Pipeline)" + (!pipelineSupportedForVersion ? " — requires Unity 2019.3+" : ""), disabled: !pipelineSupportedForVersion },
            ]}
            value={newProjectPipeline}
            onchange={(v) => (newProjectPipeline = v as RenderPipeline)}
            disabled={newProjectCreating}
          />
          {#if !pipelineSupportedForVersion}
            <p class="newproj-hint newproj-hint-warn">
              URP / HDRP require Unity 2019.3 or newer. Built-in is the
              only available option for the selected version.
            </p>
          {:else}
            <p class="newproj-hint">
              Selecting one writes the matching
              <code>Packages/manifest.json</code> entry.
            </p>
          {/if}
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-bundle">Bundle version</label>
          <input
            id="newproj-bundle"
            type="text"
            class="newproj-input"
            placeholder="0.1.0"
            bind:value={newProjectBundleVersion}
            disabled={newProjectCreating}
          />
        </section>

        <section class="newproj-field">
          <span class="newproj-label">Template</span>
          <div class="newproj-template-row" role="radiogroup" aria-label="Template">
            <label class="newproj-template-option">
              <input
                type="radio"
                name="newproj-template"
                value="empty"
                bind:group={newProjectTemplateKind}
                disabled={newProjectCreating}
              />
              <span>Empty</span>
              <span class="newproj-template-hint">Minimal scaffold (Assets/, ProjectSettings/, Packages/)</span>
            </label>
            <label
              class="newproj-template-option"
              class:newproj-template-disabled={!newProjectHubTemplatesAvailable}
              title={newProjectHubTemplatesAvailable
                ? "Pick a template from Unity Hub's downloaded templates"
                : "Unity Hub is not installed or no templates are downloaded"}
            >
              <input
                type="radio"
                name="newproj-template"
                value="hub-default"
                bind:group={newProjectTemplateKind}
                disabled={!newProjectHubTemplatesAvailable || newProjectCreating}
              />
              <span>Hub default</span>
              <span class="newproj-template-hint">
                {#if newProjectHubTemplatesAvailable}
                  {#if newProjectHubTemplatesFolder}
                    <code title={newProjectHubTemplatesFolder}>{newProjectHubTemplatesFolder}</code>
                  {/if}
                {:else}
                  <em>Unity Hub is not installed or no templates are downloaded.</em>
                {/if}
              </span>
            </label>
            {#if newProjectTemplateKind === "hub-default" && newProjectHubTemplatesAvailable}
              <Select
                class="newproj-template-picker"
                options={newProjectHubTemplates.map((tpl) => ({
                  value: tpl.path,
                  label: tpl.name + (tpl.unityVersion ? ` (${tpl.unityVersion})` : ""),
                }))}
                value={newProjectHubTemplatePath}
                onchange={(v) => (newProjectHubTemplatePath = v)}
                disabled={newProjectCreating}
              />
            {/if}
            <label class="newproj-template-option">
              <input
                type="radio"
                name="newproj-template"
                value="custom"
                bind:group={newProjectTemplateKind}
                disabled={newProjectCreating}
              />
              <span>Custom folder…</span>
              <span class="newproj-template-hint">
                Pick any Unity project root on disk. Manage the saved list in
                <strong>Settings → Custom template folders</strong>.
              </span>
            </label>
            {#if newProjectTemplateKind === "custom"}
              <div class="newproj-input-row">
                <input
                  type="text"
                  class="newproj-input"
                  placeholder="/Users/you/UnityTemplates/Empty"
                  bind:value={newProjectCustomTemplatePath}
                  disabled={newProjectCreating}
                />
                <Button variant="secondary" onclick={pickNewProjectCustomTemplate} disabled={newProjectCreating}>
                  Browse…
                </Button>
              </div>
              {#if newProjectCustomTemplatePath && settingsStore.current && !settingsStore.current.unityDiscovery.customTemplateFolders.includes(newProjectCustomTemplatePath)}
                <div class="newproj-save-hint">
                  Save this path to Settings so it appears in the
                  <strong>Custom template folders</strong> list for next time.
                  <Button
                    variant="secondary"
                    onclick={() => saveCustomTemplateToSettings(newProjectCustomTemplatePath)}
                    disabled={newProjectCreating}
                  >
                    Save to Settings
                  </Button>
                </div>
              {/if}
            {/if}
          </div>
        </section>

        {#if newProjectError}
          <p class="newproj-error" role="alert">{newProjectError}</p>
          {#if newProjectOverwriteConfirm}
            <div class="newproj-overwrite">
              <Button
                variant="destructive"
                onclick={submitNewProjectOverwrite}
                disabled={newProjectCreating}
              >
                {newProjectCreating ? "Replacing…" : "Overwrite existing folder"}
              </Button>
              <span class="newproj-overwrite-hint">
                This will delete the existing folder at
                <code>{newProjectOverwriteConfirm}</code> and replace it.
              </span>
            </div>
          {/if}
        {/if}
      </div>

      <footer class="newproj-footer">
        <Button variant="secondary" onclick={closeNewProjectModal} disabled={newProjectCreating}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onclick={submitNewProject}
          disabled={newProjectCreating || !isNewProjectFormValid()}
        >
          {newProjectCreating ? "Creating…" : "Create project"}
        </Button>
      </footer>
    </div>
  </div>
{/if}

{#if upgradeModalProjectId}
  {@const upgradeProject = projectsStore.find(upgradeModalProjectId)}
  {#if upgradeProject}
    {@const upgradePreview = previewBundleFor("0.0.0", upgradeStrategy)}
    <!-- svelte-ignore a11y_click_events_have_key_events -->
    <!-- svelte-ignore a11y_no_static_element_interactions -->
    <div
      class="upgrade-overlay"
      role="dialog"
      tabindex="-1"
      aria-modal="true"
      aria-labelledby="upgrade-modal-title"
      onclick={(e) => { if (e.target === e.currentTarget) closeUpgradeModal(); }}
      onkeydown={(e) => { if (e.key === "Escape" && !upgradeLoading) closeUpgradeModal(); }}
    >
      <div class="upgrade-modal">
        <header class="upgrade-header">
          <h2 id="upgrade-modal-title" class="upgrade-title">Upgrade Unity version</h2>
          {#if !upgradeLoading}
            <button
              type="button"
              class="walkup-close"
              aria-label="Close upgrade modal"
              onclick={closeUpgradeModal}
            >
              ×
            </button>
          {/if}
        </header>

        <div class="upgrade-body">
          <p class="upgrade-desc">
            Rewrite <code>ProjectSettings/ProjectVersion.txt</code> for
            <strong>{upgradeProject.name}</strong> and bump
            <code>ProjectSettings/ProjectManager.asset</code>'s
            <code>bundleVersion</code> per the strategy below. The
            previous file contents are snapshotted and restored if any
            write fails, so a partial upgrade never leaves the project
            in a mixed state. The exact previous bundleVersion is
            surfaced in the result banner after the upgrade completes.
          </p>

          <section class="upgrade-field">
            <span class="upgrade-label">Current state</span>
            <dl class="upgrade-summary">
              <div>
                <dt>Project path</dt>
                <dd><code title={upgradeProject.path}>{upgradeProject.path}</code></dd>
              </div>
              <div>
                <dt>Current Unity version</dt>
                <dd>
                  {#if upgradeProject.unityVersion}
                    <code>{upgradeProject.unityVersion}</code>
                  {:else}
                    <em>unknown</em>
                  {/if}
                </dd>
              </div>
            </dl>
          </section>

          <section class="upgrade-field">
            <label class="upgrade-label" for="upgrade-target">Target Unity version</label>
            {#if upgradeCandidatesList.length === 0}
              <p class="upgrade-empty">
                {#if upgradeLoading}
                  Loading installed versions…
                {:else}
                  No installed Unity version is strictly higher than
                  <code>{upgradeProject.unityVersion ?? "unknown"}</code>.
                  Install a newer Unity via Unity Hub and click Refresh to
                  try again.
                {/if}
              </p>
            {:else}
              <Select
                id="upgrade-target"
                options={upgradeCandidatesList.map((v) => ({ value: v, label: v }))}
                value={upgradeTargetVersion}
                onchange={(v) => (upgradeTargetVersion = v)}
                disabled={upgradeLoading}
              />
            {/if}
          </section>

          <section class="upgrade-field">
            <span class="upgrade-label">bundleVersion bump</span>
            <div class="upgrade-strategy" role="radiogroup" aria-label="Bundle version bump strategy">
              <label class="upgrade-strategy-option">
                <input
                  type="radio"
                  name="upgrade-strategy"
                  value="none"
                  bind:group={upgradeStrategy}
                  disabled={upgradeLoading}
                />
                <span>
                  <strong>None</strong>
                  <span class="upgrade-strategy-hint">Leave bundleVersion untouched (only the project version line is rewritten).</span>
                </span>
              </label>
              <label class="upgrade-strategy-option">
                <input
                  type="radio"
                  name="upgrade-strategy"
                  value="patch"
                  bind:group={upgradeStrategy}
                  disabled={upgradeLoading}
                />
                <span>
                  <strong>Patch</strong>
                  <span class="upgrade-strategy-hint">Bump the patch number (e.g. 1.2.3 → 1.2.4). Default.</span>
                </span>
              </label>
              <label class="upgrade-strategy-option">
                <input
                  type="radio"
                  name="upgrade-strategy"
                  value="minor"
                  bind:group={upgradeStrategy}
                  disabled={upgradeLoading}
                />
                <span>
                  <strong>Minor</strong>
                  <span class="upgrade-strategy-hint">Bump the minor number and zero the patch (e.g. 1.2.3 → 1.3.0).</span>
                </span>
              </label>
              <label class="upgrade-strategy-option">
                <input
                  type="radio"
                  name="upgrade-strategy"
                  value="major"
                  bind:group={upgradeStrategy}
                  disabled={upgradeLoading}
                />
                <span>
                  <strong>Major</strong>
                  <span class="upgrade-strategy-hint">Bump the major number and zero the rest (e.g. 1.2.3 → 2.0.0).</span>
                </span>
              </label>
            </div>
            <p class="upgrade-preview">
              <span class="upgrade-preview-label">Preview (assuming current = 0.0.0):</span>
              <code>0.0.0</code>
              <span class="upgrade-preview-arrow" aria-hidden="true">→</span>
              <code><strong>{upgradePreview.next || "0.0.0"}</strong></code>
            </p>
          </section>

          {#if upgradeError}
            <p class="upgrade-error" role="alert">{upgradeError}</p>
          {/if}
        </div>

        <footer class="upgrade-footer">
          <Button variant="secondary" onclick={closeUpgradeModal} disabled={upgradeLoading}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onclick={submitUpgrade}
            disabled={upgradeLoading || upgradeCandidatesList.length === 0 || !upgradeTargetVersion}
          >
            {upgradeLoading ? "Upgrading…" : "Upgrade"}
          </Button>
        </footer>
      </div>
    </div>
  {/if}
{/if}

{#if contextMenu}
  {@const ctxId = contextMenu.projectId}
  {@const ctxProject = projectsStore.find(ctxId)}
  {@const ctxStatus = ctxProject ? statusFor(ctxProject) : null}
  <div
    class="ctx-menu"
    role="menu"
    tabindex="-1"
    style="left: {contextMenu.x}px; top: {contextMenu.y}px;"
    onclick={(e) => e.stopPropagation()}
    onkeydown={(e) => {
      if (e.key === "Escape") closeContextMenu();
    }}
  >
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      disabled={!ctxStatus?.launchable}
      onclick={() => {
        if (ctxProject) handleLaunch(ctxProject.id);
        closeContextMenu();
      }}
    >
      Launch
    </button>
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      disabled={ctxStatus?.pathExists === false}
      onclick={() => {
        if (ctxProject) handleOpenFolder(ctxProject);
        closeContextMenu();
      }}
    >
      Open folder
    </button>
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      onclick={() => {
        if (ctxProject) handleCopyPath(ctxProject);
      }}
    >
      Copy path
    </button>
    <button
      type="button"
      class="ctx-item ctx-item-destructive"
      role="menuitem"
      title={ctxProject?.lastLaunchPid ? `Terminate pid ${ctxProject.lastLaunchPid}` : "No recorded Unity PID"}
      disabled={!ctxProject?.lastLaunchPid || killingId === ctxId}
      onclick={() => {
        if (ctxProject) handleKillUnity(ctxProject);
      }}
    >
      {killingId === ctxId ? "Terminating…" : "Terminate Unity"}
    </button>
    {#if ctxStatus?.pathExists === false}
      <button
        type="button"
        class="ctx-item ctx-item-relink"
        role="menuitem"
        title="Re-point this project to a new folder on disk"
        disabled={relinkingId === ctxId}
        onclick={() => {
          if (ctxProject) handleRelink(ctxProject);
        }}
      >
        {relinkingId === ctxId ? "Relinking…" : "Relink…"}
      </button>
    {/if}
    {#if ctxProject && canUpgrade(ctxProject)}
      <div class="ctx-sep"></div>
      <button
        type="button"
        class="ctx-item ctx-item-upgrade"
        role="menuitem"
        title="Bump the project's Unity version to an installed version higher than the current one"
        onclick={() => {
          if (ctxProject) openUpgradeModal(ctxProject);
        }}
      >
        Upgrade Unity…
      </button>
    {/if}
    {#if AI_SETUP_ENABLED && ctxProject && ctxStatus?.pathExists === true && ctxStatus.hasVersion}
      <div class="ctx-sep"></div>
      <button
        type="button"
        class="ctx-item ctx-item-ai-setup"
        role="menuitem"
        title="Install / configure the Unity AI agent for this project"
        onclick={() => {
          if (ctxProject) openAiSetupFor(ctxProject);
        }}
      >
        Configure Agent Bridge…
      </button>
    {/if}
    {#if ctxProject && (canHide(ctxProject) || canUnhide(ctxProject) || canMarkStale(ctxProject) || canUnmarkStale(ctxProject))}
      <div class="ctx-sep"></div>
      {#if canHide(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Remove this row from the list (the entry stays in projects.json with hidden=true; toggle 'Show hidden' in the toolbar to reveal)"
          disabled={hidingId === ctxId}
          onclick={() => {
            if (ctxProject) handleHide(ctxProject);
          }}
        >
          {hidingId === ctxId ? "Hiding…" : "Hide"}
        </button>
      {/if}
      {#if canUnhide(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Restore this row to the default list view"
          disabled={hidingId === ctxId}
          onclick={() => {
            if (ctxProject) handleUnhide(ctxProject);
          }}
        >
          {hidingId === ctxId ? "Unhiding…" : "Unhide"}
        </button>
      {/if}
      {#if canMarkStale(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Keep the row visible with a 'stale' chip (excluded from launch / running-Unity actions; relink to clear)"
          disabled={markingStaleId === ctxId}
          onclick={() => {
            if (ctxProject) handleMarkStale(ctxProject);
          }}
        >
          {markingStaleId === ctxId ? "Marking…" : "Mark stale"}
        </button>
      {/if}
      {#if canUnmarkStale(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Clear the stale flag — the row becomes a normal missing-path row again"
          disabled={markingStaleId === ctxId}
          onclick={() => {
            if (ctxProject) handleUnmarkStale(ctxProject);
          }}
        >
          {markingStaleId === ctxId ? "Unmarking…" : "Unmark stale"}
        </button>
      {/if}
    {/if}
    <div class="ctx-sep"></div>
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      title="Refresh project version and size"
      disabled={ctxStatus?.pathExists === false || refreshingId === ctxId}
      onclick={() => {
        if (ctxProject) handleRefreshProject(ctxProject);
      }}
    >
      {refreshingId === ctxId ? "Refreshing…" : "Refresh"}
    </button>
    <div class="ctx-sep"></div>
    <button
      type="button"
      class="ctx-item ctx-item-destructive"
      role="menuitem"
      title="Remove this project from the Hub list"
      disabled={removingId === ctxId}
      onclick={() => {
        handleRemove(ctxId);
      }}
    >
      {removingId === ctxId ? "Removing…" : "Remove from list"}
    </button>
  </div>
{/if}

{#if AI_SETUP_ENABLED && aiSetupWizardProjectId}
  {@const aiSetupProject = projectsStore.find(aiSetupWizardProjectId)}
  {#if aiSetupProject}
    <AiSetupWizard
      project={{
        id: aiSetupProject.id,
        name: aiSetupProject.name,
        path: aiSetupProject.path,
        unityVersion: aiSetupProject.unityVersion,
      }}
      onClose={closeAiSetup}
    />
  {/if}
{/if}

{#if popupProject}
  {@const ps = statusFor(popupProject)}
  {@const popupIsMoreOpen = moreMenuOpenFor === popupProject.id}
  <div
    class="settings-overlay"
    role="presentation"
    onclick={closeSettingsPopup}
    onkeydown={(e) => {
      if (e.key === "Escape") closeSettingsPopup();
    }}
  >
    <div
      class="settings-modal"
      role="dialog"
      aria-modal="true"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => {
        if (e.key === "Escape") closeSettingsPopup();
      }}
    >
      <div class="settings-modal-header">
        <div class="settings-modal-titles">
          <h2>{popupProject.name}</h2>
          <span class="settings-modal-path" title={popupProject.path}>{popupProject.path}</span>
        </div>
        <button
          type="button"
          class="modal-close-btn"
          aria-label="Close"
          onclick={closeSettingsPopup}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
          </svg>
        </button>
      </div>
      <div class="settings-modal-body">
        <div class="settings-actions">
          <Button
            variant="primary"
            disabled={!ps.launchable || launching === popupProject.id || ps.running}
            title={ps.running
              ? "Unity is already running for this project — terminate it first"
              : (!ps.launchable ? "Project not launchable" : "Launch this project")}
            onclick={handlePopupLaunch}
          >
            {launching === popupProject.id ? "Launching…" : (ps.running ? "Running" : "Launch")}
          </Button>
          <Button
            variant="secondary"
            disabled={ps.pathExists === false}
            title={ps.pathExists === false ? "Path missing" : "Open project folder"}
            onclick={() => handleOpenFolder(popupProject)}
          >
            Open Folder
          </Button>
          <Button
            variant="secondary"
            disabled={ps.pathExists === false}
            title={ps.pathExists === false ? "Path missing" : "Copy project path to clipboard"}
            onclick={() => handleCopyPath(popupProject)}
          >
            Copy Path
          </Button>
          <div class="more-wrap">
            <Button
              variant="secondary"
              onclick={() => toggleMoreMenu(popupProject.id)}
              aria-haspopup="menu"
              aria-expanded={popupIsMoreOpen}
            >
              More ▾
            </Button>
            {#if popupIsMoreOpen}
              <div class="more-menu" role="menu">
                <button type="button" class="more-item more-item-destructive" role="menuitem"
                  title={popupProject.lastLaunchPid
                    ? `Terminate pid ${popupProject.lastLaunchPid}`
                    : "No recorded Unity PID"}
                  disabled={!popupProject.lastLaunchPid || killingId === popupProject.id}
                  onclick={() => { moreMenuOpenFor = null; handleKillUnity(popupProject); }}>
                  {killingId === popupProject.id ? "Terminating…" : "Terminate Unity"}
                </button>
                <div class="more-sep"></div>
                <button type="button" class="more-item" role="menuitem"
                  title="Refresh project version and size"
                  disabled={ps.pathExists === false || refreshingId === popupProject.id}
                  onclick={() => { moreMenuOpenFor = null; handleRefreshProject(popupProject); }}>
                  {refreshingId === popupProject.id ? "Refreshing…" : "Refresh"}
                </button>
                {#if ps.pathExists === false}
                  <div class="more-sep"></div>
                  <button type="button" class="more-item more-item-relink" role="menuitem"
                    title="Re-point this project to a new folder on disk"
                    disabled={relinkingId === popupProject.id}
                    onclick={() => { moreMenuOpenFor = null; handleRelink(popupProject); }}>
                    {relinkingId === popupProject.id ? "Relinking…" : "Relink…"}
                  </button>
                {/if}
                {#if canUpgrade(popupProject)}
                  <div class="more-sep"></div>
                  <button type="button" class="more-item more-item-upgrade" role="menuitem"
                    title="Bump the project's Unity version to an installed version higher than the current one"
                    onclick={() => { moreMenuOpenFor = null; openUpgradeModal(popupProject); }}>
                    Upgrade Unity…
                  </button>
                {/if}
                {#if canHide(popupProject) || canUnhide(popupProject) || canMarkStale(popupProject) || canUnmarkStale(popupProject)}
                  <div class="more-sep"></div>
                  {#if canHide(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Remove this row from the list (entry kept in projects.json with hidden=true)"
                      disabled={hidingId === popupProject.id}
                      onclick={() => { moreMenuOpenFor = null; handleHide(popupProject); }}>
                      {hidingId === popupProject.id ? "Hiding…" : "Hide"}
                    </button>
                  {/if}
                  {#if canUnhide(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Restore this row to the default list view"
                      disabled={hidingId === popupProject.id}
                      onclick={() => { moreMenuOpenFor = null; handleUnhide(popupProject); }}>
                      {hidingId === popupProject.id ? "Unhiding…" : "Unhide"}
                    </button>
                  {/if}
                  {#if canMarkStale(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Keep the row visible with a 'stale' chip (excluded from launch / running-Unity actions)"
                      disabled={markingStaleId === popupProject.id}
                      onclick={() => { moreMenuOpenFor = null; handleMarkStale(popupProject); }}>
                      {markingStaleId === popupProject.id ? "Marking…" : "Mark stale"}
                    </button>
                  {/if}
                  {#if canUnmarkStale(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Clear the stale flag"
                      disabled={markingStaleId === popupProject.id}
                      onclick={() => { moreMenuOpenFor = null; handleUnmarkStale(popupProject); }}>
                      {markingStaleId === popupProject.id ? "Unmarking…" : "Unmark stale"}
                    </button>
                  {/if}
                {/if}
                <div class="more-sep"></div>
                <button type="button" class="more-item more-item-destructive" role="menuitem"
                  title="Remove this project from the Hub list"
                  disabled={removingId === popupProject.id}
                  onclick={() => handleRemove(popupProject.id)}>
                  {removingId === popupProject.id ? "Removing…" : "Remove from list"}
                </button>
              </div>
            {/if}
          </div>
        </div>

        <div class="settings-panels-grid">
          <section class="mini-panel">
            <header class="mini-panel-head">
              <h4 class="mini-panel-title">Launch args</h4>
              <p class="mini-panel-hint">
                Extra command-line arguments appended after the launch mode and
                <code>-buildTarget</code>. Most projects can be left empty.
              </p>
            </header>
            <textarea
              class="args-input"
              rows="2"
              spellcheck="false"
              placeholder="Optional: additional Unity launch arguments…"
              value={getArgsDraft(popupProject.id)}
              oninput={(e) => handleArgsInput(popupProject.id, (e.currentTarget as HTMLTextAreaElement).value)}
              aria-label="Launch args"
            ></textarea>
            {#if argsErrors[popupProject.id]}
              <p class="field-error">{argsErrors[popupProject.id]}</p>
            {/if}
            <div class="args-actions">
              <Button variant="primary"
                disabled={getArgsDraft(popupProject.id) === (popupProject.launchArgs ?? "") || !getArgsDraft(popupProject.id).trim() || !!argsErrors[popupProject.id] || savingArgsFor === popupProject.id}
                onclick={() => handleSaveArgs(popupProject)}>
                {savingArgsFor === popupProject.id ? "…" : "Save"}
              </Button>
              <Button variant="secondary"
                disabled={(popupProject.launchArgs ?? "") === "" || savingArgsFor === popupProject.id}
                onclick={() => handleResetArgs(popupProject)}>
                Reset
              </Button>
              <Button variant="secondary"
                title="Show example launch arguments and a link to the docs"
                onclick={() => toggleLaunchArgsInfo()}>
                Info
              </Button>
            </div>
          </section>

          <section class="mini-panel">
            <header class="mini-panel-head">
              <h4 class="mini-panel-title">Platform intent</h4>
              <p class="mini-panel-hint">
                Preferred <code>BuildTarget</code> for the next launch. Hub
                appends <code>-buildTarget &lt;name&gt;</code> to the Unity
                command line. Leave as <strong>None</strong> to launch without
                a target — Unity will use the project's current build settings.
                Only applied on the next launch; not used for a running Editor.
              </p>
            </header>
            <div class="intent-row">
              <Select
                class="intent-select"
                options={[
                  { value: "", label: "None (default)" },
                  ...intentOptions(popupProject.platformIntent ?? "").map((target) => ({
                    value: target,
                    label: BUILD_TARGET_LABELS[target] ?? target,
                  })),
                ]}
                value={getIntentDraft(popupProject.id)}
                onchange={(v) => handleIntentChange(popupProject.id, v)}
              />
              <Button variant="primary"
                disabled={getIntentDraft(popupProject.id) === (popupProject.platformIntent ?? "") || savingIntentFor === popupProject.id}
                onclick={() => handleSaveIntent(popupProject)}>
                {savingIntentFor === popupProject.id ? "…" : "Save"}
              </Button>
            </div>
            <p class="intent-status">
              {#if popupProject.platformIntent}
                Active: <strong>{popupProject.platformIntent}</strong> (applied on next launch)
              {:else if popupDefaultBuildTarget}
                No platform intent set — Unity will use the project's default build target
                (<strong title={popupDefaultBuildTarget}>{buildTargetLabel(popupDefaultBuildTarget)}</strong>,
                from <code>ProjectSettings/ProjectSettings.asset</code>).
              {:else if popupDefaultBuildTarget === null}
                No platform intent set and no default recorded in
                <code>ProjectSettings/ProjectSettings.asset</code> — Unity will pick its own default
                (typically <strong>Standalone</strong>).
              {:else}
                No platform intent set — reading default build target…
              {/if}
            </p>
          </section>

          <section class="mini-panel">
            <header class="mini-panel-head">
              <h4 class="mini-panel-title">Log shortcuts</h4>
            </header>
            {#if logPathsMap[popupProject.id]}
              {@const lp = logPathsMap[popupProject.id]}
              <div class="log-grid">
                <div class="log-row">
                  <span class="log-label">Editor logs</span>
                  <Button variant="secondary" disabled={!lp.editorLogsFolder}
                    onclick={() => { if (lp.editorLogsFolder) openPath(lp.editorLogsFolder); }}>
                    Open folder
                  </Button>
                </div>
                <div class="log-row">
                  <span class="log-label">Player logs</span>
                  <Button variant="secondary" disabled={!lp.playerLogsFolder}
                    onclick={() => { if (lp.playerLogsFolder) openPath(lp.playerLogsFolder); }}>
                    Open folder
                  </Button>
                </div>
                <div class="log-row">
                  <span class="log-label">Crash logs</span>
                  <Button variant="secondary" disabled={!lp.crashLogsFolder}
                    onclick={() => { if (lp.crashLogsFolder) openPath(lp.crashLogsFolder); }}>
                    Open folder
                  </Button>
                </div>
                <div class="log-row">
                  <span class="log-label">Editor.log</span>
                  {#if lp.editorLogFile}
                    <Button variant="secondary"
                      title={lp.editorLogFile}
                      disabled={!lp.editorLogFile}
                      onclick={() => openPath(lp.editorLogFile!)}>
                      Open file
                    </Button>
                  {:else}
                    <span class="muted-inline">—</span>
                  {/if}
                </div>
              </div>
            {:else}
              <p class="panel-empty">Loading log paths…</p>
            {/if}
          </section>

          <section class="mini-panel">
            <header class="mini-panel-head">
              <h4 class="mini-panel-title">Environment variables</h4>
              <p class="mini-panel-hint">
                Merged into the spawned Unity process for this project.
                Values in the child override the parent process when
                keys collide. The safety toggle in
                <code>Settings → Safety</code> controls whether the
                Launch button shows a confirmation listing colliding
                keys.
              </p>
            </header>
            {#if envVarsError}
              <p class="field-error" role="alert">{envVarsError}</p>
            {/if}
            {#if envVarsInfo}
              <p class="field-hint" role="status">{envVarsInfo}</p>
            {/if}
            <div class="env-grid">
              {#each envVarsDraft as row (row.uid)}
                <div class="env-row">
                  <input
                    type="text"
                    class="env-key"
                    placeholder="KEY"
                    bind:value={row.key}
                    aria-label="Environment variable name"
                    spellcheck="false"
                    autocomplete="off"
                  />
                  <div class="env-value-wrap">
                    <input
                      type={envVarsRevealed[row.uid] ? "text" : "password"}
                      class="env-value"
                      placeholder="value"
                      bind:value={row.value}
                      aria-label="Environment variable value"
                      spellcheck="false"
                      autocomplete="off"
                    />
                    <button
                      type="button"
                      class="link-btn env-reveal"
                      onclick={() => toggleEnvReveal(row.uid)}
                      aria-label={envVarsRevealed[row.uid] ? "Hide value" : "Show value"}
                    >
                      {envVarsRevealed[row.uid] ? "Hide" : "Show"}
                    </button>
                  </div>
                  <button
                    type="button"
                    class="link-btn env-remove"
                    onclick={() => removeEnvVarRow(row.uid)}
                    aria-label="Remove env var row"
                  >
                    Remove
                  </button>
                </div>
              {/each}
            </div>
            <div class="env-actions">
              <Button variant="secondary" onclick={addEnvVarRow}>+ Add env var</Button>
              <Button
                variant="primary"
                disabled={envVarsSaving}
                onclick={saveEnvVars}
              >
                {envVarsSaving ? "Saving…" : "Save"}
              </Button>
            </div>
          </section>
        </div>
      </div>
    </div>
  </div>
{/if}

{#if launchArgsInfoOpen}
  <div
    class="settings-overlay"
    role="presentation"
    onclick={toggleLaunchArgsInfo}
    onkeydown={(e) => {
      if (e.key === "Escape") toggleLaunchArgsInfo();
    }}
  >
    <div
      class="settings-modal info-modal"
      role="dialog"
      aria-modal="true"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => {
        if (e.key === "Escape") toggleLaunchArgsInfo();
      }}
    >
      <div class="settings-modal-header">
        <div class="settings-modal-titles">
          <h2>Launch args — examples</h2>
          <span class="settings-modal-path">Extra arguments appended to the Unity command line</span>
        </div>
        <button
          type="button"
          class="modal-close-btn"
          aria-label="Close"
          onclick={toggleLaunchArgsInfo}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
          </svg>
        </button>
      </div>
      <div class="settings-modal-body">
        <section class="info-block">
          <h3 class="info-title">Example</h3>
          <p class="info-text">
            Paste one or more space-separated flags. Hub will append them after
            the launch mode and the <code>-buildTarget</code> flag (if set).
            For example, to run Unity headless and stream its log to stdout:
          </p>
          <pre class="info-code">-batchmode -nographics -logFile -</pre>
        </section>

        <section class="info-block">
          <h3 class="info-title">Common arguments</h3>
          <ul class="info-list">
            {#each LAUNCH_ARGS_EXAMPLES as ex (ex.args)}
              <li class="info-item">
                <code class="info-code-inline">{ex.args}</code>
                <span class="info-desc">{ex.description}</span>
              </li>
            {/each}
          </ul>
        </section>

        <section class="info-block">
          <h3 class="info-title">Documentation</h3>
          <p class="info-text">
            The full list of supported command-line arguments lives in the
            Unity Manual:
          </p>
          <button
            type="button"
            class="info-link"
            onclick={openLaunchArgsDocs}
          >
            {LAUNCH_ARGS_DOCS_URL} ↗
          </button>
        </section>
      </div>
    </div>
  </div>
{/if}

<style>
  .projects {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    gap: 0.6rem;
  }

  /**
   * Drag-and-drop visual affordance. The Tauri webview fires enter/over
   * events for any drag entering the window; the project list draws a
   * dashed accent border and tints the toolbar while a drag is over the
   * tab. The list itself does not need its position changed — Tauri
   * blocks the default HTML drop behavior at the window level so the
   * OS cursor is the only thing the user sees moving.
   */
  .projects.drag-over .table {
    border-color: var(--hub-accent);
    box-shadow: 0 0 0 1px var(--hub-accent) inset, 0 0 0 4px rgba(92, 124, 250, 0.18);
    transition: border-color 0.12s ease, box-shadow 0.12s ease;
  }

  .projects.drag-over .toolbar {
    outline: 1px dashed var(--hub-accent);
    outline-offset: 2px;
    border-radius: 6px;
  }

  .toolbar {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
  }

  .toolbar-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .toolbar-spacer {
    flex: 1;
  }

  .icon-btn {
    align-self: center;
    width: 2.2rem;
    height: 2.2rem;
    margin-top: 0px;
    padding: 0;
    border-radius: 6px;
    border: 1px solid var(--hub-border-hover);
    background: var(--hub-selected);
    color: var(--hub-text);
    cursor: pointer;
    line-height: 1.4;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 0.82rem;
    font-weight: 500;
    font-family: inherit;
  }

  .icon-btn:hover:not(:disabled) {
    border-color: var(--hub-accent);
    color: var(--hub-text-bright);
  }

  .icon-btn:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .icon-btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .icon-btn .icon-spin {
    animation: icon-spin 0.9s linear infinite;
  }

  @keyframes icon-spin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
  }

  .toolbar-status {
    font-size: 0.78rem;
    color: var(--hub-text-muted);
  }

  .inline-error {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.7rem;
    border: 1px solid var(--hub-error-fg);
    border-radius: 6px;
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
    font-size: 0.82rem;
  }

  .inline-error-text { flex: 1; }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: var(--hub-error-fg);
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover { color: var(--hub-text-bright); }

  .search {
    flex: 0 1 18rem;
    padding: 0.45rem 0.65rem;
    border-radius: 6px;
    border: 1px solid var(--hub-border-light);
    background: var(--hub-surface);
    color: var(--hub-text);
    font-size: 0.85rem;
    outline: none;
  }

  .search::placeholder { color: var(--hub-text-placeholder); }
  .search:focus-visible {     border-color: var(--hub-accent); }

  .filter-btn {
    padding: 0.4rem 0.7rem;
    background: transparent;
    color: var(--hub-text-dim);
    border: none;
    font-size: 0.78rem;
    cursor: pointer;
    line-height: 1.4;
  }

  .filter-btn:hover { color: var(--hub-text-bright); background: var(--hub-bg); }
  .filter-btn.filter-active { background: var(--hub-selected); color: var(--hub-text-bright); }

  .table {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 8rem;
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-bg);
    overflow: hidden;
  }

  .table-head {
    display: grid;
    flex-shrink: 0;
    background: var(--hub-surface);
    border-bottom: 1px solid var(--hub-border);
    padding: 0 0.25rem;
  }

  .th {
    padding: 0.55rem 0.7rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
    user-select: none;
    cursor: default;
  }

  .th-settings { padding: 0.55rem 0.3rem; }

  .table-body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding-top: 0.4rem;
  }

  .row-wrapper {
    border-bottom: 1px solid var(--hub-card);
  }

  .row-wrapper:last-child { border-bottom: none; }

  .row {
    display: grid;
    align-items: center;
    padding: 0 0.25rem;
    cursor: pointer;
    transition: background 0.08s ease;
  }

  .row:hover { background: var(--hub-surface); }

  .row-selected {
    background: var(--hub-selected) !important;
  }

  .row-missing { opacity: 0.72; }

  .cell {
    padding: 0 0.7rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cell-settings {
    padding: 0.15rem;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.15rem;
    border-left: 1px solid var(--hub-card);
  }

  .row-action-btn {
    flex: 0 0 auto;
    border: 1px solid transparent;
    background: transparent;
    color: var(--hub-text-dim);
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    line-height: 1;
    border-radius: 6px;
  }

  .row-action-btn:hover {
    color: var(--hub-text-bright);
    background: var(--hub-bg);
  }

  .row-action-btn:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .ai-row-btn {
    width: 2.5rem;
    height: 2.5rem;
    font-size: 0.75rem;
    font-weight: 600;
    letter-spacing: 0.02em;
  }

  .settings-btn {
    width: 2.5rem;
    height: 2.5rem;
  }

  .cell-name {
    padding: 0.45rem 0.7rem;
    overflow: visible;
  }

  .name-path {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
  }

  .name-text {
    font-weight: 500;
    color: var(--hub-text);
    font-size: 0.88rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
  }

  .source-tag {
    display: inline-flex;
    align-items: center;
    padding: 0.05rem 0.4rem;
    border-radius: 999px;
    font-size: 0.6rem;
    font-weight: 600;
    line-height: 1.5;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    border: 1px solid transparent;
    white-space: nowrap;
  }

  .source-walkup {
    background: rgba(92, 124, 250, 0.18);
    color: var(--hub-source-walkup-fg);
    border-color: rgba(92, 124, 250, 0.45);
  }

  .source-hubseed {
    background: rgba(110, 118, 140, 0.18);
    color: var(--hub-source-seed-fg);
    border-color: rgba(110, 118, 140, 0.45);
  }

  .path-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    color: var(--hub-text-placeholder);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .cell-version .version-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    color: var(--hub-text-dim);
  }

  .cell-size .size-text {
    font-size: 0.78rem;
    color: var(--hub-text-dim);
    font-variant-numeric: tabular-nums;
  }

  .branch-chip {
    display: inline-block;
    max-width: 100%;
    padding: 0.1rem 0.45rem;
    border: 1px solid var(--hub-branch-chip-border);
    border-radius: 999px;
    background: var(--hub-branch-chip-bg);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    color: var(--hub-branch-chip-fg);
    line-height: 1.3;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .branch-detached {
    border-color: var(--hub-branch-detached-border);
    background: var(--hub-branch-detached-bg);
    color: var(--hub-branch-detached-fg);
  }

  .muted { color: var(--hub-text-placeholder); }

  .chips {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    flex-wrap: nowrap;
  }

  .empty-state {
    text-align: center;
    color: var(--hub-text-muted);
    padding: 2rem 0;
  }

  .empty-state p { margin: 0.2rem 0; font-size: 0.88rem; }
  .empty-state .empty-hint { font-size: 0.78rem; color: var(--hub-text-placeholder); }

  .settings-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .settings-panels-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.5rem;
  }

  .mini-panel {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.55rem 0.65rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-bg);
  }

  .mini-panel-head {
    display: flex;
    flex-direction: column;
  }

  .mini-panel-title {
    margin: 0;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
  }

  .args-input {
    flex: 1;
    min-height: 2.4rem;
    padding: 0.35rem 0.5rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-light);
    background: var(--hub-bg);
    color: var(--hub-text);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    line-height: 1.3;
    resize: vertical;
    outline: none;
  }

  .args-input:focus-visible {     border-color: var(--hub-accent); }

  .args-actions {
    display: flex;
    flex-direction: row;
    gap: 0.4rem;
    align-items: stretch;
  }

  .args-actions :global(.btn) {
    flex: 1 1 0;
    min-width: 0;
    justify-content: center;
  }

  .field-error {
    margin: 0;
    font-size: 0.74rem;
    color: var(--hub-error-fg);
  }

  .field-hint {
    margin: 0;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
  }

  .env-grid {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .env-row {
    display: grid;
    grid-template-columns: minmax(6rem, 0.8fr) 1fr auto;
    align-items: center;
    gap: 0.3rem;
  }

  .env-key,
  .env-value {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    color: var(--hub-text);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    padding: 0.3rem 0.45rem;
    outline: none;
    width: 100%;
    box-sizing: border-box;
  }

  .env-key:focus,
  .env-value:focus {
    border-color: var(--hub-accent);
  }

  .env-value-wrap {
    position: relative;
    display: flex;
    align-items: stretch;
    gap: 0.25rem;
  }

  .env-value-wrap .env-value { flex: 1; min-width: 0; }

  .env-reveal {
    flex-shrink: 0;
  }

  .env-remove {
    flex-shrink: 0;
    color: var(--hub-error-fg);
  }

  .env-remove:hover { color: var(--hub-text-bright); }

  .env-actions {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .link-btn {
    background: transparent;
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    padding: 0.25rem 0.55rem;
    color: var(--hub-text-dim);
    font-size: 0.74rem;
    cursor: pointer;
    line-height: 1.3;
    font-family: inherit;
  }

  .link-btn:hover { color: var(--hub-text-bright);     border-color: var(--hub-accent); }

  .mini-panel-hint {
    margin: 0.2rem 0 0;
    font-size: 0.7rem;
    color: var(--hub-text-placeholder);
    line-height: 1.45;
  }

  .mini-panel-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    background: var(--hub-bg);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text-dim);
  }

  .intent-row {
    display: flex;
    flex-direction: row;
    gap: 0.35rem;
    align-items: center;
  }

  .intent-status {
    margin: 0;
    font-size: 0.72rem;
    color: var(--hub-text-muted);
  }

  .intent-status strong {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    color: var(--hub-text-dim);
    font-weight: 500;
  }

  .log-grid {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .log-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
  }

  .log-row :global(.btn) {
    min-width: 6.5rem;
    justify-content: center;
  }

  .log-label {
    flex: 0 0 5.5rem;
    font-size: 0.72rem;
    color: var(--hub-text-dim);
    font-weight: 500;
  }

  .muted-inline { color: var(--hub-text-placeholder); font-size: 0.74rem; }

  .panel-empty {
    margin: 0;
    font-size: 0.74rem;
    color: var(--hub-text-placeholder);
  }

  .more-wrap { position: relative; }

  .more-menu {
    position: absolute;
    right: 0;
    top: calc(100% + 0.25rem);
    z-index: 50;
    min-width: 11rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45);
    padding: 0.25rem;
    display: flex;
    flex-direction: column;
  }

  .more-item {
    text-align: left;
    padding: 0.4rem 0.6rem;
    background: transparent;
    border: none;
    color: var(--hub-text);
    font-size: 0.82rem;
    border-radius: 4px;
    cursor: pointer;
  }

  .more-item:hover { background: var(--hub-bg); color: var(--hub-text-bright); }

  .more-item-destructive { color: var(--hub-error-fg); }
  .more-item-destructive:hover {     background: var(--hub-error-bg); color: var(--hub-text-bright); }
  .more-item-destructive:disabled,
  .more-item:disabled {     color: var(--hub-text-disabled); cursor: not-allowed; background: transparent; }

  .more-item-relink { color: var(--hub-relink-fg); }
  .more-item-relink:hover { background: var(--hub-relink-hover-bg); color: var(--hub-text-bright); }
  .more-item-relink:disabled {     color: var(--hub-text-disabled); cursor: not-allowed; background: transparent; }

  .more-sep {
    height: 1px;
    background: var(--hub-border);
    margin: 0.25rem 0;
  }

  .ctx-menu {
    position: fixed;
    z-index: 100;
    min-width: 11rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.5);
    padding: 0.25rem;
    display: flex;
    flex-direction: column;
  }

  .ctx-item {
    text-align: left;
    padding: 0.4rem 0.6rem;
    background: transparent;
    border: none;
    color: var(--hub-text);
    font-size: 0.82rem;
    border-radius: 4px;
    cursor: pointer;
  }

  .ctx-item:hover { background: var(--hub-bg); color: var(--hub-text-bright); }
  .ctx-item-destructive { color: var(--hub-error-fg); }
  .ctx-item-destructive:hover {     background: var(--hub-error-bg); color: var(--hub-text-bright); }
  .ctx-item-destructive:disabled,
  .ctx-item:disabled {     color: var(--hub-text-disabled); cursor: not-allowed; background: transparent; }

  .ctx-item-relink { color: var(--hub-relink-fg); }
  .ctx-item-relink:hover { background: var(--hub-relink-hover-bg); color: var(--hub-text-bright); }
  .ctx-item-relink:disabled {     color: var(--hub-text-disabled); cursor: not-allowed; background: transparent; }

  .ctx-sep {
    height: 1px;
    background: var(--hub-border);
    margin: 0.25rem 0;
  }

  .settings-overlay {
    position: fixed;
    inset: 0;
    z-index: 200;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .settings-modal {
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 12px;
    width: min(40rem, 92vw);
    max-height: 90vh;
    display: flex;
    flex-direction: column;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.45);
  }

  .settings-modal-header {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.85rem 1rem;
    border-bottom: 1px solid var(--hub-border);
  }

  .settings-modal-titles {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    min-width: 0;
  }

  .settings-modal-header h2 {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    color: var(--hub-text-bright);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .settings-modal-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    color: var(--hub-text-placeholder);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .settings-modal-body {
    padding: 0.85rem 1rem 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
    overflow-y: auto;
  }

  .modal-close-btn {
    padding: 0.3rem;
    border-radius: 4px;
    border: 1px solid transparent;
    background: transparent;
    color: var(--hub-text-muted);
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    line-height: 1;
    flex-shrink: 0;
  }

  .modal-close-btn:hover {
    color: var(--hub-text-bright);
    border-color: var(--hub-border-hover);
    background: var(--hub-selected);
  }

  .info-modal {
    width: min(34rem, 92vw);
  }

  .info-block {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .info-title {
    margin: 0;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
  }

  .info-text {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-text-dim);
    line-height: 1.5;
  }

  .info-text code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: var(--hub-bg);
    padding: 0 0.3rem;
    border-radius: 3px;
    color: var(--hub-text);
  }

  .info-code {
    margin: 0;
    padding: 0.5rem 0.65rem;
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: var(--hub-text);
    white-space: pre-wrap;
    word-break: break-all;
  }

  .info-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .info-item {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    padding: 0.4rem 0.55rem;
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
  }

  .info-code-inline {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text);
  }

  .info-desc {
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    line-height: 1.45;
  }

  .info-link {
    align-self: flex-start;
    background: transparent;
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    padding: 0.35rem 0.6rem;
    color: var(--hub-accent);
    font-size: 0.78rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    cursor: pointer;
    text-align: left;
  }

  .info-link:hover {
    border-color: var(--hub-accent);
    background: var(--hub-bg);
    color: var(--hub-info-fg);
  }

  .walkup-overlay {
    position: fixed;
    inset: 0;
    background: rgba(10, 10, 14, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 100;
    padding: 1rem;
  }

  .walkup-modal {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border);
    border-radius: 10px;
    width: 100%;
    max-width: 32rem;
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
    padding: 1.1rem 1.25rem 1rem;
    max-height: 80vh;
    overflow-y: auto;
  }

  .walkup-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .walkup-title {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    color: var(--hub-text);
  }

  .walkup-close {
    background: transparent;
    border: none;
    color: var(--hub-text-muted);
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
    padding: 0 0.4rem;
  }

  .walkup-close:hover {
    color: var(--hub-text);
  }

  .walkup-body {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .walkup-desc {
    margin: 0;
    font-size: 0.82rem;
    color: var(--hub-text-muted);
    line-height: 1.5;
  }

  .walkup-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .walkup-section-title {
    margin: 0;
    font-size: 0.74rem;
    font-weight: 600;
    color: var(--hub-text-dim);
    text-transform: uppercase;
    letter-spacing: 0.07em;
  }

  .walkup-roots {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    max-height: 6.5rem;
    overflow-y: auto;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-bg);
    padding: 0.4rem 0.5rem;
  }

  .walkup-root {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-source-seed-fg);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .walkup-empty {
    margin: 0;
    font-size: 0.8rem;
    color: var(--hub-error-fg);
    line-height: 1.5;
  }

  .walkup-config-list,
  .walkup-progress-list {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.35rem 0.85rem;
    margin: 0;
  }

  .walkup-config-list div,
  .walkup-progress-list div {
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
  }

  .walkup-config-list dt,
  .walkup-progress-list dt {
    font-size: 0.7rem;
    color: var(--hub-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.07em;
  }

  .walkup-config-list dd,
  .walkup-progress-list dd {
    margin: 0;
    font-size: 0.86rem;
    color: var(--hub-text);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .walkup-config,
  .walkup-progress,
  .walkup-done {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    padding-top: 0.55rem;
    border-top: 1px dashed var(--hub-border-light);
  }

  .walkup-done-line {
    margin: 0;
    font-size: 0.86rem;
    color: var(--hub-text-dim);
  }

  .walkup-done-line strong {
    color: var(--hub-text);
  }

  .walkup-error {
    margin: 0;
    color: var(--hub-error-fg);
    font-size: 0.8rem;
  }

  .walkup-footer {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    border-top: 1px solid var(--hub-border-light);
    padding-top: 0.7rem;
  }

  .walkup-footer-hint {
    flex: 1;
    min-width: 0;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    line-height: 1.45;
  }

  /**
   * M1.5-12 / M1.5-13 — New project modal. Reuses the walk-up
   * overlay + header / footer style so the two modals feel like the
   * same family; the body has its own field-level layout.
   */
  .newproj-overlay {
    position: fixed;
    inset: 0;
    background: rgba(10, 10, 14, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 100;
    padding: 1rem;
  }

  .newproj-modal {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border);
    border-radius: 10px;
    width: 100%;
    max-width: 34rem;
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
    padding: 1.1rem 1.25rem 1rem;
    max-height: 86vh;
    overflow-y: auto;
  }

  .newproj-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .newproj-title {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    color: var(--hub-text);
  }

  .newproj-body {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .newproj-desc {
    margin: 0;
    font-size: 0.82rem;
    color: var(--hub-text-muted);
    line-height: 1.5;
  }

  .newproj-field {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .newproj-label {
    font-size: 0.74rem;
    color: var(--hub-text-dim);
    text-transform: uppercase;
    letter-spacing: 0.07em;
    font-weight: 600;
  }

  .newproj-input {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    color: var(--hub-text);
    font-size: 0.86rem;
    font-family: inherit;
    padding: 0.4rem 0.5rem;
    width: 100%;
    box-sizing: border-box;
  }

  .newproj-input:focus {
    outline: 2px solid var(--hub-accent);
    outline-offset: 0;
    border-color: var(--hub-accent);
  }

  .newproj-input:disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }

  .newproj-input-row {
    display: flex;
    gap: 0.4rem;
    align-items: stretch;
  }

  .newproj-input-row .newproj-input {
    flex: 1;
    min-width: 0;
  }

  .newproj-hint {
    margin: 0;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    line-height: 1.4;
  }

  .newproj-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .newproj-hint-warn {
    color: var(--hub-error-fg);
  }

  .newproj-template-row {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-bg);
  }

  .newproj-template-option {
    display: grid;
    grid-template-columns: auto 1fr;
    align-items: start;
    column-gap: 0.5rem;
    row-gap: 0.1rem;
    font-size: 0.85rem;
    color: var(--hub-text);
    cursor: pointer;
  }

  .newproj-template-option input[type="radio"] {
    margin-top: 0.18rem;
  }

  .newproj-template-option.newproj-template-disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }

  .newproj-template-hint {
    grid-column: 2 / 3;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    line-height: 1.4;
  }

  :global(.newproj-template-picker) {
    margin-left: 1.4rem;
    max-width: calc(100% - 1.4rem);
  }

  .newproj-save-hint {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
    margin-left: 1.4rem;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    line-height: 1.4;
  }

  .newproj-error {
    margin: 0;
    color: var(--hub-error-fg);
    font-size: 0.8rem;
    line-height: 1.4;
  }

  .newproj-overwrite {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--hub-error-fg);
    border-radius: 6px;
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
    font-size: 0.78rem;
  }

  .newproj-overwrite-hint {
    flex: 1;
    min-width: 0;
    line-height: 1.4;
  }

  .newproj-overwrite-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .newproj-footer {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    justify-content: flex-end;
    border-top: 1px solid var(--hub-border-light);
    padding-top: 0.7rem;
  }

  /**
   * M1.5-14 / M1.5-15 row + toolbar styles. The row classes are
   * flat-tone modifiers on the existing `.row` selector so a
   * missing-path / stale / hidden row picks up the right opacity
   * without any per-cell work. The toolbar `Show hidden` chip is a
   * stand-alone `.filter-btn` re-uses the filter group styling so a
   * the new affordance does not introduce a third button shape.
   */
  .row-stale { opacity: 0.85; }
  .row-hidden { opacity: 0.5; }

  .show-hidden-btn {
    margin-left: 0.4rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    color: var(--hub-text-dim);
  }

  .source-hidden {
    background: rgba(110, 118, 140, 0.18);
    color: var(--hub-source-seed-fg);
    border-color: rgba(110, 118, 140, 0.45);
  }

  .ctx-item-upgrade,
  .more-item-upgrade {
    color: var(--hub-source-walkup-fg);
  }

  .ctx-item-upgrade:hover:not(:disabled),
  .more-item-upgrade:hover:not(:disabled) {
    background: rgba(92, 124, 250, 0.18);
  }

  /**
   * Upgrade modal — same overlay / panel layout vocabulary as
   * walk-up and new-project, with a tighter color palette so the
   * user reads the action as a small but consequential change.
   */
  .upgrade-overlay {
    position: fixed;
    inset: 0;
    background: rgba(8, 9, 13, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 50;
  }

  .upgrade-modal {
    width: min(40rem, 90vw);
    max-height: 80vh;
    overflow-y: auto;
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 10px;
    padding: 1rem 1.1rem;
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .upgrade-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  .upgrade-title {
    margin: 0;
    font-size: 1rem;
    color: var(--hub-text-bright);
  }

  .upgrade-body {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .upgrade-desc {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-text-dim);
    line-height: 1.45;
  }

  .upgrade-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: var(--hub-bg);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text-dim);
  }

  .upgrade-field {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .upgrade-label {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--hub-text-muted);
    font-weight: 600;
  }

  .upgrade-summary {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 0.25rem 0.7rem;
    margin: 0;
    font-size: 0.76rem;
  }

  .upgrade-summary dt {
    color: var(--hub-text-muted);
  }

  .upgrade-summary dd {
    margin: 0;
    color: var(--hub-text);
  }

  .upgrade-summary code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: var(--hub-bg);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text-dim);
  }

  .upgrade-empty {
    margin: 0;
    padding: 0.4rem 0.6rem;
    border: 1px dashed var(--hub-border-light);
    border-radius: 6px;
    color: var(--hub-text-muted);
    font-size: 0.78rem;
  }

  .upgrade-strategy {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .upgrade-strategy-option {
    display: flex;
    align-items: flex-start;
    gap: 0.4rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    cursor: pointer;
    font-size: 0.76rem;
    color: var(--hub-text-dim);
  }

  .upgrade-strategy-option:hover {     border-color: var(--hub-accent); }

  .upgrade-strategy-option:has(input:checked) {
    border-color: var(--hub-accent);
    background: rgba(92, 124, 250, 0.08);
  }

  .upgrade-strategy-option strong { color: var(--hub-text-bright); }

  .upgrade-strategy-hint {
    display: block;
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    margin-top: 0.1rem;
  }

  .upgrade-preview {
    margin: 0.2rem 0 0;
    padding: 0.4rem 0.6rem;
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    font-size: 0.76rem;
    color: var(--hub-text-dim);
    display: flex;
    align-items: center;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .upgrade-preview-label { color: var(--hub-text-muted); }

  .upgrade-preview code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    background: var(--hub-bg);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text-dim);
  }

  .upgrade-preview-arrow { color: var(--hub-accent); font-weight: 600; }

  .upgrade-error {
    margin: 0;
    padding: 0.4rem 0.6rem;
    border: 1px solid var(--hub-error-fg);
    border-radius: 6px;
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
    font-size: 0.78rem;
  }

  .upgrade-footer {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    justify-content: flex-end;
    border-top: 1px solid var(--hub-border-light);
    padding-top: 0.7rem;
  }

  /**
   * M4 Plan 2 (M4-4): AI Setup button visual states. The same
   * classes apply whether the button lives in the toolbar or the
   * settings-popup action bar; we just need three tones to match
   * hub-ui.md §Projects toolbar (disabled / ready / incomplete).
   * The disabled state is the same as the regular disabled button
   * and is rendered through the Button component's `disabled` prop
   * — we only style the active states here.
   */
  :global(.ai-setup-btn) {
    position: relative;
  }
  :global(.ai-setup-btn.ai-setup-ready) {
    border-color: var(--hub-accent);
    color: var(--hub-text-bright);
    background: rgba(92, 124, 250, 0.16);
  }
  :global(.ai-setup-btn.ai-setup-ready:hover:not(:disabled)) {
    background: rgba(92, 124, 250, 0.28);
  }
  :global(.ai-setup-btn.ai-setup-incomplete) {
    border-color: #fbbf24;
    color: #fde68a;
    background: rgba(251, 191, 36, 0.10);
  }
  :global(.ai-setup-btn.ai-setup-incomplete:hover:not(:disabled)) {
    background: rgba(251, 191, 36, 0.18);
  }

  .ctx-item-ai-setup,
  .more-item-ai-setup {
    color: var(--hub-source-walkup-fg);
  }

  .ctx-item-ai-setup:hover:not(:disabled),
  .more-item-ai-setup:hover:not(:disabled) {
    background: rgba(92, 124, 250, 0.18);
  }
</style>
