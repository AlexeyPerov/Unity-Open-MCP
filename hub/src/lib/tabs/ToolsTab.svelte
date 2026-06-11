<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import {
    getLogPaths,
    type LogPaths,
  } from "$lib/services/config";
  import { openPath } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";

  let logPaths = $state<LogPaths | null>(null);
  let logPathsError = $state<string | null>(null);
  let openingLog = $state<string | null>(null);
  let actionError = $state<string | null>(null);

  onMount(() => {
    (async () => {
      try {
        logPaths = await getLogPaths("");
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        logPathsError = `could not resolve log paths: ${msg}`;
      }
    })();
  });

  async function openLogTarget(label: string, target: string | undefined) {
    if (!target || openingLog) return;
    openingLog = target;
    actionError = null;
    try {
      await openPath(target);
      S.appendDrawerLog(`opened ${label}: ${target}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open ${label} failed: ${msg}`;
      S.appendErrorLog(actionError);
    } finally {
      openingLog = null;
    }
  }

  function handleEditorLogs() {
    void openLogTarget("editor logs folder", logPaths?.editorLogsFolder);
  }

  function handlePlayerLogs() {
    void openLogTarget("player logs folder", logPaths?.playerLogsFolder);
  }

  function handleCrashLogs() {
    void openLogTarget("crash logs folder", logPaths?.crashLogsFolder);
  }

  function handleEditorLogFile() {
    void openLogTarget("Editor.log", logPaths?.editorLogFile);
  }
</script>

<div class="tools">
  {#if actionError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{actionError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={() => (actionError = null)}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  <section class="panel" aria-labelledby="log-shortcuts-title">
    <header class="panel-head">
      <h2 id="log-shortcuts-title" class="panel-title">Editor logs</h2>
      <p class="panel-hint">
        Platform-aware paths to Unity Editor log files and folders.
        macOS uses <code>~/Library/Logs/Unity</code>, Windows uses
        <code>%LOCALAPPDATA%\Unity\Editor</code>.
      </p>
    </header>

    {#if logPathsError}
      <p class="field-error" role="alert">{logPathsError}</p>
    {:else if !logPaths}
      <p class="panel-empty">Resolving log paths…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Editor logs folder</span>
          <div class="log-actions">
            <Button
              variant="secondary"
              disabled={!logPaths.editorLogsFolder || openingLog === logPaths.editorLogsFolder}
              title={logPaths.editorLogsFolder ?? "path unavailable"}
              onclick={handleEditorLogs}
            >
              {openingLog === logPaths.editorLogsFolder ? "Opening…" : "Open folder"}
            </Button>
            <span class="log-path" title={logPaths.editorLogsFolder ?? ""}>
              {logPaths.editorLogsFolder ?? "—"}
            </span>
          </div>
        </div>
        <div class="log-row">
          <span class="log-label">Editor.log</span>
          <div class="log-actions">
            {#if logPaths.editorLogFile}
              <button
                type="button"
                class="link-btn"
                onclick={handleEditorLogFile}
                title={`Open ${logPaths.editorLogFile}`}
              >
                {openingLog === logPaths.editorLogFile ? "Opening…" : "Open file ↗"}
              </button>
            {:else}
              <span class="muted-inline">no file path resolved</span>
            {/if}
            <span class="log-path" title={logPaths.editorLogFile ?? ""}>
              {logPaths.editorLogFile ?? "—"}
            </span>
          </div>
        </div>
      </div>
    {/if}
  </section>

  <section class="panel" aria-labelledby="crash-logs-title">
    <header class="panel-head">
      <h2 id="crash-logs-title" class="panel-title">Crash logs</h2>
      <p class="panel-hint">
        Platform-aware paths to Unity crash report files and folders.
        macOS uses <code>~/Library/Logs/DiagnosticReports</code>, Windows uses
        <code>%LOCALAPPDATA%\CrashDumps</code>.
      </p>
    </header>

    {#if logPathsError}
      <p class="field-error" role="alert">{logPathsError}</p>
    {:else if !logPaths}
      <p class="panel-empty">Resolving log paths…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Crash logs folder</span>
          <div class="log-actions">
            <Button
              variant="secondary"
              disabled={!logPaths.crashLogsFolder || openingLog === logPaths.crashLogsFolder}
              title={logPaths.crashLogsFolder ?? "path unavailable"}
              onclick={handleCrashLogs}
            >
              {openingLog === logPaths.crashLogsFolder ? "Opening…" : "Open folder"}
            </Button>
            <span class="log-path" title={logPaths.crashLogsFolder ?? ""}>
              {logPaths.crashLogsFolder ?? "—"}
            </span>
          </div>
        </div>
      </div>
    {/if}
  </section>

  <section class="panel" aria-labelledby="player-logs-title">
    <header class="panel-head">
      <h2 id="player-logs-title" class="panel-title">Player logs</h2>
      <p class="panel-hint">
        Player logs are per-project, stored in each project's <code>Logs/</code> folder.
        Open a project's expanded panel in the Projects tab to access its player logs.
      </p>
    </header>

    {#if logPathsError}
      <p class="field-error" role="alert">{logPathsError}</p>
    {:else if !logPaths}
      <p class="panel-empty">Resolving log paths…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Player logs folder</span>
          <div class="log-actions">
            <Button
              variant="secondary"
              disabled={!logPaths.playerLogsFolder || openingLog === logPaths.playerLogsFolder}
              title={logPaths.playerLogsFolder ?? "path unavailable"}
              onclick={handlePlayerLogs}
            >
              {openingLog === logPaths.playerLogsFolder ? "Opening…" : "Open folder"}
            </Button>
            <span class="log-path" title={logPaths.playerLogsFolder ?? ""}>
              {logPaths.playerLogsFolder ?? "—"}
            </span>
          </div>
        </div>
      </div>
    {/if}
  </section>
</div>

<style>
  .tools {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    gap: 0.8rem;
  }

  .inline-error {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.7rem;
    border: 1px solid #5a2333;
    border-radius: 6px;
    background: #2a1320;
    color: #f0a8b8;
    font-size: 0.82rem;
  }

  .inline-error-text { flex: 1; }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: #f0a8b8;
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover { color: #fff; }

  .panel {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    padding: 0.7rem 0.85rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1a1b21;
  }

  .panel-head {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .panel-title {
    margin: 0;
    font-size: 0.78rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #c5c7d0;
    font-weight: 600;
  }

  .panel-hint {
    margin: 0;
    font-size: 0.78rem;
    color: #8b8d9a;
  }

  .panel-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
    color: #d7d8e0;
  }

  .panel-empty {
    margin: 0;
    font-size: 0.82rem;
    color: #6f7280;
  }

  .field-error {
    margin: 0;
    font-size: 0.78rem;
    color: #f0a8b8;
  }

  .log-grid {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .log-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
  }

  .log-label {
    flex: 0 0 7rem;
    font-size: 0.78rem;
    color: #c5c7d0;
    font-weight: 500;
  }

  .log-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.55rem;
    flex: 1;
    min-width: 0;
  }

  .log-path {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #8b8d9a;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .link-btn {
    background: transparent;
    border: 1px solid #3f4150;
    border-radius: 6px;
    padding: 0.4rem 0.7rem;
    color: #c5c7d0;
    font-size: 0.82rem;
    cursor: pointer;
    line-height: 1.4;
    flex-shrink: 0;
  }

  .link-btn:hover {
    border-color: #5c7cfa;
    color: #fff;
  }

  .muted-inline {
    color: #6f7280;
    font-size: 0.78rem;
  }
</style>
