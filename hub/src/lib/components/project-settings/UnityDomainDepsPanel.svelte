<script lang="ts">
  /**
   * M18 Plan 4 T18.4.2 — Hub read-only panel for the Unity domain
   * dependencies whose typed tools are bundled in the bridge.
   *
   * The Hub cannot run `Client.Add` (it is a separate process), so this
   * panel surfaces installed / missing status per domain and routes the
   * user to the AI Setup wizard (Step 3 owns the manifest install) or to
   * the in-Editor bridge window (one-click install/remove) for missing
   * deps. Built-in module domains (Particle System, Animation) render as
   * always-on.
   */
  import type { ProjectEntry, ProjectState } from "$lib/services/config";
  import { buildEmbeddedDomainInstallRows } from "$lib/services/extensions";
  import Button from "$lib/components/shell/Button.svelte";

  let {
    project,
    detection,
    onOpenAiSetup,
  }: {
    project: ProjectEntry;
    /** Latest `detect_project_state` snapshot for the project, or null
     *  while the backend has not answered yet. */
    detection: ProjectState | null;
    /** Opens the AI Setup wizard for this project (Step 3 owns the
     *  manifest install for missing Unity domain deps). */
    onOpenAiSetup: (project: ProjectEntry) => void;
  } = $props();

  let rows = $derived(
    buildEmbeddedDomainInstallRows(detection?.unityDomainDeps),
  );
  let missingCount = $derived(rows.filter((r) => !r.builtin && !r.installed).length);
</script>

<section class="mini-panel">
  <header class="mini-panel-head">
    <h3>Unity domain dependencies</h3>
    {#if detection}
      <span class="muted small">
        {#if missingCount === 0}
          all installed
        {:else}
          {missingCount} missing
        {/if}
      </span>
    {:else}
      <span class="muted small">loading…</span>
    {/if}
  </header>
  <div class="mini-panel-body">
    <p class="hint">
      Each domain maps a bundled tool group to a Unity package. Install missing
      dependencies from the <strong>AI Setup wizard</strong> (Step 3) or the
      in-Editor bridge window's <em>Optional Unity dependencies</em> panel.
      Built-in module domains ship with the Editor and are always on.
    </p>

    <ul class="dep-list">
      {#each rows as row (row.domain)}
        <li class="dep-row dep-row-{row.builtin ? 'builtin' : row.installed ? 'installed' : 'missing'}">
          <div class="dep-main">
            <span class="dep-dot" aria-hidden="true">●</span>
            <span class="dep-name">{row.displayName}</span>
            <span class="dep-status">
              {#if row.builtin}
                built-in
              {:else if row.installed}
                installed
              {:else}
                missing
              {/if}
            </span>
          </div>
          <div class="dep-meta">
            {#if row.builtin}
              <span class="muted">Unity built-in module — always on.</span>
            {:else if row.installed}
              <code>{row.upmDependency}</code>
              {#if row.reference}
                <span class="muted">→ {row.reference}</span>
              {/if}
            {:else}
              <code>{row.upmDependency}</code>
              <span class="muted">not in manifest</span>
            {/if}
          </div>
        </li>
      {/each}
    </ul>

    {#if missingCount > 0}
      <Button variant="primary" onclick={() => onOpenAiSetup(project)}>
        Open AI Setup wizard
      </Button>
    {/if}
  </div>
</section>

<style>
  .hint {
    margin: 0 0 0.6rem;
    font-size: 0.75rem;
    line-height: 1.5;
    color: var(--hub-text-dim);
  }
  .dep-list {
    list-style: none;
    margin: 0 0 0.8rem;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }
  .dep-row {
    padding: 0.35rem 0.5rem;
    border-radius: 0.35rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border);
  }
  .dep-main {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }
  .dep-dot {
    font-size: 0.7rem;
  }
  .dep-row-installed .dep-dot {
    color: #56b482;
  }
  .dep-row-missing .dep-dot {
    color: #fbbf24;
  }
  .dep-row-builtin .dep-dot {
    color: var(--hub-text-dim);
  }
  .dep-name {
    font-weight: 600;
    font-size: 0.82rem;
    color: var(--hub-text);
    flex: 1;
  }
  .dep-status {
    font-size: 0.68rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--hub-text-dim);
  }
  .dep-row-installed .dep-status {
    color: #56b482;
  }
  .dep-row-missing .dep-status {
    color: #fbbf24;
  }
  .dep-meta {
    margin-top: 0.15rem;
    margin-left: 1.1rem;
    font-size: 0.72rem;
    color: var(--hub-text-dim);
    display: flex;
    align-items: center;
    gap: 0.35rem;
    flex-wrap: wrap;
  }
  .dep-meta code {
    font-size: 0.7rem;
    background: var(--hub-bg);
    padding: 0.05rem 0.3rem;
    border-radius: 3px;
  }
  .muted {
    color: var(--hub-text-dim);
  }
  .small {
    font-size: 0.7rem;
  }
</style>
