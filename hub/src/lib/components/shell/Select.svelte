<script lang="ts">
  let {
    options = [],
    value = "",
    onchange,
    placeholder,
    disabled = false,
    title = undefined,
    id = undefined,
    class: className = undefined,
    ...rest
  }: {
    options: { value: string; label: string; disabled?: boolean }[];
    value?: string;
    onchange?: (value: string) => void;
    placeholder?: string;
    disabled?: boolean;
    title?: string;
    id?: string;
    class?: string;
    [key: string]: unknown;
  } = $props();

  let open = $state(false);
  let container: HTMLDivElement | undefined = $state();
  let activeIndex = $state(-1);

  let selectedOption = $derived(options.find((o) => o.value === value));
  let displayLabel = $derived(selectedOption?.label ?? placeholder ?? "");

  function toggleOpen() {
    if (disabled) return;
    open = !open;
    if (open) {
      activeIndex = options.findIndex((o) => o.value === value);
      if (activeIndex < 0) activeIndex = 0;
    }
  }

  function selectOption(opt: { value: string; label: string; disabled?: boolean }) {
    if (opt.disabled) return;
    onchange?.(opt.value);
    open = false;
  }

  function handleKeydown(e: KeyboardEvent) {
    if (!open) {
      if (e.key === "ArrowDown" || e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        toggleOpen();
      }
      return;
    }
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        activeIndex = Math.min(activeIndex + 1, options.length - 1);
        scrollToActive();
        break;
      case "ArrowUp":
        e.preventDefault();
        activeIndex = Math.max(activeIndex - 1, 0);
        scrollToActive();
        break;
      case "Enter":
        e.preventDefault();
        if (activeIndex >= 0 && activeIndex < options.length) {
          selectOption(options[activeIndex]);
        }
        break;
      case "Escape":
        e.preventDefault();
        open = false;
        break;
    }
  }

  function scrollToActive() {
    if (!container) return;
    const items = container.querySelectorAll("[role='option']");
    items[activeIndex]?.scrollIntoView({ block: "nearest" });
  }

  function handleClickOutside(e: MouseEvent) {
    if (container && !container.contains(e.target as Node)) {
      open = false;
    }
  }
</script>

<svelte:window onclick={handleClickOutside} />

<div
  class="select-wrap"
  class:select-wrap-class={className}
  bind:this={container}
>
  <button
    type="button"
    class="select-trigger"
    {disabled}
    {title}
    {id}
    aria-expanded={open}
    aria-haspopup="listbox"
    onclick={toggleOpen}
    onkeydown={handleKeydown}
    {...rest}
  >
    <span class="select-label">{displayLabel}</span>
    <span class="select-arrow" aria-hidden="true">&#9662;</span>
  </button>

  {#if open && !disabled}
    <ul class="select-panel" role="listbox" tabindex="-1">
      {#each options as opt, i (opt.value)}
        <li
          role="option"
          class="select-option"
          class:select-option-active={i === activeIndex}
          class:select-option-selected={opt.value === value}
          class:select-option-disabled={opt.disabled}
          aria-selected={opt.value === value}
          aria-disabled={opt.disabled}
          tabindex="-1"
          onclick={() => selectOption(opt)}
          onmouseenter={() => { activeIndex = i; }}
          onkeydown={(e) => {
            if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              selectOption(opt);
            }
          }}
        >
          {opt.label}
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .select-wrap {
    position: relative;
    display: inline-flex;
    flex-direction: column;
  }

  .select-trigger {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.45rem 0.85rem;
    border-radius: 6px;
    border: 1px solid var(--hub-border-light);
    background: var(--hub-card);
    color: var(--hub-text-dim);
    font-size: 0.82rem;
    font-weight: 500;
    cursor: pointer;
    line-height: 1.4;
    white-space: nowrap;
    text-align: left;
    width: 100%;
    box-sizing: border-box;
  }

  .select-trigger:hover:not(:disabled) {
    border-color: var(--hub-accent);
    color: var(--hub-text);
  }

  .select-trigger:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .select-trigger:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .select-label {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .select-arrow {
    flex-shrink: 0;
    font-size: 0.65rem;
    opacity: 0.6;
  }

  .select-panel {
    position: absolute;
    top: calc(100% + 4px);
    left: 0;
    z-index: 200;
    min-width: 100%;
    max-height: 18rem;
    overflow-y: auto;
    margin: 0;
    padding: 0.25rem;
    list-style: none;
    background: var(--hub-surface);
    border: 1px solid var(--hub-border);
    border-radius: 6px;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.4);
  }

  .select-option {
    padding: 0.45rem 0.65rem;
    font-size: 0.82rem;
    color: var(--hub-text);
    border-radius: 4px;
    cursor: pointer;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .select-option:hover:not(.select-option-disabled),
  .select-option-active:not(.select-option-disabled) {
    background: var(--hub-bg);
    color: var(--hub-text-bright);
  }

  .select-option-selected {
    color: var(--hub-accent);
    font-weight: 600;
  }

  .select-option-disabled {
    color: var(--hub-text-disabled);
    cursor: not-allowed;
    opacity: 0.55;
  }
</style>
