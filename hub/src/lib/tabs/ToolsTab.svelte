<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import {
    getAssetStorePaths,
    getLogPaths,
    saveProjects,
    type AssetStorePaths,
    type LogPaths,
    type ProjectEntry,
  } from "$lib/services/config";
  import { openPath } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";

  type EnvVarDraft = {
    /** Stable id for Svelte keyed each blocks (key+value would not be unique). */
    uid: string;
    key: string;
    value: string;
  };

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
      // Surface the same inline message shown on the button so the drawer
      // log mirrors what the user sees on the screen.
      S.appendDrawerLog(missing);
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

  function handleEditorPrevLogFile() {
    void openLogTarget("Editor-prev.log", logPaths?.editorPrevLogFile);
  }

  function handlePlayerLogFile() {
    void openLogTarget("Player.log", logPaths?.playerLogFile);
  }

  function handleUnityPlayerLogFile() {
    void openLogTarget("standalone Player.log", logPaths?.unityPlayerLogFile);
  }

  // M1.5-17: env-vars panel state. The draft mirrors the persisted
  // `envVars` record for the currently selected project; saving writes
  // the record back to `projects.json` via the same atomic path the
  // rest of the project mutations use. The draft is a local copy so
  // the user can add / edit / remove rows before pressing Save.
  let envVarsDraft = $state<EnvVarDraft[]>([]);
  let envVarsRevealed = $state<Record<string, boolean>>({});
  let envVarsSaving = $state(false);
  let envVarsError = $state<string | null>(null);
  let envVarsInfo = $state<string | null>(null);
  let nextDraftUid = 1;

  const envVarsSelectedProject = $derived<ProjectEntry | null>(
    projectsStore.selectedProjectId
      ? (projectsStore.find(projectsStore.selectedProjectId) ?? null)
      : null,
  );

  $effect(() => {
    // Re-seed the draft when the user picks a different project (or
    // clears the selection). Skipping this effect when the project is
    // null avoids clobbering an in-progress edit if the underlying
    // store reloads transiently.
    const project = envVarsSelectedProject;
    if (!project) {
      envVarsDraft = [];
      envVarsError = null;
      envVarsInfo = null;
      return;
    }
    envVarsDraft = Object.entries(project.envVars ?? {})
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, value]) => ({
        uid: `seed-${nextDraftUid++}`,
        key,
        value,
      }));
    envVarsError = null;
    envVarsInfo = null;
  });

  function newEnvVarDraft(): EnvVarDraft {
    return { uid: `draft-${nextDraftUid++}`, key: "", value: "" };
  }

  function addEnvVarRow() {
    envVarsDraft = [...envVarsDraft, newEnvVarDraft()];
  }

  function removeEnvVarRow(uid: string) {
    envVarsDraft = envVarsDraft.filter((r) => r.uid !== uid);
  }

  function toggleReveal(uid: string) {
    envVarsRevealed = { ...envVarsRevealed, [uid]: !envVarsRevealed[uid] };
  }

  function isValidEnvVarDraft(rows: EnvVarDraft[]): { ok: true; map: Record<string, string> } | { ok: false; error: string } {
    const map: Record<string, string> = {};
    for (const row of rows) {
      const key = row.key.trim();
      if (key === "") {
        return { ok: false, error: "env-var keys cannot be empty" };
      }
      if (key.includes("=")) {
        return { ok: false, error: `env-var key cannot contain '=': ${key}` };
      }
      if (Object.prototype.hasOwnProperty.call(map, key)) {
        return { ok: false, error: `duplicate env-var key: ${key}` };
      }
      map[key] = row.value;
    }
    return { ok: true, map };
  }

  async function saveEnvVars() {
    const project = envVarsSelectedProject;
    if (!project) return;
    const validation = isValidEnvVarDraft(envVarsDraft);
    if (!validation.ok) {
      envVarsError = validation.error;
      return;
    }
    envVarsError = null;
    envVarsInfo = null;
    envVarsSaving = true;
    try {
      const updated: ProjectEntry = {
        ...project,
        envVars: validation.map,
      };
      const nextList = projectsStore.projects.map((p) =>
        p.id === project.id ? updated : p,
      );
      await saveProjects({ version: 1, projects: nextList });
      projectsStore.replaceAll(nextList);
      envVarsInfo = `saved ${Object.keys(validation.map).length} env var${Object.keys(validation.map).length === 1 ? "" : "s"} for ${project.name}`;
      S.appendDrawerLog(envVarsInfo);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      envVarsError = `save env vars failed: ${msg}`;
      S.appendErrorLog(envVarsError);
    } finally {
      envVarsSaving = false;
    }
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

  <section class="panel" aria-labelledby="env-vars-title">
    <header class="panel-head">
      <h2 id="env-vars-title" class="panel-title">Environment variables</h2>
      <p class="panel-hint">
        Per-project environment variables merged into the spawned Unity process.
        Values in the child override the parent process when keys collide. Manage
        per-project on this tab; the safety toggle in
        <code>Settings → Safety</code> controls whether the launch button shows a
        confirmation modal listing colliding keys.
      </p>
    </header>

    {#if !envVarsSelectedProject}
      <p class="panel-empty">
        Select a project on the Projects tab to edit its environment variables.
      </p>
    {:else}
      <p class="panel-subhead">
        Editing: <strong>{envVarsSelectedProject?.name}</strong>
      </p>

      {#if envVarsError}
        <p class="field-error" role="alert">{envVarsError}</p>
      {/if}
      {#if envVarsInfo}
        <p class="field-hint" role="status">{envVarsInfo}</p>
      {/if}

      <div class="env-grid">
        {#each envVarsDraft as row (row.uid)}
          <div class="env-row">
            <input
              type="text"
              class="env-key"
              placeholder="KEY"
              bind:value={row.key}
              aria-label="Environment variable name"
              spellcheck="false"
              autocomplete="off"
            />
            <div class="env-value-wrap">
              <input
                type={envVarsRevealed[row.uid] ? "text" : "password"}
                class="env-value"
                placeholder="value"
                bind:value={row.value}
                aria-label="Environment variable value"
                spellcheck="false"
                autocomplete="off"
              />
              <button
                type="button"
                class="link-btn env-reveal"
                onclick={() => toggleReveal(row.uid)}
                aria-label={envVarsRevealed[row.uid] ? "Hide value" : "Show value"}
              >
                {envVarsRevealed[row.uid] ? "Hide" : "Show"}
              </button>
            </div>
            <button
              type="button"
              class="link-btn env-remove"
              onclick={() => removeEnvVarRow(row.uid)}
              aria-label="Remove env var row"
            >
              Remove
            </button>
          </div>
        {/each}
      </div>

      <div class="env-actions">
        <Button variant="secondary" onclick={addEnvVarRow}>+ Add env var</Button>
        <Button
          variant="primary"
          disabled={envVarsSaving}
          onclick={saveEnvVars}
        >
          {envVarsSaving ? "Saving…" : "Save"}
        </Button>
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

  .field-hint {
    margin: 0;
    font-size: 0.76rem;
    color: #b89e5a;
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

  .panel-subhead {
    margin: 0;
    font-size: 0.78rem;
    color: #c5c7d0;
  }

  .env-grid {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .env-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .env-key,
  .env-value {
    flex: 1 1 8rem;
    min-width: 0;
    background: #14151a;
    color: #e9e9ef;
    border: 1px solid #34353f;
    border-radius: 6px;
    padding: 0.4rem 0.55rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.82rem;
    line-height: 1.4;
  }

  .env-key:focus,
  .env-value:focus {
    outline: 2px solid #5c7cfa;
    outline-offset: 0;
    border-color: #5c7cfa;
  }

  .env-key {
    flex: 0 1 12rem;
  }

  .env-value-wrap {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    flex: 1 1 12rem;
    min-width: 0;
  }

  .env-value {
    flex: 1;
  }

  .env-reveal,
  .env-remove {
    flex: 0 0 auto;
  }

  .env-actions {
    display: flex;
    flex-direction: row;
    gap: 0.5rem;
    align-items: center;
    margin-top: 0.2rem;
  }
</style>
