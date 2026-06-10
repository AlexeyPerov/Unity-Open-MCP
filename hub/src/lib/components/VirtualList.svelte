<script lang="ts" generics="T">
  import type { Snippet } from "svelte";

  let {
    items,
    itemHeight,
    overscan = 6,
    minItemsForWindowing = 60,
    children,
    empty,
    class: className = "",
  }: {
    items: T[];
    itemHeight: number;
    overscan?: number;
    minItemsForWindowing?: number;
    children: Snippet<[T, number]>;
    empty?: Snippet;
    class?: string;
  } = $props();

  let viewport: HTMLDivElement | undefined = $state();
  let scrollTop = $state(0);
  let viewportHeight = $state(0);

  function handleScroll() {
    if (viewport) scrollTop = viewport.scrollTop;
  }

  $effect(() => {
    if (!viewport) return;
    viewportHeight = viewport.clientHeight;
    const ro = new ResizeObserver(() => {
      if (viewport) viewportHeight = viewport.clientHeight;
    });
    ro.observe(viewport);
    return () => ro.disconnect();
  });

  let totalHeight = $derived(items.length * itemHeight);
  let useWindowing = $derived(items.length >= minItemsForWindowing);

  let window = $derived.by(() => {
    if (!useWindowing) {
      return { start: 0, end: items.length };
    }
    const startRaw = Math.floor(scrollTop / itemHeight) - overscan;
    const visibleCount = Math.ceil(viewportHeight / itemHeight) + overscan * 2;
    const start = Math.max(0, startRaw);
    const end = Math.min(items.length, start + visibleCount);
    return { start, end };
  });

  function handleKeydown(e: KeyboardEvent) {
    if (!useWindowing) return;
    if (!viewport) return;
    if (e.key !== "PageDown" && e.key !== "PageUp" && e.key !== "Home" && e.key !== "End") {
      return;
    }
    const pageSize = Math.max(1, Math.floor(viewportHeight / itemHeight) - 1);
    if (e.key === "PageDown") {
      viewport.scrollTop = Math.min(totalHeight - viewportHeight, viewport.scrollTop + pageSize * itemHeight);
      e.preventDefault();
    } else if (e.key === "PageUp") {
      viewport.scrollTop = Math.max(0, viewport.scrollTop - pageSize * itemHeight);
      e.preventDefault();
    } else if (e.key === "Home") {
      viewport.scrollTop = 0;
      e.preventDefault();
    } else if (e.key === "End") {
      viewport.scrollTop = totalHeight;
      e.preventDefault();
    }
  }
</script>

<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
  class="vlist {className}"
  bind:this={viewport}
  onscroll={handleScroll}
  onkeydown={handleKeydown}
  role="list"
  tabindex="0"
  aria-label="Scrollable list"
>
  {#if items.length === 0}
    <div class="vlist-empty">
      {@render empty?.()}
    </div>
  {:else if useWindowing}
    <div class="vlist-spacer" style="height: {totalHeight}px;">
      <div
        class="vlist-window"
        style="transform: translateY({window.start * itemHeight}px);"
      >
        {#each items.slice(window.start, window.end) as item, i (window.start + i)}
          <div class="vlist-row" style="height: {itemHeight}px;">
            {@render children(item, window.start + i)}
          </div>
        {/each}
      </div>
    </div>
  {:else}
    <div class="vlist-window vlist-window-static">
      {#each items as item, i (i)}
        <div class="vlist-row" style="height: {itemHeight}px;">
          {@render children(item, i)}
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .vlist {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
    position: relative;
    outline: none;
  }

  .vlist:focus-visible {
    box-shadow: inset 0 0 0 1px #5c7cfa;
  }

  .vlist-spacer {
    position: relative;
    width: 100%;
  }

  .vlist-window {
    position: absolute;
    inset: 0;
    will-change: transform;
  }

  .vlist-window-static {
    position: relative;
  }

  .vlist-row {
    width: 100%;
  }

  .vlist-empty {
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: #6f7280;
    font-size: 0.85rem;
  }
</style>
