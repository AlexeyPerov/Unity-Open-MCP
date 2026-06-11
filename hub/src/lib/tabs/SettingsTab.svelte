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
    type ProjectListSortBy,
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

  // "idle" before the user clicks, "copied" briefly after a successful
  // write, "error" if the navigator rejects the write. The Button
  // re-uses the same disabled state for the "Copied ✓" flash.
  let cliCopyState = $state<"idle" | "copied" | "error">("idle");

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

  async function setShowGitBranchColumn(value: boolean) {
    lastError = null;
    await withErrorBoundary("save show-git-branch-column", () =>
      settingsStore.setShowGitBranchColumn(value)
    );
  }

  async function setProjectListSortBy(value: ProjectListSortBy) {
    lastError = null;
    await withErrorBoundary("save project-list-sort-by", () =>
      settingsStore.setProjectListSortBy(value)
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

  async function setAutoOpenDrawerOnLaunchFailure(value: boolean) {
    lastError = null;
    await withErrorBoundary("save auto-open-drawer-on-launch-failure", () =>
      settingsStore.setAutoOpenDrawerOnLaunchFailure(value)
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

  const CLI_HELP_TEXT = [
    "unity-agent-hub - launch a Unity project from the terminal.",
    "",
    "USAGE:",
    "  unity-agent-hub -projectPath <path>",
    "",
    "OPTIONS:",
    "  -projectPath <path>   Open the Unity project at <path> with the matching",
    "                        installed Unity version and exit. The Hub window does",
    "                        not open. -projectPath=<path> and --projectPath are",
    "                        also accepted.",
    "  -h, --help            Show this help text and exit.",
    "  -V, --version         Show the version and exit.",
    "",
    "EXIT CODES:",
    "  0   Unity was spawned successfully.",
    "  1   The path is missing, not a directory, not a Unity project root,",
    "      the project's Unity version is not installed, or the spawn failed.",
    "      A one-line 'unity-agent-hub: <reason>' message is written to stderr.",
    "",
    "EXAMPLES:",
    "  unity-agent-hub -projectPath ~/Projects/MyUnityGame",
    "  unity-agent-hub -projectPath \"C:\\Users\\me\\Unity\\MyProject\"",
  ].join("\n");

  async function handleCopyCliHelp() {
    // The webview exposes `navigator.clipboard.writeText` only on
    // secure contexts; Tauri webviews are secure, but the API still
    // rejects on permission denial (e.g. when the host blocks the
    // permission prompt). We surface both shapes via the inline state
    // so the user can fall back to selecting the visible copy in the
    // diagnostics panel manually.
    try {
      if (typeof navigator === "undefined" || !navigator.clipboard) {
        throw new Error("clipboard API is not available in this context");
      }
      await navigator.clipboard.writeText(CLI_HELP_TEXT);
      cliCopyState = "copied";
      S.appendDrawerLog("copied CLI help to clipboard");
      setTimeout(() => {
        cliCopyState = "idle";
      }, 1600);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      cliCopyState = "error";
      S.appendErrorLog(`copy CLI help failed: ${msg}`);
      setTimeout(() => {
        cliCopyState = "idle";
      }, 2200);
    }
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

  type SettingsGroupId =
    | "launch"
    | "projectList"
    | "safety"
    | "discovery"
    | "diagnostics";

  let openGroups = $state<Record<SettingsGroupId, boolean>>({
    launch: true,
    projectList: true,
    safety: true,
    discovery: true,
    diagnostics: true,
  });

  function toggleGroup(id: SettingsGroupId) {
    openGroups = { ...openGroups, [id]: !openGroups[id] };
  }

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

  const sortByOptions: {
    id: ProjectListSortBy;
    label: string;
    description: string;
  }[] = [
    {
      id: "frecency",
      label: "Frecency (default)",
      description:
        "Sort by how often each project was launched, with a 14-day half-life decay. Ties break on last-modified time. Frecency is preserved regardless of this choice.",
    },
    {
      id: "lastModified",
      label: "Last modified",
      description: "Pure last-modified-time sort, descending. Frecency is preserved in the background.",
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
        <button
          type="button"
          class="group-header"
          aria-expanded={openGroups.launch}
          aria-controls="group-launch-body"
          onclick={() => toggleGroup("launch")}
        >
          <span class="group-chevron" class:group-chevron-open={openGroups.launch} aria-hidden="true">▸</span>
          <span class="group-header-text">
            <h3 id="group-launch" class="group-title">Launch</h3>
            <p class="group-hint">Default behavior when launching Unity from the Hub.</p>
          </span>
        </button>
        {#if openGroups.launch}
          <div id="group-launch-body" class="group-body">
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
                  <span class="widget-text">
                    <span class="radio-label">{opt.label}</span>
                    <span class="radio-desc">{opt.description}</span>
                  </span>
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
        {/if}
      </section>

      <section class="group" aria-labelledby="group-project-list">
        <button
          type="button"
          class="group-header"
          aria-expanded={openGroups.projectList}
          aria-controls="group-project-list-body"
          onclick={() => toggleGroup("projectList")}
        >
          <span class="group-chevron" class:group-chevron-open={openGroups.projectList} aria-hidden="true">▸</span>
          <span class="group-header-text">
            <h3 id="group-project-list" class="group-title">Project list</h3>
            <p class="group-hint">Columns and search scope in the Projects tab.</p>
          </span>
        </button>
        {#if openGroups.projectList}
          <div id="group-project-list-body" class="group-body">
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
                checked={settings.projectList.showGitBranchColumn}
                onchange={(e) =>
                  setShowGitBranchColumn((e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="widget-text">
                <span class="check-label">Show git branch column</span>
                <span class="check-desc">
                  Adds a Branch column to the Projects tab. Resolved from
                  <code>.git/HEAD</code> on Refresh (and on first display).
                  Non-git projects render an empty cell.
                </span>
              </span>
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
            <div
              class="radio-group"
              role="radiogroup"
              aria-labelledby="group-project-list"
            >
              <span class="radio-group-label">Default sort</span>
              {#each sortByOptions as opt (opt.id)}
                <label class="radio-row">
                  <input
                    type="radio"
                    name="projectListSortBy"
                    value={opt.id}
                    checked={settings.projectList.sortBy === opt.id}
                    onchange={() => setProjectListSortBy(opt.id)}
                  />
                  <span class="widget-text">
                    <span class="radio-label">{opt.label}</span>
                    <span class="radio-desc">{opt.description}</span>
                  </span>
                </label>
              {/each}
            </div>
          </div>
        {/if}
      </section>

      <section class="group" aria-labelledby="group-safety">
        <button
          type="button"
          class="group-header"
          aria-expanded={openGroups.safety}
          aria-controls="group-safety-body"
          onclick={() => toggleGroup("safety")}
        >
          <span class="group-chevron" class:group-chevron-open={openGroups.safety} aria-hidden="true">▸</span>
          <span class="group-header-text">
            <h3 id="group-safety" class="group-title">Safety</h3>
            <p class="group-hint">Confirm destructive actions before they run.</p>
          </span>
        </button>
        {#if openGroups.safety}
          <div id="group-safety-body" class="group-body">
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
        {/if}
      </section>

      <section class="group" aria-labelledby="group-discovery">
        <button
          type="button"
          class="group-header"
          aria-expanded={openGroups.discovery}
          aria-controls="group-discovery-body"
          onclick={() => toggleGroup("discovery")}
        >
          <span class="group-chevron" class:group-chevron-open={openGroups.discovery} aria-hidden="true">▸</span>
          <span class="group-header-text">
            <h3 id="group-discovery" class="group-title">Additional parent folders</h3>
            <p class="group-hint">
              Extra folders Hub scans for Unity Editor installs on top of the
              OS-default Hub paths (<code>/Applications/Unity/Hub/Editor</code> on
              macOS, <code>%ProgramFiles%\Unity\Hub\Editor</code> on Windows) and
              the <code>$UNITY_HUB</code> environment variable. Those built-in
              sources are always scanned regardless of this list. Adding or
              removing a folder here triggers a background rescan of the Unity
              Versions tab.
            </p>
          </span>
        </button>
        {#if openGroups.discovery}
          <div id="group-discovery-body" class="group-body">
            <ul
              class="folder-list"
              aria-label="Additional parent folders"
            >
              {#each settings.unityDiscovery.parentFolders as folder, i (folder + ":" + i)}
                <li class="folder-item">
                  <span class="folder-path" title={folder}>{folder}</span>
                  <button
                    type="button"
                    class="folder-remove"
                    onclick={() => handleRemoveFolder(i)}
                    aria-label={`Remove additional folder ${folder}`}
                    title={`Remove ${folder}`}
                  >
                    Remove
                  </button>
                </li>
              {:else}
                <li class="folder-empty">
                  No additional folders. Hub will still scan the OS-default Hub
                  paths and <code>$UNITY_HUB</code> if set.
                </li>
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
        {/if}
      </section>

      <section class="group" aria-labelledby="group-diagnostics">
        <button
          type="button"
          class="group-header"
          aria-expanded={openGroups.diagnostics}
          aria-controls="group-diagnostics-body"
          onclick={() => toggleGroup("diagnostics")}
        >
          <span class="group-chevron" class:group-chevron-open={openGroups.diagnostics} aria-hidden="true">▸</span>
          <span class="group-header-text">
            <h3 id="group-diagnostics" class="group-title">Diagnostics</h3>
            <p class="group-hint">
              Reveal Hub config files in your file manager, or export a support
              bundle (settings + projects + log tail + version info) for sharing
              with support.
            </p>
          </span>
        </button>
        {#if openGroups.diagnostics}
          <div id="group-diagnostics-body" class="group-body">
            <label class="check-row">
              <input
                type="checkbox"
                checked={settings.diagnostics.autoOpenDrawerOnLaunchFailure}
                onchange={(e) =>
                  setAutoOpenDrawerOnLaunchFailure(
                    (e.currentTarget as HTMLInputElement).checked
                  )}
              />
              <span class="widget-text">
                <span class="check-label">Auto-open status drawer on launch failure</span>
                <span class="check-desc">
                  When a Unity launch fails, expand the Status / Log drawer and
                  tail the last 200 lines from the per-launch log
                  (<code>~/.config/unity-agent-hub/logs/launches.log</code>).
                  When off, failures are still logged but the drawer stays as
                  you left it.
                </span>
              </span>
            </label>
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
            <div class="cli-help">
              <span class="cli-help-label">CLI mode</span>
              <code class="cli-help-cmd" title="unity-agent-hub -projectPath &lt;path&gt;"
                >unity-agent-hub -projectPath &lt;path&gt;</code
              >
              <Button
                variant="secondary"
                aria-label="Copy CLI help to clipboard"
                title="Copy the CLI help block to the clipboard"
                onclick={handleCopyCliHelp}
                disabled={cliCopyState === "copied" ? true : false}
              >
                {cliCopyState === "copied"
                  ? "Copied ✓"
                  : cliCopyState === "error"
                    ? "Copy failed"
                    : "Copy CLI help"}
              </Button>
            </div>
            <p class="cli-help-desc">
              Run from a terminal to launch the project at <code>&lt;path&gt;</code> with the
              matching installed Unity version. The Hub window does not open; the process
              records <code>lastLaunchPid</code> / <code>lastLaunchAt</code> in
              <code>projects.json</code> and exits. Invalid paths print a one-line error
              to stderr and return a non-zero exit code. The same flag is exposed via
              <code>tauri-plugin-cli</code> for programmatic access.
            </p>
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
        {/if}
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
    min-width: 0;
    gap: 0.6rem;
    width: 100%;
    max-width: 56rem;
    align-self: center;
  }

  .body {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    overflow-y: auto;
    padding: 0.25rem 0.5rem 0.25rem 0.25rem;
    min-height: 0;
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
    overflow: visible;
  }

  .group-header {
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 0.55rem;
    padding: 0.6rem 0.85rem;
    border: none;
    border-bottom: 1px solid #24252c;
    background: #1e1f26;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  .group-header:hover {
    background: #23242d;
  }

  .group-header:focus-visible {
    outline: 2px solid #5c7cfa;
    outline-offset: -2px;
  }

  .group-chevron {
    display: inline-block;
    flex-shrink: 0;
    width: 0.9rem;
    margin-top: 0.15rem;
    color: #8b8d9a;
    font-size: 0.7rem;
    transition: transform 0.15s ease;
  }

  .group-chevron-open {
    transform: rotate(90deg);
  }

  .group-header-text {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    min-width: 0;
    flex: 1;
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
    margin: 0;
    font-size: 0.78rem;
    color: #8b8d9a;
    line-height: 1.5;
  }

  .group-body {
    padding: 0.7rem 0.95rem 0.85rem;
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }

  .radio-group {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .radio-group-label {
    font-size: 0.78rem;
    color: #8b8d9a;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    font-weight: 600;
    margin-bottom: 0.1rem;
  }

  .radio-row,
  .check-row {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 0.6rem;
    font-size: 0.88rem;
    color: #d7d8e0;
    cursor: pointer;
    line-height: 1.4;
  }

  .radio-row {
    padding: 0.3rem 0.1rem;
  }

  .radio-row input,
  .check-row input {
    margin-top: 0.2rem;
    accent-color: #5c7cfa;
    flex-shrink: 0;
  }

  .widget-text {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    min-width: 0;
    flex: 1;
  }

  .radio-label,
  .check-label {
    font-weight: 500;
  }

  .radio-desc {
    color: #8b8d9a;
    font-size: 0.78rem;
    line-height: 1.45;
  }

  .check-desc {
    display: block;
    color: #8b8d9a;
    font-size: 0.78rem;
    line-height: 1.45;
    margin-top: 0.15rem;
  }

  .check-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
    color: #d7d8e0;
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
    flex-direction: column;
    align-items: stretch;
    gap: 0.35rem;
    padding: 0.3rem 0;
    border-bottom: 1px dashed #24252c;
  }

  .diag-row:last-of-type {
    border-bottom: none;
  }

  .diag-label {
    font-size: 0.82rem;
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

  .cli-help {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.55rem;
    padding: 0.5rem 0.65rem;
    border: 1px solid #2a2b33;
    border-radius: 6px;
    background: #14151a;
    flex-wrap: wrap;
  }

  .cli-help-label {
    font-size: 0.78rem;
    color: #c5c7d0;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    flex-shrink: 0;
  }

  .cli-help-cmd {
    flex: 1;
    min-width: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #c5c7d0;
    background: #1a1b21;
    border: 1px solid #2a2b33;
    border-radius: 4px;
    padding: 0.25rem 0.5rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cli-help-desc {
    margin: 0;
    color: #8b8d9a;
    font-size: 0.78rem;
    line-height: 1.5;
  }

  .cli-help-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
    color: #d7d8e0;
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
