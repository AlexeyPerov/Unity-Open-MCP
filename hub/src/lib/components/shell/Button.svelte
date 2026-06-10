<script lang="ts">
  let {
    variant = "primary",
    disabled = false,
    title = undefined,
    onclick,
    children,
    ...rest
  }: {
    variant?: "primary" | "secondary" | "destructive";
    disabled?: boolean;
    title?: string;
    onclick?: () => void;
    children?: import("svelte").Snippet;
    [key: string]: unknown;
  } = $props();
</script>

<button
  type="button"
  class="btn btn-{variant}"
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
    border: 1px solid #474957;
    background: #32343f;
    color: #d7d8e0;
    font-size: 0.82rem;
    font-weight: 500;
    cursor: pointer;
    line-height: 1.4;
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  .btn:hover:not(:disabled) {
    border-color: #5c7cfa;
    color: #fff;
  }

  .btn:focus-visible {
    outline: 2px solid #5c7cfa;
    outline-offset: 1px;
  }

  .btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .btn-secondary {
    border-color: #3f4150;
    background: #2a2b33;
    color: #a1a3b0;
  }

  .btn-secondary:hover:not(:disabled) {
    border-color: #5c7cfa;
    color: #d7d8e0;
  }

  .btn-destructive {
    border-color: #7a2a3a;
    background: #3a1a25;
    color: #f0a8b8;
  }

  .btn-destructive:hover:not(:disabled) {
    border-color: #de3576;
    background: #4a1a2a;
    color: #fff;
  }
</style>
