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

<aside class="nav">
  <FilterBar />

  {#if groups.length === 0}
    <div class="empty">No scenarios match the current filters.</div>
  {/if}

  {#each groups as group (group.milestone)}
    <section class="group">
      <h2>{group.milestone}</h2>
      <ul>
        {#each group.scenarios as s (s.id)}
          <li>
            <button
              class="row"
              class:active={app.selectedScenarioId === s.id}
              onclick={() => app.select(s.id)}
            >
              <span class="title">{s.title}</span>
              <span class="meta">
                <TierBadge level={s.requirementLevel} automated={(s.automatedCoverage?.length ?? 0) > 0} />
                <StatusBadge status={statusOf(s)} />
              </span>
              <span class="id">{s.id}</span>
            </button>
          </li>
        {/each}
      </ul>
    </section>
  {/each}
</aside>

<style>
  .nav {
    width: 340px;
    flex: none;
    border-right: 1px solid var(--border);
    overflow-y: auto;
    background: var(--bg);
  }
  .empty {
    padding: 20px 14px;
    color: var(--text-faint);
    font-size: 13px;
  }
  .group {
    padding: 6px 0 12px;
  }
  h2 {
    margin: 10px 14px 6px;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-faint);
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .row {
    display: block;
    width: 100%;
    text-align: left;
    padding: 8px 14px;
    border: none;
    background: transparent;
    color: var(--text);
    border-left: 2px solid transparent;
  }
  .row:hover {
    background: var(--bg-elev);
  }
  .row.active {
    background: var(--bg-elev);
    border-left-color: var(--accent);
  }
  .title {
    display: block;
    font-size: 13px;
    font-weight: 500;
  }
  .meta {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    margin-top: 5px;
  }
  .id {
    display: block;
    margin-top: 4px;
    font-family: var(--mono);
    font-size: 11px;
    color: var(--text-faint);
  }
</style>
