<script lang="ts">
  import { onMount } from "svelte";
  import { app } from "../lib/state/app.svelte.ts";
  import ProjectBar from "../lib/components/ProjectBar.svelte";
  import TestNav from "../lib/components/TestNav.svelte";
  import Runner from "../lib/components/Runner.svelte";

  onMount(() => {
    void app.init();
  });
</script>

{#if app.fatal}
  <div class="fatal">
    <h1>Validation Suite could not start</h1>
    <p>{app.fatal}</p>
  </div>
{:else}
  <ProjectBar />

  {#if app.activeProject}
    <div class="layout">
      <TestNav />
      <Runner />
    </div>
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
{/if}

<style>
  .layout {
    display: flex;
    height: calc(100vh - 53px);
  }
  .fatal {
    padding: 40px;
    color: var(--bad);
  }
  .welcome {
    max-width: 480px;
    margin: 80px auto;
    text-align: center;
    color: var(--text-dim);
  }
  .welcome h2 {
    color: var(--text);
    font-weight: 600;
  }
  .welcome p {
    line-height: 1.6;
  }
  .welcome code {
    font-family: var(--mono);
    font-size: 12px;
  }
  .primary {
    background: var(--accent);
    color: #fff;
    border: none;
    padding: 9px 18px;
    border-radius: var(--radius-sm);
    font-weight: 500;
    font-size: 14px;
    margin-top: 8px;
  }
  .primary:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .warn {
    margin-top: 18px;
    color: var(--warn);
    font-size: 13px;
  }
</style>
