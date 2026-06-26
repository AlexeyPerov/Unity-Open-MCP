<script lang="ts">
  import { app, type BridgeStatusToken } from "../../state/app.svelte.ts";
  import Button from "./Button.svelte";
  import { logs } from "../../state/logs.svelte.ts";

  function bridgeLabel(status: BridgeStatusToken): string {
    switch (status) {
      case "running": return "Bridge · running";
      case "compiling": return "Bridge · compiling";
      case "stopped": return "Bridge · stopped";
      case "dead_bridge": return "Bridge · dead";
      case "cli_missing": return "CLI · missing";
      default: return "Bridge · ?"; // unknown / never probed
    }
  }

  function bridgeTitle(status: BridgeStatusToken, nextStep: string | null): string {
    const head = bridgeLabel(status);
    if (status === "cli_missing") {
      // Self-diagnosing hint: the engine CLI isn't on PATH. The actionable
      // detail (binary name + install command) lives in nextStep.
      const detail = nextStep ?? "The engine CLI binary was not found on PATH.";
      return `${head}\n${detail}`;
    }
    if (nextStep && nextStep.length > 0) return `${head}\n${nextStep}`;
    return `${head}\nClick to re-check via unity_open_mcp_bridge_status.`;
  }

  // Export menu: copy the run-summary markdown to the clipboard, or save
  // it as a file under the project's exportsDir (phase-5 deliverable).
  let exportOpen = $state(false);
  let exportSavedPath = $state<string | null>(null);

  async function onCopyExport() {
    exportOpen = false;
    const ok = await app.copyExport();
    if (!ok) logs.error("export copy failed");
  }

  async function onSaveExport() {
    exportOpen = false;
    exportSavedPath = null;
    try {
      exportSavedPath = await app.saveExportFile();
    } catch (e) {
      logs.error(`export save failed: ${String(e)}`);
    }
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
    {#if app.activeProject}
      <div class="export-wrap">
        <Button variant="secondary" onclick={() => (exportOpen = !exportOpen)} disabled={app.busy || app.scenarios.length === 0}>
          Export…
        </Button>
        {#if exportOpen}
          <div class="export-menu" role="menu" aria-label="Export run summary">
            <button type="button" class="export-item" role="menuitem" onclick={onCopyExport}>
              <span class="export-item-title">Copy summary to clipboard</span>
              <span class="export-item-hint">Markdown — paste into a checklist or changelog</span>
            </button>
            <button type="button" class="export-item" role="menuitem" onclick={onSaveExport}>
              <span class="export-item-title">Save summary as file…</span>
              <span class="export-item-hint">UserSettings/ValidationSuite/exports/</span>
            </button>
          </div>
        {/if}
      </div>
    {/if}
    <Button variant={app.activeProject ? "secondary" : "primary"} onclick={() => app.pickProject()} disabled={app.busy}>
      {app.activeProject ? "Change…" : "Open project…"}
    </Button>
  </div>
</header>

{#if exportSavedPath}
  <div class="export-toast" role="status">
    <span>Export saved to <code>{exportSavedPath}</code></span>
    <button type="button" class="toast-dismiss" onclick={() => (exportSavedPath = null)}>Dismiss</button>
  </div>
{/if}

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
     amber, stopped = muted gray, dead_bridge = red, cli_missing = warning
     amber (action needed: install/link the CLI), unknown = neutral. */
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
  .bridge-cli_missing .bridge-dot {
    background: #fbbf24;
    box-shadow: 0 0 0 2px rgba(251, 191, 36, 0.22);
  }

  .muted {
    color: var(--hub-text-placeholder);
    font-size: 0.85rem;
  }

  .actions {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  /* Export menu (phase-5): a small dropdown anchored under the Export
     button. Copy = clipboard markdown; Save = file under exportsDir. */
  .export-wrap {
    position: relative;
    display: inline-flex;
  }

  .export-menu {
    position: absolute;
    top: calc(100% + 0.3rem);
    right: 0;
    z-index: 20;
    min-width: 16rem;
    background: var(--hub-surface);
    border: 1px solid var(--hub-border);
    border-radius: 6px;
    box-shadow: 0 6px 20px rgba(0, 0, 0, 0.35);
    overflow: hidden;
  }

  .export-item {
    display: block;
    width: 100%;
    text-align: left;
    padding: 0.5rem 0.65rem;
    border: none;
    background: transparent;
    color: var(--hub-text);
    cursor: pointer;
  }

  .export-item + .export-item {
    border-top: 1px solid var(--hub-border-light);
  }

  .export-item:hover {
    background: var(--hub-bg);
    color: var(--hub-text-bright);
  }

  .export-item-title {
    display: block;
    font-size: 0.82rem;
    font-weight: 500;
  }

  .export-item-hint {
    display: block;
    margin-top: 0.15rem;
    font-size: 0.7rem;
    color: var(--hub-text-placeholder);
  }

  /* Toast: ephemeral confirmation that a file export landed. */
  .export-toast {
    position: fixed;
    right: 1rem;
    bottom: 1rem;
    z-index: 40;
    display: flex;
    align-items: center;
    gap: 0.75rem;
    max-width: 30rem;
    padding: 0.55rem 0.75rem;
    background: var(--hub-surface);
    border: 1px solid var(--hub-accent);
    border-radius: 6px;
    box-shadow: 0 6px 20px rgba(0, 0, 0, 0.35);
    font-size: 0.8rem;
    color: var(--hub-text);
  }

  .export-toast code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text-dim);
    word-break: break-all;
  }

  .toast-dismiss {
    flex-shrink: 0;
    border: 1px solid var(--hub-border);
    background: var(--hub-bg);
    color: var(--hub-text-muted);
    border-radius: 4px;
    padding: 0.2rem 0.5rem;
    font-size: 0.74rem;
    cursor: pointer;
  }

  .toast-dismiss:hover {
    color: var(--hub-text-bright);
  }
</style>

