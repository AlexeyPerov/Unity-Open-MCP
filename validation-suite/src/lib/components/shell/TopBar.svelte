<script lang="ts">
  import { app } from "../../state/app.svelte.ts";
  import Button from "./Button.svelte";
</script>

<header class="topbar">
  <div class="brand">
    <span class="logo" aria-hidden="true">VS</span>
    <span class="title">Validation Suite</span>
    {#if app.profile}
      <span class="profile-chip" title={app.profile.id}>{app.profile.displayName}</span>
    {/if}
  </div>

  <div class="project">
    {#if app.activeProject}
      <span class="label">Project</span>
      <code class="path" title={app.activeProject}>{app.activeProject}</code>
    {:else}
      <span class="muted">No project selected</span>
    {/if}
  </div>

  <div class="actions">
    <Button variant={app.activeProject ? "secondary" : "primary"} onclick={() => app.pickProject()} disabled={app.busy}>
      {app.activeProject ? "Change…" : "Open project…"}
    </Button>
  </div>
</header>

<style>
  .topbar {
    flex-shrink: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 1rem;
    padding: 0.55rem 0.85rem;
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-surface);
  }

  .brand {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.55rem;
    flex-shrink: 0;
  }

  .logo {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 1.7rem;
    height: 1.7rem;
    border-radius: 5px;
    background: var(--hub-accent);
    color: #fff;
    font-size: 0.72rem;
    font-weight: 700;
    letter-spacing: 0.02em;
  }

  .title {
    font-size: 0.92rem;
    font-weight: 600;
    color: var(--hub-text-bright);
  }

  .profile-chip {
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    background: var(--hub-bg);
    padding: 0.1rem 0.5rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-light);
  }

  .project {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.55rem;
    overflow: hidden;
  }

  .label {
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--hub-text-muted);
    font-weight: 600;
    flex-shrink: 0;
  }

  .path {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: var(--hub-text-dim);
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    padding: 0.2rem 0.5rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .muted {
    color: var(--hub-text-placeholder);
    font-size: 0.85rem;
  }

  .actions {
    flex-shrink: 0;
  }
</style>
