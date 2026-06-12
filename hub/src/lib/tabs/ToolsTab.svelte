<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import {
    getAssetStorePaths,
    getLogPaths,
    type AssetStorePaths,
    type LogPaths,
  } from "$lib/services/config";
  import { openPath } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";

  let logPaths = $state<LogPaths | null>(null);
  let logPathsError = $state<string | null>(null);
  let assetStorePaths = $state<AssetStorePaths | null>(null);
  let assetStoreError = $state<string | null>(null);
  let openingLog = $state<string | null>(null);
  let openingAssetStore = $state(false);
  let actionError = $state<string | null>(null);

  onMount(() => {
    (async () => {
      try {
        logPaths = await getLogPaths("");
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        logPathsError = `could not resolve log paths: ${msg}`;
      }
      try {
        assetStorePaths = await getAssetStorePaths();
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        assetStoreError = `could not resolve asset store path: ${msg}`;
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

  async function openAssetStore() {
    const target = assetStorePaths?.folder;
    const missing = assetStorePaths?.missingMessage;
    if (!target || openingAssetStore) return;
    openingAssetStore = true;
    actionError = null;
    try {
      await openPath(target);
      const note = assetStorePaths?.versioned
        ? ""
        : " (versioned Asset Store subfolder not found — opened the Unity parent folder)";
      S.appendDrawerLog(`opened asset store downloads: ${target}${note}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open asset store downloads failed: ${msg}`;
      S.appendErrorLog(actionError);
    } finally {
      openingAssetStore = false;
    }
    if (missing && assetStorePaths && !assetStorePaths.versioned) {
      S.appendDrawerLog(missing);
    }
  }

  function handleEditorLogs() {
    void openLogTarget("editor logs folder", logPaths?.editorLogsFolder);
  }

  function handleCrashLogs() {
    void openLogTarget("crash logs folder", logPaths?.crashLogsFolder);
  }

  function handleEditorLogFile() {
    void openLogTarget("Editor.log", logPaths?.editorLogFile);
  }

  function handleEditorPrevLogFile() {
    void openLogTarget("Editor-prev.log", logPaths?.editorPrevLogFile);
  }

  function handlePlayerLogFile() {
    void openLogTarget("Player.log", logPaths?.playerLogFile);
  }

  function handleUnityPlayerLogFile() {
    void openLogTarget("standalone Player.log", logPaths?.unityPlayerLogFile);
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
        <div class="log-row">
          <span class="log-label">Editor-prev.log</span>
          <div class="log-actions">
            {#if logPaths.editorPrevLogFile}
              <button
                type="button"
                class="link-btn"
                onclick={handleEditorPrevLogFile}
                title={`Open ${logPaths.editorPrevLogFile}`}
              >
                {openingLog === logPaths.editorPrevLogFile ? "Opening…" : "Open file ↗"}
              </button>
            {:else}
              <span class="muted-inline">no file path resolved</span>
            {/if}
            <span class="log-path" title={logPaths.editorPrevLogFile ?? ""}>
              {logPaths.editorPrevLogFile ?? "—"}
            </span>
          </div>
        </div>
        <div class="log-row">
          <span class="log-label">Player.log (editor preview)</span>
          <div class="log-actions">
            {#if logPaths.playerLogFile}
              <button
                type="button"
                class="link-btn"
                onclick={handlePlayerLogFile}
                title={`Open ${logPaths.playerLogFile}`}
              >
                {openingLog === logPaths.playerLogFile ? "Opening…" : "Open file ↗"}
              </button>
            {:else}
              <span class="muted-inline">no file path resolved</span>
            {/if}
            <span class="log-path" title={logPaths.playerLogFile ?? ""}>
              {logPaths.playerLogFile ?? "—"}
            </span>
          </div>
        </div>
      </div>
    {/if}
  </section>

  <section class="panel" aria-labelledby="standalone-player-log-title">
    <header class="panel-head">
      <h2 id="standalone-player-log-title" class="panel-title">Standalone Player log</h2>
      <p class="panel-hint">
        Per-user <code>Player.log</code> written by standalone Unity Player builds (not
        the editor preview). macOS uses <code>~/Library/Logs/Unity/Player.log</code>,
        Windows uses <code>%LOCALAPPDATA%\Unity\Player.log</code>, Linux uses
        <code>~/.config/unity3d/Player.log</code>. The button is disabled until a
        standalone build has been run on this machine (the file does not exist yet).
      </p>
    </header>

    {#if logPathsError}
      <p class="field-error" role="alert">{logPathsError}</p>
    {:else if !logPaths}
      <p class="panel-empty">Resolving log paths…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Player.log (standalone)</span>
          <div class="log-actions">
            {#if logPaths.unityPlayerLogFile}
              <button
                type="button"
                class="link-btn"
                onclick={handleUnityPlayerLogFile}
                title={`Open ${logPaths.unityPlayerLogFile}`}
              >
                {openingLog === logPaths.unityPlayerLogFile ? "Opening…" : "Open file ↗"}
              </button>
            {:else}
              <span class="muted-inline">no standalone player log on disk yet</span>
            {/if}
            <span class="log-path" title={logPaths.unityPlayerLogFile ?? ""}>
              {logPaths.unityPlayerLogFile ?? "—"}
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
        Per-project, opened from each project's settings popup in the
        Projects tab (click the gear icon on a row).
      </p>
    </header>
  </section>

  <section class="panel" aria-labelledby="asset-store-title">
    <header class="panel-head">
      <h2 id="asset-store-title" class="panel-title">Asset Store downloads</h2>
      <p class="panel-hint">
        Opens the folder where Unity drops Asset Store packages. macOS uses
        <code>~/Library/Application Support/Unity/Asset Store-5.x</code>,
        Windows uses <code>%LOCALAPPDATA%\Unity\Asset Store-5.x</code>. If no
        versioned subfolder exists yet, falls back to the <code>Unity</code>
        parent folder.
      </p>
    </header>

    {#if assetStoreError}
      <p class="field-error" role="alert">{assetStoreError}</p>
    {:else if !assetStorePaths}
      <p class="panel-empty">Resolving asset store path…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Asset Store folder</span>
          <div class="log-actions">
            <Button
              variant="secondary"
              disabled={!assetStorePaths.folder || openingAssetStore}
              title={assetStorePaths.folder ?? "path unavailable"}
              onclick={openAssetStore}
            >
              {openingAssetStore ? "Opening…" : assetStorePaths.versioned ? "Open folder" : "Open parent folder"}
            </Button>
            <span class="log-path" title={assetStorePaths.folder ?? ""}>
              {assetStorePaths.folder ?? "—"}
            </span>
          </div>
        </div>
        {#if assetStorePaths.missingMessage}
          <p class="field-hint" role="status">{assetStorePaths.missingMessage}</p>
        {/if}
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
    overflow-y: auto;
  }

  .inline-error {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.7rem;
    border: 1px solid var(--hub-error-fg);
    border-radius: 6px;
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
    font-size: 0.82rem;
  }

  .inline-error-text { flex: 1; }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: var(--hub-error-fg);
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover { color: var(--hub-text-bright); }

  .panel {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    padding: 0.7rem 0.85rem;
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-bg);
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
    color: var(--hub-text-dim);
    font-weight: 600;
  }

  .panel-hint {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-text-muted);
  }

  .panel-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: var(--hub-bg);
    padding: 0 0.3rem;
    border-radius: 3px;
    color: var(--hub-text);
  }

  .panel-empty {
    margin: 0;
    font-size: 0.82rem;
    color: var(--hub-text-placeholder);
  }

  .field-error {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-error-fg);
  }

  .field-hint {
    margin: 0;
    font-size: 0.76rem;
    color: var(--hub-warning);
    line-height: 1.4;
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
    color: var(--hub-text-dim);
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
    color: var(--hub-text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .link-btn {
    background: transparent;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    padding: 0.4rem 0.7rem;
    color: var(--hub-text-dim);
    font-size: 0.82rem;
    cursor: pointer;
    line-height: 1.4;
    flex-shrink: 0;
  }

  .link-btn:hover {
    border-color: var(--hub-accent);
    color: var(--hub-text-bright);
  }

  .muted-inline {
    color: var(--hub-text-placeholder);
    font-size: 0.78rem;
  }
</style>
