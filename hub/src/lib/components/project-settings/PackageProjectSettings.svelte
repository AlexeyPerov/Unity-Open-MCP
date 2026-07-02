<script lang="ts">
  import type { ProjectEntry, PackageManifest } from "$lib/services/config";
  import {
    readPackageManifest,
    writePackageManifest,
    regeneratePackageMetaGuids,
    addMissingPackageMeta,
    migratePackageFiles,
    saveProjects,
    type MigrateResult,
    type MetaOperationResult,
  } from "$lib/services/config";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { S } from "$lib/state.svelte";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import Button from "$lib/components/shell/Button.svelte";
  import LineCounterPanel from "$lib/components/project-settings/LineCounterPanel.svelte";

  let {
    project,
    onMutated,
  }: {
    project: ProjectEntry;
    onMutated: (updated: ProjectEntry) => void;
  } = $props();

  type Tab = "manifest" | "meta" | "migrate" | "lineCounter";
  let activeTab = $state<Tab>("manifest");

  // --- Manifest tab ---
  let manifest = $state<PackageManifest | null>(null);
  let manifestLoading = $state(true);
  let manifestError = $state<string | null>(null);
  let manifestSaving = $state(false);
  let manifestSaved = $state<string | null>(null);
  let originalVersion = $state<string | undefined>(undefined);
  let bumpChangelog = $state(true);
  let keywordsText = $state("");

  // Local editable dependency list (key/value rows).
  let depRows = $state<{ key: string; value: string }[]>([]);

  async function loadManifest() {
    manifestLoading = true;
    manifestError = null;
    try {
      const m = await readPackageManifest(project.id);
      manifest = m;
      originalVersion = m.version;
      keywordsText = (m.keywords ?? []).join(", ");
      depRows = Object.entries(m.dependencies ?? {}).map(([key, value]) => ({ key, value }));
    } catch (e) {
      manifestError = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`read package.json failed: ${manifestError}`);
    } finally {
      manifestLoading = false;
    }
  }

  async function saveManifest() {
    if (!manifest) return;
    manifestSaving = true;
    manifestSaved = null;
    manifestError = null;
    try {
      const updated: PackageManifest = {
        ...manifest,
        keywords: keywordsText
          .split(",")
          .map((k) => k.trim())
          .filter((k) => k.length > 0),
        dependencies: Object.fromEntries(
          depRows.filter((r) => r.key.trim().length > 0).map((r) => [r.key.trim(), r.value.trim()])
        ),
      };
      const result = await writePackageManifest(
        project.id,
        updated,
        originalVersion,
        bumpChangelog,
        undefined
      );
      manifest = result;
      originalVersion = result.version;
      manifestSaved = `saved${bumpChangelog && originalVersion !== result.version ? " (+ changelog)" : ""}`;
      S.appendDrawerLog(`package.json updated for ${project.name}`);
    } catch (e) {
      manifestError = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`write package.json failed: ${manifestError}`);
    } finally {
      manifestSaving = false;
    }
  }

  function addDepRow() {
    depRows = [...depRows, { key: "", value: "" }];
  }
  function removeDepRow(idx: number) {
    depRows = depRows.filter((_, i) => i !== idx);
  }

  // --- Meta tab ---
  let metaBusy = $state(false);
  let metaResult = $state<MetaOperationResult | null>(null);

  async function runRegenGuids() {
    metaBusy = true;
    metaResult = null;
    try {
      metaResult = await regeneratePackageMetaGuids(project.id);
      S.appendDrawerLog(`regenerated ${metaResult.regenerated} GUIDs for ${project.name}`);
      for (const note of metaResult.notes) S.appendDrawerLog(note);
    } catch (e) {
      S.appendErrorLog(`regenerate GUIDs failed: ${e}`);
    } finally {
      metaBusy = false;
    }
  }

  async function runAddMissingMeta() {
    metaBusy = true;
    metaResult = null;
    try {
      metaResult = await addMissingPackageMeta(project.id);
      S.appendDrawerLog(`added ${metaResult.added} .meta files for ${project.name}`);
      for (const note of metaResult.notes) S.appendDrawerLog(note);
    } catch (e) {
      S.appendErrorLog(`add missing .meta failed: ${e}`);
    } finally {
      metaBusy = false;
    }
  }

  // --- Migrate tab ---
  let migrateSource = $state<string>("");
  let migrateBusy = $state(false);
  let migrateResult = $state<MigrateResult | null>(null);
  let migrateError = $state<string | null>(null);
  // Skip .meta files even when they match by name. Off by default.
  let migrateSkipMeta = $state(false);
  // Sync the saved source folder from the project entry (pre-fills
  // when the popup opens on a package with a saved source).
  $effect(() => {
    migrateSource = project.migrateSourceFolder ?? "";
  });

  // Grouped buckets of the last result, derived so the template can
  // render each section only when non-empty.
  let replacedEntries = $derived(
    migrateResult?.entries.filter((e) => e.action === "replaced") ?? []
  );
  let skippedMetaEntries = $derived(
    migrateResult?.entries.filter((e) => e.action === "skipped-meta") ?? []
  );
  let skippedNewEntries = $derived(
    migrateResult?.entries.filter((e) => e.action === "skipped-new") ?? []
  );
  let untouchedEntries = $derived(
    migrateResult?.entries.filter((e) => e.action === "untouched") ?? []
  );
  let skippedDuplicateEntries = $derived(
    migrateResult?.entries.filter((e) => e.action === "skipped-duplicate") ?? []
  );

  async function pickSource() {
    const selected = await openDialog({
      directory: true,
      multiple: false,
      title: "Select migration source folder",
    });
    if (selected && typeof selected === "string") {
      migrateSource = selected;
    }
  }

  async function saveSource() {
    const updated: ProjectEntry = { ...project, migrateSourceFolder: migrateSource };
    const nextList = projectsStore.projects.map((p) => (p.id === project.id ? updated : p));
    try {
      await saveProjects({ version: 1, projects: nextList });
      projectsStore.replaceAll(nextList);
      onMutated(updated);
      S.appendDrawerLog(`saved migration source for ${project.name}: ${migrateSource}`);
    } catch (e) {
      S.appendErrorLog(`save migration source failed: ${e}`);
    }
  }

  async function runMigrate() {
    if (!migrateSource) return;
    migrateBusy = true;
    migrateError = null;
    migrateResult = null;
    try {
      migrateResult = await migratePackageFiles(
        project.id,
        migrateSource,
        migrateSkipMeta
      );
      S.appendDrawerLog(
        `migrated ${migrateResult.replaced} files for ${project.name} ` +
          `(${migrateResult.replaced} replaced, ${migrateResult.skippedNew} new in source, ` +
          `${migrateResult.skippedMeta} .meta skipped, ${migrateResult.skippedDuplicate} duplicate, ` +
          `${migrateResult.untouched} untouched)`
      );
      // Reflect the persisted source folder + mtime back into the store.
      const updated: ProjectEntry = { ...project, migrateSourceFolder: migrateResult.savedSourceFolder };
      onMutated(updated);
    } catch (e) {
      migrateError = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`migrate failed: ${migrateError}`);
    } finally {
      migrateBusy = false;
    }
  }

  // Load manifest on mount.
  $effect(() => {
    // Re-run when the project id changes (popup reopened on a different package).
    if (project.id) loadManifest();
  });
</script>

<div class="package-settings">
  <nav class="pkg-tabs">
    <button class="pkg-tab" class:active={activeTab === "manifest"} onclick={() => (activeTab = "manifest")}>Manifest</button>
    <button class="pkg-tab" class:active={activeTab === "meta"} onclick={() => (activeTab = "meta")}>Meta</button>
    <button class="pkg-tab" class:active={activeTab === "migrate"} onclick={() => (activeTab = "migrate")}>Migrate</button>
    <button class="pkg-tab" class:active={activeTab === "lineCounter"} onclick={() => (activeTab = "lineCounter")}>Line counter</button>
  </nav>

  {#if activeTab === "manifest"}
    <section class="pkg-panel">
      {#if manifestLoading}
        <p class="muted">Loading package.json…</p>
      {:else if manifestError}
        <p class="error-text">{manifestError}</p>
      {:else if manifest}
        <div class="form-grid">
          <label class="field">
            <span class="field-label">name</span>
            <input bind:value={manifest.name} placeholder="com.author.pkg" spellcheck="false" />
          </label>
          <label class="field">
            <span class="field-label">version</span>
            <input bind:value={manifest.version} placeholder="1.0.0" spellcheck="false" />
          </label>
          <label class="field">
            <span class="field-label">displayName</span>
            <input bind:value={manifest.displayName} placeholder="Package Name" />
          </label>
          <label class="field field-wide">
            <span class="field-label">description</span>
            <input bind:value={manifest.description} placeholder="What the package does" />
          </label>
          <label class="field">
            <span class="field-label">unity</span>
            <input bind:value={manifest.unity} placeholder="2022.3" spellcheck="false" />
          </label>
          <label class="field">
            <span class="field-label">unityRelease</span>
            <input bind:value={manifest.unityRelease} placeholder="1f1" spellcheck="false" />
          </label>
          <label class="field field-wide">
            <span class="field-label">keywords (comma-separated)</span>
            <input bind:value={keywordsText} placeholder="tool, utility" spellcheck="false" />
          </label>
          <label class="field">
            <span class="field-label">author.name</span>
            <input bind:value={manifest.author!.name} placeholder="Author" />
          </label>
          <label class="field">
            <span class="field-label">author.url</span>
            <input bind:value={manifest.author!.url} placeholder="https://…" spellcheck="false" />
          </label>
        </div>

        <div class="dep-section">
          <div class="dep-head">
            <span class="field-label">dependencies</span>
            <button type="button" class="link-btn" onclick={addDepRow}>+ Add</button>
          </div>
          {#each depRows as row, idx}
            <div class="dep-row">
              <input
                bind:value={row.key}
                placeholder="com.unity.xr.management"
                spellcheck="false"
                aria-label="Dependency name"
              />
              <input
                bind:value={row.value}
                placeholder="4.0.1"
                spellcheck="false"
                aria-label="Dependency version"
              />
              <button type="button" class="link-btn dep-remove" onclick={() => removeDepRow(idx)} aria-label="Remove dependency">Remove</button>
            </div>
          {/each}
        </div>

        <label class="checkbox-row">
          <input type="checkbox" bind:checked={bumpChangelog} />
          <span>Bump CHANGELOG.md when version changes</span>
        </label>

        {#if manifestError}<p class="error-text">{manifestError}</p>{/if}
        {#if manifestSaved}<p class="ok-text">{manifestSaved}</p>{/if}

        <div class="form-actions">
          <Button variant="primary" disabled={manifestSaving} onclick={saveManifest}>
            {manifestSaving ? "Saving…" : "Save manifest"}
          </Button>
        </div>
      {/if}
    </section>

  {:else if activeTab === "meta"}
    <section class="pkg-panel">
      <p class="hint">
        Regenerate every <code>.meta</code> GUID in the package (useful after
        duplicating assets), or create <code>.meta</code> files for assets that
        lack one. Assets inside <code>~</code>-suffixed folders are skipped.
      </p>
      <div class="meta-actions">
        <Button variant="secondary" disabled={metaBusy} onclick={runAddMissingMeta}>
          Add missing .meta files
        </Button>
        <Button variant="secondary" disabled={metaBusy} onclick={runRegenGuids}>
          Regenerate all GUIDs
        </Button>
      </div>
      {#if metaResult}
        <p class="ok-text">
          {#if metaResult.regenerated > 0}
            Regenerated {metaResult.regenerated} GUIDs.
          {/if}
          {#if metaResult.added > 0}
            Added {metaResult.added} .meta files.
          {/if}
        </p>
        {#if metaResult.notes.length > 0}
          <ul class="notes-list">
            {#each metaResult.notes as note}<li>{note}</li>{/each}
          </ul>
        {/if}
      {/if}
    </section>

  {:else if activeTab === "migrate"}
    <section class="pkg-panel">
      <p class="hint">
        Overwrite files in this package from a source folder. Matching is by
        <strong>file name</strong> (folder is ignored) — a source <code>Foo.cs</code>
        overwrites any package file named <code>Foo.cs</code>. If the same name
        appears more than once on either side it is skipped as ambiguous (shown
        in the report). Files only in the source are not copied, and files only
        in the package are left untouched. The source folder is saved
        per-package for next time.
      </p>
      <div class="migrate-source">
        <input
          bind:value={migrateSource}
          placeholder="/path/to/source"
          spellcheck="false"
          aria-label="Migration source folder"
        />
        <Button variant="secondary" onclick={pickSource}>Browse…</Button>
        <Button variant="secondary" onclick={saveSource}>Save source</Button>
      </div>
      <label class="checkbox-row">
        <input type="checkbox" bind:checked={migrateSkipMeta} />
        <span>Skip .meta files (leave matched .meta files untouched)</span>
      </label>
      <div class="meta-actions">
        <Button variant="primary" disabled={migrateBusy || !migrateSource} onclick={runMigrate}>
          {migrateBusy ? "Migrating…" : "Migrate"}
        </Button>
      </div>
      {#if migrateError}<p class="error-text">{migrateError}</p>{/if}
      {#if migrateResult}
        <p class="ok-text">
          Replaced {migrateResult.replaced}
          {#if migrateResult.skippedMeta}
            , .meta skipped {migrateResult.skippedMeta}{/if}
          {#if migrateResult.skippedDuplicate}
            , duplicates skipped {migrateResult.skippedDuplicate}{/if}
          {#if migrateResult.skippedNew}
            , new in source {migrateResult.skippedNew}{/if}
          {#if migrateResult.untouched}
            , untouched {migrateResult.untouched}{/if}.
        </p>
        {#if replacedEntries.length}
          <p class="migrate-group-title">Replaced ({replacedEntries.length})</p>
          <ul class="notes-list migrate-log">
            {#each replacedEntries as entry}
              <li>
                <span class="migrate-action migrate-replaced">replaced</span>
                <span class="migrate-path">{entry.relPath}</span>
              </li>
            {/each}
          </ul>
        {/if}
        {#if skippedMetaEntries.length}
          <p class="migrate-group-title">Skipped .meta ({skippedMetaEntries.length})</p>
          <ul class="notes-list migrate-log">
            {#each skippedMetaEntries as entry}
              <li>
                <span class="migrate-action migrate-skipped-meta">skipped-meta</span>
                <span class="migrate-path">{entry.relPath}</span>
              </li>
            {/each}
          </ul>
        {/if}
        {#if skippedDuplicateEntries.length}
          <p class="migrate-group-title">
            Duplicates — name not unique, skipped ({skippedDuplicateEntries.length})
          </p>
          <ul class="notes-list migrate-log">
            {#each skippedDuplicateEntries as entry}
              <li>
                <span class="migrate-action migrate-skipped-duplicate">duplicate</span>
                <span class="migrate-path">{entry.relPath}</span>
              </li>
            {/each}
          </ul>
        {/if}
        {#if skippedNewEntries.length}
          <p class="migrate-group-title">
            New in source — not copied ({skippedNewEntries.length})
          </p>
          <ul class="notes-list migrate-log">
            {#each skippedNewEntries as entry}
              <li>
                <span class="migrate-action migrate-skipped-new">skipped-new</span>
                <span class="migrate-path">{entry.relPath}</span>
              </li>
            {/each}
          </ul>
        {/if}
        {#if untouchedEntries.length}
          <p class="migrate-group-title">
            Only in package — untouched ({untouchedEntries.length})
          </p>
          <ul class="notes-list migrate-log">
            {#each untouchedEntries as entry}
              <li>
                <span class="migrate-action migrate-untouched">untouched</span>
                <span class="migrate-path">{entry.relPath}</span>
              </li>
            {/each}
          </ul>
        {/if}
      {/if}
    </section>
  {:else if activeTab === "lineCounter"}
    <LineCounterPanel {project} />
  {/if}
</div>

<style>
  .package-settings {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .pkg-tabs {
    display: flex;
    gap: 0.25rem;
    border-bottom: 1px solid var(--hub-border);
  }
  .pkg-tab {
    padding: 0.4rem 0.8rem;
    background: transparent;
    border: none;
    border-bottom: 2px solid transparent;
    color: var(--hub-text-dim);
    font-size: 0.8rem;
    cursor: pointer;
  }
  .pkg-tab.active {
    color: var(--hub-text);
    border-bottom-color: var(--hub-accent, #5c7cfa);
  }
  .pkg-panel {
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
  }
  .form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.6rem;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }
  .field-wide { grid-column: 1 / -1; }
  .field-label {
    font-size: 0.7rem;
    color: var(--hub-text-dim);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  input {
    padding: 0.3rem 0.4rem;
    border: 1px solid var(--hub-border);
    border-radius: 0.3rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    font-size: 0.8rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }
  .dep-section {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }
  .dep-head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }
  .dep-row {
    display: grid;
    grid-template-columns: 1fr 1fr auto;
    gap: 0.4rem;
  }
  .dep-remove { color: var(--hub-danger); }
  .checkbox-row {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    font-size: 0.8rem;
    color: var(--hub-text-dim);
  }
  .form-actions, .meta-actions {
    display: flex;
    gap: 0.5rem;
  }
  .migrate-source {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 0.4rem;
    align-items: center;
  }
  .link-btn {
    background: transparent;
    border: none;
    color: var(--hub-accent, #5c7cfa);
    cursor: pointer;
    font-size: 0.75rem;
    padding: 0;
  }
  .hint {
    margin: 0;
    font-size: 0.75rem;
    line-height: 1.5;
    color: var(--hub-text-dim);
  }
  .error-text { color: var(--hub-danger); font-size: 0.8rem; margin: 0; }
  .ok-text { color: #56b482; font-size: 0.8rem; margin: 0; }
  .muted { color: var(--hub-text-dim); }
  .notes-list {
    list-style: none;
    margin: 0;
    padding: 0;
    max-height: 200px;
    overflow-y: auto;
    border: 1px solid var(--hub-border);
    border-radius: 0.3rem;
    font-size: 0.72rem;
  }
  .notes-list li {
    padding: 0.2rem 0.4rem;
    border-bottom: 1px solid var(--hub-border);
    color: var(--hub-text-dim);
  }
  .notes-list li:last-child { border-bottom: none; }
  .migrate-log li {
    display: flex;
    gap: 0.4rem;
    align-items: center;
  }
  .migrate-action {
    font-size: 0.65rem;
    font-weight: 700;
    text-transform: uppercase;
    min-width: 4rem;
  }
  .migrate-replaced { color: #e0a230; }
  .migrate-skipped-meta { color: #8a8f98; }
  .migrate-skipped-new { color: #5c7cfa; }
  .migrate-skipped-duplicate { color: var(--hub-danger); }
  .migrate-untouched { color: #8a8f98; }
  .migrate-group-title {
    margin: 0.6rem 0 0.25rem;
    font-size: 0.72rem;
    font-weight: 600;
    color: var(--hub-text-dim);
  }
  .migrate-path {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }
</style>
