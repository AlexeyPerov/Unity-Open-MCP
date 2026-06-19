<script lang="ts">
  /**
   * Scrollable monospace log pane for the Open-MCP command runner.
   * Ports vibe-launcher's `Console.svelte` shape (dark pane, line
   * segments, optional clear button) without its frontend/backend
   * split — the lines are passed in directly by the parent.
   */
  let {
    lines = $bindable<string[]>([]),
    onclear,
    title,
  }: {
    lines?: string[];
    onclear?: () => void;
    title?: string;
  } = $props();

  let scrollEl = $state<HTMLDivElement | null>(null);

  // Auto-scroll to the bottom when new lines arrive (only when the
  // user is already near the bottom — don't yank them if they scrolled
  // up to read earlier output).
  $effect(() => {
    // Reference lines.length so the effect re-runs on append.
    void lines.length;
    if (scrollEl) {
      const el = scrollEl;
      queueMicrotask(() => {
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
        if (nearBottom) el.scrollTop = el.scrollHeight;
      });
    }
  });
</script>

<div class="console-wrap">
  {#if title || onclear}
    <div class="console-head">
      {#if title}<span class="console-title">{title}</span>{/if}
      {#if onclear}
        <button type="button" class="link-btn" onclick={onclear}>Clear</button>
      {/if}
    </div>
  {/if}
  <div class="console" bind:this={scrollEl}>
    {#if lines.length === 0}
      <span class="console-empty">No output yet.</span>
    {:else}
      <pre class="console-pre">{lines.join("\n")}</pre>
    {/if}
  </div>
</div>

<style>
  .console-wrap {
    display: flex;
    flex-direction: column;
    border: 1px solid var(--hub-border);
    border-radius: 0.4rem;
    overflow: hidden;
  }
  .console-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.25rem 0.5rem;
    background: var(--hub-card);
    border-bottom: 1px solid var(--hub-border);
  }
  .console-title {
    font-size: 0.7rem;
    color: var(--hub-text-dim);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .console {
    background: #1a1b21;
    color: #c9d1d9;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    line-height: 1.45;
    padding: 0.4rem 0.5rem;
    overflow: auto;
    max-height: 240px;
    min-height: 80px;
    white-space: pre-wrap;
    word-break: break-word;
  }
  .console-pre {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
  }
  .console-empty {
    color: #6e7681;
  }
  .link-btn {
    background: transparent;
    border: none;
    color: var(--hub-accent, #5c7cfa);
    cursor: pointer;
    font-size: 0.7rem;
  }
</style>
