<script lang="ts">
  import { onMount } from "svelte";
  import { listen, type UnlistenFn } from "@tauri-apps/api/event";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { discoveryStore, type VersionHealth } from "$lib/state/discovery.svelte";
  import {
    fetchReleases,
    refreshReleases,
    runUnityInstall,
    installUnityVersion,
    type ReleaseEntry,
    type ReleasesResult,
    type RunUnityError,
    type UnityInstallation,
    type InstallError,
  } from "$lib/services/config";
  import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import VirtualList from "$lib/components/VirtualList.svelte";

  const ROW_HEIGHT = 38;

  type ViewMode = "installed" | "all";

  let search = $state("");
  let selectedVersion = $state<string | null>(null);
  let refreshing = $state(false);
  let running = $state<string | null>(null);
  let actionError = $state<string | null>(null);

  let viewMode = $state<ViewMode>("installed");
  let releases = $state<ReleasesResult | null>(null);
  let releasesError = $state<string | null>(null);
  let releasesLoading = $state(false);
  let releasesSearch = $state("");
  let releasesContext = $state<{ x: number; y: number; entry: ReleaseEntry } | null>(null);
  let includeArchived = $state(false);
  let installingVersion = $state<string | null>(null);
  let installError = $state<string | null>(null);

  let unlistenInstallLog: UnlistenFn | null = null;
  let unlistenInstallComplete: UnlistenFn | null = null;

  onMount(() => {
    let cancelled = false;
    (async () => {
      if (!projectsStore.settings || projectsStore.projects.length === 0) {
        await projectsStore.load();
      }
      if (cancelled) return;
      await discoveryStore.load();
      if (cancelled) return;
      try {
        const result = await fetchReleases(includeArchived);
        if (cancelled) return;
        releases = result;
      } catch (e) {
        if (cancelled) return;
        const msg = e instanceof Error ? e.message : String(e);
        releasesError = `could not load releases: ${msg}`;
      }
    })();
    window.addEventListener("click", closeReleasesContext, true);

    (async () => {
      unlistenInstallLog = await listen<string>("install-log", (event) => {
        S.appendDrawerLog(`[install] ${event.payload}`);
      });
      unlistenInstallComplete = await listen<string>("install-complete", async (event) => {
        S.appendDrawerLog(`install complete: ${event.payload}`);
        installingVersion = null;
        installError = null;
        await discoveryStore.refresh();
        await loadReleases();
      });
    })();

    return () => {
      cancelled = true;
      window.removeEventListener("click", closeReleasesContext, true);
      unlistenInstallLog?.();
      unlistenInstallComplete?.();
    };
  });

  async function loadReleases() {
    releasesLoading = true;
    releasesError = null;
    try {
      releases = await fetchReleases(includeArchived);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      releasesError = `could not load releases: ${msg}`;
    } finally {
      releasesLoading = false;
    }
  }

  async function refreshReleasesAction() {
    releasesLoading = true;
    releasesError = null;
    try {
      releases = await refreshReleases(includeArchived);
      S.appendDrawerLog(`refreshed Unity releases (cache: ${releases?.cachePath ?? "—"})`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      releasesError = `refresh failed: ${msg}`;
    } finally {
      releasesLoading = false;
    }
  }

  function setViewMode(mode: ViewMode) {
    viewMode = mode;
    if (mode === "all" && !releases && !releasesLoading) {
      void loadReleases();
    }
  }

  async function toggleArchived() {
    includeArchived = !includeArchived;
    await loadReleases();
  }

  function installedVersionSet(): Set<string> {
    return new Set(discoveryStore.installations.map((i) => i.version));
  }

  function isInstalled(version: string): boolean {
    return installedVersionSet().has(version);
  }

  let filteredReleases = $derived.by(() => {
    if (!releases) return [];
    const q = releasesSearch.trim().toLowerCase();
    if (!q) return releases.entries;
    return releases.entries.filter((e) => {
      if (e.version.toLowerCase().includes(q)) return true;
      if (e.stream.toLowerCase().includes(q)) return true;
      if (e.releaseDate && e.releaseDate.includes(q)) return true;
      return false;
    });
  });

  function streamLabel(stream: ReleaseEntry["stream"]): string {
    switch (stream) {
      case "lts":
        return "LTS";
      case "tech":
        return "TECH";
      case "beta":
        return "BETA";
      case "alpha":
        return "ALPHA";
    }
  }

  function streamTone(stream: ReleaseEntry["stream"]): "ok" | "warn" | "missing" {
    switch (stream) {
      case "lts":
        return "ok";
      case "tech":
        return "warn";
      case "beta":
      case "alpha":
        return "missing";
    }
  }

  async function openReleaseNotes(url: string) {
    try {
      await openUrl(url);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`open release notes failed: ${msg}`);
    }
  }

  async function copyVersion(version: string) {
    try {
      await navigator.clipboard.writeText(version);
      S.appendDrawerLog(`copied version: ${version}`);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`copy version failed: ${msg}`);
    }
  }

  function openReleasesContextMenu(e: MouseEvent, entry: ReleaseEntry) {
    e.preventDefault();
    releasesContext = { x: e.clientX, y: e.clientY, entry };
  }

  function closeReleasesContext() {
    releasesContext = null;
  }

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
      S.appendErrorLog(actionError);
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

  function formatInstallError(err: InstallError): string {
    switch (err.type) {
      case "hubNotFound":
        return "Unity Hub is not installed. Install Unity Hub or add the editor manually.";
      case "installInProgress":
        return "Another install is already in progress.";
      case "versionEmpty":
        return "No version specified.";
      case "installFailed":
        return err.message;
      default:
        return `unknown error: ${JSON.stringify(err)}`;
    }
  }

  async function handleInstall(entry: ReleaseEntry) {
    if (installingVersion) return;
    installingVersion = entry.version;
    installError = null;
    S.drawerExpanded = true;
    S.appendDrawerLog(`installing Unity ${entry.version}${entry.changeset ? ` (changeset: ${entry.changeset})` : ""}…`);
    try {
      await installUnityVersion(entry.version, entry.changeset);
    } catch (e) {
      const err = e as InstallError;
      const msg = formatInstallError(err);
      installError = msg;
      installingVersion = null;
      S.appendErrorLog(`install failed: ${msg}`);
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
      S.appendErrorLog(actionError);
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
      S.appendErrorLog(actionError);
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
      S.appendErrorLog(actionError);
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
      S.appendErrorLog(message);
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
      class:hidden={viewMode !== "installed"}
    />
    <input
      type="search"
      class="search"
      placeholder="Search releases…"
      bind:value={releasesSearch}
      aria-label="Search Unity releases"
      class:hidden={viewMode !== "all"}
    />
    <div class="view-toggle" role="tablist" aria-label="Unity Versions view">
      <button
        type="button"
        role="tab"
        class="view-toggle-btn"
        class:view-toggle-btn-active={viewMode === "installed"}
        aria-selected={viewMode === "installed"}
        onclick={() => setViewMode("installed")}
      >
        Installed
      </button>
      <button
        type="button"
        role="tab"
        class="view-toggle-btn"
        class:view-toggle-btn-active={viewMode === "all"}
        aria-selected={viewMode === "all"}
        onclick={() => setViewMode("all")}
      >
        All releases
      </button>
    </div>
    <div class="toolbar-spacer"></div>
    <div class:hidden={viewMode !== "installed"}>
      <Button variant="secondary" onclick={handleRefresh} disabled={refreshing}>
        {refreshing ? "Refreshing…" : "Refresh"}
      </Button>
    </div>
    <div class:hidden={viewMode !== "all"}>
      <label class="archived-toggle">
        <input type="checkbox" bind:checked={includeArchived} onchange={toggleArchived} />
        <span class="archived-label">Include archived</span>
      </label>
      <Button variant="secondary" onclick={refreshReleasesAction} disabled={releasesLoading}>
        {releasesLoading ? "Refreshing…" : "Refresh"}
      </Button>
    </div>
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

  <div class:hidden={viewMode !== "installed"}>
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

  <!-- M1.5-19: All releases sub-section. Same shell as the
       installations table, with a "stale" badge + Retry when the
       on-disk cache is older than the TTL. Clicking a row opens
       the release-notes URL in the system browser; right-click
       exposes Copy version / Use as Upgrade target. -->
  <div class:hidden={viewMode !== "all"} style="display: flex; flex-direction: column; gap: 0.6rem; flex: 1; min-height: 0;">
    {#if installError}
      <div class="inline-error" role="alert">
        <span class="inline-error-text">{installError}</span>
        <button
          type="button"
          class="inline-error-dismiss"
          onclick={() => (installError = null)}
          aria-label="Dismiss error"
        >
          ×
        </button>
      </div>
    {/if}

    <div class="releases-meta">
      <span class="releases-meta-text">
        {filteredReleases.length} release{filteredReleases.length === 1 ? "" : "s"}
        {#if releases?.stale}
          <span class="stale-badge" title="Cached data is older than the 1-hour TTL; click Refresh to reload.">stale</span>
        {/if}
      </span>
      {#if releasesError}
        <span class="releases-error" role="alert">{releasesError}</span>
      {/if}
    </div>

    <div class="table" role="grid" aria-rowcount={filteredReleases.length + 1} aria-colcount={5}>
      <div class="table-head" role="row">
        <div class="th" role="columnheader">Version</div>
        <div class="th" role="columnheader">Stream</div>
        <div class="th" role="columnheader">Released</div>
        <div class="th" role="columnheader">Notes</div>
        <div class="th" role="columnheader">Status</div>
      </div>

      <div class="table-body">
        {#if !releases}
          <div class="empty-state">
            <p>Loading releases…</p>
          </div>
        {:else if filteredReleases.length === 0}
          <div class="empty-state">
            <p>No releases match the current search.</p>
          </div>
        {:else}
          {#each filteredReleases as entry (entry.version)}
            <div
              class="row"
              role="row"
              onclick={() => openReleaseNotes(entry.releaseNotesUrl)}
              oncontextmenu={(e) => openReleasesContextMenu(e, entry)}
              onkeydown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  openReleaseNotes(entry.releaseNotesUrl);
                }
              }}
              tabindex={0}
              title="Click to open release notes; right-click for more actions."
            >
              <div class="cell cell-version" role="gridcell">
                <div class="version-line">
                  <span class="version-text">{entry.version}</span>
                </div>
              </div>
              <div class="cell" role="gridcell">
                <StatusChip tone={streamTone(entry.stream)} label={streamLabel(entry.stream)} />
              </div>
              <div class="cell" role="gridcell">
                <span class="muted">{entry.releaseDate ?? "—"}</span>
              </div>
              <div class="cell" role="gridcell" title={entry.releaseNotesUrl}>
                <span class="path-text">{entry.releaseNotesUrl.replace(/^https?:\/\//, "")}</span>
              </div>
              <div class="cell" role="gridcell">
                {#if isInstalled(entry.version)}
                  <StatusChip tone="ok" label="installed" />
                {:else if installingVersion === entry.version}
                  <span class="install-status">Installing…</span>
                {:else}
                  <button
                    type="button"
                    class="install-btn"
                    disabled={!!installingVersion}
                    onclick={(e) => { e.stopPropagation(); handleInstall(entry); }}
                    title={installingVersion ? "Another install is in progress" : `Install Unity ${entry.version} via Unity Hub`}
                  >
                    Install
                  </button>
                {/if}
              </div>
            </div>
          {/each}
        {/if}
      </div>
    </div>

    {#if releasesContext}
      {@const ctxEntry = releasesContext.entry}
      <div
        class="ctx-menu"
        style="left: {releasesContext.x}px; top: {releasesContext.y}px;"
        role="menu"
        onclick={(e) => e.stopPropagation()}
        onkeydown={(e) => {
          if (e.key === "Escape") closeReleasesContext();
        }}
      >
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          onclick={() => {
            void openReleaseNotes(ctxEntry.releaseNotesUrl);
            closeReleasesContext();
          }}
        >
          Open release notes ↗
        </button>
        <button
          type="button"
          class="ctx-item"
          role="menuitem"
          onclick={() => {
            void copyVersion(ctxEntry.version);
            closeReleasesContext();
          }}
        >
          Copy version
        </button>
        {#if !isInstalled(ctxEntry.version)}
          <button
            type="button"
            class="ctx-item ctx-item-install"
            role="menuitem"
            disabled={!!installingVersion}
            title={installingVersion ? "Another install is in progress" : `Install Unity ${ctxEntry.version}`}
            onclick={() => {
              handleInstall(ctxEntry);
              closeReleasesContext();
            }}
          >
            Install
          </button>
        {/if}
        <button
          type="button"
          class="ctx-item ctx-item-upgrade"
          role="menuitem"
          title="Switch to the Projects tab and pre-select a project that could upgrade to this version"
          disabled={projectsStore.projects.length === 0}
          onclick={() => {
            const matching = projectsStore.projects.find(
              (p) => p.unityVersion && p.unityVersion !== ctxEntry.version,
            );
            closeReleasesContext();
            if (matching) {
              S.appendDrawerLog(
                `open ${matching.name} in the Projects tab to upgrade to ${ctxEntry.version}`,
              );
              projectsStore.select(matching.id);
              S.activeTab = "projects";
            } else {
              S.appendDrawerLog(
                `no upgrade target — no project has a Unity version set; open Unity Versions → Installed for the canonical list.`,
              );
            }
          }}
        >
          Use as Upgrade target
        </button>
      </div>
    {/if}
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
    border: 1px solid var(--hub-border-light);
    background: var(--hub-surface);
    color: var(--hub-text);
    font-size: 0.85rem;
    outline: none;
  }

  .search::placeholder {
    color: var(--hub-text-placeholder);
  }

  .search:focus-visible {
    border-color: var(--hub-accent);
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

  .inline-error-text {
    flex: 1;
  }

  .inline-error-dismiss {
    background: transparent;
    border: none;
    color: var(--hub-error-fg);
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0 0.25rem;
  }

  .inline-error-dismiss:hover {
    color: var(--hub-text-bright);
  }

  .warnings {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 0.55rem;
    padding: 0.55rem 0.75rem;
    border: 1px solid var(--hub-warn-fg);
    border-radius: 6px;
    background: var(--hub-warn-bg);
    color: var(--hub-warn-fg);
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
    color: var(--hub-warn-fg);
  }

  .warnings-link {
    background: transparent;
    border: 1px solid var(--hub-warn-fg);
    border-radius: 4px;
    padding: 0.25rem 0.55rem;
    color: var(--hub-warn-fg);
    font-size: 0.78rem;
    cursor: pointer;
    line-height: 1.3;
  }

  .warnings-link:hover {
    border-color: var(--hub-warn-fg);
    color: var(--hub-text-bright);
  }

  .table {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 8rem;
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-bg);
    overflow: hidden;
  }

  .table-head {
    display: grid;
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4.5rem, 0.7fr) minmax(12rem, 2.4fr) minmax(4rem, 0.6fr) minmax(6rem, 0.7fr);
    flex-shrink: 0;
    background: var(--hub-surface);
    border-bottom: 1px solid var(--hub-border);
    padding: 0 0.25rem;
  }

  .th {
    padding: 0.55rem 0.7rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
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
    padding-top: 0.4rem;
  }

  .row {
    display: grid;
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4.5rem, 0.7fr) minmax(12rem, 2.4fr) minmax(4rem, 0.6fr) minmax(6rem, 0.7fr);
    align-items: center;
    border-bottom: 1px solid var(--hub-card);
    padding: 0 0.25rem;
    cursor: pointer;
    transition: background 0.08s ease;
    outline: none;
  }

  .row:hover {
    background: var(--hub-surface);
  }

  .row:focus-visible {
    background: var(--hub-surface);
    box-shadow: inset 2px 0 0 var(--hub-accent);
  }

  .row-selected {
    background: var(--hub-selected) !important;
  }

  .row-selected:focus-visible {
    box-shadow: inset 2px 0 0 var(--hub-accent);
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
    color: var(--hub-text);
  }

  .dot {
    display: inline-block;
    width: 0.55rem;
    height: 0.55rem;
    border-radius: 50%;
    flex-shrink: 0;
  }

  .dot-ok {
    background: var(--hub-success);
    box-shadow: 0 0 0 2px rgba(111, 207, 151, 0.18);
  }

  .dot-warn {
    background: var(--hub-warning);
    box-shadow: 0 0 0 2px rgba(224, 191, 90, 0.18);
  }

  .dot-missing {
    background: var(--hub-error-fg);
    box-shadow: 0 0 0 2px rgba(240, 168, 194, 0.18);
  }

  .source-text {
    font-size: 0.78rem;
    color: var(--hub-text-dim);
  }

  .cell-path .path-text {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
  }

  .count-text {
    font-variant-numeric: tabular-nums;
    font-size: 0.82rem;
    color: var(--hub-text-dim);
    display: inline-block;
    width: 100%;
    text-align: right;
  }

  .muted {
    color: var(--hub-text-placeholder);
  }

  .empty-state {
    text-align: center;
    color: var(--hub-text-muted);
  }

  .empty-state p {
    margin: 0.2rem 0;
    font-size: 0.88rem;
  }

  .empty-state .empty-hint {
    font-size: 0.78rem;
    color: var(--hub-text-placeholder);
  }

  .empty-state code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: var(--hub-bg);
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
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-surface);
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
    color: var(--hub-text-bright);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.82rem;
  }

  .action-sep {
    color: var(--hub-text-disabled);
  }

  .action-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    color: var(--hub-text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .action-hint {
    color: var(--hub-text-placeholder);
    font-size: 0.82rem;
  }

  .action-buttons {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
    flex-shrink: 0;
  }

  /* M1.5-19: view toggle (Installed | All releases). */
  .view-toggle {
    display: inline-flex;
    flex-direction: row;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    overflow: hidden;
    background: var(--hub-surface);
  }

  .view-toggle-btn {
    background: transparent;
    color: var(--hub-text-dim);
    border: none;
    padding: 0.4rem 0.75rem;
    font-size: 0.78rem;
    cursor: pointer;
    line-height: 1.3;
  }

  .view-toggle-btn:hover {
    color: var(--hub-text-bright);
    background: var(--hub-bg);
  }

  .view-toggle-btn-active {
    background: var(--hub-selected);
    color: var(--hub-text-bright);
  }

  .view-toggle-btn-active:hover {
    background: var(--hub-selected);
  }

  .hidden {
    display: none !important;
  }

  /* M1.5-19: All releases meta line + stale badge. */
  .releases-meta {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    font-size: 0.78rem;
    color: var(--hub-text-muted);
  }

  .stale-badge {
    display: inline-block;
    margin-left: 0.4rem;
    padding: 0 0.4rem;
    border-radius: 999px;
    background: var(--hub-warn-bg);
    color: var(--hub-warning);
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    border: 1px solid var(--hub-warn-fg);
  }

  .releases-error {
    color: var(--hub-error-fg);
    font-size: 0.78rem;
  }

  /* M1.5-19: releases row hover affordance (rows are clickable). */
  .row[tabindex="0"] {
    cursor: pointer;
  }

  /* M1.5-19: context menu styling (re-uses the .ctx-* vocabulary the
     Projects tab already uses so the look matches). */
  .ctx-menu {
    position: fixed;
    z-index: 100;
    min-width: 11rem;
    background: var(--hub-surface);
    border: 1px solid var(--hub-border);
    border-radius: 6px;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.4);
    padding: 0.25rem;
    display: flex;
    flex-direction: column;
  }

  .ctx-item {
    text-align: left;
    background: transparent;
    border: none;
    color: var(--hub-text);
    padding: 0.45rem 0.65rem;
    font-size: 0.82rem;
    border-radius: 4px;
    cursor: pointer;
  }

  .ctx-item:hover:not(:disabled) {
    background: var(--hub-bg);
    color: var(--hub-text-bright);
  }

  .ctx-item:disabled {
    color: var(--hub-text-disabled);
    cursor: not-allowed;
  }

  .ctx-item-upgrade {
    color: var(--hub-accent);
  }

  .ctx-item-upgrade:hover:not(:disabled) {
    color: var(--hub-text-bright);
  }

  .install-btn {
    padding: 0.2rem 0.55rem;
    border-radius: 4px;
    border: 1px solid var(--hub-accent);
    background: transparent;
    color: var(--hub-accent);
    font-size: 0.72rem;
    font-weight: 500;
    cursor: pointer;
    line-height: 1.3;
  }

  .install-btn:hover:not(:disabled) {
    background: var(--hub-accent);
    color: var(--hub-bg);
  }

  .install-btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .install-status {
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    font-style: italic;
  }

  .ctx-item-install {
    color: var(--hub-accent);
  }

  .ctx-item-install:hover:not(:disabled) {
    color: var(--hub-text-bright);
  }

  .archived-toggle {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    cursor: pointer;
    font-size: 0.78rem;
    color: var(--hub-text-dim);
    user-select: none;
  }

  .archived-toggle input[type="checkbox"] {
    accent-color: var(--hub-accent);
    width: 0.85rem;
    height: 0.85rem;
    cursor: pointer;
  }

  .archived-label {
    font-size: 0.78rem;
  }
</style>
