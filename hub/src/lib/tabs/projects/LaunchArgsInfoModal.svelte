<script lang="ts">
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { LAUNCH_ARGS_DOCS_URL, LAUNCH_ARGS_EXAMPLES } from "./constants.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();
</script>

{#if state.launchArgsInfoOpen}
  <div
    class="settings-overlay"
    role="presentation"
    onclick={handlers.toggleLaunchArgsInfo}
    onkeydown={(e) => {
      if (e.key === "Escape") handlers.toggleLaunchArgsInfo();
    }}
  >
    <div
      class="settings-modal info-modal"
      role="dialog"
      aria-modal="true"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => {
        if (e.key === "Escape") handlers.toggleLaunchArgsInfo();
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
          onclick={handlers.toggleLaunchArgsInfo}
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
            onclick={handlers.openLaunchArgsDocs}
          >
            {LAUNCH_ARGS_DOCS_URL} ↗
          </button>
        </section>
      </div>
    </div>
  </div>
{/if}
