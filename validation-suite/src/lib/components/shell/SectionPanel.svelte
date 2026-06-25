<script lang="ts">
  import type { Snippet } from "svelte";

  /**
   * Collapsible section panel — the card pattern used in the Hub's
   * settings popups (`.group` / `.group-header` / `.group-body`). A
   * header row with a chevron, a title, an optional hint, and a body
   * that expands/collapses. Reused for step groups, info blocks, and
   * any other grouping in the runner.
   */
  let {
    title,
    hint = undefined,
    open = $bindable(true),
    accent = undefined,
    actions = undefined,
    children,
  }: {
    title: string;
    hint?: string;
    /** Two-way bindable open state so callers can drive it externally. */
    open?: boolean;
    /** Optional accent label/chip rendered at the right of the header. */
    accent?: Snippet;
    /** Optional header actions (buttons) rendered at the far right. */
    actions?: Snippet;
    children?: Snippet;
  } = $props();

  function toggle() {
    open = !open;
  }
</script>

<section class="group">
  <button
    type="button"
    class="group-header"
    aria-expanded={open}
    onclick={toggle}
  >
    <span class="group-chevron" class:group-chevron-open={open} aria-hidden="true">▸</span>
    <span class="group-header-text">
      <h3 class="group-title">{title}</h3>
      {#if hint}<p class="group-hint">{hint}</p>{/if}
    </span>
    {#if accent}
      <span class="group-accent" onclick={(e) => e.stopPropagation()} role="presentation">
        {@render accent()}
      </span>
    {/if}
    {#if actions}
      <span class="group-actions" onclick={(e) => e.stopPropagation()} role="presentation">
        {@render actions()}
      </span>
    {/if}
  </button>
  {#if open}
    <div class="group-body">
      {@render children?.()}
    </div>
  {/if}
</section>

<style>
  .group {
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-bg);
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
    border-bottom: 1px solid var(--hub-card);
    background: var(--hub-surface);
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  .group-header:hover {
    background: var(--hub-card);
  }

  .group-header:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: -2px;
  }

  .group-chevron {
    display: inline-block;
    flex-shrink: 0;
    width: 0.9rem;
    margin-top: 0.15rem;
    color: var(--hub-text-muted);
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
    color: var(--hub-text-dim);
    font-weight: 600;
  }

  .group-hint {
    margin: 0;
    font-size: 0.78rem;
    color: var(--hub-text-muted);
    line-height: 1.5;
  }

  .group-accent,
  .group-actions {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  .group-body {
    padding: 0.7rem 0.95rem 0.85rem;
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }
</style>
