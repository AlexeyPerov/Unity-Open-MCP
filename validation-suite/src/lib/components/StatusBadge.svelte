<script lang="ts">
  import type { Status } from "@validation-suite/core";

  let { status }: { status: Status } = $props();

  const label: Record<Status, string> = {
    awaiting: "Awaiting",
    done: "Done",
    blocked: "Blocked",
  };
</script>

<span class="badge {status}" title={label[status]}>{label[status]}</span>

<style>
  .badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 8px;
    border-radius: 999px;
    font-size: 11px;
    font-weight: 500;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    white-space: nowrap;
  }
  .awaiting {
    color: var(--text-dim);
    background: var(--bg-elev-2);
    border: 1px solid var(--border);
  }
  .done {
    color: var(--ok);
    background: var(--ok-soft);
    border: 1px solid transparent;
  }
  .blocked {
    color: var(--bad);
    background: var(--bad-soft);
    border: 1px solid transparent;
  }
</style>
