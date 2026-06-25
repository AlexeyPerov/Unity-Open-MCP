<script lang="ts">
  import { logs } from "../../state/logs.svelte";

  function toggle() {
    logs.expanded = !logs.expanded;
  }

  function handleClear() {
    logs.clear();
  }
</script>

<div class="drawer" class:expanded={logs.expanded}>
  <div
    class="drawer-bar"
    onclick={toggle}
    role="button"
    tabindex="0"
    onkeydown={(e) => e.key === "Enter" && toggle()}
  >
    <div class="drawer-bar-left">
      {#if logs.lines.length > 0}
        <span class="chip chip-muted" title="Log line count">{logs.lines.length}</span>
      {/if}
      <span class="drawer-label">Status / Log</span>
      {#if logs.lines.length > 0}
        <span class="drawer-tail">{logs.lines[logs.lines.length - 1]}</span>
      {/if}
    </div>
    <div class="drawer-bar-right">
      {#if logs.expanded && logs.lines.length > 0}
        <button
          type="button"
          class="drawer-action"
          onclick={(e) => {
            e.stopPropagation();
            handleClear();
          }}
        >Clear</button
        >
      {/if}
      <span class="drawer-chevron" aria-hidden="true">
        {#if logs.expanded}
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="6 15 12 9 18 15" />
          </svg>
        {:else}
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="6 9 12 15 18 9" />
          </svg>
        {/if}
      </span>
    </div>
  </div>
  {#if logs.expanded}
    <div class="drawer-body">
      {#if logs.lines.length === 0}
        <p class="drawer-empty">No log output yet.</p>
      {:else}
        <div class="drawer-log-scroll">
          {#each logs.lines as line}
            <pre class="drawer-log-line">{line}</pre>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .drawer {
    flex-shrink: 0;
    border-top: 1px solid var(--hub-border);
    background: var(--hub-surface);
  }

  .drawer-bar {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    padding: 0.35rem 0.65rem;
    cursor: pointer;
    gap: 0.5rem;
    user-select: none;
  }

  .drawer-bar:hover {
    background: var(--hub-bg);
  }

  .drawer-bar-left {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    min-width: 0;
    flex: 1;
  }

  .drawer-bar-right {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    flex-shrink: 0;
  }

  .drawer-label {
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--hub-text-muted);
    font-weight: 600;
    white-space: nowrap;
  }

  .drawer-tail {
    font-size: 0.72rem;
    color: var(--hub-text-disabled);
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.4rem;
    border-radius: 4px;
    font-size: 0.68rem;
    font-weight: 600;
    line-height: 1.3;
  }

  .chip-muted {
    background: var(--hub-selected);
    color: var(--hub-text-muted);
    border: 1px solid var(--hub-border-light);
  }

  .drawer-action {
    padding: 0.15rem 0.45rem;
    font-size: 0.68rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-hover);
    background: var(--hub-selected);
    color: var(--hub-text);
    cursor: pointer;
  }

  .drawer-action:hover {
    border-color: var(--hub-accent);
    color: var(--hub-text-bright);
  }

  .drawer-chevron {
    color: var(--hub-text-muted);
    display: flex;
    align-items: center;
  }

  .drawer-body {
    padding: 0.45rem 0.65rem 0.65rem;
    border-top: 1px solid var(--hub-border-light);
  }

  .drawer-empty {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-text-disabled);
  }

  .drawer-log-scroll {
    max-height: 12rem;
    overflow: auto;
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
  }

  .drawer-log-line {
    margin: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    line-height: 1.4;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--hub-text-dim);
  }
</style>
