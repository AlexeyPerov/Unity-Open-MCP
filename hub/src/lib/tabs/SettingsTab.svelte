<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { revealItemInDir } from "@tauri-apps/plugin-opener";
  import {
    getDiagnosticsPaths,
    exportDiagnostics,
    type DiagnosticsPaths,
  } from "$lib/services/config";
  import Button from "$lib/components/shell/Button.svelte";
  import { APP_NAME, APP_VERSION } from "$lib/version";

  let addingFolder = $state(false);
  let lastError = $state<string | null>(null);
  let savedFlash = $state(false);

  let diagnosticsPaths = $state<DiagnosticsPaths | null>(null);
  let diagnosticsError = $state<string | null>(null);
  let exporting = $state(false);
  let lastExportPath = $state<string | null>(null);

  const LOG_TAIL_LIMIT = 200;

  onMount(() => {
    let cancelled = false;
    (async () => {
      try {
        if (!settingsStore.isLoaded()) {
          await settingsStore.load();
        }
      } catch (e) {
        if (cancelled) return;
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`settings load failed: ${msg}`);
      }
      try {
        diagnosticsPaths = await getDiagnosticsPaths();
      } catch (e) {
        if (cancelled) return;
        const msg = e instanceof Error ? e.message : String(e);
        diagnosticsError = `config dir lookup failed: ${msg}`;
        S.appendErrorLog(diagnosticsError);
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  function flashSaved() {
    savedFlash = true;
    setTimeout(() => {
      savedFlash = false;
    }, 1400);
  }

  async function withErrorBoundary(label: string, fn: () => Promise<void>) {
    try {
      await fn();
      flashSaved();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      lastError = `${label}: ${msg}`;
      S.appendErrorLog(`${label} failed: ${msg}`);
    }
  }

  async function setLaunchMode(value: "openProject" | "openEditor") {
    lastError = null;
    await withErrorBoundary("save launch mode", () =>
      settingsStore.setLaunchMode(value)
    );
  }

  async function setRememberLastSelection(value: boolean) {
    lastError = null;
    await withErrorBoundary("save remember-last-selection", () =>
      settingsStore.setRememberLastSelection(value)
    );
  }

  async function setShowPathColumn(value: boolean) {
    lastError = null;
    await withErrorBoundary("save show-path-column", () =>
      settingsStore.setShowPathColumn(value)
    );
  }

  async function setShowModifiedColumn(value: boolean) {
    lastError = null;
    await withErrorBoundary("save show-modified-column", () =>
      settingsStore.setShowModifiedColumn(value)
    );
  }

  async function setSearchIncludesPath(value: boolean) {
    lastError = null;
    await withErrorBoundary("save search-includes-path", () =>
      settingsStore.setSearchIncludesPath(value)
    );
  }

  async function setConfirmKillUnity(value: boolean) {
    lastError = null;
    await withErrorBoundary("save confirm-kill", () =>
      settingsStore.setConfirmKillUnity(value)
    );
  }

  async function setConfirmRemoveProject(value: boolean) {
    lastError = null;
    await withErrorBoundary("save confirm-remove", () =>
      settingsStore.setConfirmRemoveProject(value)
    );
  }

  async function handleAddFolder() {
    if (addingFolder) return;
    addingFolder = true;
    lastError = null;
    try {
      const picked = await openDialog({
        directory: true,
        multiple: false,
        title: "Select Unity Editor parent folder",
      });
      if (!picked || typeof picked !== "string") {
        return;
      }
      // Some platforms can return a trailing separator; trim so equality
      // checks against existing entries work and the stored path is clean.
      const normalized = trimTrailingSeparators(picked);
      await withErrorBoundary("add discovery folder", () =>
        settingsStore.addDiscoveryFolder(normalized)
      );
      S.appendDrawerLog(
        `added discovery folder: ${normalized} (rescanning Unity installs…)`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      lastError = `folder picker failed: ${msg}`;
      S.appendErrorLog(lastError);
    } finally {
      addingFolder = false;
    }
  }

  async function handleRemoveFolder(index: number) {
    lastError = null;
    const folder = settingsStore.current?.unityDiscovery.parentFolders[index];
    await withErrorBoundary("remove discovery folder", () =>
      settingsStore.removeDiscoveryFolder(index)
    );
    if (folder) {
      S.appendDrawerLog(
        `removed discovery folder: ${folder} (rescanning Unity installs…)`
      );
    }
  }

  function dismissError() {
    lastError = null;
  }

  function trimTrailingSeparators(path: string): string {
    let end = path.length;
    while (end > 1 && (path[end - 1] === "/" || path[end - 1] === "\\")) {
      end--;
    }
    return path.slice(0, end);
  }

  async function handleReveal(label: string, filePath: string | undefined) {
    if (!filePath) return;
    try {
      await revealItemInDir(filePath);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      lastError = `reveal ${label} failed: ${msg}`;
      S.appendErrorLog(lastError);
    }
  }

  function buildLogTail(): string {
    const logs = S.drawerLogs;
    if (logs.length === 0) return "";
    const tail = logs.length > LOG_TAIL_LIMIT ? logs.slice(-LOG_TAIL_LIMIT) : logs;
    const header =
      `# Status / Log drawer tail (last ${tail.length} of ${logs.length} lines)\n` +
      `# Exported from ${APP_NAME} v${APP_VERSION}\n` +
      `# Captured at ${new Date().toISOString()}\n\n`;
    return header + tail.map((line) => line + "\n").join("");
  }

  function buildDefaultExportName(): string {
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, "0");
    const stamp =
      `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}` +
      `_${pad(now.getHours())}-${pad(now.getMinutes())}-${pad(now.getSeconds())}`;
    return `unity-agent-hub-diagnostics-${stamp}`;
  }

  async function handleExportBundle() {
    if (exporting) return;
    exporting = true;
    lastError = null;
    try {
      const defaultName = buildDefaultExportName();
      const parent = await openDialog({
        title: "Choose where to create the diagnostics bundle folder",
        directory: true,
        multiple: false,
      });
      if (!parent || typeof parent !== "string") {
        return;
      }
      const trimmedParent = trimTrailingSeparators(parent);
      const target = `${trimmedParent}/${defaultName}`;
      const logTail = buildLogTail();
      const result = await exportDiagnostics(target, logTail.length > 0 ? logTail : null);
      lastExportPath = result.path;
      S.appendDrawerLog(
        `exported diagnostics bundle to ${result.path} ` +
          `(settings: ${result.settingsCopied ? "yes" : "no"}, ` +
          `projects: ${result.projectsCopied ? "yes" : "no"}, ` +
          `log: ${result.logIncluded ? "yes" : "no"})`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      lastError = `export failed: ${msg}`;
      S.appendErrorLog(lastError);
    } finally {
      exporting = false;
    }
  }

  let settings = $derived(settingsStore.current);

  const launchModeOptions: {
    id: "openProject" | "openEditor";
    label: string;
    description: string;
  }[] = [
    {
      id: "openProject",
      label: "Open project scene on launch",
      description: "Default. Hub launches Unity with -projectPath <path>.",
    },
    {
      id: "openEditor",
      label: "Open empty editor only",
      description: "Hub launches Unity without -projectPath.",
    },
  ];
</script>

<div class="settings">
  <div class="body" role="region" aria-label="Settings">
    {#if !settings}
      <div class="loading">
        <p>Loading settings…</p>
      </div>
    {:else}
      <section class="group" aria-labelledby="group-launch">
        <header class="group-header">
          <h3 id="group-launch" class="group-title">Launch</h3>
          <p class="group-hint">Default behavior when launching Unity from the Hub.</p>
        </header>
        <div class="group-body">
          <div
            class="radio-group"
            role="radiogroup"
            aria-labelledby="group-launch"
          >
            {#each launchModeOptions as opt (opt.id)}
              <label class="radio-row">
                <input
                  type="radio"
                  name="launchMode"
                  value={opt.id}
                  checked={settings.launch.mode === opt.id}
                  onchange={() => setLaunchMode(opt.id)}
                />
                <span class="radio-label">{opt.label}</span>
                <span class="radio-desc">{opt.description}</span>
              </label>
            {/each}
          </div>

          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.launch.rememberLastSelection}
              onchange={(e) =>
                setRememberLastSelection((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Remember last selected project on startup</span>
          </label>
        </div>
      </section>

      <section class="group" aria-labelledby="group-project-list">
        <header class="group-header">
          <h3 id="group-project-list" class="group-title">Project list</h3>
          <p class="group-hint">Columns and search scope in the Projects tab.</p>
        </header>
        <div class="group-body">
          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.projectList.showPathColumn}
              onchange={(e) =>
                setShowPathColumn((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Show path column</span>
          </label>
          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.projectList.showModifiedColumn}
              onchange={(e) =>
                setShowModifiedColumn((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Show modified column</span>
          </label>
          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.projectList.searchIncludesPath}
              onchange={(e) =>
                setSearchIncludesPath((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Search path in addition to name</span>
          </label>
        </div>
      </section>

      <section class="group" aria-labelledby="group-safety">
        <header class="group-header">
          <h3 id="group-safety" class="group-title">Safety</h3>
          <p class="group-hint">Confirm destructive actions before they run.</p>
        </header>
        <div class="group-body">
          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.safety.confirmKillUnity}
              onchange={(e) =>
                setConfirmKillUnity((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Confirm before Kill Unity</span>
          </label>
          <label class="check-row">
            <input
              type="checkbox"
              checked={settings.safety.confirmRemoveProject}
              onchange={(e) =>
                setConfirmRemoveProject((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-label">Confirm before removing project from list</span>
          </label>
        </div>
      </section>

      <section class="group" aria-labelledby="group-discovery">
        <header class="group-header">
          <h3 id="group-discovery" class="group-title">Unity discovery</h3>
          <p class="group-hint">
            Parent folders scanned for Unity Editor installs. Changes trigger a
            background rescan of the Unity Versions tab.
          </p>
        </header>
        <div class="group-body">
          <ul
            class="folder-list"
            aria-label="Discovery parent folders"
          >
            {#each settings.unityDiscovery.parentFolders as folder, i (folder + ":" + i)}
              <li class="folder-item">
                <span class="folder-path" title={folder}>{folder}</span>
                <button
                  type="button"
                  class="folder-remove"
                  onclick={() => handleRemoveFolder(i)}
                  aria-label={`Remove discovery folder ${folder}`}
                  title={`Remove ${folder}`}
                >
                  Remove
                </button>
              </li>
            {:else}
              <li class="folder-empty">No custom discovery folders. Only OS default Hub paths and $UNITY_HUB will be scanned.</li>
            {/each}
          </ul>
          <div class="folder-actions">
            <Button
              variant="secondary"
              onclick={handleAddFolder}
              disabled={addingFolder}
            >
              {addingFolder ? "Adding…" : "Add Folder"}
            </Button>
          </div>
        </div>
      </section>

      <section class="group" aria-labelledby="group-diagnostics">
        <header class="group-header">
          <h3 id="group-diagnostics" class="group-title">Diagnostics</h3>
          <p class="group-hint">
            Reveal Hub config files in your file manager, or export a support
            bundle (settings + projects + log tail + version info) for sharing
            with support.
          </p>
        </header>
        <div class="group-body">
          {#if diagnosticsError}
            <p class="placeholder-note placeholder-error" role="alert">
              {diagnosticsError}
            </p>
          {:else if !diagnosticsPaths}
            <p class="placeholder-note">Loading config paths…</p>
          {:else}
            <div class="diag-row">
              <span class="diag-label">Config dir</span>
              <code class="diag-path" title={diagnosticsPaths.configDir}>
                {diagnosticsPaths.configDir}
              </code>
              <Button
                variant="secondary"
                onclick={() => handleReveal("config dir", diagnosticsPaths!.configDir)}
              >
                Reveal
              </Button>
            </div>
            <div class="diag-row">
              <span class="diag-label">settings.json</span>
              <code class="diag-path" title={diagnosticsPaths.settingsFile}>
                {diagnosticsPaths.settingsFile}
              </code>
              <Button
                variant="secondary"
                onclick={() => handleReveal("settings.json", diagnosticsPaths!.settingsFile)}
              >
                Reveal
              </Button>
            </div>
            <div class="diag-row">
              <span class="diag-label">projects.json</span>
              <code class="diag-path" title={diagnosticsPaths.projectsFile}>
                {diagnosticsPaths.projectsFile}
              </code>
              <Button
                variant="secondary"
                onclick={() => handleReveal("projects.json", diagnosticsPaths!.projectsFile)}
              >
                Reveal
              </Button>
            </div>
          {/if}
          <div class="diag-actions">
            <Button
              variant="primary"
              onclick={handleExportBundle}
              disabled={exporting}
            >
              {exporting ? "Exporting…" : "Export diagnostics bundle…"}
            </Button>
            {#if lastExportPath}
              <span class="diag-last-export" title={lastExportPath}>
                Last export: {lastExportPath}
              </span>
            {/if}
          </div>
        </div>
      </section>
    {/if}
  </div>

  {#if lastError}
    <div class="inline-error" role="alert">
      <span class="inline-error-text">{lastError}</span>
      <button
        type="button"
        class="inline-error-dismiss"
        onclick={dismissError}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  {/if}

  <footer class="footer">
    <div class="footer-status" aria-live="polite">
      {#if settingsStore.saving}
        Saving…
      {:else if savedFlash}
        Saved ✓
      {:else if settingsStore.saveError}
        <span class="footer-status-error">Save failed</span>
      {:else}
        Changes save automatically
      {/if}
    </div>
    <div class="footer-version">{APP_NAME} v{APP_VERSION} · build</div>
  </footer>
</div>

<style>
  .settings {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    gap: 0.6rem;
  }

  .body {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    overflow-y: auto;
    padding-right: 0.25rem;
  }

  .loading {
    padding: 1.5rem 0;
    text-align: center;
    color: #8b8d9a;
    font-size: 0.88rem;
  }

  .group {
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1a1b21;
    overflow: hidden;
  }

  .group-header {
    padding: 0.6rem 0.85rem 0.4rem;
    border-bottom: 1px solid #24252c;
    background: #1e1f26;
  }

  .group-title {
    margin: 0;
    font-size: 0.78rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #c5c7d0;
    font-weight: 600;
  }

  .group-hint {
    margin: 0.2rem 0 0;
    font-size: 0.74rem;
    color: #6f7280;
    line-height: 1.4;
  }

  .group-body {
    padding: 0.55rem 0.85rem 0.7rem;
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
  }

  .radio-group {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .radio-row,
  .check-row {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 0.55rem;
    font-size: 0.84rem;
    color: #d7d8e0;
    cursor: pointer;
    line-height: 1.4;
  }

  .radio-row {
    padding: 0.25rem 0.1rem;
  }

  .radio-row input,
  .check-row input {
    margin-top: 0.18rem;
    accent-color: #5c7cfa;
    flex-shrink: 0;
  }

  .radio-label,
  .check-label {
    font-weight: 500;
  }

  .radio-desc {
    color: #6f7280;
    font-size: 0.76rem;
    margin-left: 0.25rem;
  }

  .folder-list {
    list-style: none;
    margin: 0;
    padding: 0;
    border: 1px solid #34353f;
    border-radius: 6px;
    background: #14151a;
    max-height: 14rem;
    overflow-y: auto;
  }

  .folder-item {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.65rem;
    border-bottom: 1px solid #24252c;
  }

  .folder-item:last-child {
    border-bottom: none;
  }

  .folder-path {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #c5c7d0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .folder-remove {
    flex-shrink: 0;
    background: transparent;
    border: 1px solid #3f4150;
    border-radius: 4px;
    padding: 0.2rem 0.55rem;
    color: #a1a3b0;
    font-size: 0.74rem;
    cursor: pointer;
    line-height: 1.3;
  }

  .folder-remove:hover {
    border-color: #7a2a3a;
    color: #f0a8b8;
  }

  .folder-empty {
    padding: 0.6rem 0.7rem;
    color: #6f7280;
    font-size: 0.78rem;
    text-align: center;
  }

  .folder-actions {
    display: flex;
    flex-direction: row;
    gap: 0.4rem;
  }

  .placeholder-note {
    margin: 0;
    color: #6f7280;
    font-size: 0.82rem;
  }

  .placeholder-error {
    color: #f0a8b8;
  }

  .diag-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.55rem;
    padding: 0.25rem 0;
  }

  .diag-label {
    flex-shrink: 0;
    width: 6.5rem;
    font-size: 0.78rem;
    color: #c5c7d0;
    font-weight: 500;
  }

  .diag-path {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #9ea1ad;
    background: #14151a;
    border: 1px solid #2a2b33;
    border-radius: 4px;
    padding: 0.25rem 0.45rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .diag-actions {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    margin-top: 0.25rem;
  }

  .diag-last-export {
    font-size: 0.72rem;
    color: #6f7280;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 100%;
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

  .footer {
    flex-shrink: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 0.5rem 0.85rem;
    border-top: 1px solid #34353f;
    background: #1a1b21;
    border-radius: 6px;
  }

  .footer-status {
    font-size: 0.76rem;
    color: #8b8d9a;
  }

  .footer-status-error {
    color: #f0a8b8;
  }

  .footer-version {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #6f7280;
    user-select: none;
  }
</style>
