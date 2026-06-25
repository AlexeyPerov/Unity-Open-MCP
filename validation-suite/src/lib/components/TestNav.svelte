<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StatusBadge from "./StatusBadge.svelte";
  import TierBadge from "./TierBadge.svelte";
  import FilterBar from "./FilterBar.svelte";
  import type { Scenario } from "@validation-suite/core";

  // Build the milestone groups from the filtered set so toggling a
  // filter collapses empty groups out of view.
  const groups = $derived.by(() => {
    const filteredIds = new Set(app.filteredScenarios.map((s) => s.id));
    return app.milestones
      .map((g) => ({
        milestone: g.milestone,
        scenarios: g.scenarios.filter((s) => filteredIds.has(s.id)),
      }))
      .filter((g) => g.scenarios.length > 0);
  });

  function statusOf(s: Scenario) {
    return app.statusOf(s.id);
  }
</script>

<aside class="sidebar" aria-label="Scenario navigation">
  <section class="filters-section" aria-label="Filters">
    <h3 class="section-title">Filters</h3>
    <FilterBar />
  </section>

  <div class="list-scroll">
    {#if groups.length === 0}
      <p class="empty">No scenarios match the current filters.</p>
    {/if}

    {#each groups as group (group.milestone)}
      <section class="group">
        <h3 class="group-heading">{group.milestone}</h3>
        <ul class="rows">
          {#each group.scenarios as s (s.id)}
            <li>
              <button
                type="button"
                class="row"
                class:row-active={app.selectedScenarioId === s.id}
                onclick={() => app.select(s.id)}
              >
                <span class="row-title">{s.title}</span>
                <span class="row-meta">
                  <TierBadge level={s.requirementLevel} automated={(s.automatedCoverage?.length ?? 0) > 0} />
                  <StatusBadge status={statusOf(s)} />
                </span>
                <span class="row-id">{s.id}</span>
              </button>
            </li>
          {/each}
        </ul>
      </section>
    {/each}
  </div>
</aside>

<style>
  .sidebar {
    flex-shrink: 0;
    width: 17rem;
    display: flex;
    flex-direction: column;
    min-height: 0;
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-surface);
    overflow: hidden;
  }

  .filters-section {
    flex-shrink: 0;
    border-bottom: 1px solid var(--hub-border);
    background: var(--hub-bg);
  }

  .section-title {
    margin: 0;
    padding: 0.5rem 0.85rem 0;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
  }

  .list-scroll {
    flex: 1;
    overflow-y: auto;
    padding: 0.4rem;
  }

  .empty {
    padding: 1rem 0.85rem;
    color: var(--hub-text-placeholder);
    font-size: 0.82rem;
    margin: 0;
  }

  .group {
    margin-bottom: 0.6rem;
  }

  .group-heading {
    margin: 0.6rem 0.5rem 0.3rem;
    font-size: 0.7rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--hub-text-placeholder);
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .row {
    display: block;
    width: 100%;
    text-align: left;
    padding: 0.5rem 0.65rem;
    border: 1px solid transparent;
    border-radius: 6px;
    background: transparent;
    color: var(--hub-text);
  }

  .row:hover {
    color: var(--hub-text-bright);
    background: var(--hub-bg);
  }

  .row:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .row-active {
    color: var(--hub-text-bright);
    border-color: var(--hub-accent);
    background: rgba(92, 124, 250, 0.12);
  }

  .row-title {
    display: block;
    font-size: 0.82rem;
    font-weight: 500;
    line-height: 1.3;
  }

  .row-meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.3rem;
    margin-top: 0.35rem;
  }

  .row-id {
    display: block;
    margin-top: 0.3rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.68rem;
    color: var(--hub-text-placeholder);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
