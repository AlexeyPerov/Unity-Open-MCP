<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import {
    addProject,
    checkPathsExists,
    killUnity,
    launchProject,
    refreshAllProjects,
    removeProject,
    type AddProjectError,
    type KillUnityResult,
    type LaunchError,
    type ProjectEntry,
    type RemoveProjectError,
  } from "$lib/services/config";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, revealItemInDir } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import RelativeTime from "$lib/components/RelativeTime.svelte";
  import VirtualList from "$lib/components/VirtualList.svelte";
  import { AI_SETUP_ENABLED } from "$lib/features";

  type FilterPreset = "all" | "launchable" | "missingVersion" | "missingPath";
  type StatusKind =
    | "ok"
    | "warn"
    | "missing"
    | "missingVersion"
    | "missingPath"
    | "loading"
    | "unknown";

  interface RowStatus {
    pathExists: boolean | null;
    hasVersion: boolean;
    chips: { tone: "ok" | "warn" | "missing" | "info" | "muted"; label: string; title: string }[];
    kind: StatusKind;
    launchable: boolean;
  }

  let search = $state("");
  let filterPreset = $state<FilterPreset>("all");
  let pathExistsMap = $state<Record<string, boolean>>({});
  let checkingPaths = $state(false);
  let launching = $state<string | null>(null);
  let contextMenu = $state<{ x: number; y: number; projectId: string } | null>(null);
  let moreMenuOpen = $state(false);
  let addingProject = $state(false);
  let refreshing = $state(false);
  let removingId = $state<string | null>(null);
  let killingId = $state<string | null>(null);
  let addError = $state<string | null>(null);
  let actionError = $state<string | null>(null);

  const ROW_HEIGHT = 38;

  onMount(() => {
    let cancelled = false;
    (async () => {
      const pendingFilter = S.consumeProjectsFilter();
      if (pendingFilter) {
        filterPreset = pendingFilter;
      }
      await projectsStore.load();
      if (cancelled) return;
      await refreshPathExistence();
    })();
    window.addEventListener("click", closeContextMenu, true);
    window.addEventListener("keydown", handleGlobalKeydown, true);
    return () => {
      cancelled = true;
      window.removeEventListener("click", closeContextMenu, true);
      window.removeEventListener("keydown", handleGlobalKeydown, true);
    };
  });

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
      S.appendDrawerLog(`path check failed: ${msg}`);
    } finally {
      checkingPaths = false;
    }
  }

  function statusFor(project: ProjectEntry): RowStatus {
    const hasVersion = !!project.unityVersion && project.unityVersion.length > 0;
    const exists = pathExistsMap[project.path];

    if (exists === undefined) {
      return {
        pathExists: null,
        hasVersion,
        chips: [{ tone: "muted", label: "checking…", title: "Checking path" }],
        kind: "loading",
        launchable: false,
      };
    }

    if (!exists) {
      return {
        pathExists: false,
        hasVersion,
        chips: [{ tone: "missing", label: "missing path", title: project.path }],
        kind: "missingPath",
        launchable: false,
      };
    }

    if (!hasVersion) {
      return {
        pathExists: true,
        hasVersion: false,
        chips: [
          { tone: "warn", label: "version missing", title: "No Unity version detected" },
          { tone: "info", label: "launchable", title: "Project will try to launch" },
        ],
        kind: "missingVersion",
        launchable: false,
      };
    }

    return {
      pathExists: true,
      hasVersion: true,
      chips: [
        { tone: "ok", label: "ok", title: "Detected" },
        { tone: "info", label: "launchable", title: "Ready to launch" },
      ],
      kind: "ok",
      launchable: true,
    };
  }

  let filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    const includePath = projectsStore.settings?.projectList.searchIncludesPath ?? true;
    return projectsStore.projects.filter((p) => {
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
        default:
          return true;
      }
    });
  });

  let selected = $derived(
    projectsStore.selectedProjectId
      ? projectsStore.projects.find((p) => p.id === projectsStore.selectedProjectId) ?? null
      : null
  );

  let selectedStatus = $derived(selected ? statusFor(selected) : null);

  function selectRow(id: string) {
    projectsStore.select(id);
  }

  function closeContextMenu() {
    contextMenu = null;
  }

  function openContextMenu(e: MouseEvent, projectId: string) {
    e.preventDefault();
    const x = e.clientX;
    const y = e.clientY;
    contextMenu = { x, y, projectId };
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
    if (moreMenuOpen && e.key === "Escape") {
      moreMenuOpen = false;
      return;
    }
  }

  function moveSelection(delta: number) {
    if (filtered.length === 0) return;
    const idx = projectsStore.selectedProjectId
      ? filtered.findIndex((p) => p.id === projectsStore.selectedProjectId)
      : -1;
    let next = idx + delta;
    if (idx === -1) next = delta > 0 ? 0 : filtered.length - 1;
    if (next < 0) next = 0;
    if (next >= filtered.length) next = filtered.length - 1;
    projectsStore.select(filtered[next].id);
  }

  async function handleLaunch(id: string) {
    if (launching) return;
    const project = projectsStore.find(id);
    if (!project) return;
    const status = statusFor(project);
    if (status.pathExists === false) {
      S.appendDrawerLog(`cannot launch: path missing — ${project.path}`);
      return;
    }
    launching = id;
    try {
      const result = await launchProject(id);
      const updated: ProjectEntry = {
        ...project,
        lastLaunchPid: result.pid,
        lastLaunchAt: new Date().toISOString(),
        unityVersion: result.unityVersion ?? project.unityVersion,
      };
      await projectsStore.update(updated);
      S.appendDrawerLog(
        `launched ${project.name} (pid ${result.pid}, ${result.unityVersion ?? "version unknown"})`
      );
    } catch (e) {
      const err = e as LaunchError;
      const message = formatLaunchError(err, project);
      S.appendDrawerLog(message);
    } finally {
      launching = null;
    }
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

  function handleRowKeydown(e: KeyboardEvent, project: ProjectEntry) {
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        moveSelection(1);
        break;
      case "ArrowUp":
        e.preventDefault();
        moveSelection(-1);
        break;
      case "Home":
        e.preventDefault();
        if (filtered.length > 0) projectsStore.select(filtered[0].id);
        break;
      case "End":
        e.preventDefault();
        if (filtered.length > 0) projectsStore.select(filtered[filtered.length - 1].id);
        break;
      case "Enter":
        e.preventDefault();
        if (statusFor(project).launchable) handleLaunch(project.id);
        break;
      case "ContextMenu":
        e.preventDefault();
        if (contextMenu && contextMenu.projectId === project.id) {
          closeContextMenu();
        } else {
          projectsStore.select(project.id);
          const row = (e.currentTarget as HTMLElement).getBoundingClientRect();
          contextMenu = {
            x: row.left + 24,
            y: row.top + 8,
            projectId: project.id,
          };
        }
        break;
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
        S.appendDrawerLog(
          `added project ${result.project.name} (${result.project.unityVersion ?? "version unknown"})`
        );
      } catch (e) {
        const err = e as AddProjectError;
        addError = formatAddProjectError(err);
        S.appendDrawerLog(`add project failed: ${addError}`);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendDrawerLog(`folder picker failed: ${msg}`);
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

  async function handleRefresh() {
    if (refreshing) return;
    refreshing = true;
    try {
      const result = await refreshAllProjects();
      projectsStore.replaceAll(result.projects.projects);
      await refreshPathExistence();
      const updatedCount = result.updated.length;
      const skippedCount = result.skipped.length;
      S.appendDrawerLog(
        `refreshed projects (${updatedCount} updated${skippedCount ? `, ${skippedCount} skipped` : ""})`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendDrawerLog(`refresh failed: ${msg}`);
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

  async function performKill(project: ProjectEntry, pid: number) {
    if (killingId) return;
    killingId = project.id;
    actionError = null;
    try {
      const result = await killUnity(pid);
      S.appendDrawerLog(formatKillResult(result));
      if (result.status === "killed" || result.status === "notFound") {
        // Clear the recorded PID for this project so subsequent Kill
        // buttons show the "no recent launch" state.
        const cleared: ProjectEntry = { ...project, lastLaunchPid: undefined };
        await projectsStore.update(cleared);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `kill failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      killingId = null;
    }
  }

  async function handleKillUnity(project: ProjectEntry) {
    closeContextMenu();
    moreMenuOpen = false;
    const pid = project.lastLaunchPid;
    if (!pid) {
      actionError = `no recent Unity launch recorded for ${project.name}`;
      S.appendDrawerLog(actionError);
      return;
    }
    const confirmKill = projectsStore.settings?.safety.confirmKillUnity ?? true;
    if (confirmKill) {
      const ok = await S.confirm(
        "Kill Unity for this project?",
        `Send a terminate signal to pid ${pid} (last launched from “${project.name}”). Other Unity instances on this machine are not affected.`
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
      S.appendDrawerLog(`remove failed: ${message}`);
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
        `“${project.name}” will be removed from the Hub project list. The project folder on disk and Unity Hub registry will not be touched.`
      );
      if (!ok) return;
    }
    closeContextMenu();
    moreMenuOpen = false;
    await performRemove(id);
  }

  function handleCopyPath(project: ProjectEntry) {
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(project.path).then(
        () => S.appendDrawerLog(`copied path: ${project.path}`),
        () => S.appendDrawerLog("copy failed: clipboard unavailable")
      );
    }
    closeContextMenu();
  }

  async function handleOpenFolder(project: ProjectEntry) {
    closeContextMenu();
    moreMenuOpen = false;
    const status = statusFor(project);
    if (status.pathExists === false) {
      actionError = `cannot open folder: path missing — ${project.path}`;
      S.appendDrawerLog(actionError);
      return;
    }
    try {
      await openPath(project.path);
      S.appendDrawerLog(`opened folder: ${project.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open folder failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  async function handleReveal(project: ProjectEntry) {
    closeContextMenu();
    moreMenuOpen = false;
    const status = statusFor(project);
    if (status.pathExists === false) {
      actionError = `cannot reveal: path missing — ${project.path}`;
      S.appendDrawerLog(actionError);
      return;
    }
    try {
      await revealItemInDir(project.path);
      S.appendDrawerLog(`revealed in file manager: ${project.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `reveal failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  function rowStatus(p: ProjectEntry) {
    return statusFor(p);
  }

  let showPath = $derived(projectsStore.settings?.projectList.showPathColumn ?? true);
  let showModified = $derived(projectsStore.settings?.projectList.showModifiedColumn ?? true);

  const filterOptions: { id: FilterPreset; label: string }[] = [
    { id: "all", label: "All" },
    { id: "launchable", label: "Launchable" },
    { id: "missingVersion", label: "Missing version" },
    { id: "missingPath", label: "Missing path" },
  ];

  function gridTemplate(): string {
    const cols = ["minmax(8rem, 1.1fr)"];
    if (showPath) cols.push("minmax(10rem, 2.4fr)");
    cols.push("minmax(6rem, 0.9fr)");
    if (showModified) cols.push("minmax(5rem, 0.8fr)");
    cols.push("minmax(11rem, 1.5fr)");
    return cols.join(" ");
  }
</script>

<div class="projects">
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
        <Button variant="primary" onclick={handleAddProject} disabled={addingProject}>
          {addingProject ? "Adding…" : "Add Project"}
        </Button>
        <Button variant="secondary" onclick={handleRefresh} disabled={refreshing}>
          {refreshing ? "Refreshing…" : "Refresh"}
        </Button>
        {#if removingId}
          <span class="toolbar-status" aria-live="polite">Removing…</span>
        {/if}
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

  <div
    class="table"
    role="grid"
    aria-rowcount={filtered.length + 1}
    aria-colcount={showPath && showModified ? 5 : showPath || showModified ? 4 : 3}
  >
    <div class="table-head" role="row" style="grid-template-columns: {gridTemplate()};">
      <div class="th" role="columnheader">Name</div>
      {#if showPath}
        <div class="th" role="columnheader">Path</div>
      {/if}
      <div class="th" role="columnheader">Version</div>
      {#if showModified}
        <div class="th" role="columnheader">Modified</div>
      {/if}
      <div class="th" role="columnheader">Status</div>
    </div>

    <div class="table-body">
      <VirtualList items={filtered} itemHeight={ROW_HEIGHT}>
        {#snippet children(project: ProjectEntry, index: number)}
          {@const s = rowStatus(project)}
          <div
            class="row"
            class:row-selected={projectsStore.selectedProjectId === project.id}
            class:row-missing={s.kind === "missingPath"}
            role="row"
            aria-rowindex={index + 2}
            aria-selected={projectsStore.selectedProjectId === project.id}
            tabindex={projectsStore.selectedProjectId === project.id ? 0 : -1}
            style="grid-template-columns: {gridTemplate()};"
            onclick={() => selectRow(project.id)}
            ondblclick={() => handleLaunch(project.id)}
            oncontextmenu={(e) => openContextMenu(e, project.id)}
            onkeydown={(e) => handleRowKeydown(e, project)}
          >
            <div class="cell cell-name" role="gridcell" title={project.name}>
              <span class="name-text">{project.name}</span>
            </div>
            {#if showPath}
              <div class="cell cell-path" role="gridcell" title={project.path}>
                <span class="path-text">{project.path}</span>
              </div>
            {/if}
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
            <div class="cell cell-status" role="gridcell">
              <div class="chips">
                {#each s.chips as chip}
                  <StatusChip tone={chip.tone} label={chip.label} title={chip.title} />
                {/each}
              </div>
            </div>
          </div>
        {/snippet}
        {#snippet empty()}
          <div class="empty-state">
            {#if projectsStore.projects.length === 0}
              <p>No projects yet.</p>
              <p class="empty-hint">Use <strong>Add Project</strong> to register a Unity project folder.</p>
            {:else}
              <p>No projects match the current filter.</p>
            {/if}
          </div>
        {/snippet}
      </VirtualList>
    </div>
  </div>

  {#if selected}
    {@const s = selectedStatus}
    <div class="detail-strip" role="region" aria-label="Selected project actions">
      <div class="detail-summary">
        <span class="detail-name">{selected.name}</span>
        {#if selected.unityVersion}
          <span class="detail-sep">·</span>
          <span class="detail-version">{selected.unityVersion}</span>
        {/if}
        <span class="detail-sep">·</span>
        <span class="detail-path" title={selected.path}>{selected.path}</span>
      </div>
      <div class="detail-actions">
        <Button
          variant="primary"
          disabled={!s?.launchable || launching === selected.id}
          onclick={() => handleLaunch(selected.id)}
        >
          {launching === selected.id ? "Launching…" : "Launch"}
        </Button>
        <Button
          variant="secondary"
          disabled={s?.pathExists === false}
          title={s?.pathExists === false ? "Path missing — cannot open folder" : "Open project folder"}
          onclick={() => handleOpenFolder(selected)}
        >
          Open Folder
        </Button>
        <Button
          variant="secondary"
          disabled={!selected.lastLaunchPid || killingId === selected.id}
          title={selected.lastLaunchPid
            ? `Terminate pid ${selected.lastLaunchPid} (last launched from this project)`
            : "No recorded Unity PID — launch Unity once to enable Kill"}
          onclick={() => handleKillUnity(selected)}
        >
          {killingId === selected.id ? "Killing…" : "Kill Unity"}
        </Button>
        <div class="more-wrap">
          <Button
            variant="secondary"
            onclick={() => (moreMenuOpen = !moreMenuOpen)}
            aria-haspopup="menu"
            aria-expanded={moreMenuOpen}
          >
            More ▾
          </Button>
          {#if moreMenuOpen}
            <!-- svelte-ignore a11y_interactive_supports_focus -->
            <!-- svelte-ignore a11y_click_events_have_key_events -->
            <div class="more-menu" role="menu">
              <button
                type="button"
                class="more-item"
                role="menuitem"
                onclick={() => {
                  moreMenuOpen = false;
                  handleCopyPath(selected);
                }}
              >
                Copy path
              </button>
              <button
                type="button"
                class="more-item"
                role="menuitem"
                disabled={s?.pathExists === false}
                title={s?.pathExists === false ? "Path missing — cannot reveal" : "Reveal in file manager"}
                onclick={() => handleReveal(selected)}
              >
                Reveal in file manager
              </button>
              <div class="more-sep"></div>
              <button
                type="button"
                class="more-item more-item-destructive"
                role="menuitem"
                disabled={removingId === selected.id}
                onclick={() => handleRemove(selected.id)}
              >
                {removingId === selected.id ? "Removing…" : "Remove from list"}
              </button>
            </div>
          {/if}
        </div>
      </div>
    </div>
  {/if}
</div>

{#if contextMenu}
  {@const ctxId = contextMenu.projectId}
  {@const ctxProject = projectsStore.find(ctxId)}
  {@const ctxStatus = ctxProject ? statusFor(ctxProject) : null}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_interactive_supports_focus -->
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
      title={ctxStatus?.pathExists === false ? "Path missing — cannot open folder" : "Open project folder"}
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
      disabled={ctxStatus?.pathExists === false}
      title={ctxStatus?.pathExists === false ? "Path missing — cannot reveal" : "Reveal in file manager"}
      onclick={() => {
        if (ctxProject) handleReveal(ctxProject);
        closeContextMenu();
      }}
    >
      Reveal in file manager
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
      disabled={!ctxProject?.lastLaunchPid || killingId === ctxId}
      title={ctxProject?.lastLaunchPid
        ? `Terminate pid ${ctxProject.lastLaunchPid} (last launched from this project)`
        : "No recorded Unity PID — launch Unity once to enable Kill"}
      onclick={() => {
        if (ctxProject) handleKillUnity(ctxProject);
      }}
    >
      {killingId === ctxId ? "Killing…" : "Kill Unity"}
    </button>
    <div class="ctx-sep"></div>
    <button
      type="button"
      class="ctx-item ctx-item-destructive"
      role="menuitem"
      disabled={removingId === ctxId}
      onclick={() => {
        handleRemove(ctxId);
      }}
    >
      {removingId === ctxId ? "Removing…" : "Remove from list"}
    </button>
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

  .inline-error-text {
    flex: 1;
  }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: #f0a8b8;
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover {
    color: #fff;
  }

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

  .search::placeholder {
    color: #6f7280;
  }

  .search:focus-visible {
    border-color: #5c7cfa;
  }

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

  .filter-btn:last-child {
    border-right: none;
  }

  .filter-btn:hover {
    color: #fff;
    background: #2a2b33;
  }

  .filter-btn.filter-active {
    background: #32343f;
    color: #f2f3f7;
  }

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

  .table-head,
  .row {
    display: grid;
    gap: 0;
    align-items: center;
  }

  .table-head {
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
  }

  .table-body {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }

  .row {
    border-bottom: 1px solid #24252c;
    padding: 0 0.25rem;
    cursor: pointer;
    transition: background 0.08s ease;
    outline: none;
  }

  .row:hover {
    background: #1e1f26;
  }

  .row:focus-visible {
    background: #1e1f26;
    box-shadow: inset 2px 0 0 #5c7cfa;
  }

  .row-selected {
    background: #242a3a !important;
  }

  .row-selected:focus-visible {
    box-shadow: inset 2px 0 0 #5c7cfa;
  }

  .row-missing {
    opacity: 0.72;
  }

  .cell {
    padding: 0 0.7rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cell-name .name-text {
    font-weight: 500;
    color: #e9e9ef;
  }

  .cell-path .path-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #8b8d9a;
  }

  .cell-version .version-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    color: #c5c7d0;
  }

  .muted {
    color: #6f7280;
  }

  .chips {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    flex-wrap: nowrap;
  }

  .empty-state {
    text-align: center;
    color: #8b8d9a;
  }

  .empty-state p {
    margin: 0.2rem 0;
    font-size: 0.88rem;
  }

  .empty-state .empty-hint {
    font-size: 0.78rem;
    color: #6f7280;
  }

  .detail-strip {
    flex-shrink: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.6rem 0.85rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1e1f26;
  }

  .detail-summary {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    min-width: 0;
    flex: 1;
    overflow: hidden;
  }

  .detail-name {
    font-weight: 600;
    color: #f2f3f7;
  }

  .detail-version {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #c5c7d0;
  }

  .detail-sep {
    color: #555;
  }

  .detail-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #8b8d9a;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .detail-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    flex-shrink: 0;
  }

  .more-wrap {
    position: relative;
  }

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

  .more-item:hover {
    background: #2a2b33;
    color: #fff;
  }

  .more-item-destructive {
    color: #f0a8b8;
  }

  .more-item-destructive:hover {
    background: #3a1a25;
    color: #fff;
  }

  .more-item-destructive:disabled,
  .more-item:disabled {
    color: #555;
    cursor: not-allowed;
    background: transparent;
  }

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

  .ctx-item:hover {
    background: #2a2b33;
    color: #fff;
  }

  .ctx-item-destructive {
    color: #f0a8b8;
  }

  .ctx-item-destructive:hover {
    background: #3a1a25;
    color: #fff;
  }

  .ctx-item-destructive:disabled,
  .ctx-item:disabled {
    color: #555;
    cursor: not-allowed;
    background: transparent;
  }

  .ctx-sep {
    height: 1px;
    background: #34353f;
    margin: 0.25rem 0;
  }
</style>
