<script lang="ts">
  import { projectsStore } from "$lib/state/projects.svelte";
  import { AI_SETUP_ENABLED } from "$lib/features";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { kindLabel } from "./helpers.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();

  let ctxId = $derived(state.contextMenu?.projectId ?? null);
  let ctxProject = $derived(ctxId ? projectsStore.find(ctxId) ?? null : null);
  let ctxStatus = $derived(ctxProject ? state.statusFor(ctxProject) : null);
</script>

{#if state.contextMenu && ctxProject}
  <div
    class="ctx-menu"
    role="menu"
    tabindex="-1"
    style="left: {state.contextMenu.x}px; top: {state.contextMenu.y}px;"
    onclick={(e) => e.stopPropagation()}
    onkeydown={(e) => {
      if (e.key === "Escape") handlers.closeContextMenu();
    }}
  >
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      disabled={!ctxStatus?.launchable}
      onclick={() => {
        handlers.handleLaunch(ctxProject!.id);
        handlers.closeContextMenu();
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
        handlers.handleOpenFolder(ctxProject!);
        handlers.closeContextMenu();
      }}
    >
      Open folder
    </button>
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      onclick={() => {
        handlers.handleCopyPath(ctxProject!);
      }}
    >
      Copy path
    </button>
    <button
      type="button"
      class="ctx-item ctx-item-destructive"
      role="menuitem"
      title={ctxProject?.lastLaunchPid ? `Terminate pid ${ctxProject.lastLaunchPid}` : "No recorded Unity PID"}
      disabled={!ctxProject?.lastLaunchPid || state.killingId === ctxId}
      onclick={() => {
        handlers.handleKillUnity(ctxProject!);
      }}
    >
      {state.killingId === ctxId ? "Terminating…" : "Terminate Unity"}
    </button>
    {#if ctxStatus?.pathExists === false}
      <button
        type="button"
        class="ctx-item ctx-item-relink"
        role="menuitem"
        title="Re-point this project to a new folder on disk"
        disabled={state.relinkingId === ctxId}
        onclick={() => {
          handlers.handleRelink(ctxProject!);
        }}
      >
        {state.relinkingId === ctxId ? "Relinking…" : "Relink…"}
      </button>
    {/if}
    {#if handlers.canUpgrade(ctxProject)}
      <div class="ctx-sep"></div>
      <button
        type="button"
        class="ctx-item ctx-item-upgrade"
        role="menuitem"
        title="Bump the project's Unity version to an installed version higher than the current one"
        onclick={() => {
          handlers.openUpgradeModal(ctxProject!);
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
          handlers.openAiSetupFor(ctxProject!);
        }}
      >
        Configure Agent Bridge…
      </button>
    {/if}
    {#if handlers.canHide(ctxProject) || handlers.canUnhide(ctxProject) || handlers.canMarkStale(ctxProject) || handlers.canUnmarkStale(ctxProject)}
      <div class="ctx-sep"></div>
      {#if handlers.canHide(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Remove this row from the list (the entry stays in projects.json with hidden=true; toggle 'Show hidden' in the toolbar to reveal)"
          disabled={state.hidingId === ctxId}
          onclick={() => {
            handlers.handleHide(ctxProject!);
          }}
        >
          {state.hidingId === ctxId ? "Hiding…" : "Hide"}
        </button>
      {/if}
      {#if handlers.canUnhide(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Restore this row to the default list view"
          disabled={state.hidingId === ctxId}
          onclick={() => {
            handlers.handleUnhide(ctxProject!);
          }}
        >
          {state.hidingId === ctxId ? "Unhiding…" : "Unhide"}
        </button>
      {/if}
      {#if handlers.canMarkStale(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Keep the row visible with a 'stale' chip (excluded from launch / running-Unity actions; relink to clear)"
          disabled={state.markingStaleId === ctxId}
          onclick={() => {
            handlers.handleMarkStale(ctxProject!);
          }}
        >
          {state.markingStaleId === ctxId ? "Marking…" : "Mark stale"}
        </button>
      {/if}
      {#if handlers.canUnmarkStale(ctxProject)}
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          title="Clear the stale flag — the row becomes a normal missing-path row again"
          disabled={state.markingStaleId === ctxId}
          onclick={() => {
            handlers.handleUnmarkStale(ctxProject!);
          }}
        >
          {state.markingStaleId === ctxId ? "Unmarking…" : "Unmark stale"}
        </button>
      {/if}
    {/if}
    <div class="ctx-sep"></div>
    <button
      type="button"
      class="ctx-item"
      role="menuitem"
      title="Refresh project version and size"
      disabled={ctxStatus?.pathExists === false || state.refreshingId === ctxId}
      onclick={() => {
        handlers.handleRefreshProject(ctxProject!);
      }}
    >
      {state.refreshingId === ctxId ? "Refreshing…" : "Refresh"}
    </button>
    <div class="ctx-sep"></div>
    <button
      type="button"
      class="ctx-item ctx-item-destructive"
      role="menuitem"
      title="Remove this project from the Hub list"
      disabled={state.removingId === ctxId}
      onclick={() => {
        handlers.handleRemove(ctxId!);
      }}
    >
      {state.removingId === ctxId ? "Removing…" : "Remove from list"}
    </button>
  </div>
{/if}
