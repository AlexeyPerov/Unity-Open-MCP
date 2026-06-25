<script lang="ts">
  import { app } from "../state/app.svelte.ts";
</script>

<div class="filters">
  <label class="check-row">
    <input
      type="checkbox"
      checked={app.filters.core}
      onchange={() => app.toggleFilter("core")}
    />
    <span class="dot core"></span>
    <span class="check-label">Required · core</span>
  </label>
  <label class="check-row">
    <input
      type="checkbox"
      checked={app.filters.extended}
      onchange={() => app.toggleFilter("extended")}
    />
    <span class="dot extended"></span>
    <span class="check-label">Required · extended</span>
  </label>
  <label class="check-row">
    <input
      type="checkbox"
      checked={app.filters.optional}
      onchange={() => app.toggleFilter("optional")}
    />
    <span class="dot optional"></span>
    <span class="check-label">Optional</span>
  </label>
  <label class="check-row check-row-inset">
    <input
      type="checkbox"
      checked={app.filters.onlyIncomplete}
      onchange={() => app.toggleFilter("onlyIncomplete")}
    />
    <span class="check-label">Only incomplete</span>
  </label>
</div>

<style>
  .filters {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.5rem 0.65rem 0.65rem;
  }

  .check-row {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.8rem;
    color: var(--hub-text);
    cursor: pointer;
    line-height: 1.4;
  }

  .check-row-inset {
    margin-top: 0.4rem;
    padding-top: 0.45rem;
    border-top: 1px solid var(--hub-border-light);
    color: var(--hub-text-muted);
  }

  .check-row input {
    accent-color: var(--hub-accent);
    flex-shrink: 0;
    margin: 0;
  }

  .check-label {
    font-weight: 500;
  }

  .dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    flex: none;
  }

  .dot.core {
    background: var(--hub-accent);
  }

  .dot.extended {
    background: var(--hub-warning);
  }

  .dot.optional {
    background: var(--hub-text-placeholder);
  }
</style>
