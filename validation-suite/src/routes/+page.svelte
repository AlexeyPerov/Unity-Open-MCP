<script lang="ts">
  import { onMount } from "svelte";
  import { app } from "../lib/state/app.svelte.ts";
  import TopBar from "../lib/components/shell/TopBar.svelte";
  import TestNav from "../lib/components/TestNav.svelte";
  import Runner from "../lib/components/Runner.svelte";
  import LogDrawer from "../lib/components/shell/LogDrawer.svelte";

  onMount(() => {
    void app.init();
  });
</script>

{#if app.fatal}
  <div class="fatal" role="alert">
    <h1>Validation Suite could not start</h1>
    <p>{app.fatal}</p>
  </div>
{:else}
  <div class="shell" role="application" aria-label="Validation Suite">
    <div class="titlebar" data-tauri-drag-region></div>
    <div class="app">
      <TopBar />
      <main class="content">
        {#if app.activeProject}
          <TestNav />
          <Runner />
        {:else}
          <div class="welcome">
            <h2>Open a Unity project to begin</h2>
            <p>
              Pick the Unity project you want to validate (for example the
              bundled <code>demo/</code> project). The suite scopes all state
              and fixtures under that project.
            </p>
            <button class="primary" onclick={() => app.pickProject()} disabled={app.busy}>
              {app.busy ? "Working…" : "Open project…"}
            </button>
            {#if app.warning}
              <p class="warn">{app.warning.title}. {app.warning.body}</p>
            {/if}
          </div>
        {/if}
      </main>
    </div>

    <LogDrawer />
  </div>
{/if}

<style>
  .shell {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
  }

  /* macOS overlay title bar drag region — matches the Hub. The
   * traffic lights sit above this strip under the overlay titleBarStyle. */
  .titlebar {
    flex-shrink: 0;
    height: 32px;
    background: var(--hub-surface);
    -webkit-app-region: drag;
    app-region: drag;
  }

  .app {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    box-sizing: border-box;
    padding: 0 0.75rem 0.75rem 0.75rem;
    gap: 0.75rem;
  }

  .content {
    flex: 1;
    display: flex;
    flex-direction: row;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    gap: 0.75rem;
  }

  .fatal {
    padding: 40px;
    color: var(--hub-error);
  }

  .welcome {
    flex: 1;
    max-width: 480px;
    margin: auto;
    text-align: center;
    color: var(--hub-text-muted);
    align-self: center;
  }

  .welcome h2 {
    color: var(--hub-text);
    font-weight: 600;
  }

  .welcome p {
    line-height: 1.6;
  }

  .welcome code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
  }

  .primary {
    background: var(--hub-accent);
    color: #fff;
    border: 1px solid var(--hub-border-hover);
    padding: 0.55rem 1.1rem;
    border-radius: 6px;
    font-weight: 500;
    font-size: 0.88rem;
    margin-top: 0.5rem;
  }

  .primary:hover:not(:disabled) {
    color: var(--hub-text-bright);
  }

  .primary:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .warn {
    margin-top: 1rem;
    color: var(--hub-warn-fg);
    font-size: 0.82rem;
  }
</style>
