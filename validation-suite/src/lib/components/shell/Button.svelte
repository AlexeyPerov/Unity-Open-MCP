<script lang="ts">
  let {
    variant = "primary",
    disabled = false,
    title = undefined,
    onclick,
    children,
    class: extraClass = "",
    ...rest
  }: {
    variant?: "primary" | "secondary" | "destructive";
    disabled?: boolean;
    title?: string;
    onclick?: () => void;
    children?: import("svelte").Snippet;
    class?: string;
    [key: string]: unknown;
  } = $props();
</script>

<button
  type="button"
  class="btn btn-{variant} {extraClass}"
  {disabled}
  {title}
  {onclick}
  {...rest}
>
  {@render children?.()}
</button>

<style>
  .btn {
    align-self: flex-start;
    padding: 0.45rem 0.85rem;
    border-radius: 6px;
    border: 1px solid var(--hub-border-hover);
    background: var(--hub-selected);
    color: var(--hub-text);
    font-size: 0.82rem;
    font-weight: 500;
    cursor: pointer;
    line-height: 1.4;
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  .btn:hover:not(:disabled) {
    border-color: var(--hub-accent);
    color: var(--hub-text-bright);
  }

  .btn:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .btn-secondary {
    border-color: var(--hub-border-light);
    background: var(--hub-card);
    color: var(--hub-text-dim);
  }

  .btn-secondary:hover:not(:disabled) {
    border-color: var(--hub-accent);
    color: var(--hub-text);
  }

  .btn-destructive {
    border-color: var(--hub-error);
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
  }

  .btn-destructive:hover:not(:disabled) {
    border-color: var(--hub-error);
    background: var(--hub-error-bg);
    color: var(--hub-text-bright);
  }
</style>
