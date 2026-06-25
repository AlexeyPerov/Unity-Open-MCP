<script lang="ts">
  import { app, type BridgeStatusToken } from "../../state/app.svelte.ts";
  import Button from "./Button.svelte";

  function bridgeLabel(status: BridgeStatusToken): string {
    switch (status) {
      case "running": return "Bridge · running";
      case "compiling": return "Bridge · compiling";
      case "stopped": return "Bridge · stopped";
      case "dead_bridge": return "Bridge · dead";
      default: return "Bridge · ?"; // unknown / never probed
    }
  }

  function bridgeTitle(status: BridgeStatusToken, nextStep: string | null): string {
    const head = bridgeLabel(status);
    if (nextStep && nextStep.length > 0) return `${head}\n${nextStep}`;
    return `${head}\nClick to re-check via unity_open_mcp_bridge_status.`;
  }
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
      <button
        class="bridge-chip bridge-{app.bridgeStatus}"
        onclick={() => app.refreshBridgeStatus()}
        disabled={app.busy || app.bridgeRefreshing}
        title={bridgeTitle(app.bridgeStatus, app.bridgeNextStep)}
      >
        <span class="bridge-dot" aria-hidden="true"></span>
        {bridgeLabel(app.bridgeStatus)}
      </button>
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

  .bridge-chip {
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.74rem;
    font-family: inherit;
    color: var(--hub-text);
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    padding: 0.2rem 0.5rem;
    cursor: pointer;
    transition: background 0.1s ease;
  }

  .bridge-chip:hover:not(:disabled) {
    background: var(--hub-surface);
  }

  .bridge-chip:disabled {
    cursor: default;
    opacity: 0.7;
  }

  .bridge-dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--hub-text-placeholder);
    flex-shrink: 0;
  }

  /* Phase 3: bridge_status token → chip color. running = green, compiling =
     amber, stopped = muted gray, dead_bridge = red, unknown = neutral. */
  .bridge-running .bridge-dot {
    background: #4ade80;
    box-shadow: 0 0 0 2px rgba(74, 222, 128, 0.18);
  }
  .bridge-compiling .bridge-dot {
    background: #fbbf24;
    box-shadow: 0 0 0 2px rgba(251, 191, 36, 0.18);
  }
  .bridge-stopped .bridge-dot {
    background: var(--hub-text-placeholder);
  }
  .bridge-dead_bridge .bridge-dot {
    background: #f87171;
    box-shadow: 0 0 0 2px rgba(248, 113, 113, 0.2);
  }

  .muted {
    color: var(--hub-text-placeholder);
    font-size: 0.85rem;
  }

  .actions {
    flex-shrink: 0;
  }
</style>
