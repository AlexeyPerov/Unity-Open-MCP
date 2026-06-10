<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import {
    getLogPaths,
    killUnity,
    type KillUnityResult,
    type LogPaths,
    type ProjectEntry,
  } from "$lib/services/config";
  import { openPath } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";

  const BUILD_TARGETS: string[] = [
    "StandaloneWindows64",
    "StandaloneWindows",
    "StandaloneOSX",
    "StandaloneLinux64",
    "iOS",
    "Android",
    "WebGL",
    "WSAPlayer",
    "tvOS",
    "VisionOS",
  ];

  // Characters that could break the persisted JSON, the launch arg parser, or
  // be confused for shell injection. Tabs are not blocked (rare but legitimate
  // for some Unity flags), but newlines / NULs / shell metas are.
  const UNSAFE_RE = /[\n\r\0`$|&;<>]/;

  let loaded = $state(false);
  let argsDraft = $state("");
  let argsDirty = $state(false);
  let argsError = $state<string | null>(null);
  let savingArgs = $state(false);
  let platformIntentDraft = $state<string>("");
  let platformIntentDirty = $state(false);
  let savingIntent = $state(false);
  let actionError = $state<string | null>(null);
  let logPaths = $state<LogPaths | null>(null);
  let logPathsError = $state<string | null>(null);
  let openingLog = $state<string | null>(null);
  let killing = $state(false);

  let selected = $derived<ProjectEntry | null>(
    projectsStore.selectedProjectId
      ? projectsStore.projects.find((p) => p.id === projectsStore.selectedProjectId) ?? null
      : null
  );

  onMount(() => {
    let cancelled = false;
    (async () => {
      if (!projectsStore.settings || projectsStore.projects.length === 0) {
        await projectsStore.load();
      }
      if (cancelled) return;
      if (
        !projectsStore.selectedProjectId &&
        projectsStore.projects.length > 0
      ) {
        projectsStore.select(projectsStore.projects[0].id);
      }
      loaded = true;
    })();
    return () => {
      cancelled = true;
    };
  });

  $effect(() => {
    if (!loaded) return;
    if (!selected) {
      argsDraft = "";
      platformIntentDraft = "";
      argsDirty = false;
      platformIntentDirty = false;
      argsError = null;
      logPaths = null;
      logPathsError = null;
      return;
    }
    argsDraft = selected.launchArgs ?? "";
    platformIntentDraft = selected.platformIntent ?? "";
    argsDirty = false;
    platformIntentDirty = false;
    argsError = null;
    void refreshLogPaths(selected);
  });

  async function refreshLogPaths(project: ProjectEntry) {
    logPathsError = null;
    try {
      logPaths = await getLogPaths(project.path);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      logPathsError = `could not resolve log paths: ${msg}`;
      logPaths = null;
    }
  }

  function validateArgs(value: string): string | null {
    const match = value.match(UNSAFE_RE);
    if (match) {
      return `unsafe character "${match[0]}" — strip newlines, pipes, semicolons, or backticks before saving`;
    }
    return null;
  }

  async function handleSaveArgs() {
    if (!selected || savingArgs) return;
    if (argsDraft.trim().length === 0) {
      argsError = "launch args are empty — use Reset to clear instead";
      return;
    }
    const err = validateArgs(argsDraft);
    if (err) {
      argsError = err;
      return;
    }
    savingArgs = true;
    actionError = null;
    try {
      const updated: ProjectEntry = { ...selected, launchArgs: argsDraft };
      await projectsStore.update(updated);
      argsDirty = false;
      argsError = null;
      S.appendDrawerLog(`saved launch args for ${selected.name}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `save launch args failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      savingArgs = false;
    }
  }

  async function handleResetArgs() {
    if (!selected || savingArgs) return;
    savingArgs = true;
    actionError = null;
    try {
      const updated: ProjectEntry = { ...selected, launchArgs: "" };
      await projectsStore.update(updated);
      argsDraft = "";
      argsDirty = false;
      argsError = null;
      S.appendDrawerLog(`cleared launch args for ${selected.name}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `reset launch args failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      savingArgs = false;
    }
  }

  function handleArgsInput(e: Event) {
    const target = e.currentTarget as HTMLTextAreaElement;
    argsDraft = target.value;
    argsDirty = argsDraft !== (selected?.launchArgs ?? "");
    if (argsError) argsError = validateArgs(argsDraft);
  }

  async function handleSaveIntent() {
    if (!selected || savingIntent) return;
    savingIntent = true;
    actionError = null;
    try {
      const next = platformIntentDraft.trim();
      const updated: ProjectEntry = { ...selected, platformIntent: next };
      await projectsStore.update(updated);
      platformIntentDirty = false;
      S.appendDrawerLog(
        next
          ? `set platform intent for ${selected.name} to ${next}`
          : `cleared platform intent for ${selected.name}`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `save platform intent failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      savingIntent = false;
    }
  }

  function handleIntentChange(e: Event) {
    const target = e.currentTarget as HTMLSelectElement;
    platformIntentDraft = target.value;
    platformIntentDirty = platformIntentDraft !== (selected?.platformIntent ?? "");
  }

  function handleProjectChange(e: Event) {
    const target = e.currentTarget as HTMLSelectElement;
    projectsStore.select(target.value || null);
  }

  function intentOptions(): string[] {
    const current = selected?.platformIntent ?? "";
    if (current && !BUILD_TARGETS.includes(current)) {
      return [current, ...BUILD_TARGETS];
    }
    return BUILD_TARGETS;
  }

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
      S.appendDrawerLog(actionError);
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

  function handleEditorLogFileAuxClick(e: MouseEvent) {
    // Open file on middle-click where the OS supports it. The main click
    // handler covers left-click; auxclick fires for middle.
    if (e.button === 1) {
      e.preventDefault();
      handleEditorLogFile();
    }
  }

  async function handleCopyPath() {
    if (!selected) return;
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(selected.path).then(
        () => S.appendDrawerLog(`copied path: ${selected!.path}`),
        () => S.appendDrawerLog("copy failed: clipboard unavailable")
      );
    }
  }

  async function handleOpenProjectFolder() {
    if (!selected) return;
    actionError = null;
    try {
      await openPath(selected.path);
      S.appendDrawerLog(`opened project folder: ${selected.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open project folder failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  function formatKillResult(result: KillUnityResult): string {
    switch (result.status) {
      case "killed":
        return `kill: terminated pid ${result.pid} — ${result.message}`;
      case "notFound":
        return `kill: pid ${result.pid} is not running (${result.message})`;
      case "accessDenied":
        return `kill: access denied for pid ${result.pid} — ${result.message}`;
      default:
        return `kill: ${JSON.stringify(result)}`;
    }
  }

  async function performKill(pid: number) {
    if (!selected || killing) return;
    killing = true;
    actionError = null;
    try {
      const result = await killUnity(pid);
      S.appendDrawerLog(formatKillResult(result));
      if (result.status === "killed") {
        // Clear the recorded PID so subsequent Kill Unity actions show the
        // "no recent launch" state until the next launch.
        const cleared: ProjectEntry = { ...selected, lastLaunchPid: undefined };
        await projectsStore.update(cleared);
      } else if (result.status === "notFound") {
        // PID is stale — clear it so the UI no longer references a dead PID.
        const cleared: ProjectEntry = { ...selected, lastLaunchPid: undefined };
        await projectsStore.update(cleared);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `kill failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      killing = false;
    }
  }

  async function handleKillUnity() {
    if (!selected) return;
    const pid = selected.lastLaunchPid;
    if (!pid) {
      actionError = "no recent Unity launch recorded for this project";
      S.appendDrawerLog(actionError);
      return;
    }
    const confirmKill = projectsStore.settings?.safety.confirmKillUnity ?? true;
    if (confirmKill) {
      const ok = await S.confirm(
        "Kill Unity for this project?",
        `Send a terminate signal to pid ${pid} (last launched from “${selected.name}”). Other Unity instances on this machine are not affected.`
      );
      if (!ok) return;
    }
    await performKill(pid);
  }

  let killButtonLabel = $derived.by(() => {
    if (killing) return "Killing…";
    if (!selected?.lastLaunchPid) return "Kill Unity (no recent launch)";
    return `Kill Unity (pid ${selected.lastLaunchPid})`;
  });

  let killDisabled = $derived(!selected || killing);
  let killTitle = $derived.by(() => {
    if (!selected) return "Select a project above first";
    if (!selected.lastLaunchPid) {
      return "No recorded Unity PID for this project — launch Unity once to enable Kill";
    }
    return `Terminate pid ${selected.lastLaunchPid} (last launched from this project)`;
  });
</script>

<div class="tools">
  <div class="context-bar" role="region" aria-label="Project context">
    <label class="context-label" for="tools-project">Working on</label>
    <select
      id="tools-project"
      class="context-select"
      onchange={handleProjectChange}
      disabled={projectsStore.projects.length === 0}
      aria-label="Project context"
    >
      {#if projectsStore.projects.length === 0}
        <option value="">No projects yet</option>
      {:else if !selected}
        <option value="">Select a project…</option>
      {/if}
      {#each projectsStore.projects as p (p.id)}
        <option value={p.id} selected={p.id === projectsStore.selectedProjectId}>
          {p.name}{p.unityVersion ? ` · ${p.unityVersion}` : ""}
        </option>
      {/each}
    </select>
    {#if selected}
      <span class="context-path" title={selected.path}>{selected.path}</span>
    {/if}
  </div>

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

  <section class="panel" aria-labelledby="launch-args-title">
    <header class="panel-head">
      <h2 id="launch-args-title" class="panel-title">Launch args</h2>
      <p class="panel-hint">Custom arguments appended to the Unity executable on launch.</p>
    </header>

    {#if !selected}
      <p class="panel-empty">Select a project above to edit its launch args.</p>
    {:else}
      <div class="args-row">
        <textarea
          class="args-input"
          rows="2"
          spellcheck="false"
          placeholder="-logFile -&#10;-batchmode"
          value={argsDraft}
          oninput={handleArgsInput}
          aria-label="Launch args"
          aria-invalid={argsError ? "true" : "false"}
          aria-describedby="args-hint"
        ></textarea>
        <div class="args-actions">
          <Button
            variant="primary"
            disabled={!argsDirty || argsDraft.trim().length === 0 || argsError !== null || savingArgs}
            onclick={handleSaveArgs}
          >
            {savingArgs ? "Saving…" : "Save"}
          </Button>
          <Button
            variant="secondary"
            disabled={(!argsDirty && (selected.launchArgs ?? "") === "") || savingArgs}
            onclick={handleResetArgs}
            title="Clear launch args for this project"
          >
            Reset
          </Button>
        </div>
      </div>
      {#if argsError}
        <p class="field-error" role="alert">{argsError}</p>
      {/if}
      <p id="args-hint" class="field-hint">
        Whitespace-separated. Save persists to <code>projects.json</code>; the next launch for {selected.name} uses them.
        Current saved value:
        <code class="saved-value">{(selected.launchArgs ?? "").length === 0 ? "—" : (selected.launchArgs ?? "")}</code>
      </p>
    {/if}
  </section>

  <section class="panel" aria-labelledby="platform-intent-title">
    <header class="panel-head">
      <h2 id="platform-intent-title" class="panel-title">Platform intent</h2>
      <p class="panel-hint">
        Stored <code>BuildTarget</code> for this project. Hub appends
        <code>-buildTarget &lt;name&gt;</code> on the next launch — not applied to a running editor.
      </p>
    </header>

    {#if !selected}
      <p class="panel-empty">Select a project above to set its platform intent.</p>
    {:else}
      <div class="intent-row">
        <div class="intent-control">
          <label class="intent-label" for="platform-intent-select">Build target</label>
          <select
            id="platform-intent-select"
            class="intent-select"
            onchange={handleIntentChange}
            value={platformIntentDraft}
            aria-label="Platform intent"
          >
            <option value="">None (use Unity default)</option>
            {#each intentOptions() as target}
              <option value={target}>{target}</option>
            {/each}
          </select>
        </div>
        <div class="intent-actions">
          <Button
            variant="primary"
            disabled={!platformIntentDirty || savingIntent}
            onclick={handleSaveIntent}
          >
            {savingIntent ? "Saving…" : "Save"}
          </Button>
        </div>
        <p class="intent-status">
          Current: <strong>{selected.platformIntent && selected.platformIntent.length > 0 ? selected.platformIntent : "—"}</strong>
        </p>
      </div>
    {/if}
  </section>

  <section class="panel" aria-labelledby="log-shortcuts-title">
    <header class="panel-head">
      <h2 id="log-shortcuts-title" class="panel-title">Log shortcuts</h2>
      <p class="panel-hint">
        Platform-aware paths. macOS uses <code>~/Library/Logs/Unity</code>, Windows uses
        <code>%LOCALAPPDATA%\Unity\Editor</code>. Player logs live under the project's
        <code>Logs/</code> folder. Crash logs use the OS diagnostic folder.
      </p>
    </header>

    {#if !selected}
      <p class="panel-empty">Select a project above to view its log locations.</p>
    {:else if logPathsError}
      <p class="field-error" role="alert">{logPathsError}</p>
    {:else if !logPaths}
      <p class="panel-empty">Resolving log paths…</p>
    {:else}
      <div class="log-grid">
        <div class="log-row">
          <span class="log-label">Editor logs</span>
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
          <span class="log-label">Player logs</span>
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
        <div class="log-row">
          <span class="log-label">Crash logs</span>
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
        <div class="log-row">
          <span class="log-label">Editor.log</span>
          <div class="log-actions">
            {#if logPaths.editorLogFile}
              <button
                type="button"
                class="link-btn"
                onclick={handleEditorLogFile}
                onauxclick={handleEditorLogFileAuxClick}
                title={`Open ${logPaths.editorLogFile} (middle-click supported)`}
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

  <section class="panel" aria-labelledby="utilities-title">
    <header class="panel-head">
      <h2 id="utilities-title" class="panel-title">Utilities</h2>
      <p class="panel-hint">
        One-click actions scoped to the selected project. Kill Unity targets the
        <code>lastLaunchPid</code> recorded in <code>projects.json</code>; it does not affect other
        Unity instances.
      </p>
    </header>

    {#if !selected}
      <p class="panel-empty">Select a project above to use utilities.</p>
    {:else}
      <div class="utilities-row">
        <Button
          variant="destructive"
          disabled={killDisabled}
          title={killTitle}
          onclick={handleKillUnity}
        >
          {killButtonLabel}
        </Button>
        <Button
          variant="secondary"
          onclick={handleOpenProjectFolder}
          title="Open the project folder in the system file manager"
        >
          Open project folder
        </Button>
        <Button
          variant="secondary"
          onclick={handleCopyPath}
          title="Copy the project path to the clipboard"
        >
          Copy project path
        </Button>
      </div>
      <p class="field-hint utilities-hint">
        {#if selected.lastLaunchPid}
          Last recorded launch: pid <code>{selected.lastLaunchPid}</code>
          {#if selected.lastLaunchAt}at {new Date(selected.lastLaunchAt).toLocaleString()}{/if}
        {:else}
          No Unity PID recorded for this project yet. Launch Unity once to enable Kill.
        {/if}
      </p>
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

  .context-bar {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    padding: 0.55rem 0.8rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1e1f26;
    flex-wrap: wrap;
  }

  .context-label {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #8b8d9a;
    font-weight: 600;
  }

  .context-select {
    flex: 0 1 18rem;
    padding: 0.4rem 0.55rem;
    border-radius: 6px;
    border: 1px solid #3f4150;
    background: #1a1b21;
    color: #e9e9ef;
    font-size: 0.85rem;
    outline: none;
  }

  .context-select:focus-visible {
    border-color: #5c7cfa;
  }

  .context-select:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .context-path {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #8b8d9a;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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

  .inline-error-text {
    flex: 1;
  }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: #f0a8b8;
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover {
    color: #fff;
  }

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

  .panel-hint code,
  .field-hint code,
  .saved-value {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
    color: #d7d8e0;
  }

  .saved-value {
    display: inline-block;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    vertical-align: bottom;
  }

  .panel-empty {
    margin: 0;
    font-size: 0.82rem;
    color: #6f7280;
  }

  .args-row {
    display: flex;
    flex-direction: row;
    gap: 0.5rem;
    align-items: flex-start;
  }

  .args-input {
    flex: 1;
    min-height: 3.2rem;
    padding: 0.5rem 0.65rem;
    border-radius: 6px;
    border: 1px solid #3f4150;
    background: #14151a;
    color: #e9e9ef;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.82rem;
    line-height: 1.4;
    resize: vertical;
    outline: none;
  }

  .args-input::placeholder {
    color: #555;
  }

  .args-input:focus-visible {
    border-color: #5c7cfa;
  }

  .args-input[aria-invalid="true"] {
    border-color: #b94867;
  }

  .args-actions {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    flex-shrink: 0;
  }

  .field-error {
    margin: 0;
    font-size: 0.78rem;
    color: #f0a8b8;
  }

  .field-hint {
    margin: 0;
    font-size: 0.78rem;
    color: #8b8d9a;
  }

  .intent-row {
    display: flex;
    flex-direction: row;
    align-items: flex-end;
    gap: 0.65rem;
    flex-wrap: wrap;
  }

  .intent-control {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    flex: 0 1 16rem;
  }

  .intent-label {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #8b8d9a;
    font-weight: 600;
  }

  .intent-select {
    padding: 0.4rem 0.55rem;
    border-radius: 6px;
    border: 1px solid #3f4150;
    background: #14151a;
    color: #e9e9ef;
    font-size: 0.85rem;
    outline: none;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .intent-select:focus-visible {
    border-color: #5c7cfa;
  }

  .intent-actions {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .intent-status {
    margin: 0 0 0.3rem;
    font-size: 0.78rem;
    color: #8b8d9a;
  }

  .intent-status strong {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    color: #c5c7d0;
    font-weight: 500;
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

  .utilities-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    flex-wrap: wrap;
  }

  .utilities-hint {
    margin-top: 0.2rem;
  }
</style>
