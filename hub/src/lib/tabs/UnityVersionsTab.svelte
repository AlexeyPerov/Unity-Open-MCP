<script lang="ts">
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { discoveryStore, type VersionHealth } from "$lib/state/discovery.svelte";
  import {
    fetchReleases,
    refreshReleases,
    runUnityInstall,
    openUnityHubInstall,
    type ReleaseEntry,
    type ReleasesResult,
    type RunUnityError,
    type UnityInstallation,
  } from "$lib/services/config";
  import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import VirtualList from "$lib/components/VirtualList.svelte";

  const ROW_HEIGHT = 38;

  type ViewMode = "installed" | "all";
  /// Filter scope inside the "All releases" sub-section. `lts` shows
  /// only Long-Term-Support releases (the recommended install line);
  /// `all` shows everything Unity publishes, including archived TECH,
  /// BETA, and ALPHA builds.
  type ReleasesScope = "lts" | "all";

  /// Grid template for the All-releases table. Applied inline on both
  /// the header row and every body row (the same pattern ProjectsTab
  /// uses) so header and cells can never drift out of alignment and we
  /// sidestep the stylesheet cascade that fought the shared 7-column
  /// `.table-head` / `.row` rules. Single source of truth.
  ///
  /// Track minimums are sized to fit the widest content without
  /// ellipsizing: Stream fits `SUPPORTED`, Released fits `2026-06-17`,
  /// Version fits `6000.0.77f1`. On narrow widths the `minmax()` tracks
  /// compress toward their minimums and `.cell`/`.th` ellipsize, the
  /// same behavior the Installed and Projects tables exhibit.
  const releasesGridTemplate =
    "minmax(7.5rem, 1.2fr) minmax(6.5rem, 0.8fr) minmax(7rem, 0.9fr) minmax(8rem, 2.5fr) minmax(4.5rem, 0.7fr)";

  let search = $state("");
  let selectedVersion = $state<string | null>(null);
  let refreshing = $state(false);
  let running = $state<string | null>(null);
  let actionError = $state<string | null>(null);

  let viewMode = $state<ViewMode>("installed");
  let releasesScope = $state<ReleasesScope>("lts");
  let releases = $state<ReleasesResult | null>(null);
  let releasesError = $state<string | null>(null);
  let releasesLoading = $state(false);
  let releasesSearch = $state("");
  let releasesContext = $state<{ x: number; y: number; entry: ReleaseEntry } | null>(null);

  onMount(() => {
    let cancelled = false;
    (async () => {
      if (!projectsStore.settings || projectsStore.projects.length === 0) {
        await projectsStore.load();
      }
      if (cancelled) return;
      await discoveryStore.load();
    })();
    window.addEventListener("click", closeReleasesContext, true);

    return () => {
      cancelled = true;
      window.removeEventListener("click", closeReleasesContext, true);
    };
  });

  async function loadReleases() {
    releasesLoading = true;
    releasesError = null;
    try {
      releases = await fetchReleases();
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
      releases = await refreshReleases();
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

  function installedVersionSet(): Set<string> {
    return new Set(discoveryStore.installations.map((i) => i.version));
  }

  function isInstalled(version: string): boolean {
    return installedVersionSet().has(version);
  }

  /// Unity "major" line for grouping. Unity 6 versions (`6000.x`) use the
  /// first two numeric segments as the line — `6000.0`, `6000.3`, … — so
  /// each Unity 6 release train is its own line. Pre-Unity-6 versions
  /// (`2022.3`, `2023.1`, …) use the year as the line. Returns `null`
  /// for versions that do not match the expected shape so they are
  /// excluded from grouping rather than crashing it.
  function majorLine(version: string): string | null {
    const m = /^(\d+)\.(\d+)\./.exec(version);
    if (!m) return null;
    const a = Number(m[1]);
    if (!Number.isFinite(a)) return null;
    return a >= 6000 ? `${m[1]}.${m[2]}` : m[1];
  }

  /// Compare two Unity version strings for "which is newer". Parses the
  /// `major.minor.patch` numeric segments plus the suffix letter+number
  /// (`f1`, `b2`, `a7`). Order: higher major wins; ties break on minor,
  /// then patch, then the suffix letter (`f` final > `b` beta > `a`
  /// alpha), then the suffix number. Returns >0 if `a` is newer, <0 if
  /// `b` is newer, 0 on tie. Falls back to a plain string compare for
  /// versions that do not parse, so they still order deterministically.
  ///
  /// Comparing the full major.minor (not just the patch, as the old
  /// implementation did) is required so versions from different lines
  /// order correctly — e.g. `6000.5.0f1` is newer than `6000.0.77f1`
  /// even though the latter has a higher patch number.
  function compareUnityVersions(a: string, b: string): number {
    const pa = /^(\d+)\.(\d+)\.(\d+)([a-z])(\d+)$/.exec(a);
    const pb = /^(\d+)\.(\d+)\.(\d+)([a-z])(\d+)$/.exec(b);
    if (!pa || !pb) return a < b ? -1 : a > b ? 1 : 0;
    // Major first, then minor, then patch.
    for (let i = 1; i <= 3; i++) {
      const delta = Number(pa[i]) - Number(pb[i]);
      if (delta !== 0) return delta;
    }
    // Suffix letter: f > b > a. Compare ranks so any unknown letter
    // sorts below the known set.
    const rank = (c: string): number =>
      c === "f" ? 3 : c === "b" ? 2 : c === "a" ? 1 : 0;
    const letterDelta = rank(pa[4]) - rank(pb[4]);
    if (letterDelta !== 0) return letterDelta;
    return Number(pa[5]) - Number(pb[5]);
  }

  /// Collapse an LTS list to the newest patch of each major line. Unity
  /// ships multiple LTS lines (`6000.0`, `6000.3`, `2022`, …); for the
  /// "what should I install?" view we surface only the newest patch of
  /// each line — one row per LTS train — sorted newest-first by date.
  /// Older LTS majors that no longer appear in the archive feed (Unity
  /// only lists Unity 6 in the public catalog now) are simply absent.
  function newestPatchPerLtsLine(entries: ReleaseEntry[]): ReleaseEntry[] {
    const newestByLine = new Map<string, ReleaseEntry>();
    for (const e of entries) {
      const line = majorLine(e.version);
      if (!line) continue;
      const cur = newestByLine.get(line);
      if (!cur || compareUnityVersions(e.version, cur.version) > 0) {
        newestByLine.set(line, e);
      }
    }
    return [...newestByLine.values()];
  }

  /// Whether a release counts as "LTS" for the install view. Unity's
  /// SUPPORTED stream is the active, fully-supported stable release line
  /// (the newest engine release that is not yet LTS but is shipping for
  /// production); for the user it is the same install recommendation as
  /// LTS, so the LTS scope treats both as one population.
  function isLtsLike(stream: ReleaseEntry["stream"]): boolean {
    return stream === "lts" || stream === "supported";
  }

  let filteredReleases = $derived.by(() => {
    if (!releases) return [];
    // Scope narrows the stream first: `lts` keeps LTS **and** SUPPORTED
    // releases (both are stable, production-ready install lines) and
    // collapses them to the newest patch of each major line (so the user
    // sees one row per line, e.g. `6000.0.77f1`, `6000.3.18f1`,
    // `6000.4.12f1`, instead of every patch); `all` keeps every release
    // Unity publishes (archived TECH/BETA/ALPHA included). The text query
    // then runs on top of the scoped set.
    const scoped =
      releasesScope === "lts"
        ? newestPatchPerLtsLine(releases.entries.filter((e) => isLtsLike(e.stream)))
        : releases.entries;
    const q = releasesSearch.trim().toLowerCase();
    const filtered = !q
      ? scoped
      : scoped.filter((e) => {
          if (e.version.toLowerCase().includes(q)) return true;
          if (e.stream.toLowerCase().includes(q)) return true;
          if (e.releaseDate && e.releaseDate.includes(q)) return true;
          return false;
        });
    // Sort newest-version-first so the top of the list is the highest
    // installable version, regardless of release date. Stable so entries
    // that tie on version (shouldn't happen, but defensive) keep their
    // feed order.
    return [...filtered].sort((a, b) => compareUnityVersions(b.version, a.version));
  });

  function streamLabel(stream: ReleaseEntry["stream"]): string {
    switch (stream) {
      case "lts":
        return "LTS";
      case "supported":
        return "SUPPORTED";
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
      case "supported":
        // LTS and the active SUPPORTED line are both stable, shipped
        // releases → green. SUPPORTED is the newest non-LTS engine
        // release Unity is actively maintaining.
        return "ok";
      case "tech":
        return "warn";
      case "beta":
      case "alpha":
        return "missing";
    }
  }

  /**
   * M15 T6.4: tone for a `UnityInstallation.releaseType` chip. Mirrors
   * `streamTone` but works on the short label string the discovery
   * scan produces (`"LTS"` / `"TECH"` / `"Beta"` / `"Alpha"`).
   */
  function streamToneForRelease(release: string): "ok" | "warn" | "missing" {
    switch (release) {
      case "LTS":
        return "ok";
      case "TECH":
        return "warn";
      case "Beta":
      case "Alpha":
        return "missing";
      default:
        return "warn";
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

  // Unity release-notes prefix. The Hub frontend uses the dashed form
  // (`.` → `-`) which is the canonical Unity URL shape; the mcp-server and
  // Rust backend build the same prefix from RELEASE_NOTES_URL_PREFIX in
  // their respective constants modules.
  const RELEASE_NOTES_URL_PREFIX = "https://unity.com/releases/editor/whats-new/";

  function releaseNotesUrl(version: string): string {
    const dashed = version.replace(/\./g, "-");
    return `${RELEASE_NOTES_URL_PREFIX}${dashed}`;
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

  /// Open Unity Hub's install dialog for a release by firing its
  /// `unityhub://` deep link. The Hub runs the download itself, so
  /// there is no in-app progress; we tell the user to continue in the
  /// Hub and to click Refresh on the Installed panel once it finishes.
  async function handleInstall(entry: ReleaseEntry) {
    S.drawerExpanded = true;
    S.appendDrawerLog(
      `opening Unity Hub to install Unity ${entry.version}${entry.changeset ? ` (changeset: ${entry.changeset})` : ""}…`,
    );
    try {
      await openUnityHubInstall(entry.version, entry.changeset);
      S.appendDrawerLog(
        `continue the install in Unity Hub, then click Refresh to see Unity ${entry.version} here.`,
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`install failed: ${msg}`);
    }
  }

  /// Whether a release row offers an install action. Installed versions
  /// are inert; everything else routes to Unity Hub on click. Drives
  /// both the row's click affordance and its `title`/cursor.
  function canInstallRow(entry: ReleaseEntry): boolean {
    return !isInstalled(entry.version);
  }

  /// Row click / Enter / Space → install (the primary action). A no-op
  /// for installed rows so the row stays non-interactive in that state.
  /// Opening the release notes is the dedicated Notes button's job, not
  /// the row's.
  function handleRowActivate(entry: ReleaseEntry) {
    if (!canInstallRow(entry)) return;
    void handleInstall(entry);
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
    <div class="table" role="grid" aria-rowcount={filtered.length + 1} aria-colcount={7}>
    <div class="table-head" role="row">
      <div class="th" role="columnheader">Version</div>
      <div class="th" role="columnheader">Stream</div>
      <div class="th" role="columnheader">Source</div>
      <div class="th" role="columnheader">Platforms</div>
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
          {@const platforms = inst.platforms ?? []}
          {@const stream = inst.releaseType ?? ""}
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
              {#if stream}
                <StatusChip
                  tone={streamToneForRelease(stream)}
                  label={stream}
                  title={`Unity ${inst.version} is on the ${stream} stream`}
                />
              {/if}
            </div>
            <div class="cell" role="gridcell">
              <span class="source-text">{inst.source || "Manual"}</span>
            </div>
            <div class="cell cell-platforms" role="gridcell" title={platforms.join(", ")}>
              {#if platforms.length === 0}
                <span class="muted">—</span>
              {:else}
                <span class="platforms-text">{platforms.join(", ")}</span>
              {/if}
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
    <div class="releases-meta">
      <div class="scope-toggle" role="tablist" aria-label="Releases scope">
        <button
          type="button"
          role="tab"
          class="scope-toggle-btn"
          class:scope-toggle-btn-active={releasesScope === "lts"}
          aria-selected={releasesScope === "lts"}
          onclick={() => (releasesScope = "lts")}
          title="Show only Long-Term-Support releases (the recommended install line)"
        >
          LTS releases
        </button>
        <button
          type="button"
          role="tab"
          class="scope-toggle-btn"
          class:scope-toggle-btn-active={releasesScope === "all"}
          aria-selected={releasesScope === "all"}
          onclick={() => (releasesScope = "all")}
          title="Show every release Unity publishes, including archived TECH/BETA/ALPHA builds"
        >
          All releases
        </button>
      </div>
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

    <div class="table releases-table" role="grid" aria-rowcount={filteredReleases.length + 1} aria-colcount={5}>
      <div class="table-head" role="row" style="grid-template-columns: {releasesGridTemplate}">
        <div class="th" role="columnheader">Version</div>
        <div class="th" role="columnheader">Stream</div>
        <div class="th" role="columnheader">Released</div>
        <div class="th" role="columnheader">Notes</div>
        <div class="th" role="columnheader">Status</div>
      </div>

      <div class="table-body releases-table-body">
        {#if !releases}
          <div class="empty-state">
            <p>Loading releases…</p>
          </div>
        {:else if filteredReleases.length === 0}
          <div class="empty-state">
            <p>
              {#if releasesScope === "lts"}
                No LTS releases match the current search. Switch to <strong>All releases</strong> to see archived TECH/BETA/ALPHA builds.
              {:else}
                No releases match the current search.
              {/if}
            </p>
          </div>
        {:else}
          {#each filteredReleases as entry, index (entry.version)}
            {@const installable = canInstallRow(entry)}
            <div
              class="row releases-row"
              class:releases-row-inert={!installable}
              role="row"
              aria-rowindex={index + 2}
              style="grid-template-columns: {releasesGridTemplate};"
              onclick={() => handleRowActivate(entry)}
              oncontextmenu={(e) => openReleasesContextMenu(e, entry)}
              onkeydown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  handleRowActivate(entry);
                }
              }}
              tabindex={0}
              title={isInstalled(entry.version)
                ? `Unity ${entry.version} is already installed`
                : `Click to install Unity ${entry.version} in Unity Hub; click Notes to open release notes`}
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
              <div class="cell cell-notes" role="gridcell">
                <button
                  type="button"
                  class="notes-btn"
                  onclick={(e) => { e.stopPropagation(); void openReleaseNotes(entry.releaseNotesUrl); }}
                  title={`Open release notes for Unity ${entry.version} in your browser`}
                >
                  Notes ↗
                </button>
              </div>
              <div class="cell" role="gridcell">
                {#if isInstalled(entry.version)}
                  <StatusChip tone="ok" label="Installed" />
                {:else}
                  <button
                    type="button"
                    class="install-btn"
                    onclick={(e) => { e.stopPropagation(); handleInstall(entry); }}
                    title={`Install Unity ${entry.version} in Unity Hub`}
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
        tabindex="-1"
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
            title={`Install Unity ${ctxEntry.version} in Unity Hub`}
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
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4rem, 0.5fr) minmax(4rem, 0.6fr) minmax(4rem, 0.7fr) minmax(12rem, 2.5fr) minmax(3.5rem, 0.5fr) minmax(5rem, 0.6fr);
    flex-shrink: 0;
    background: var(--hub-surface);
    border-bottom: 1px solid var(--hub-border);
    padding: 0 0.25rem;
  }

  /* Dedicated 5-column grid for the All-releases table.
     The grid template is applied inline (see `releasesGridTemplate` in
     the script block) on both the header and every row, the same
     pattern ProjectsTab uses, so header and body cells stay aligned at
     any width and there is no stylesheet-cascade fight with the shared
     7-column `.table-head` / `.row` rules. */

  .th {
    padding: 0.55rem 0.7rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
    user-select: none;
    /* Grid items default to min-width: auto, which lets an uppercase
       header label overflow its track and push the next column's header
       out of alignment with the body cells below it. Force the header
       to respect its track the same way .cell does. */
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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

  /* Releases table body: unlike the Installed table (which hosts a
     VirtualList that owns its own scroll), the All-releases body renders
     rows directly and is the scroll container itself. Mirrors the
     Projects-tab pattern so the list scrolls cleanly within the bounded
     table box — no extra empty space below the last row. The All scope
     tops out around ~245 rows, trivially small for an in-flow render. */
  .releases-table-body {
    overflow-y: auto;
  }

  .row {
    display: grid;
    grid-template-columns: minmax(10rem, 1.2fr) minmax(4rem, 0.5fr) minmax(4rem, 0.6fr) minmax(4rem, 0.7fr) minmax(12rem, 2.5fr) minmax(3.5rem, 0.5fr) minmax(5rem, 0.6fr);
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

  .cell-platforms {
    min-width: 0;
  }

  .platforms-text {
    font-size: 0.74rem;
    color: var(--hub-text-dim);
    /* Keep long platform lists on a single line; the full list is
       already exposed via the cell's `title` tooltip. */
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    display: inline-block;
    max-width: 100%;
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

  /* LTS vs All releases scope toggle (mirrors the Installed/All
     view-toggle look so the two controls read as a family). */
  .scope-toggle {
    display: inline-flex;
    flex-direction: row;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    overflow: hidden;
    background: var(--hub-surface);
    flex-shrink: 0;
  }

  .scope-toggle-btn {
    background: transparent;
    color: var(--hub-text-dim);
    border: none;
    padding: 0.35rem 0.65rem;
    font-size: 0.74rem;
    cursor: pointer;
    line-height: 1.3;
  }

  .scope-toggle-btn:hover {
    color: var(--hub-text-bright);
    background: var(--hub-bg);
  }

  .scope-toggle-btn-active {
    background: var(--hub-selected);
    color: var(--hub-text-bright);
  }

  .scope-toggle-btn-active:hover {
    background: var(--hub-selected);
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

  .ctx-item-install {
    color: var(--hub-accent);
  }

  .ctx-item-install:hover:not(:disabled) {
    color: var(--hub-text-bright);
  }

  /* Releases-table row affordances. Inert rows (installed, or another
     install in progress) drop the pointer cursor and hover highlight so
     they read as non-interactive; the Notes button inside them still
     works. */
  .releases-row {
    cursor: pointer;
  }

  .releases-row-inert {
    cursor: default;
  }

  .releases-row-inert:hover {
    background: transparent;
  }

  /* Notes-column button: opens the release-notes URL in the browser.
     Visually a quiet secondary affordance so it does not compete with
     the primary Install action, but stays clearly clickable. */
  .notes-btn {
    padding: 0.2rem 0.55rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-light);
    background: transparent;
    color: var(--hub-text-dim);
    font-size: 0.72rem;
    font-weight: 500;
    cursor: pointer;
    line-height: 1.3;
  }

  .notes-btn:hover {
    border-color: var(--hub-accent);
    color: var(--hub-accent);
    background: var(--hub-bg);
  }
</style>
