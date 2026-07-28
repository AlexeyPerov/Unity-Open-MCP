<script lang="ts">
  import type { ProjectEntry } from "$lib/services/config";
  import { countLines } from "$lib/services/config";
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";
  import { describeInvokeError } from "$lib/components/project-settings/invoke-errors.ts";

  let { project }: { project: ProjectEntry } = $props();

  let running = $state(false);
  let error = $state<string | null>(null);
  // Seed from the cached stats on the entry (populated by a prior run
  // or the git-popup auto-calc). Kept in plain state so the "Run line
  // count" button can update them without re-deriving from the prop.
  let lastTotal = $state<number | null>(null);
  let lastScannedAt = $state<string | null>(null);
  // A13: the last `project.id` we synced the cached stats from. The sync
  // effect must re-seed from `project.lineCountStats` when the user switches
  // to a different project, but NOT on a same-project identity bump (another
  // tab's `saveSource` / `saveManifest` / `runMigrate` round-trips the
  // projects store with a fresh `{ ...project }` object). Snapshotting the
  // id into a local did not reliably decouple from the prop-identity bump,
  // so a freshly-computed `runCount()` result could be stomped by the stale
  // cached stats in the same tick. Comparing the id VALUE fixes it.
  let lastSyncedProjectId = $state<string | null>(null);
  $effect(() => {
    const id = project.id;
    if (id && id !== lastSyncedProjectId) {
      lastSyncedProjectId = id;
      const stats = project.lineCountStats;
      lastTotal = stats?.totalLines ?? null;
      lastScannedAt = stats?.scannedAt ?? null;
    }
  });

  async function runCount() {
    if (running) return;
    running = true;
    error = null;
    try {
      const result = await countLines(project.id, true);
      lastTotal = result.stats.totalLines;
      lastScannedAt = result.stats.scannedAt;
      // The full four-section report (extensions counted/ignored,
      // skipped dirs, .gitignore respected) goes to the app logs so
      // the user can review exactly what was counted.
      S.appendDrawerLog(`line count for ${project.name} (${result.stats.totalLines} lines):`);
      for (const line of result.report.split("\n")) {
        S.appendDrawerLog(line);
      }
    } catch (e) {
      error = describeInvokeError(e);
      S.appendErrorLog(`line count failed for ${project.name}: ${error}`);
    } finally {
      running = false;
    }
  }
</script>

<section class="mini-panel">
  <header class="mini-panel-head">
    <h3>Line counter</h3>
    {#if lastScannedAt}
      <span class="muted small">scanned {new Date(lastScannedAt).toLocaleString()}</span>
    {/if}
  </header>
  <div class="mini-panel-body">
    <p class="hint">
      Counts newline bytes in source files (extension allowlist), pruning
      dot-directories and dependency folders. Respects the root and
      nested <code>.gitignore</code> files. The detailed report is appended
      to the app logs.
    </p>
    {#if lastTotal !== null}
      <p class="stat">
        <span class="stat-value">{lastTotal.toLocaleString()}</span>
        <span class="stat-label">total lines</span>
      </p>
    {/if}
    {#if error}
      <p class="error">{error}</p>
    {/if}
    <Button variant="primary" disabled={running} onclick={runCount}>
      {running ? "Counting…" : "Run line count"}
    </Button>
  </div>
</section>

<style>
  .hint {
    margin: 0 0 0.6rem;
    font-size: 0.75rem;
    line-height: 1.5;
    color: var(--hub-text-dim);
  }
  .stat {
    margin: 0 0 0.8rem;
    display: flex;
    align-items: baseline;
    gap: 0.4rem;
  }
  .stat-value {
    font-size: 1.4rem;
    font-weight: 700;
    color: var(--hub-text);
  }
  .stat-label {
    font-size: 0.75rem;
    color: var(--hub-text-dim);
  }
  .error {
    margin: 0 0 0.6rem;
    font-size: 0.75rem;
    color: var(--hub-danger);
  }
  .muted {
    color: var(--hub-text-dim);
  }
  .small {
    font-size: 0.7rem;
  }
</style>
