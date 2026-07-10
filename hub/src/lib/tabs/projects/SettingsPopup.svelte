<script lang="ts">
  import { openPath } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import PackageProjectSettings from "$lib/components/project-settings/PackageProjectSettings.svelte";
  import OpenMcpProjectSettings from "$lib/components/project-settings/OpenMcpProjectSettings.svelte";
  import CustomProjectSettings from "$lib/components/project-settings/CustomProjectSettings.svelte";
  import LineCounterPanel from "$lib/components/project-settings/LineCounterPanel.svelte";
  import UnityDomainDepsPanel from "$lib/components/project-settings/UnityDomainDepsPanel.svelte";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { kindLabel } from "./helpers.ts";
  import { BUILD_TARGET_LABELS, buildTargetLabel, intentOptions } from "./constants.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();

  let popupProject = $derived(state.popupProject);
  let ps = $derived(popupProject ? state.statusFor(popupProject) : null);
  let popupIsMoreOpen = $derived(popupProject ? state.moreMenuOpenFor === popupProject.id : false);
  let popupKind = $derived(popupProject ? state.projectKindOf(popupProject) : "unity");
</script>

{#if popupProject && ps}
  <div
    class="settings-overlay"
    role="presentation"
    onclick={handlers.closeSettingsPopup}
    onkeydown={(e) => {
      if (e.key === "Escape") handlers.closeSettingsPopup();
    }}
  >
    <div
      class="settings-modal"
      role="dialog"
      aria-modal="true"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => {
        if (e.key === "Escape") handlers.closeSettingsPopup();
      }}
    >
      <div class="settings-modal-header">
        <div class="settings-modal-titles">
          <h2>
            {popupProject.name}
            {#if popupKind !== "unity"}
              <span class="source-tag source-kind source-kind-{popupKind}">{kindLabel(popupKind)}</span>
            {/if}
          </h2>
          <span class="settings-modal-path" title={popupProject.path}>{popupProject.path}</span>
        </div>
        <button
          type="button"
          class="modal-close-btn"
          aria-label="Close"
          onclick={handlers.closeSettingsPopup}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
          </svg>
        </button>
      </div>
      <div class="settings-modal-body">
        {#if popupKind === "unity"}
        <div class="settings-actions">
          <Button
            variant="primary"
            disabled={!ps.launchable || state.launching === popupProject.id || ps.running}
            title={ps.running
              ? "Unity is already running for this project — terminate it first"
              : (!ps.launchable ? "Project not launchable" : "Launch this project")}
            onclick={handlers.handlePopupLaunch}
          >
            {state.launching === popupProject.id ? "Launching…" : (ps.running ? "Running" : "Launch")}
          </Button>
          <Button
            variant="secondary"
            disabled={ps.pathExists === false}
            title={ps.pathExists === false ? "Path missing" : "Open project folder"}
            onclick={() => handlers.handleOpenFolder(popupProject)}
          >
            Open Folder
          </Button>
          <Button
            variant="secondary"
            disabled={ps.pathExists === false}
            title={ps.pathExists === false ? "Path missing" : "Copy project path to clipboard"}
            onclick={() => handlers.handleCopyPath(popupProject)}
          >
            Copy Path
          </Button>
          <div class="more-wrap">
            <Button
              variant="secondary"
              onclick={() => handlers.toggleMoreMenu(popupProject.id)}
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
                  disabled={!popupProject.lastLaunchPid || state.killingId === popupProject.id}
                  onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleKillUnity(popupProject!); }}>
                  {state.killingId === popupProject.id ? "Terminating…" : "Terminate Unity"}
                </button>
                <div class="more-sep"></div>
                <button type="button" class="more-item" role="menuitem"
                  title="Refresh project version and size"
                  disabled={ps.pathExists === false || state.refreshingId === popupProject.id}
                  onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleRefreshProject(popupProject!); }}>
                  {state.refreshingId === popupProject.id ? "Refreshing…" : "Refresh"}
                </button>
                {#if ps.pathExists === false}
                  <div class="more-sep"></div>
                  <button type="button" class="more-item more-item-relink" role="menuitem"
                    title="Re-point this project to a new folder on disk"
                    disabled={state.relinkingId === popupProject.id}
                    onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleRelink(popupProject!); }}>
                    {state.relinkingId === popupProject.id ? "Relinking…" : "Relink…"}
                  </button>
                {/if}
                {#if handlers.canUpgrade(popupProject)}
                  <div class="more-sep"></div>
                  <button type="button" class="more-item more-item-upgrade" role="menuitem"
                    title="Bump the project's Unity version to an installed version higher than the current one"
                    onclick={() => { handlers.setMoreMenuOpen(null); handlers.openUpgradeModal(popupProject!); }}>
                    Upgrade Unity…
                  </button>
                {/if}
                {#if handlers.canHide(popupProject) || handlers.canUnhide(popupProject) || handlers.canMarkStale(popupProject) || handlers.canUnmarkStale(popupProject)}
                  <div class="more-sep"></div>
                  {#if handlers.canHide(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Remove this row from the list (entry kept in projects.json with hidden=true)"
                      disabled={state.hidingId === popupProject.id}
                      onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleHide(popupProject!); }}>
                      {state.hidingId === popupProject.id ? "Hiding…" : "Hide"}
                    </button>
                  {/if}
                  {#if handlers.canUnhide(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Restore this row to the default list view"
                      disabled={state.hidingId === popupProject.id}
                      onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleUnhide(popupProject!); }}>
                      {state.hidingId === popupProject.id ? "Unhiding…" : "Unhide"}
                    </button>
                  {/if}
                  {#if handlers.canMarkStale(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Keep the row visible with a 'stale' chip (excluded from launch / running-Unity actions)"
                      disabled={state.markingStaleId === popupProject.id}
                      onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleMarkStale(popupProject!); }}>
                      {state.markingStaleId === popupProject.id ? "Marking…" : "Mark stale"}
                    </button>
                  {/if}
                  {#if handlers.canUnmarkStale(popupProject)}
                    <button type="button" class="more-item" role="menuitem"
                      title="Clear the stale flag"
                      disabled={state.markingStaleId === popupProject.id}
                      onclick={() => { handlers.setMoreMenuOpen(null); handlers.handleUnmarkStale(popupProject!); }}>
                      {state.markingStaleId === popupProject.id ? "Unmarking…" : "Unmark stale"}
                    </button>
                  {/if}
                {/if}
                <div class="more-sep"></div>
                <button type="button" class="more-item more-item-destructive" role="menuitem"
                  title="Remove this project from the Hub list"
                  disabled={state.removingId === popupProject.id}
                  onclick={() => handlers.handleRemove(popupProject.id)}>
                  {state.removingId === popupProject.id ? "Removing…" : "Remove from list"}
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
              value={handlers.getArgsDraft(popupProject.id)}
              oninput={(e) => handlers.handleArgsInput(popupProject.id, (e.currentTarget as HTMLTextAreaElement).value)}
              aria-label="Launch args"
            ></textarea>
            {#if state.argsErrors[popupProject.id]}
              <p class="field-error">{state.argsErrors[popupProject.id]}</p>
            {/if}
            <div class="args-actions">
              <Button variant="primary"
                disabled={handlers.getArgsDraft(popupProject.id) === (popupProject.launchArgs ?? "") || !handlers.getArgsDraft(popupProject.id).trim() || !!state.argsErrors[popupProject.id] || state.savingArgsFor === popupProject.id}
                onclick={() => handlers.handleSaveArgs(popupProject)}>
                {state.savingArgsFor === popupProject.id ? "…" : "Save"}
              </Button>
              <Button variant="secondary"
                disabled={(popupProject.launchArgs ?? "") === "" || state.savingArgsFor === popupProject.id}
                onclick={() => handlers.handleResetArgs(popupProject)}>
                Reset
              </Button>
              <Button variant="secondary"
                title="Show example launch arguments and a link to the docs"
                onclick={() => handlers.toggleLaunchArgsInfo()}>
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
                value={handlers.getIntentDraft(popupProject.id)}
                onchange={(v) => handlers.handleIntentChange(popupProject.id, v)}
              />
              <Button variant="primary"
                disabled={handlers.getIntentDraft(popupProject.id) === (popupProject.platformIntent ?? "") || state.savingIntentFor === popupProject.id}
                onclick={() => handlers.handleSaveIntent(popupProject)}>
                {state.savingIntentFor === popupProject.id ? "…" : "Save"}
              </Button>
            </div>
            <p class="intent-status">
              {#if popupProject.platformIntent}
                Active: <strong>{popupProject.platformIntent}</strong> (applied on next launch)
              {:else if state.popupDefaultBuildTarget}
                No platform intent set — Unity will use the project's default build target
                (<strong title={state.popupDefaultBuildTarget}>{buildTargetLabel(state.popupDefaultBuildTarget)}</strong>,
                from <code>ProjectSettings/ProjectSettings.asset</code>).
              {:else if state.popupDefaultBuildTarget === null}
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
            {#if state.logPathsMap[popupProject.id]}
              {@const lp = state.logPathsMap[popupProject.id]}
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
            {#if state.envVarsError}
              <p class="field-error" role="alert">{state.envVarsError}</p>
            {/if}
            {#if state.envVarsInfo}
              <p class="field-hint" role="status">{state.envVarsInfo}</p>
            {/if}
            <div class="env-grid">
              {#each state.envVarsDraft as row (row.uid)}
                <div class="env-row">
                  <input
                    type="text"
                    class="env-key"
                    placeholder="KEY"
                    value={row.key}
                    oninput={(e) => handlers.setEnvVarDraft(row.uid, "key", (e.currentTarget as HTMLInputElement).value)}
                    aria-label="Environment variable name"
                    spellcheck="false"
                    autocomplete="off"
                  />
                  <div class="env-value-wrap">
                    <input
                      type={state.envVarsRevealed[row.uid] ? "text" : "password"}
                      class="env-value"
                      placeholder="value"
                      value={row.value}
                      oninput={(e) => handlers.setEnvVarDraft(row.uid, "value", (e.currentTarget as HTMLInputElement).value)}
                      aria-label="Environment variable value"
                      spellcheck="false"
                      autocomplete="off"
                    />
                    <button
                      type="button"
                      class="link-btn env-reveal"
                      onclick={() => handlers.toggleEnvReveal(row.uid)}
                      aria-label={state.envVarsRevealed[row.uid] ? "Hide value" : "Show value"}
                    >
                      {state.envVarsRevealed[row.uid] ? "Hide" : "Show"}
                    </button>
                  </div>
                  <button
                    type="button"
                    class="link-btn env-remove"
                    onclick={() => handlers.removeEnvVarRow(row.uid)}
                    aria-label="Remove env var row"
                  >
                    Remove
                  </button>
                </div>
              {/each}
            </div>
            <div class="env-actions">
              <Button variant="secondary" onclick={handlers.addEnvVarRow}>+ Add env var</Button>
              <Button
                variant="primary"
                disabled={state.envVarsSaving}
                onclick={handlers.saveEnvVars}
              >
                {state.envVarsSaving ? "Saving…" : "Save"}
              </Button>
            </div>
          </section>

          <LineCounterPanel project={popupProject} />

          <UnityDomainDepsPanel
            project={popupProject}
            detection={state.aiDetectMap[popupProject.path] ?? null}
            onOpenAiSetup={handlers.openAiSetupFor}
          />
        </div>
        {:else if popupKind === "package"}
          <PackageProjectSettings project={popupProject} onMutated={handlers.handlePopupProjectMutated} />
        {:else if popupKind === "openMcp"}
          <OpenMcpProjectSettings project={popupProject} onMutated={handlers.handlePopupProjectMutated} />
        {:else}
          <CustomProjectSettings project={popupProject} onMutated={handlers.handlePopupProjectMutated} />
        {/if}
      </div>
    </div>
  </div>
{/if}
