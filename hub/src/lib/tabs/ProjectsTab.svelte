<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { runningUnityStore } from "$lib/state/running_unity.svelte";
  import { walkUpScanStore } from "$lib/state/walk_up_scan.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import {
    addProject,
    checkPathsExists,
    getCrashLogPath,
    getDefaultBuildTarget,
    getGitBranches,
    getLaunchLogTail,
    getLogPaths,
    getProjectSizes,
    isPidAlive,
    killUnity,
    launchProject,
    refreshAllProjects,
    refreshProjectVersion,
    relinkProject,
    removeProject,
    type AddProjectError,
    type KillUnityResult,
    type LaunchError,
    type LogPaths,
    type ProjectEntry,
    type RelinkProjectError,
    type RemoveProjectError,
  } from "$lib/services/config";
  import {
    compareFrecency,
    compareLastModified,
  } from "$lib/frecency";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
  import { getCurrentWebview } from "@tauri-apps/api/webview";
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import RelativeTime from "$lib/components/RelativeTime.svelte";
  import { AI_SETUP_ENABLED } from "$lib/features";

  type FilterPreset = "all" | "launchable" | "missingVersion" | "missingPath" | "running";
  type StatusKind =
    | "ok"
    | "warn"
    | "missing"
    | "missingVersion"
    | "missingPath"
    | "running"
    | "loading"
    | "unknown";

  interface RowStatus {
    pathExists: boolean | null;
    hasVersion: boolean;
    running: boolean;
    chips: { tone: "ok" | "warn" | "missing" | "running" | "info" | "muted"; label: string; title: string }[];
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
      await refreshPathExistence();
      await loadSizes();
      await loadGitBranches();
    })();
    // Start the running-Unity polling loop. The cadence is read from
    // `settings.discovery.scanIntervalSeconds` (default 5s, M1.5-10);
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

    if (exists === undefined) {
      return {
        pathExists: null,
        hasVersion,
        running,
        chips: [{ tone: "muted", label: "checking…", title: "Checking path" }],
        kind: "loading",
        launchable: false,
      };
    }

    if (!exists) {
      return {
        pathExists: false,
        hasVersion,
        running,
        chips: [{ tone: "missing", label: "missing path", title: project.path }],
        kind: "missingPath",
        launchable: false,
      };
    }

    if (!hasVersion) {
      return {
        pathExists: true,
        hasVersion: false,
        running,
        chips: [
          { tone: "warn", label: "version missing", title: "No Unity version detected" },
          { tone: "info", label: "launchable", title: "Project will try to launch" },
        ],
        kind: "missingVersion",
        launchable: false,
      };
    }

    const baseChips: { tone: "ok" | "warn" | "missing" | "running" | "info" | "muted"; label: string; title: string }[] = [
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
      chips: baseChips,
      kind: running ? "running" : "ok",
      launchable: true,
    };
  }

  let filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    const includePath = projectsStore.settings?.projectList.searchIncludesPath ?? true;
    const sortBy = projectsStore.settings?.projectList.sortBy ?? "frecency";
    // Touch the running-Unity store so the `running` filter and the
    // chip re-render on every scan tick, even if no project field
    // changes between ticks (e.g. the list is empty, or a still-running
    // Unity happens to be on a path whose row hasn't been edited).
    const runningTick = runningUnityStore.lastScanAt;
    void runningTick;
    const list = projectsStore.projects.filter((p) => {
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
          return s.pathExists === true && !s.hasVersion;
        case "missingPath":
          return s.pathExists === false;
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
    launching = id;
    try {
      const result = await launchProject(id);
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
      await handleLaunchFailure(project, err, message, autoOpen);
    } finally {
      launching = null;
    }
  }

  async function handleLaunchFailure(
    project: ProjectEntry,
    err: LaunchError,
    message: string,
    autoOpen: boolean,
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
    });

    // The original `message` was already added via `appendLaunchLog`; this
    // is a no-op in normal flow but keeps the helper self-contained for
    // future extension.
    void message;
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
        "no walk-up roots configured — add at least one in Settings → Additional parent folders";
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

  function handleAiSetupStub() {
    if (AI_SETUP_ENABLED) {
      S.appendDrawerLog("AI Setup — placeholder for M4 wizard");
      return;
    }
    S.appendDrawerLog("AI Setup — coming in a later milestone");
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
    const settings = "2.6rem";
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
    { id: "missingPath", label: "Missing path" },
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
    <input
      type="search"
      class="search"
      placeholder="Search projects…"
      bind:value={search}
      aria-label="Search projects"
    />

    <div class="filter-group" role="group" aria-label="Filter projects">
      {#each filterOptions as opt}
        <button
          type="button"
          class="filter-btn"
          class:filter-active={filterPreset === opt.id}
          onclick={() => (filterPreset = opt.id)}
          aria-pressed={filterPreset === opt.id}
        >
          {opt.label}
        </button>
      {/each}
    </div>

    <div class="toolbar-spacer"></div>

    {#if AI_SETUP_ENABLED}
      <Button variant="secondary" onclick={handleAiSetupStub} title="AI Setup — coming in M4">
        AI Setup
      </Button>
    {:else}
      <Button
        variant="secondary"
        onclick={handleAiSetupStub}
        disabled
        title="AI Setup — coming in a later milestone (reserved slot)"
      >
        AI Setup
      </Button>
    {/if}
    <Button
      variant="secondary"
      onclick={openWalkUpModal}
      disabled={walkUpScanStore.scanning}
      title="Walk-up directory scan — discover Unity projects under configured roots"
    >
      {walkUpScanStore.scanning ? "Scanning…" : "Walk-up Scan…"}
    </Button>
    <Button variant="primary" onclick={handleAddProject} disabled={addingProject}>
      {addingProject ? "Adding…" : "Add Project"}
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

  <p class="drop-hint" aria-hidden="true">
    Drop a Unity project folder here to add it to the list.
  </p>

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
                <button
                  type="button"
                  class="settings-btn"
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
        <h2 id="walkup-modal-title" class="walkup-title">Walk-up directory scan</h2>
        {#if !walkUpScanStore.scanning}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close walk-up scan"
            onclick={closeWalkUpModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="walkup-body">
        <p class="walkup-desc">
          Hub will recurse into the configured roots and append every
          folder that contains both <code>Assets/</code> and
          <code>ProjectSettings/</code> to the project list as
          <code>source: walk-up</code>.
        </p>

        <section class="walkup-config">
          <h3 class="walkup-section-title">Configured roots</h3>
          {#if settingsStore.current && settingsStore.current.unityDiscovery.walkUpRoots.length > 0}
            <ul class="walkup-roots">
              {#each settingsStore.current.unityDiscovery.walkUpRoots as root (root)}
                <li class="walkup-root" title={root}>{root}</li>
              {/each}
            </ul>
          {:else}
            <p class="walkup-empty">
              No roots configured. Add at least one in
              <strong>Settings → Additional parent folders → Walk-up directory scan</strong>.
            </p>
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

{#if popupProject}
  {@const ps = statusFor(popupProject)}
  {@const popupIsMoreOpen = moreMenuOpenFor === popupProject.id}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="settings-overlay" onclick={closeSettingsPopup}>
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="settings-modal" onclick={(e) => e.stopPropagation()}>
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
            disabled={!ps.launchable || launching === popupProject.id}
            onclick={handlePopupLaunch}
          >
            {launching === popupProject.id ? "Launching…" : "Launch"}
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
              <select class="intent-select"
                onchange={(e) => handleIntentChange(popupProject.id, (e.currentTarget as HTMLSelectElement).value)}
                value={getIntentDraft(popupProject.id)}>
                <option value="">None (default)</option>
                {#each intentOptions(popupProject.platformIntent ?? "") as target}
                  <option value={target}>{target}</option>
                {/each}
              </select>
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
        </div>
      </div>
    </div>
  </div>
{/if}

{#if launchArgsInfoOpen}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="settings-overlay" onclick={toggleLaunchArgsInfo}>
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="settings-modal info-modal" onclick={(e) => e.stopPropagation()}>
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
    border-color: #5c7cfa;
    box-shadow: 0 0 0 1px #5c7cfa inset, 0 0 0 4px rgba(92, 124, 250, 0.18);
    transition: border-color 0.12s ease, box-shadow 0.12s ease;
  }

  .projects.drag-over .toolbar {
    outline: 1px dashed #5c7cfa;
    outline-offset: 2px;
    border-radius: 6px;
  }

  .drop-hint {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0.4rem 0.6rem;
    border: 1px dashed #5c7cfa;
    border-radius: 6px;
    background: rgba(92, 124, 250, 0.08);
    color: #c8d3ff;
    font-size: 0.78rem;
    letter-spacing: 0.02em;
  }

  .toolbar {
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
    margin-top: -2px;
    padding: 0;
    border-radius: 6px;
    border: 1px solid #474957;
    background: #32343f;
    color: #d7d8e0;
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
    border-color: #5c7cfa;
    color: #fff;
  }

  .icon-btn:focus-visible {
    outline: 2px solid #5c7cfa;
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
    color: #8b8d9a;
  }

  .inline-error {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.7rem;
    border: 1px solid #5a2333;
    border-radius: 6px;
    background: #2a1320;
    color: #f0a8b8;
    font-size: 0.82rem;
  }

  .inline-error-text { flex: 1; }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: #f0a8b8;
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover { color: #fff; }

  .search {
    flex: 0 1 18rem;
    padding: 0.45rem 0.65rem;
    border-radius: 6px;
    border: 1px solid #3f4150;
    background: #1e1f26;
    color: #e9e9ef;
    font-size: 0.85rem;
    outline: none;
  }

  .search::placeholder { color: #6f7280; }
  .search:focus-visible { border-color: #5c7cfa; }

  .filter-group {
    display: inline-flex;
    border: 1px solid #3f4150;
    border-radius: 6px;
    overflow: hidden;
    background: #1e1f26;
  }

  .filter-btn {
    padding: 0.4rem 0.7rem;
    background: transparent;
    color: #a1a3b0;
    border: none;
    border-right: 1px solid #3f4150;
    font-size: 0.78rem;
    cursor: pointer;
    line-height: 1.4;
  }

  .filter-btn:last-child { border-right: none; }
  .filter-btn:hover { color: #fff; background: #2a2b33; }
  .filter-btn.filter-active { background: #32343f; color: #f2f3f7; }

  .table {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 8rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1a1b21;
    overflow: hidden;
  }

  .table-head {
    display: grid;
    flex-shrink: 0;
    background: #1e1f26;
    border-bottom: 1px solid #34353f;
    padding: 0 0.25rem;
  }

  .th {
    padding: 0.55rem 0.7rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #8b8d9a;
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
    border-bottom: 1px solid #24252c;
  }

  .row-wrapper:last-child { border-bottom: none; }

  .row {
    display: grid;
    align-items: center;
    padding: 0 0.25rem;
    cursor: pointer;
    transition: background 0.08s ease;
  }

  .row:hover { background: #1e1f26; }

  .row-selected {
    background: #242a3a !important;
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
    padding: 0;
    display: flex;
    align-items: stretch;
    justify-content: stretch;
    border-left: 1px solid #24252c;
  }

  .settings-btn {
    flex: 1;
    width: 100%;
    height: 100%;
    border: none;
    background: transparent;
    color: #8b8d9a;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    line-height: 1;
    border-radius: 0;
  }

  .settings-btn:hover {
    background: #2a2b33;
    color: #e9e9ef;
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
    color: #e9e9ef;
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
    color: #9bb3ff;
    border-color: rgba(92, 124, 250, 0.45);
  }

  .source-hubseed {
    background: rgba(110, 118, 140, 0.18);
    color: #b4b8c5;
    border-color: rgba(110, 118, 140, 0.45);
  }

  .path-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    color: #6f7280;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .cell-version .version-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    color: #c5c7d0;
  }

  .cell-size .size-text {
    font-size: 0.78rem;
    color: #a1a3b0;
    font-variant-numeric: tabular-nums;
  }

  .branch-chip {
    display: inline-block;
    max-width: 100%;
    padding: 0.1rem 0.45rem;
    border: 1px solid #3a4255;
    border-radius: 999px;
    background: #1d2330;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    color: #b6c2d6;
    line-height: 1.3;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .branch-detached {
    border-color: #6b4f1a;
    background: #2a200f;
    color: #d8b86a;
  }

  .muted { color: #6f7280; }

  .chips {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    flex-wrap: nowrap;
  }

  .empty-state {
    text-align: center;
    color: #8b8d9a;
    padding: 2rem 0;
  }

  .empty-state p { margin: 0.2rem 0; font-size: 0.88rem; }
  .empty-state .empty-hint { font-size: 0.78rem; color: #6f7280; }

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
    border: 1px solid #2a2b33;
    border-radius: 6px;
    background: #1a1b21;
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
    color: #8b8d9a;
    font-weight: 600;
  }

  .args-input {
    flex: 1;
    min-height: 2.4rem;
    padding: 0.35rem 0.5rem;
    border-radius: 4px;
    border: 1px solid #3f4150;
    background: #14151a;
    color: #e9e9ef;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    line-height: 1.3;
    resize: vertical;
    outline: none;
  }

  .args-input:focus-visible { border-color: #5c7cfa; }

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
    color: #f0a8b8;
  }

  .mini-panel-hint {
    margin: 0.2rem 0 0;
    font-size: 0.7rem;
    color: #6f7280;
    line-height: 1.45;
  }

  .mini-panel-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    background: #2a2b33;
    padding: 0 0.25rem;
    border-radius: 3px;
    color: #c5c7d0;
  }

  .intent-row {
    display: flex;
    flex-direction: row;
    gap: 0.35rem;
    align-items: center;
  }

  .intent-select {
    flex: 1;
    padding: 0.3rem 0.4rem;
    border-radius: 4px;
    border: 1px solid #3f4150;
    background: #14151a;
    color: #e9e9ef;
    font-size: 0.78rem;
    outline: none;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .intent-select:focus-visible { border-color: #5c7cfa; }

  .intent-status {
    margin: 0;
    font-size: 0.72rem;
    color: #8b8d9a;
  }

  .intent-status strong {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    color: #c5c7d0;
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
    color: #c5c7d0;
    font-weight: 500;
  }

  .muted-inline { color: #6f7280; font-size: 0.74rem; }

  .panel-empty {
    margin: 0;
    font-size: 0.74rem;
    color: #6f7280;
  }

  .more-wrap { position: relative; }

  .more-menu {
    position: absolute;
    right: 0;
    top: calc(100% + 0.25rem);
    z-index: 50;
    min-width: 11rem;
    background: #24252c;
    border: 1px solid #3f4150;
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
    color: #d7d8e0;
    font-size: 0.82rem;
    border-radius: 4px;
    cursor: pointer;
  }

  .more-item:hover { background: #2a2b33; color: #fff; }

  .more-item-destructive { color: #f0a8b8; }
  .more-item-destructive:hover { background: #3a1a25; color: #fff; }
  .more-item-destructive:disabled,
  .more-item:disabled { color: #555; cursor: not-allowed; background: transparent; }

  .more-item-relink { color: #c8d3ff; }
  .more-item-relink:hover { background: #243056; color: #fff; }
  .more-item-relink:disabled { color: #555; cursor: not-allowed; background: transparent; }

  .more-sep {
    height: 1px;
    background: #34353f;
    margin: 0.25rem 0;
  }

  .ctx-menu {
    position: fixed;
    z-index: 100;
    min-width: 11rem;
    background: #24252c;
    border: 1px solid #3f4150;
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
    color: #d7d8e0;
    font-size: 0.82rem;
    border-radius: 4px;
    cursor: pointer;
  }

  .ctx-item:hover { background: #2a2b33; color: #fff; }
  .ctx-item-destructive { color: #f0a8b8; }
  .ctx-item-destructive:hover { background: #3a1a25; color: #fff; }
  .ctx-item-destructive:disabled,
  .ctx-item:disabled { color: #555; cursor: not-allowed; background: transparent; }

  .ctx-item-relink { color: #c8d3ff; }
  .ctx-item-relink:hover { background: #243056; color: #fff; }
  .ctx-item-relink:disabled { color: #555; cursor: not-allowed; background: transparent; }

  .ctx-sep {
    height: 1px;
    background: #34353f;
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
    background: #24252c;
    border: 1px solid #3f4150;
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
    border-bottom: 1px solid #34353f;
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
    color: #f2f3f7;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .settings-modal-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    color: #6f7280;
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
    color: #8b8d9a;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    line-height: 1;
    flex-shrink: 0;
  }

  .modal-close-btn:hover {
    color: #fff;
    border-color: #474957;
    background: #32343f;
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
    color: #8b8d9a;
    font-weight: 600;
  }

  .info-text {
    margin: 0;
    font-size: 0.78rem;
    color: #c5c7d0;
    line-height: 1.5;
  }

  .info-text code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
    color: #d7d8e0;
  }

  .info-code {
    margin: 0;
    padding: 0.5rem 0.65rem;
    background: #14151a;
    border: 1px solid #2a2b33;
    border-radius: 4px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #d7d8e0;
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
    background: #14151a;
    border: 1px solid #2a2b33;
    border-radius: 4px;
  }

  .info-code-inline {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #d7d8e0;
  }

  .info-desc {
    font-size: 0.74rem;
    color: #8b8d9a;
    line-height: 1.45;
  }

  .info-link {
    align-self: flex-start;
    background: transparent;
    border: 1px solid #3f4150;
    border-radius: 4px;
    padding: 0.35rem 0.6rem;
    color: #5c7cfa;
    font-size: 0.78rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    cursor: pointer;
    text-align: left;
  }

  .info-link:hover {
    border-color: #5c7cfa;
    background: #1a1b21;
    color: #7c9cfa;
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
    background: #1a1b21;
    border: 1px solid #34353f;
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
    color: #e9e9ef;
  }

  .walkup-close {
    background: transparent;
    border: none;
    color: #8b8d9a;
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
    padding: 0 0.4rem;
  }

  .walkup-close:hover {
    color: #e9e9ef;
  }

  .walkup-body {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .walkup-desc {
    margin: 0;
    font-size: 0.82rem;
    color: #8b8d9a;
    line-height: 1.5;
  }

  .walkup-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: #2a2b33;
    color: #d6d8e0;
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .walkup-section-title {
    margin: 0;
    font-size: 0.74rem;
    font-weight: 600;
    color: #c5c7d0;
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
    border: 1px solid #2a2b33;
    border-radius: 6px;
    background: #14151a;
    padding: 0.4rem 0.5rem;
  }

  .walkup-root {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #b4b8c5;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .walkup-empty {
    margin: 0;
    font-size: 0.8rem;
    color: #f0a8b8;
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
    color: #8b8d9a;
    text-transform: uppercase;
    letter-spacing: 0.07em;
  }

  .walkup-config-list dd,
  .walkup-progress-list dd {
    margin: 0;
    font-size: 0.86rem;
    color: #e9e9ef;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .walkup-config,
  .walkup-progress,
  .walkup-done {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    padding-top: 0.55rem;
    border-top: 1px dashed #2a2b33;
  }

  .walkup-done-line {
    margin: 0;
    font-size: 0.86rem;
    color: #c5c7d0;
  }

  .walkup-done-line strong {
    color: #e9e9ef;
  }

  .walkup-error {
    margin: 0;
    color: #f0a8b8;
    font-size: 0.8rem;
  }

  .walkup-footer {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    border-top: 1px solid #2a2b33;
    padding-top: 0.7rem;
  }

  .walkup-footer-hint {
    flex: 1;
    min-width: 0;
    font-size: 0.74rem;
    color: #8b8d9a;
    line-height: 1.45;
  }
</style>
