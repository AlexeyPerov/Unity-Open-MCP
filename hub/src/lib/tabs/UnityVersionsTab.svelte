<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { discoveryStore, type VersionHealth } from "$lib/state/discovery.svelte";
  import {
    runUnityInstall,
    type RunUnityError,
    type UnityInstallation,
  } from "$lib/services/config";
  import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import VirtualList from "$lib/components/VirtualList.svelte";

  const ROW_HEIGHT = 38;

  let search = $state("");
  let selectedVersion = $state<string | null>(null);
  let refreshing = $state(false);
  let running = $state<string | null>(null);
  let actionError = $state<string | null>(null);

  onMount(() => {
    let cancelled = false;
    (async () => {
      if (!projectsStore.settings || projectsStore.projects.length === 0) {
        await projectsStore.load();
      }
      if (cancelled) return;
      await discoveryStore.load();
    })();
    return () => {
      cancelled = true;
    };
  });

  function releaseNotesUrl(version: string): string {
    const dashed = version.replace(/\./g, "-");
    return `https://unity.com/releases/editor/whats-new/${dashed}`;
  }

  function formatInstallDate(iso: string | undefined): string {
    if (!iso) return "—";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleDateString(undefined, { year: "numeric", month: "short" });
  }

  let buckets = $derived(discoveryStore.bucketCounts(projectsStore.projects));
  let missingBuckets = $derived(discoveryStore.missingVersionBuckets(projectsStore.projects));
  let missingTotal = $derived(missingBuckets.reduce((sum, b) => sum + b.count, 0));

  let filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    return discoveryStore.installations.filter((inst) => {
      if (!q) return true;
      if (inst.version.toLowerCase().includes(q)) return true;
      if (inst.path.toLowerCase().includes(q)) return true;
      return false;
    });
  });

  let selected = $derived(
    selectedVersion
      ? discoveryStore.installations.find((i) => i.version === selectedVersion) ?? null
      : null
  );

  function selectRow(version: string) {
    selectedVersion = version;
    actionError = null;
  }

  function healthFor(inst: UnityInstallation): VersionHealth {
    return discoveryStore.healthFor(inst.version, projectsStore.projects);
  }

  function chipForHealth(health: VersionHealth): { tone: "ok" | "warn" | "missing"; label: string; title: string } | null {
    switch (health) {
      case "ok":
        return { tone: "ok", label: "ok", title: "Installed and used by projects" };
      case "warn":
        return { tone: "warn", label: "unused", title: "Installed but no projects use it" };
      case "missing":
        return { tone: "missing", label: "missing", title: "Not discovered" };
    }
  }

  async function handleRefresh() {
    if (refreshing) return;
    refreshing = true;
    try {
      await discoveryStore.refresh();
      S.appendDrawerLog(
        `refreshed Unity discovery (${discoveryStore.installations.length} installs${discoveryStore.errors.length ? `, ${discoveryStore.errors.length} error(s)` : ""})`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `refresh failed: ${msg}`;
      S.appendDrawerLog(actionError);
    } finally {
      refreshing = false;
    }
  }

  function handleShowMissingProjects() {
    S.requestProjectsFilter("missingVersion");
    S.appendDrawerLog("switched to Projects tab with missing-version filter");
  }

  function formatRunError(err: RunUnityError): string {
    switch (err.type) {
      case "versionMissing":
        return "no Unity version selected";
      case "installNotFound":
        return `Unity ${err.version} is not installed`;
      case "executableMissing":
        return `Unity ${err.version} executable missing at ${err.installPath}`;
      case "launchFailed":
        return `failed to launch Unity ${err.version}: ${err.message}`;
      default:
        return `unknown error: ${JSON.stringify(err)}`;
    }
  }

  async function handleOpenFolder() {
    if (!selected) return;
    actionError = null;
    try {
      await openPath(selected.path);
      S.appendDrawerLog(`opened install folder: ${selected.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open folder failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  async function handleReveal() {
    if (!selected) return;
    actionError = null;
    try {
      await revealItemInDir(selected.path);
      S.appendDrawerLog(`revealed install folder: ${selected.path}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `reveal failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  async function handleReleaseNotes() {
    if (!selected) return;
    actionError = null;
    try {
      await openUrl(releaseNotesUrl(selected.version));
      S.appendDrawerLog(`opened release notes: ${releaseNotesUrl(selected.version)}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      actionError = `open release notes failed: ${msg}`;
      S.appendDrawerLog(actionError);
    }
  }

  async function handleRunUnity() {
    if (!selected || running) return;
    running = selected.version;
    actionError = null;
    try {
      const result = await runUnityInstall(selected.version);
      S.appendDrawerLog(
        `launched Unity ${result.version} (pid ${result.pid}) from ${result.executablePath}`
      );
    } catch (e) {
      const err = e as RunUnityError;
      const message = formatRunError(err);
      actionError = message;
      S.appendDrawerLog(message);
    } finally {
      running = null;
    }
  }

  function handleRowKeydown(e: KeyboardEvent, version: string) {
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        moveSelection(1);
        break;
      case "ArrowUp":
        e.preventDefault();
        moveSelection(-1);
        break;
      case "Home":
        e.preventDefault();
        if (filtered.length > 0) selectedVersion = filtered[0].version;
        break;
      case "End":
        e.preventDefault();
        if (filtered.length > 0) selectedVersion = filtered[filtered.length - 1].version;
        break;
      case "Enter":
        e.preventDefault();
        if (selectedVersion) handleRunUnity();
        break;
    }
  }

  function moveSelection(delta: number) {
    if (filtered.length === 0) return;
    const idx = selectedVersion
      ? filtered.findIndex((i) => i.version === selectedVersion)
      : -1;
    let next = idx + delta;
    if (idx === -1) next = delta > 0 ? 0 : filtered.length - 1;
    if (next < 0) next = 0;
    if (next >= filtered.length) next = filtered.length - 1;
    selectedVersion = filtered[next].version;
  }
</script>

<div class="versions">
  <div class="toolbar">
    <input
      type="search"
      class="search"
      placeholder="Search versions…"
      bind:value={search}
      aria-label="Search Unity versions"
    />
    <div class="toolbar-spacer"></div>
    <Button variant="secondary" onclick={handleRefresh} disabled={refreshing}>
      {refreshing ? "Refreshing…" : "Refresh"}
    </Button>
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

  {#if missingBuckets.length > 0}
    <div class="warnings" role="alert">
      <span class="warnings-icon" aria-hidden="true">⚠</span>
      <div class="warnings-body">
        <div class="warnings-text">
          {missingTotal} project{missingTotal === 1 ? "" : "s"} reference
          {missingBuckets.length === 1 ? "a" : missingBuckets.length}
          Unity version{missingBuckets.length === 1 ? "" : "s"} that {missingBuckets.length === 1 ? "is" : "are"} not installed:
          <span class="warnings-versions">
            {missingBuckets
              .map((b) => `${b.version} (${b.count})`)
              .join(", ")}
          </span>
        </div>
        <button
          type="button"
          class="warnings-link"
          onclick={handleShowMissingProjects}
        >
          Show projects →
        </button>
      </div>
    </div>
  {/if}

  <div class="table" role="grid" aria-rowcount={filtered.length + 1} aria-colcount={5}>
    <div class="table-head" role="row">
      <div class="th" role="columnheader">Version</div>
      <div class="th" role="columnheader">Source</div>
      <div class="th" role="columnheader">Path</div>
      <div class="th th-num" role="columnheader">Projects</div>
      <div class="th" role="columnheader">Installed</div>
    </div>

    <div class="table-body">
      <VirtualList items={filtered} itemHeight={ROW_HEIGHT}>
        {#snippet children(inst: UnityInstallation, index: number)}
          {@const health = healthFor(inst)}
          {@const chip = chipForHealth(health)}
          {@const projectCount = buckets.get(inst.version) ?? 0}
          <div
            class="row"
            class:row-selected={selectedVersion === inst.version}
            role="row"
            aria-rowindex={index + 2}
            aria-selected={selectedVersion === inst.version}
            tabindex={selectedVersion === inst.version ? 0 : -1}
            onclick={() => selectRow(inst.version)}
            onkeydown={(e) => handleRowKeydown(e, inst.version)}
            ondblclick={handleRunUnity}
          >
            <div class="cell cell-version" role="gridcell">
              <div class="version-line">
                <span class="dot dot-{health}" aria-hidden="true"></span>
                <span class="version-text">{inst.version}</span>
                {#if chip}
                  <StatusChip tone={chip.tone} label={chip.label} title={chip.title} />
                {/if}
              </div>
            </div>
            <div class="cell" role="gridcell">
              <span class="source-text">{inst.source || "Manual"}</span>
            </div>
            <div class="cell cell-path" role="gridcell" title={inst.path}>
              <span class="path-text">{inst.path}</span>
            </div>
            <div class="cell th-num" role="gridcell">
              <span class="count-text">{projectCount}</span>
            </div>
            <div class="cell" role="gridcell">
              <span class="muted">{formatInstallDate(inst.installDate)}</span>
            </div>
          </div>
        {/snippet}
        {#snippet empty()}
          <div class="empty-state">
            {#if discoveryStore.installations.length === 0}
              <p>No Unity installations discovered yet.</p>
              <p class="empty-hint">
                Add a parent folder in <strong>Settings → Unity discovery</strong> or set the
                <code>UNITY_HUB</code> environment variable, then click <strong>Refresh</strong>.
              </p>
            {:else}
              <p>No installations match the current search.</p>
            {/if}
          </div>
        {/snippet}
      </VirtualList>
    </div>
  </div>

  <div class="action-bar" role="region" aria-label="Selected installation actions">
    <div class="action-summary">
      {#if selected}
        <span class="action-name">{selected.version}</span>
        <span class="action-sep">·</span>
        <span class="action-path" title={selected.path}>{selected.path}</span>
      {:else}
        <span class="action-hint">Select a Unity installation to enable actions.</span>
      {/if}
    </div>
    <div class="action-buttons">
      <Button
        variant="secondary"
        disabled={!selected}
        title={selected ? "Open install folder" : "Select an installation first"}
        onclick={handleOpenFolder}
      >
        Open Install Folder
      </Button>
      <Button
        variant="secondary"
        disabled={!selected}
        title={selected ? "Reveal in file manager" : "Select an installation first"}
        onclick={handleReveal}
      >
        Reveal
      </Button>
      <Button
        variant="secondary"
        disabled={!selected}
        title={selected ? "Open release notes in browser" : "Select an installation first"}
        onclick={handleReleaseNotes}
      >
        Release Notes ↗
      </Button>
      <Button
        variant="primary"
        disabled={!selected || running === selected?.version}
        title={selected ? `Run Unity ${selected.version}` : "Select an installation first"}
        onclick={handleRunUnity}
      >
        {running && selected && running === selected.version ? "Running…" : "Run Unity"}
      </Button>
    </div>
  </div>
</div>

<style>
  .versions {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    gap: 0.6rem;
  }

  .toolbar {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .toolbar-spacer {
    flex: 1;
  }

  .search {
    flex: 0 1 18rem;
    padding: 0.45rem 0.65rem;
    border-radius: 6px;
    border: 1px solid #3f4150;
    background: #1e1f26;
    color: #e9e9ef;
    font-size: 0.85rem;
    outline: none;
  }

  .search::placeholder {
    color: #6f7280;
  }

  .search:focus-visible {
    border-color: #5c7cfa;
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

  .warnings {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 0.55rem;
    padding: 0.55rem 0.75rem;
    border: 1px solid #6b4e16;
    border-radius: 6px;
    background: #2a210f;
    color: #f0c97a;
    font-size: 0.82rem;
  }

  .warnings-icon {
    flex-shrink: 0;
    font-size: 0.95rem;
    line-height: 1.2;
  }

  .warnings-body {
    flex: 1;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
  }

  .warnings-text {
    flex: 1;
    min-width: 12rem;
  }

  .warnings-versions {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #f0c97a;
  }

  .warnings-link {
    background: transparent;
    border: 1px solid #6b4e16;
    border-radius: 4px;
    padding: 0.25rem 0.55rem;
    color: #f0c97a;
    font-size: 0.78rem;
    cursor: pointer;
    line-height: 1.3;
  }

  .warnings-link:hover {
    border-color: #f0c97a;
    color: #fff;
  }

  .table {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 8rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1a1b21;
    overflow: hidden;
  }

  .table-head {
    display: grid;
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4.5rem, 0.7fr) minmax(12rem, 2.4fr) minmax(4rem, 0.6fr) minmax(6rem, 0.7fr);
    flex-shrink: 0;
    background: #1e1f26;
    border-bottom: 1px solid #34353f;
    padding: 0 0.25rem;
  }

  .th {
    padding: 0.55rem 0.7rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #8b8d9a;
    font-weight: 600;
    user-select: none;
  }

  .th-num {
    text-align: right;
  }

  .table-body {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }

  .row {
    display: grid;
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4.5rem, 0.7fr) minmax(12rem, 2.4fr) minmax(4rem, 0.6fr) minmax(6rem, 0.7fr);
    align-items: center;
    border-bottom: 1px solid #24252c;
    padding: 0 0.25rem;
    cursor: pointer;
    transition: background 0.08s ease;
    outline: none;
  }

  .row:hover {
    background: #1e1f26;
  }

  .row:focus-visible {
    background: #1e1f26;
    box-shadow: inset 2px 0 0 #5c7cfa;
  }

  .row-selected {
    background: #242a3a !important;
  }

  .row-selected:focus-visible {
    box-shadow: inset 2px 0 0 #5c7cfa;
  }

  .cell {
    padding: 0 0.7rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cell-version .version-line {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
  }

  .version-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #e9e9ef;
  }

  .dot {
    display: inline-block;
    width: 0.55rem;
    height: 0.55rem;
    border-radius: 50%;
    flex-shrink: 0;
  }

  .dot-ok {
    background: #6fcf97;
    box-shadow: 0 0 0 2px rgba(111, 207, 151, 0.18);
  }

  .dot-warn {
    background: #e0bf5a;
    box-shadow: 0 0 0 2px rgba(224, 191, 90, 0.18);
  }

  .dot-missing {
    background: #f0a8c2;
    box-shadow: 0 0 0 2px rgba(240, 168, 194, 0.18);
  }

  .source-text {
    font-size: 0.78rem;
    color: #c5c7d0;
  }

  .cell-path .path-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: #8b8d9a;
  }

  .count-text {
    font-variant-numeric: tabular-nums;
    font-size: 0.82rem;
    color: #c5c7d0;
    display: inline-block;
    width: 100%;
    text-align: right;
  }

  .muted {
    color: #6f7280;
  }

  .empty-state {
    text-align: center;
    color: #8b8d9a;
  }

  .empty-state p {
    margin: 0.2rem 0;
    font-size: 0.88rem;
  }

  .empty-state .empty-hint {
    font-size: 0.78rem;
    color: #6f7280;
  }

  .empty-state code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: #2a2b33;
    padding: 0 0.3rem;
    border-radius: 3px;
  }

  .action-bar {
    flex-shrink: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.6rem 0.85rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1e1f26;
  }

  .action-summary {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.45rem;
    min-width: 0;
    flex: 1;
    overflow: hidden;
  }

  .action-name {
    font-weight: 600;
    color: #f2f3f7;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.82rem;
  }

  .action-sep {
    color: #555;
  }

  .action-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: #8b8d9a;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .action-hint {
    color: #6f7280;
    font-size: 0.82rem;
  }

  .action-buttons {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    flex-shrink: 0;
  }
</style>
