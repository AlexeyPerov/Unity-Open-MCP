<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StatusBadge from "./StatusBadge.svelte";
</script>

<header class="bar">
  <div class="brand">
    <span class="logo">VS</span>
    <span class="title">Validation Suite</span>
    {#if app.profile}
      <span class="profile">{app.profile.displayName}</span>
    {/if}
  </div>

  <div class="project">
    {#if app.activeProject}
      <span class="path" title={app.activeProject}>{app.activeProject}</span>
      <StatusBadge status="done" />
    {:else}
      <span class="muted">No project selected</span>
    {/if}
  </div>

  <div class="actions">
    <button class="primary" onclick={() => app.pickProject()} disabled={app.busy}>
      {app.activeProject ? "Change project…" : "Open project…"}
    </button>
  </div>
</header>

<style>
  .bar {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 10px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-elev);
    /* Leave room for the macOS traffic lights under the overlay title bar. */
    padding-left: 84px;
  }
  .brand {
    display: flex;
    align-items: center;
    gap: 10px;
    font-weight: 600;
  }
  .logo {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    border-radius: 6px;
    background: var(--accent);
    color: #fff;
    font-size: 11px;
    font-weight: 700;
  }
  .title {
    font-size: 14px;
  }
  .profile {
    font-size: 12px;
    color: var(--text-dim);
    background: var(--bg-elev-2);
    padding: 2px 8px;
    border-radius: 999px;
  }
  .project {
    flex: 1;
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .path {
    font-family: var(--mono);
    font-size: 12px;
    color: var(--text-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .muted {
    color: var(--text-faint);
    font-size: 13px;
  }
  .primary {
    background: var(--accent);
    color: #fff;
    border: none;
    padding: 7px 14px;
    border-radius: var(--radius-sm);
    font-weight: 500;
  }
  .primary:disabled {
    opacity: 0.5;
    cursor: default;
  }
</style>
