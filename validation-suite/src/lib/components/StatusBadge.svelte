<script lang="ts">
  import type { Status } from "@validation-suite/core";

  let { status }: { status: Status } = $props();

  const label: Record<Status, string> = {
    awaiting: "Awaiting",
    done: "Done",
    blocked: "Blocked",
  };
</script>

<span class="chip chip-{status}" title={label[status]}>{label[status]}</span>

<style>
  .chip {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
    font-size: 0.68rem;
    font-weight: 600;
    line-height: 1.3;
    white-space: nowrap;
  }

  .chip-awaiting {
    background: var(--hub-selected);
    color: var(--hub-text-muted);
    border: 1px solid var(--hub-border-light);
  }

  .chip-done {
    background: var(--hub-warn-bg);
    color: var(--hub-warn-fg);
    border: 1px solid var(--hub-warn-fg);
  }

  .chip-blocked {
    background: var(--hub-error-bg);
    color: var(--hub-error-fg);
    border: 1px solid var(--hub-error-fg);
  }
</style>
