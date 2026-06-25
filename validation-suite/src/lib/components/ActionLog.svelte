<script lang="ts">
  /**
   * Per-step action log panel (phase-2 deliverable).
   *
   * Renders the log lines a setup step's actions produced (or reset
   * consumed): per-action success/failure, MCP CLI output snippets, and
   * reset warnings. Read from the reactive `actionLog` store keyed by
   * scenario/step, so it updates live as the runner appends lines.
   */
  import { actionLog } from "../state/action_log.svelte";
  import type { ActionLogLine } from "../services/backend.ts";

  let { scenarioId, stepId }: { scenarioId: string; stepId: string } = $props();

  const lines = $derived(actionLog.get(scenarioId, stepId));
</script>

{#if lines.length > 0}
  <div class="action-log" role="log" aria-label="Setup action log">
    <div class="action-log-head">Action log</div>
    <div class="action-log-body">
      {#each lines as line, i (i)}
        <pre class="action-log-line" data-level={line.level}>{line.message}</pre>
        {#if line.snippet}
          <pre class="action-log-snippet">{line.snippet}</pre>
        {/if}
      {/each}
    </div>
  </div>
{/if}

<style>
  .action-log {
    margin-top: 0.6rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-surface);
    overflow: hidden;
  }

  .action-log-head {
    padding: 0.35rem 0.6rem;
    font-size: 0.68rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--hub-text-muted);
    font-weight: 600;
    border-bottom: 1px solid var(--hub-card);
    background: var(--hub-bg);
  }

  .action-log-body {
    max-height: 11rem;
    overflow: auto;
    padding: 0.4rem 0.6rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .action-log-line {
    margin: 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    line-height: 1.4;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--hub-text-dim);
  }

  .action-log-line[data-level="warn"] {
    color: var(--hub-warn-fg, #c9a227);
  }

  .action-log-line[data-level="error"] {
    color: var(--hub-error-fg, #de3576);
  }

  .action-log-snippet {
    margin: 0 0 0 0.6rem;
    padding: 0.3rem 0.45rem;
    background: var(--hub-bg);
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
    line-height: 1.4;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--hub-text-placeholder);
    max-height: 8rem;
    overflow: auto;
  }
</style>
