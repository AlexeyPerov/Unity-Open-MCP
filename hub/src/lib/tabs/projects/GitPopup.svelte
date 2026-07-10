<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { kindLabel } from "./helpers.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();
</script>

{#if state.gitPopupProject}
  <div
    class="settings-overlay"
    role="presentation"
    onclick={handlers.closeGitPopup}
    onkeydown={(e) => { if (e.key === "Escape") handlers.closeGitPopup(); }}
  >
    <div
      class="settings-modal git-modal"
      role="dialog"
      aria-modal="true"
      aria-labelledby="git-popup-title"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => { if (e.key === "Escape") handlers.closeGitPopup(); }}
    >
      <div class="settings-modal-header">
        <div class="settings-modal-titles">
          <h2 id="git-popup-title">
            Git status
            <span class="source-tag source-kind source-kind-{state.projectKindOf(state.gitPopupProject)}">
              {kindLabel(state.projectKindOf(state.gitPopupProject))}
            </span>
          </h2>
          <span class="settings-modal-path" title={state.gitPopupProject.path}>{state.gitPopupProject.path}</span>
        </div>
        <button
          type="button"
          class="modal-close-btn"
          aria-label="Close"
          onclick={handlers.closeGitPopup}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
          </svg>
        </button>
      </div>
      <div class="settings-modal-body">
        {#if state.gitStatusLoading}
          <p class="muted">Loading git status…</p>
        {:else if state.gitStatusError}
          <p class="error-text">{state.gitStatusError}</p>
        {:else if state.gitStatusData}
          <section class="git-summary">
            <div class="git-summary-row">
              <span class="git-label">Branch:</span>
              <span class="git-value">
                {state.gitStatusData.branch ?? "—"}
              </span>
            </div>
            {#if !state.gitStatusData.noUpstream}
              <div class="git-summary-row">
                <span class="git-label">Ahead / behind:</span>
                <span class="git-value">
                  <span class="git-ahead">↑{state.gitStatusData.ahead}</span>
                  <span class="git-behind">↓{state.gitStatusData.behind}</span>
                </span>
              </div>
            {:else}
              <div class="git-summary-row">
                <span class="git-label">Upstream:</span>
                <span class="git-value muted">no upstream branch</span>
              </div>
            {/if}
            <div class="git-summary-row">
              <span class="git-label">Pending files:</span>
              <span class="git-value">{state.gitStatusData.pending.length}</span>
            </div>
            {#if state.gitPopupLineStats}
              <div class="git-summary-row">
                <span class="git-label">Lines (auto):</span>
                <span class="git-value">
                  {state.gitPopupLineStats.totalLines.toLocaleString()}
                  <span class="muted small">— scanned {new Date(state.gitPopupLineStats.scannedAt).toLocaleString()}</span>
                </span>
              </div>
            {/if}
          </section>
          <section class="git-pending">
            <h3>Pending changes</h3>
            {#if state.gitStatusData.pending.length === 0}
              <p class="muted">Working tree clean.</p>
            {:else}
              <ul class="pending-list">
                {#each state.gitStatusData.pending as file}
                  <li class="pending-item">
                    <span class="pending-status pending-{file.status}">{file.status}</span>
                    {#if file.staged}
                      <span class="pending-staged" title="Staged">●</span>
                    {/if}
                    <span class="pending-path" title={file.path}>{file.path}</span>
                    {#if file.renameFrom}
                      <span class="pending-rename">← {file.renameFrom}</span>
                    {/if}
                  </li>
                {/each}
              </ul>
            {/if}
          </section>
          <div class="git-actions">
            <Button variant="secondary" onclick={handlers.refreshGitStatus}>Refresh</Button>
          </div>
        {:else}
          <p class="muted">No git status available.</p>
        {/if}
      </div>
    </div>
  </div>
{/if}
