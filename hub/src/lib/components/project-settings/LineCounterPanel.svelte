<script lang="ts">
  import type { ProjectEntry } from "$lib/services/config";
  import { countLines } from "$lib/services/config";
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";

  let { project }: { project: ProjectEntry } = $props();

  let running = $state(false);
  let error = $state<string | null>(null);
  // Seed from the cached stats on the entry (populated by a prior run
  // or the git-popup auto-calc). Kept in plain state so the "Run line
  // count" button can update them without re-deriving from the prop.
  let lastTotal = $state<number | null>(null);
  let lastScannedAt = $state<string | null>(null);
  // Sync from the prop when it changes (e.g. when the popup opens on a
  // different project, or the cached stats are refreshed upstream).
  $effect(() => {
    lastTotal = project.lineCountStats?.totalLines ?? null;
    lastScannedAt = project.lineCountStats?.scannedAt ?? null;
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
      error = e instanceof Error ? e.message : String(e);
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
      dot-directories and dependency folders. Respects the root
      <code>.gitignore</code>. The detailed report is appended to the app logs.
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
