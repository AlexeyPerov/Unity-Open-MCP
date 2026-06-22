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

  function handleDismissNotice() {
    S.clearLaunchInfoNotice();
  }

  function handleTerminateAndRelaunch() {
    // The action is reachable from either the calm "already running"
    // notice (the normal path) or the red launch-failed card. The
    // registered handler in ProjectsTab resolves the conflict PID from
    // whichever surface matches the project id.
    const target = S.launchInfoNotice ?? S.lastLaunchFailure;
    if (!target) return;
    void S.requestTerminateAndRelaunch(target.projectId);
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
        <span class="chip chip-muted" title="Log line count (dev only)">{S.drawerLogs.length}</span>
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
      {#if S.launchInfoNotice}
        <div class="notice-card" role="status">
          <div class="notice-head">
            <div class="notice-title">
              <span class="chip chip-info">already running</span>
              <span class="notice-project">{S.launchInfoNotice.projectName}</span>
              <span class="notice-time" title={S.launchInfoNotice.timestamp}>
                {formatFailureTime(S.launchInfoNotice.timestamp)}
              </span>
            </div>
            <button
              type="button"
              class="drawer-action"
              onclick={handleDismissNotice}
              aria-label="Dismiss notice"
              title="Dismiss"
            >
              ×
            </button>
          </div>
          <p class="notice-body">{S.launchInfoNotice.message}</p>
          <div class="notice-actions">
            {#if S.launchInfoNotice.conflictPid !== null}
              <button
                type="button"
                class="drawer-action drawer-action-accent"
                onclick={handleTerminateAndRelaunch}
                title={`Terminate pid ${S.launchInfoNotice.conflictPid} and re-launch ${S.launchInfoNotice.projectName}`}
              >
                Terminate &amp; relaunch
              </button>
              <span class="notice-hint">
                conflict pid {S.launchInfoNotice.conflictPid}
              </span>
            {/if}
          </div>
        </div>
      {/if}
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
            {#if S.lastLaunchFailure.conflictPid !== null}
              <button
                type="button"
                class="drawer-action drawer-action-accent"
                onclick={handleTerminateAndRelaunch}
                title={`Terminate pid ${S.lastLaunchFailure.conflictPid} and re-launch ${S.lastLaunchFailure.projectName}`}
              >
                Terminate &amp; relaunch
              </button>
              <span class="failure-hint">
                conflict pid {S.lastLaunchFailure.conflictPid}
              </span>
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

  .chip-info {
    background: var(--hub-info-bg);
    color: var(--hub-info-fg);
    border: 1px solid var(--hub-info-fg);
  }

  .chip-muted {
    background: var(--hub-selected);
    color: var(--hub-text-muted);
    border: 1px solid var(--hub-border-light);
  }

  .chip-warn {
    background: var(--hub-warn-bg);
    color: var(--hub-warn-fg);
    border: 1px solid var(--hub-warn-fg);
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

  .drawer-action-accent {
    background: var(--hub-info-bg);
    border-color: var(--hub-info-fg);
    color: var(--hub-info-fg);
  }

  .drawer-action-accent:hover {
    background: var(--hub-selected);
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

  .failure-card {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.45rem 0.55rem;
    margin-bottom: 0.45rem;
    border: 1px solid var(--hub-error-fg);
    border-radius: 6px;
    background: var(--hub-error-bg);
  }

  .notice-card {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    padding: 0.45rem 0.55rem;
    margin-bottom: 0.45rem;
    border: 1px solid var(--hub-info-fg);
    border-radius: 6px;
    background: var(--hub-info-bg);
  }

  .notice-head {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.45rem;
  }

  .notice-title {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    min-width: 0;
    flex: 1;
  }

  .notice-project {
    font-size: 0.78rem;
    color: var(--hub-info-fg);
    font-weight: 600;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .notice-time {
    font-size: 0.7rem;
    color: var(--hub-text-muted);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .notice-body {
    margin: 0;
    font-size: 0.76rem;
    color: var(--hub-text);
    line-height: 1.4;
  }

  .notice-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    flex-wrap: wrap;
  }

  .notice-hint {
    font-size: 0.7rem;
    color: var(--hub-text-muted);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    flex: 1;
    min-width: 0;
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
    color: var(--hub-warn-fg);
    font-weight: 600;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .failure-time {
    font-size: 0.7rem;
    color: var(--hub-warning);
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
    color: var(--hub-warning);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    flex: 1;
    min-width: 0;
  }
</style>
