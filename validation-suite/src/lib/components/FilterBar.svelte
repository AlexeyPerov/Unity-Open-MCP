<script lang="ts">
  import { app } from "../state/app.svelte.ts";
</script>

<div class="filters">
  <label class="check">
    <input
      type="checkbox"
      checked={app.filters.core}
      onchange={() => app.toggleFilter("core")}
    />
    <span class="dot core"></span>Required · core
  </label>
  <label class="check">
    <input
      type="checkbox"
      checked={app.filters.extended}
      onchange={() => app.toggleFilter("extended")}
    />
    <span class="dot extended"></span>Required · extended
  </label>
  <label class="check">
    <input
      type="checkbox"
      checked={app.filters.optional}
      onchange={() => app.toggleFilter("optional")}
    />
    <span class="dot optional"></span>Optional
  </label>
  <label class="check inset">
    <input
      type="checkbox"
      checked={app.filters.onlyIncomplete}
      onchange={() => app.toggleFilter("onlyIncomplete")}
    />
    Only incomplete
  </label>
</div>

<style>
  .filters {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border-bottom: 1px solid var(--border);
  }
  .check {
    display: flex;
    align-items: center;
    gap: 7px;
    font-size: 12.5px;
    color: var(--text-dim);
    cursor: pointer;
  }
  .check.inset {
    margin-top: 4px;
    padding-top: 6px;
    border-top: 1px solid var(--border);
    color: var(--text-faint);
  }
  input[type="checkbox"] {
    accent-color: var(--accent);
  }
  .dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex: none;
  }
  .dot.core {
    background: var(--accent);
  }
  .dot.extended {
    background: var(--warn);
  }
  .dot.optional {
    background: var(--text-faint);
  }
</style>
