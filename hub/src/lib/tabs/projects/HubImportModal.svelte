<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();
</script>

{#if state.hubImportModalOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="hub-import-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="hub-import-title"
    onclick={(e: MouseEvent) => {
      if (e.target === e.currentTarget) handlers.closeHubImportModal();
    }}
    onkeydown={(e: KeyboardEvent) => {
      if (e.key === "Escape") handlers.closeHubImportModal();
    }}
  >
    <div class="hub-import-modal">
      <header class="hub-import-header">
        <h2 id="hub-import-title">Import from Unity Hub</h2>
        <button
          type="button"
          class="hub-import-close"
          onclick={handlers.closeHubImportModal}
          disabled={!!state.hubImportAddingPath}
          aria-label="Close"
          title={state.hubImportAddingPath ? "Wait for the in-flight import to finish" : "Close"}
        >×</button>
      </header>

      <div class="hub-import-body">
        <p class="hub-import-help">
          Unity Hub tracks every project you open with it. This list is a live, read-only
          view of that registry — pick the entries you want to add to your Hub project list.
          Already-tracked paths are shown greyed out.
        </p>

        {#if state.hubImportLoading}
          <p class="hub-import-empty">Scanning Unity Hub data…</p>
        {:else if state.hubImportError}
          <p class="hub-import-error" role="alert">{state.hubImportError}</p>
          <Button variant="secondary" onclick={handlers.loadHubCandidates}>Retry</Button>
        {:else if state.hubImportCandidates.length === 0}
          <p class="hub-import-empty">No projects found in Unity Hub's registry.</p>
        {:else}
          <ul class="hub-import-list" role="list">
            {#each state.hubImportCandidates as candidate (candidate.path)}
              <li class="hub-import-row" class:tracked={candidate.alreadyTracked}>
                <div class="hub-import-row-main">
                  <span class="hub-import-row-name">{candidate.name}</span>
                  <span class="hub-import-row-path" title={candidate.path}>{candidate.path}</span>
                  <span class="hub-import-row-meta">
                    {#if candidate.unityVersion}<span>{candidate.unityVersion}</span>{/if}
                    {#if candidate.renderPipeline}<span>{candidate.renderPipeline}</span>{/if}
                    {#if candidate.defaultBuildTarget}<span>{candidate.defaultBuildTarget}</span>{/if}
                    {#if !candidate.exists}<span class="hub-import-missing">missing path</span>{/if}
                  </span>
                </div>
                <div class="hub-import-row-action">
                  {#if candidate.alreadyTracked}
                    <span class="hub-import-tracked" title="Already in your project list">tracked</span>
                  {:else}
                    <Button
                      variant="primary"
                      onclick={() => handlers.importHubCandidate(candidate)}
                      disabled={!!state.hubImportAddingPath || !candidate.exists}
                      title={
                        !candidate.exists
                          ? "Path is missing — relink via Add Project after the folder is back"
                          : state.hubImportAddingPath === candidate.path
                            ? "Adding…"
                            : `Add ${candidate.name} to your project list`
                      }
                    >
                      {state.hubImportAddingPath === candidate.path ? "Adding…" : "Add"}
                    </Button>
                  {/if}
                </div>
              </li>
            {/each}
          </ul>
        {/if}
      </div>

      <footer class="hub-import-footer">
        <Button variant="secondary" onclick={handlers.closeHubImportModal} disabled={!!state.hubImportAddingPath}>
          Done
        </Button>
      </footer>
    </div>
  </div>
{/if}
