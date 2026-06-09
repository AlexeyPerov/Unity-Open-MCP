<script lang="ts">
  import { S } from "$lib/state.svelte";

  function toggle() {
    S.drawerExpanded = !S.drawerExpanded;
  }

  function handleClear() {
    S.clearDrawerLogs();
  }
</script>

<div class="drawer" class:expanded={S.drawerExpanded}>
  <div class="drawer-bar" onclick={toggle} role="button" tabindex="0" onkeydown={(e) => e.key === "Enter" && toggle()}>
    <div class="drawer-bar-left">
      {#if S.drawerLogs.length > 0}
        <span class="chip chip-info">{S.drawerLogs.length}</span>
      {/if}
      <span class="drawer-label">Status / Log</span>
      {#if S.drawerLogs.length > 0}
        <span class="drawer-tail">{S.drawerLogs[S.drawerLogs.length - 1]}</span>
      {/if}
    </div>
    <div class="drawer-bar-right">
      {#if S.drawerExpanded && S.drawerLogs.length > 0}
        <button type="button" class="drawer-action" onclick={(e) => { e.stopPropagation(); handleClear(); }}>Clear</button>
      {/if}
      <span class="drawer-chevron" class:chevron-up={S.drawerExpanded}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <polyline points="6 9 12 15 18 9"/>
        </svg>
      </span>
    </div>
  </div>
  {#if S.drawerExpanded}
    <div class="drawer-body">
      {#if S.drawerLogs.length === 0}
        <p class="drawer-empty">No log output yet.</p>
      {:else}
        <div class="drawer-log-scroll">
          {#each S.drawerLogs as line}
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
    border-top: 1px solid #34353f;
    background: #1a1b21;
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
    background: #1e1f26;
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
    color: #8b8d9a;
    font-weight: 600;
    white-space: nowrap;
  }

  .drawer-tail {
    font-size: 0.72rem;
    color: #555;
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

  .chip-info {
    background: #1e2a4a;
    color: #7c9cfa;
    border: 1px solid #2a3a6a;
  }

  .drawer-action {
    padding: 0.15rem 0.45rem;
    font-size: 0.68rem;
    border-radius: 4px;
    border: 1px solid #474957;
    background: #32343f;
    color: #c5c7d0;
    cursor: pointer;
  }

  .drawer-action:hover {
    border-color: #5c7cfa;
    color: #fff;
  }

  .drawer-chevron {
    color: #8b8d9a;
    display: flex;
    align-items: center;
    transition: transform 0.15s ease;
  }

  .chevron-up {
    transform: rotate(180deg);
  }

  .drawer-body {
    padding: 0.45rem 0.65rem 0.65rem;
    border-top: 1px solid #2a2b33;
  }

  .drawer-empty {
    margin: 0;
    font-size: 0.78rem;
    color: #555;
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
    color: #9ea1ad;
  }
</style>
