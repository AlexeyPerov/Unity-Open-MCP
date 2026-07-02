<script lang="ts">
  import type { ProjectEntry } from "$lib/services/config";
  import {
    runProjectBuild,
    runProjectTest,
    runProjectCustom,
    runProjectNpmVersion,
    runProjectNpmPublishDryRun,
    runProjectNpmPublish,
    runProjectSyncVersion,
    readMcpPackageInfo,
    queryNpmRegistry,
    stopProjectCommand,
    type CommandPanel,
    type McpPackageInfo,
    type NpmRegistryInfo,
    type SyncVersionLine,
    type SyncVersionAction,
  } from "$lib/services/config";
  import { commandLogsStore, emptyProjectPanels, type ProjectPanels } from "$lib/state/command_logs.svelte";
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";
  import Console from "$lib/components/project-settings/Console.svelte";
  import LineCounterPanel from "$lib/components/project-settings/LineCounterPanel.svelte";

  let {
    project,
    onMutated,
  }: {
    project: ProjectEntry;
    onMutated: (updated: ProjectEntry) => void;
  } = $props();

  // The store lazily creates the panels object on first access. Svelte 5
  // forbids mutating state inside a `$derived` (it throws
  // `state_unsafe_mutation`), so we split the lifecycle: an `$effect`
  // seeds the panels object for this project once on mount, and the
  // `$derived` below only reads it. Without this split the modal opened
  // with a blank body for Open-MCP projects because the derived crashed
  // before the component could render.
  $effect(() => {
    commandLogsStore.forProject(project.id);
  });
  let panels = $derived<ProjectPanels>(
    commandLogsStore.projects[project.id] ?? emptyProjectPanels(),
  );

  let customArgs = $state("run lint");

  // The popup uses a two-tab strip: "Commands" holds all the npm panels
  // (build / test / version / publish / sync / custom), and "Line counter"
  // isolates the inspection affordance. The project-summary header above
  // the strip is always visible.
  type Tab = "commands" | "lineCounter";
  let activeTab = $state<Tab>("commands");

  // --- Maintainer panel state -------------------------------------------

  // Read-only package identity + registry snapshot. `packageInfo` is the
  // local `package.json`; `registry` is the best-effort public-registry
  // query. Both load on mount and can be refreshed.
  let packageInfo = $state<McpPackageInfo | null>(null);
  let packageInfoError = $state<string | null>(null);
  let registry = $state<NpmRegistryInfo | null>(null);
  let registryLoading = $state(false);

  // Version-bump level selector for the Version panel.
  let versionLevel = $state<"patch" | "minor" | "major">("patch");

  // --- Repo version sync (scripts/sync-version.mjs) state ---------------

  // The repo-wide sync panel drives a different tool than the npm "Version
  // bump" above: it rewrites every generated version target (trio: 5 files
  // from `version.json`; hub: 3 files from `hub/version.json`) and powers
  // the CI drift gate. State mirrors the script's grammar: a line + an
  // action, with an operand that depends on the action.
  let syncLine = $state<SyncVersionLine>("trio");
  let syncAction = $state<SyncVersionAction>("sync");
  let syncBumpLevel = $state<"patch" | "minor" | "major">("patch");
  let syncSetVersion = $state("");

  // True when the current set-version input parses as plain X.Y.Z (a
  // leading `v` is tolerated, matching the script). Gates the Run button
  // so the user gets immediate feedback before spawning node.
  let syncSetValid = $derived(/^[vV]?\d+\.\d+\.\d+$/.test(syncSetVersion.trim()));
  let syncInputValid = $derived(syncAction !== "set" || syncSetValid);
  // The exact argv the backend will spawn, for the console title and the
  // Run button tooltip. Mirrors command_runner::run_project_sync_version.
  let syncCommandPreview = $derived(buildSyncCommandPreview());

  function buildSyncCommandPreview(): string {
    const hub = syncLine === "hub" ? " --hub" : "";
    const base = "node scripts/sync-version.mjs";
    switch (syncAction) {
      case "check":
        return `${base} --check${hub}`;
      case "bump":
        return `${base} bump ${syncBumpLevel}${hub}`;
      case "set": {
        const v = syncSetVersion.trim().replace(/^[vV]/, "");
        return `${base} set ${v || "<X.Y.Z>"}${hub}`;
      }
      case "sync":
      default:
        return `${base}${hub}`;
    }
  }

  // Publish confirmation. Real publish is mutating and irreversible, so
  // the panel gates it behind an explicit confirmation modal. Dry-run is
  // safe and needs no confirmation.
  let showPublishConfirm = $state(false);

  // `true` once the build panel reports a successful build (exit 0) OR
  // the maintainer has run the dry-run in this session. Surfaced as a
  // soft hint in the publish dialog; never a hard gate.
  let distBuiltHint = $state(false);

  function badgeClass(running: boolean, exitCode: number | null): string {
    if (running) return "badge-running";
    if (exitCode === null) return "badge-idle";
    return exitCode === 0 ? "badge-ok" : "badge-fail";
  }
  function badgeLabel(running: boolean, exitCode: number | null): string {
    if (running) return "running";
    if (exitCode === null) return "idle";
    return exitCode === 0 ? "passed" : `failed (${exitCode})`;
  }

  async function refreshPackageInfo() {
    try {
      packageInfo = await readMcpPackageInfo(project.path, project.kind ?? "openMcp");
      packageInfoError = null;
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      packageInfoError = msg;
      packageInfo = null;
    }
  }

  async function refreshRegistry() {
    registryLoading = true;
    try {
      registry = await queryNpmRegistry(project.path, project.kind ?? "openMcp");
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      registry = { viewError: msg, whoamiError: msg };
    } finally {
      registryLoading = false;
    }
  }

  async function start(panel: CommandPanel) {
    commandLogsStore.markRunning(project.id, panel);
    commandLogsStore.clear(project.id, panel);
    try {
      const kind = project.kind ?? "openMcp";
      if (panel === "build") await runProjectBuild(project.id, project.path, kind);
      else if (panel === "test") await runProjectTest(project.id, project.path, kind);
      else if (panel === "version") {
        await runProjectNpmVersion(project.id, project.path, kind, versionLevel);
      } else if (panel === "publishDryRun") {
        await runProjectNpmPublishDryRun(project.id, project.path, kind);
      } else if (panel === "publish") {
        await runProjectNpmPublish(project.id, project.path, kind);
      } else if (panel === "sync") {
        // Repo-wide version sync. Only bump/set carry an operand.
        const bumpLevel = syncAction === "bump" ? syncBumpLevel : undefined;
        const setVersion =
          syncAction === "set" ? syncSetVersion.trim().replace(/^[vV]/, "") : undefined;
        await runProjectSyncVersion(
          project.id,
          project.path,
          kind,
          syncLine,
          syncAction,
          bumpLevel,
          setVersion,
        );
      } else {
        const args = customArgs.trim().split(/\s+/).filter((a) => a.length > 0);
        await runProjectCustom(project.id, project.path, kind, args);
      }
      S.appendDrawerLog(`started ${panel} for ${project.name}`);
    } catch (e) {
      commandLogsStore.markExited(project.id, panel, 1);
      S.appendErrorLog(`${panel} failed to start: ${e}`);
    }
  }

  async function stop(panel: CommandPanel) {
    try {
      await stopProjectCommand(project.id, panel);
      commandLogsStore.markExited(project.id, panel, null);
      S.appendDrawerLog(`stopped ${panel} for ${project.name}`);
    } catch (e) {
      S.appendErrorLog(`stop ${panel} failed: ${e}`);
    }
  }

  // After a successful build, set the "dist built" hint so the publish
  // dialog can surface it. Watches the build panel's exit code.
  $effect(() => {
    if (panels.build.lastExitCode === 0) {
      distBuiltHint = true;
    }
  });

  // After a successful version bump or publish, refresh the local package
  // info so the info header reflects the new version.
  $effect(() => {
    if (panels.version.lastExitCode === 0 || panels.publish.lastExitCode === 0) {
      void refreshPackageInfo();
    }
  });

  // Load the info header + registry snapshot on first mount.
  $effect(() => {
    if (packageInfo === null && packageInfoError === null) {
      void refreshPackageInfo();
    }
  });

  let canPublish = $derived(
    packageInfo !== null && packageInfo.version.length > 0 && !panels.publish.running,
  );

  function openPublishConfirm() {
    if (!canPublish) return;
    showPublishConfirm = true;
  }

  async function confirmPublish() {
    showPublishConfirm = false;
    await start("publish");
  }

  function versionComparisonLine(): string {
    const local = packageInfo?.version;
    const pub = registry?.publishedVersion;
    if (local && pub) return `${local} (local) → ${pub} (registry)`;
    if (local) return `${local} (local); registry: ${registry?.viewError || "unknown"}`;
    return "—";
  }
</script>

<div class="openmcp-settings">
  <section class="info-block">
    <p class="info-text">
      This folder is tracked as an <strong>Open-MCP repository</strong>. npm commands
      run with cwd <code>{project.path}/mcp-server</code> — the publishable package
      lives there, not at the repo root. Run build / test, bump the version, dry-run
      the publish, then publish. Logs stream live and are capped to 1000 lines.
    </p>
  </section>

  <section class="info-header">
    <div class="info-row">
      <span class="info-label">Package</span>
      <span class="info-value">
        {#if packageInfo}
          <strong>{packageInfo.name || "(unnamed)"}</strong>
          <code>v{packageInfo.version || "0.0.0"}</code>
        {:else if packageInfoError}
          <span class="info-error">{packageInfoError}</span>
        {:else}
          <span class="info-muted">loading…</span>
        {/if}
        <button type="button" class="link-btn" onclick={refreshPackageInfo}>Refresh</button>
      </span>
    </div>
    <div class="info-row">
      <span class="info-label">Registry</span>
      <span class="info-value">
        {#if registryLoading}
          <span class="info-muted">querying…</span>
        {:else if registry}
          {#if registry.publishedVersion}
            published <code>v{registry.publishedVersion}</code>
          {:else if registry.viewError}
            <span class="info-warn">unavailable: {registry.viewError}</span>
          {:else}
            <span class="info-muted">not published</span>
          {/if}
          {#if registry.whoami}
            · logged in as <strong>{registry.whoami}</strong>
          {:else if registry.whoamiError}
            · <span class="info-warn">not logged in</span>
          {/if}
        {:else}
          <span class="info-muted">not queried</span>
        {/if}
        <button type="button" class="link-btn" onclick={refreshRegistry}>Refresh</button>
      </span>
    </div>
    <p class="info-hint">
      The Hub never stores npm credentials — authenticate with <code>npm login</code>
      on this machine. <code>npm whoami</code> confirms publish auth without a token
      round-trip. Compare against the registry before publishing.
    </p>
  </section>

  <nav class="popup-tabs">
    <button class="popup-tab" class:active={activeTab === "commands"} onclick={() => (activeTab = "commands")}>Commands</button>
    <button class="popup-tab" class:active={activeTab === "lineCounter"} onclick={() => (activeTab = "lineCounter")}>Line counter</button>
  </nav>

  {#if activeTab === "commands"}
  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Build</span>
      <span class={`status-badge ${badgeClass(panels.build.running, panels.build.lastExitCode)}`}>
        {badgeLabel(panels.build.running, panels.build.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.build.running}
          <Button variant="secondary" onclick={() => stop("build")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("build")}>Run npm build</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "build")}>Clear</button>
      </div>
    </div>
    <Console lines={panels.build.lines} title="npm run build" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Tests</span>
      <span class={`status-badge ${badgeClass(panels.test.running, panels.test.lastExitCode)}`}>
        {badgeLabel(panels.test.running, panels.test.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.test.running}
          <Button variant="secondary" onclick={() => stop("test")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("test")}>Run npm test</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "test")}>Clear</button>
      </div>
    </div>
    <Console lines={panels.test.lines} title="npm run test" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Version bump</span>
      <span class={`status-badge ${badgeClass(panels.version.running, panels.version.lastExitCode)}`}>
        {badgeLabel(panels.version.running, panels.version.lastExitCode)}
      </span>
      <div class="panel-actions">
        <select class="version-select" bind:value={versionLevel} disabled={panels.version.running}>
          <option value="patch">patch</option>
          <option value="minor">minor</option>
          <option value="major">major</option>
        </select>
        {#if panels.version.running}
          <Button variant="secondary" onclick={() => stop("version")}>Stop</Button>
        {:else}
          <Button
            variant="primary"
            onclick={() => start("version")}
            disabled={!packageInfo}
            title={packageInfo ? `npm version ${versionLevel} --no-git-tag-version` : "Load package info first"}
          >
            Bump {versionLevel}
          </Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "version")}>Clear</button>
      </div>
    </div>
    <p class="panel-hint">
      Updates <code>{packageInfo?.manifestPath ?? "mcp-server/package.json"}</code> only —
      <code>--no-git-tag-version</code> keeps the bump local. The Hub never creates git
      tags; tagging stays in the release runbook (CI publishes on a <code>v*</code> tag).
    </p>
    <Console lines={panels.version.lines} title={`npm version ${versionLevel} --no-git-tag-version`} />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Publish dry-run</span>
      <span class={`status-badge ${badgeClass(panels.publishDryRun.running, panels.publishDryRun.lastExitCode)}`}>
        {badgeLabel(panels.publishDryRun.running, panels.publishDryRun.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.publishDryRun.running}
          <Button variant="secondary" onclick={() => stop("publishDryRun")}>Stop</Button>
        {:else}
          <Button
            variant="primary"
            onclick={() => start("publishDryRun")}
            disabled={!packageInfo}
          >
            Dry-run publish
          </Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "publishDryRun")}>Clear</button>
      </div>
    </div>
    <p class="panel-hint">
      <code>npm publish --dry-run --access public</code> — preflight only, safe without
      confirmation. Lists the files that would ship (<code>dist/</code>, README, LICENSE
      per the <code>files</code> whitelist).
    </p>
    <Console lines={panels.publishDryRun.lines} title="npm publish --dry-run --access public" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Publish</span>
      <span class={`status-badge ${badgeClass(panels.publish.running, panels.publish.lastExitCode)}`}>
        {badgeLabel(panels.publish.running, panels.publish.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.publish.running}
          <Button variant="secondary" onclick={() => stop("publish")}>Stop</Button>
        {:else}
          <Button
            variant="primary"
            onclick={openPublishConfirm}
            disabled={!canPublish}
            title={canPublish ? "Publish to npm (confirmation required)" : "Build + load package info first"}
          >
            Publish
          </Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "publish")}>Clear</button>
      </div>
    </div>
    <Console lines={panels.publish.lines} title="npm publish --access public" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Repo version sync</span>
      <span class={`status-badge ${badgeClass(panels.sync.running, panels.sync.lastExitCode)}`}>
        {badgeLabel(panels.sync.running, panels.sync.lastExitCode)}
      </span>
      <div class="panel-actions">
        <select
          class="version-select"
          bind:value={syncLine}
          disabled={panels.sync.running}
          title="Which version line to target"
        >
          <option value="trio">trio (server+bridge+verify)</option>
          <option value="hub">hub (Unity Hub Pro app)</option>
        </select>
        <select
          class="version-select"
          bind:value={syncAction}
          disabled={panels.sync.running}
          title="Which sync-version action to run"
        >
          <option value="sync">sync</option>
          <option value="check">check (drift gate)</option>
          <option value="bump">bump</option>
          <option value="set">set</option>
        </select>
        {#if syncAction === "bump"}
          <select class="version-select" bind:value={syncBumpLevel} disabled={panels.sync.running}>
            <option value="patch">patch</option>
            <option value="minor">minor</option>
            <option value="major">major</option>
          </select>
        {:else if syncAction === "set"}
          <input
            class="custom-input sync-set-input"
            bind:value={syncSetVersion}
            placeholder="0.2.0"
            spellcheck="false"
            disabled={panels.sync.running}
            size="8"
          />
        {/if}
        {#if panels.sync.running}
          <Button variant="secondary" onclick={() => stop("sync")}>Stop</Button>
        {:else}
          <Button
            variant="primary"
            onclick={() => start("sync")}
            disabled={!syncInputValid}
            title={syncInputValid ? syncCommandPreview : "Enter a valid X.Y.Z version"}
          >
            Run
          </Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "sync")}>Clear</button>
      </div>
    </div>
    <p class="panel-hint">
      Runs <code>scripts/sync-version.mjs</code> at the repo root — the release/drift tool,
      <strong>not</strong> the npm "Version bump" above (which only touches
      <code>mcp-server/package.json</code>). <strong>sync</strong> rewrites every generated
      target from the source file; <strong>check</strong> is the read-only CI drift gate
      (exit 1 = drift); <strong>bump</strong>/<strong>set</strong> change the source then
      sync. Trio source: <code>version.json</code>; Hub source: <code>hub/version.json</code>.
      The Hub never creates git tags — commit and tag manually as the script prints.
    </p>
    <Console lines={panels.sync.lines} title={syncCommandPreview} />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Custom</span>
      <span class={`status-badge ${badgeClass(panels.custom.running, panels.custom.lastExitCode)}`}>
        {badgeLabel(panels.custom.running, panels.custom.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.custom.running}
          <Button variant="secondary" onclick={() => stop("custom")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("custom")}>Run</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "custom")}>Clear</button>
      </div>
    </div>
    <input
      class="custom-input"
      bind:value={customArgs}
      placeholder="run lint  (npm args; empty = npm install)"
      spellcheck="false"
    />
    <Console lines={panels.custom.lines} title="npm (custom)" />
  </div>
  {:else if activeTab === "lineCounter"}
    <LineCounterPanel {project} />
  {/if}
</div>

{#if showPublishConfirm}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="confirm-overlay" role="dialog" tabindex="-1" aria-modal="true" onclick={() => (showPublishConfirm = false)}>
    <div class="confirm-shell" onclick={(e) => e.stopPropagation()}>
      <h3>Publish to npm?</h3>
      <p>
        This runs <code>npm publish --access public</code> from
        <code>{project.path}/mcp-server</code> against the public registry.
        It is mutating and irreversible — once published, a version cannot be
        reused or overwritten.
      </p>
      <dl class="confirm-grid">
        <dt>Package</dt>
        <dd>{packageInfo?.name ?? "—"}</dd>
        <dt>Version</dt>
        <dd>{versionComparisonLine()}</dd>
        <dt>Auth</dt>
        <dd>
          {#if registry?.whoami}
            logged in as <strong>{registry.whoami}</strong>
          {:else}
            <span class="info-warn">not logged in — run <code>npm login</code> first</span>
          {/if}
        </dd>
        <dt>Build</dt>
        <dd>
          {#if distBuiltHint}
            <span class="info-ok">dist built this session</span>
          {:else}
            <span class="info-warn">not built this session — run Build first</span>
          {/if}
        </dd>
      </dl>
      <div class="confirm-actions">
        <Button variant="secondary" onclick={() => (showPublishConfirm = false)}>Cancel</Button>
        <Button variant="primary" onclick={confirmPublish}>
          Publish {packageInfo?.name ?? "unity-open-mcp"}@{packageInfo?.version ?? "…"}
        </Button>
      </div>
    </div>
  </div>
{/if}

<style>
  .openmcp-settings {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .popup-tabs {
    display: flex;
    gap: 0.25rem;
    border-bottom: 1px solid var(--hub-border);
  }
  .popup-tab {
    padding: 0.4rem 0.8rem;
    background: transparent;
    border: none;
    border-bottom: 2px solid transparent;
    color: var(--hub-text-dim);
    font-size: 0.8rem;
    cursor: pointer;
  }
  .popup-tab.active {
    color: var(--hub-text);
    border-bottom-color: var(--hub-accent, #5c7cfa);
  }
  .info-block {
    padding: 0.6rem 0.8rem;
    border-radius: 0.5rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border);
  }
  .info-text {
    margin: 0;
    font-size: 0.8rem;
    line-height: 1.5;
    color: var(--hub-text-dim);
  }
  .info-header {
    padding: 0.6rem 0.8rem;
    border-radius: 0.5rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border);
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }
  .info-row {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
    font-size: 0.8rem;
  }
  .info-label {
    min-width: 5rem;
    color: var(--hub-text-dim);
    text-transform: uppercase;
    font-size: 0.66rem;
    letter-spacing: 0.06em;
    font-weight: 600;
  }
  .info-value {
    color: var(--hub-text);
    display: flex;
    align-items: center;
    gap: 0.4rem;
    flex-wrap: wrap;
  }
  .info-value code {
    font-size: 0.74rem;
    background: var(--hub-bg);
    padding: 0.05rem 0.3rem;
    border-radius: 3px;
  }
  .info-muted { color: var(--hub-text-dim); }
  .info-warn { color: #fbbf24; }
  .info-ok { color: #4ade80; }
  .info-error { color: var(--hub-error-fg); }
  .info-hint {
    margin: 0.2rem 0 0;
    font-size: 0.72rem;
    color: var(--hub-text-dim);
    line-height: 1.4;
  }
  .info-hint code {
    font-size: 0.7rem;
  }
  .panel-row {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }
  .panel-head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }
  .panel-label {
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--hub-text);
    min-width: 8rem;
  }
  .panel-hint {
    margin: 0;
    font-size: 0.72rem;
    color: var(--hub-text-dim);
    line-height: 1.4;
  }
  .panel-hint code {
    font-size: 0.7rem;
    background: var(--hub-bg);
    padding: 0.05rem 0.3rem;
    border-radius: 3px;
  }
  .status-badge {
    font-size: 0.65rem;
    font-weight: 700;
    text-transform: uppercase;
    padding: 0.1rem 0.4rem;
    border-radius: 3px;
  }
  .badge-idle { background: var(--hub-card); color: var(--hub-text-dim); }
  .badge-running { background: rgba(92, 124, 250, 0.2); color: #5c7cfa; }
  .badge-ok { background: rgba(86, 180, 130, 0.2); color: #56b482; }
  .badge-fail { background: rgba(224, 86, 86, 0.2); color: #e05656; }
  .panel-actions {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin-left: auto;
  }
  .version-select {
    padding: 0.25rem 0.4rem;
    border: 1px solid var(--hub-border);
    border-radius: 0.3rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    font-size: 0.78rem;
  }
  .custom-input {
    padding: 0.3rem 0.4rem;
    border: 1px solid var(--hub-border);
    border-radius: 0.3rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    font-size: 0.78rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }
  .sync-set-input {
    width: 6rem;
  }
  .link-btn {
    background: transparent;
    border: none;
    color: var(--hub-accent, #5c7cfa);
    cursor: pointer;
    font-size: 0.7rem;
  }
  .confirm-overlay {
    position: fixed;
    inset: 0;
    z-index: 300;
    background: rgba(8, 9, 13, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1.5rem;
  }
  .confirm-shell {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border);
    border-radius: 12px;
    max-width: 560px;
    width: 100%;
    padding: 1.2rem 1.4rem;
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
    box-shadow: 0 12px 48px rgba(0, 0, 0, 0.55);
  }
  .confirm-shell h3 {
    margin: 0;
    font-size: 1rem;
    color: var(--hub-text-bright);
  }
  .confirm-shell p {
    margin: 0;
    font-size: 0.82rem;
    line-height: 1.5;
    color: var(--hub-text-muted);
  }
  .confirm-shell code {
    font-size: 0.74rem;
    background: var(--hub-card);
    padding: 0 0.25rem;
    border-radius: 3px;
  }
  .confirm-grid {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 0.25rem 0.85rem;
    margin: 0;
    font-size: 0.8rem;
  }
  .confirm-grid dt {
    color: var(--hub-text-dim);
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .confirm-grid dd {
    margin: 0;
    color: var(--hub-text);
  }
  .confirm-actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
  }
</style>
