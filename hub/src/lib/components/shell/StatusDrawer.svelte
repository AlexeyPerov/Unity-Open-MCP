<script lang="ts">
  import { S } from "$lib/state.svelte";
  import { revealItemInDir } from "@tauri-apps/plugin-opener";

  function toggle() {
    S.drawerExpanded = !S.drawerExpanded;
  }

  function handleClear() {
    S.clearDrawerLogs();
  }

  function handleRevealCrashLog() {
    const target = S.lastLaunchFailure?.crashLogPath;
    if (!target) return;
    revealItemInDir(target).catch((e: unknown) => {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`reveal crash logs failed: ${msg}`);
    });
  }

  function handleDismissFailure() {
    S.clearLastLaunchFailure();
  }

  function formatFailureTime(iso: string): string {
    try {
      const d = new Date(iso);
      return d.toLocaleString();
    } catch {
      return iso;
    }
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
      <span class="drawer-chevron" aria-hidden="true">
        {#if S.drawerExpanded}
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="6 15 12 9 18 15"/>
          </svg>
        {:else}
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="6 9 12 15 18 9"/>
          </svg>
        {/if}
      </span>
    </div>
  </div>
  {#if S.drawerExpanded}
    <div class="drawer-body">
      {#if S.lastLaunchFailure}
        <div class="failure-card" role="alert">
          <div class="failure-head">
            <div class="failure-title">
              <span class="chip chip-warn">launch failed</span>
              <span class="failure-project">{S.lastLaunchFailure.projectName}</span>
              <span class="failure-time" title={S.lastLaunchFailure.timestamp}>
                {formatFailureTime(S.lastLaunchFailure.timestamp)}
              </span>
            </div>
            <button
              type="button"
              class="drawer-action"
              onclick={handleDismissFailure}
              aria-label="Dismiss launch failure"
              title="Dismiss"
            >
              ×
            </button>
          </div>
          <div class="failure-actions">
            {#if S.lastLaunchFailure.isLikelyCrash && S.lastLaunchFailure.crashLogPath}
              <button
                type="button"
                class="drawer-action"
                onclick={handleRevealCrashLog}
                title={S.lastLaunchFailure.crashLogPath}
              >
                Reveal crash logs
              </button>
            {/if}
            {#if S.lastLaunchFailure.launchLogPath}
              <span class="failure-hint" title={S.lastLaunchFailure.launchLogPath}>
                on-disk log: {S.lastLaunchFailure.launchLogPath}
              </span>
            {/if}
          </div>
        </div>
      {/if}
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

  .chip-warn {
    background: #4a3a1e;
    color: #f0c87a;
    border: 1px solid #6a4a2a;
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

  .failure-card {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.45rem 0.55rem;
    margin-bottom: 0.45rem;
    border: 1px solid #5a2333;
    border-radius: 6px;
    background: #2a1320;
  }

  .failure-head {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.45rem;
  }

  .failure-title {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    min-width: 0;
    flex: 1;
  }

  .failure-project {
    font-size: 0.78rem;
    color: #f0d8b8;
    font-weight: 600;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .failure-time {
    font-size: 0.7rem;
    color: #b89e5a;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .failure-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    flex-wrap: wrap;
  }

  .failure-hint {
    font-size: 0.7rem;
    color: #b89e5a;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    flex: 1;
    min-width: 0;
  }
</style>
