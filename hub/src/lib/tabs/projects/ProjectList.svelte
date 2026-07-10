<script lang="ts">
  import { projectsStore } from "$lib/state/projects.svelte";
  import { walkUpScanStore } from "$lib/state/walk_up_scan.svelte";
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import RelativeTime from "$lib/components/RelativeTime.svelte";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { kindLabel, formatSize as formatSizeImported } from "./helpers.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();

  type FilterPreset = "all" | "launchable" | "missingVersion" | "missingPath" | "missingOrStale" | "running";

  const filterOptions: { id: FilterPreset; label: string }[] = [
    { id: "all", label: "All" },
    { id: "launchable", label: "Launchable" },
    { id: "running", label: "Running" },
    { id: "missingVersion", label: "Missing version" },
    { id: "missingOrStale", label: "Missing or stale" },
  ];
</script>

<div class="projects" class:drag-over={state.isDragOver}>
  <div class="toolbar">
    <div class="toolbar-row">
      <input
        type="search"
        class="search"
        placeholder="Search projects…"
        value={state.search}
        oninput={(e) => handlers.setSearch((e.currentTarget as HTMLInputElement).value)}
        aria-label="Search projects"
      />

      <Select
        options={filterOptions.map((o) => ({ value: o.id, label: o.label }))}
        value={state.filterPreset}
        onchange={(v) => handlers.setFilterPreset(v)}
        aria-label="Filter projects"
        title="Filter projects"
      />

      {#if projectsStore.projects.some((p) => p.hidden)}
        <button
          type="button"
          class="filter-btn show-hidden-btn"
          class:filter-active={state.showHidden}
          onclick={handlers.toggleShowHidden}
          aria-pressed={state.showHidden}
          title={state.showHidden
            ? "Hide soft-deleted projects from the list"
            : "Show soft-deleted projects (entries kept in projects.json; use Hide from the row menu to soft-delete)"}
        >
          {state.showHidden ? "✓ " : ""}Show hidden
        </button>
      {/if}

      <div class="toolbar-spacer"></div>
    </div>
    <div class="toolbar-row">
      <div class="toolbar-spacer"></div>
      <Button
        variant="secondary"
        onclick={handlers.openNewProjectModal}
        disabled={state.newProjectCreating}
        title="New project — scaffold a fresh Unity project from a template"
      >
        New project…
      </Button>
      <Button variant="primary" onclick={handlers.handleAddProject} disabled={state.addingProject}>
        {state.addingProject ? "Adding…" : "Add Project"}
      </Button>
      <Button
        variant="secondary"
        onclick={handlers.openWalkUpModal}
        disabled={walkUpScanStore.scanning}
        title="Add Multiple Projects — pick a parent folder and discover Unity projects underneath"
      >
        {walkUpScanStore.scanning ? "Scanning…" : "Add Multiple Projects"}
      </Button>
      <Button
        variant="secondary"
        onclick={handlers.openHubImportModal}
        disabled={state.hubImportLoading}
        title="Import from Hub — scan Unity Hub's recent-projects list and pick entries to add"
      >
        {state.hubImportLoading ? "Scanning…" : "Import from Hub"}
      </Button>
      <button
        type="button"
        class="icon-btn"
        onclick={handlers.handleRefresh}
        disabled={state.refreshing}
        title={state.refreshing ? "Refreshing…" : "Refresh"}
        aria-label={state.refreshing ? "Refreshing…" : "Refresh"}
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
          class:icon-spin={state.refreshing}
          aria-hidden="true"
        >
          <polyline points="23 4 23 10 17 10"/>
          <polyline points="1 20 1 14 7 14"/>
          <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/>
        </svg>
      </button>
      {#if state.removingId}
        <span class="toolbar-status" aria-live="polite">Removing…</span>
      {/if}
    </div>
  </div>

  {#if state.addError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{state.addError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={handlers.dismissAddError}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  {#if state.actionError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{state.actionError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={handlers.dismissActionError}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  <div class="table" role="grid">
    <div class="table-head" role="row" style="grid-template-columns: {state.gridTemplate};">
      <div class="th th-name" role="columnheader">Name</div>
      <div class="th" role="columnheader">Editor Version</div>
      {#if state.showModified}
        <div class="th" role="columnheader">Modified</div>
      {/if}
      {#if state.showGitBranch}
        <div class="th" role="columnheader" title="Current git branch (detached HEAD shows the SHA on hover)">Branch</div>
      {/if}
      <div class="th" role="columnheader" title="Folder size excluding Library, Temp, Logs, UserSettings and gitignored directories">Size</div>
      <div class="th" role="columnheader">Status</div>
      <div class="th th-settings" role="columnheader"></div>
    </div>

    <div class="table-body">
      {#if state.filtered.length === 0}
        <div class="empty-state">
          {#if projectsStore.projects.length === 0}
            <p>No projects yet.</p>
            <p class="empty-hint">Use <strong>Add Project</strong> to register a folder — Unity project, UPM package, Open-MCP repo, or any other folder.</p>
          {:else}
            <p>No projects match the current filter.</p>
          {/if}
        </div>
      {:else}
        {#each state.filtered as project, index (project.id)}
          {@const s = state.statusFor(project)}
          {@const kind = state.projectKindOf(project)}
          <div class="row-wrapper">
            <!-- svelte-ignore a11y_interactive_supports_focus -->
            <!-- svelte-ignore a11y_click_events_have_key_events -->
            <div
              class="row"
              class:row-missing={s.kind === "missingPath"}
              class:row-stale={s.kind === "stale"}
              class:row-hidden={project.hidden === true}
              class:row-selected={projectsStore.selectedProjectId === project.id}
              class:row-nonlaunchable={kind !== "unity"}
              role="row"
              aria-selected={projectsStore.selectedProjectId === project.id}
              title={project.path}
              style="grid-template-columns: {state.gridTemplate};"
              onclick={() => {
                if (kind === "unity") {
                  handlers.handleLaunch(project.id);
                } else {
                  handlers.openSettingsPopup(project.id);
                }
              }}
              oncontextmenu={(e) => handlers.openContextMenu(e, project.id)}
            >
              <div class="cell cell-name" role="gridcell">
                <div class="name-path">
                  <span class="name-text">
                    <span class="name-label">{project.name}</span>
                    {#if kind !== "unity"}
                      <span
                        class="source-tag source-kind source-kind-{kind}"
                        title={`Folder type: ${kindLabel(kind)}`}
                      >{kindLabel(kind)}</span
                      >
                    {/if}
                    {#if project.source === "hub-seed"}
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
                  <span class="path-text"><span class="path-text-inner" title={project.path}>{project.path}</span></span>
                </div>
              </div>
              <div class="cell cell-version" role="gridcell">
                {#if project.unityVersion}
                  <span class="version-text">{project.unityVersion}</span>
                {:else}
                  <span class="muted">—</span>
                {/if}
                {#if project.renderPipeline}
                  <span
                    class="meta-chip meta-chip-{(project.renderPipeline ?? "").toLowerCase()}"
                    title={`Render pipeline: ${project.renderPipeline} (read from ProjectSettings/GraphicsSettings.asset)`}
                  >{project.renderPipeline}</span>
                {/if}
                {#if project.defaultBuildTarget}
                  <span
                    class="meta-chip meta-chip-target"
                    title={`Default build target: ${project.defaultBuildTarget} (read from ProjectSettings/ProjectSettings.asset)`}
                  >{project.defaultBuildTarget}</span>
                {/if}
              </div>
              {#if state.showModified}
                <div class="cell cell-modified" role="gridcell">
                  <RelativeTime iso={project.lastOpenedAt ?? project.lastModifiedAt} />
                </div>
              {/if}
              {#if state.showGitBranch}
                <div class="cell cell-branch" role="gridcell">
                  {#if project.gitBranch}
                    <!-- svelte-ignore a11y_click_events_have_key_events -->
                    <!-- svelte-ignore a11y_no_static_element_interactions -->
                    <span
                      class="branch-chip branch-clickable"
                      title="Click for git status"
                      onclick={(e: MouseEvent) => { e.stopPropagation(); handlers.openGitPopup(project.id); }}
                    >
                      {#if project.gitBranch.startsWith("detached:")}
                        <span class="branch-detached">detached</span>
                      {:else}
                        <span>{project.gitBranch}</span>
                      {/if}
                    </span>
                  {/if}
                </div>
              {/if}
              <div class="cell cell-size" role="gridcell">
                <span class="size-text">{formatSizeImported(state.sizeMap[project.path] ?? 0)}</span>
              </div>
              <div class="cell cell-status" role="gridcell">
                <div class="chips">
                  {#each s.chips as chip}
                    <StatusChip tone={chip.tone} label={chip.label} title={chip.title} />
                  {/each}
                </div>
              </div>
              <div class="cell cell-settings" role="gridcell">
                {#if state.aiSetupEnabled && kind === "unity" && s.pathExists === true && s.hasVersion && !s.stale}
                  {@const aiReady = state.aiReadyFor(project.path)}
                  <button
                    type="button"
                    class="row-action-btn ai-row-btn ai-setup-btn ai-setup-{aiReady ? 'complete' : s.launchable ? 'ready' : 'incomplete'}"
                    onclick={(e: MouseEvent) => { e.stopPropagation(); handlers.openAiSetupFor(project); }}
                    aria-label="AI setup"
                    title={aiReady
                      ? "AI setup complete — click to re-open the AI setup wizard"
                      : state.aiDetectMap[project.path]
                        ? "AI setup incomplete — click to install / configure the Unity AI agent"
                        : "AI — install / configure the Unity AI agent for this project"}
                  >
                    AI
                  </button>
                {/if}
                <button
                  type="button"
                  class="row-action-btn settings-btn"
                  onclick={(e: MouseEvent) => { e.stopPropagation(); handlers.openSettingsPopup(project.id); }}
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
