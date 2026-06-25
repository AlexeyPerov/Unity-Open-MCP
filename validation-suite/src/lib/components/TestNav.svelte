<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StatusBadge from "./StatusBadge.svelte";
  import TierBadge from "./TierBadge.svelte";
  import FilterBar from "./FilterBar.svelte";
  import type { Scenario } from "@validation-suite/core";

  // Build the milestone groups from the filtered set so toggling a
  // filter collapses empty groups out of view. Within each milestone the
  // required scenarios render directly while optional scenarios collapse
  // into a default-closed subsection (idea.md → Coverage policy: optional
  // scenarios are runnable but de-emphasized; most show automated coverage).
  const groups = $derived.by(() => {
    const filteredIds = new Set(app.filteredScenarios.map((s) => s.id));
    return app.milestones
      .map((g) => {
        const scenarios = g.scenarios.filter((s) => filteredIds.has(s.id));
        return {
          milestone: g.milestone,
          required: scenarios.filter((s) => s.requirementLevel !== "optional"),
          optional: scenarios.filter((s) => s.requirementLevel === "optional"),
        };
      })
      .filter((g) => g.required.length > 0 || g.optional.length > 0);
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
          {#each group.required as s (s.id)}
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

        {#if group.optional.length > 0}
          <details class="optional-group">
            <summary class="optional-summary">
              <span class="optional-label">Optional</span>
              <span class="optional-count">{group.optional.length}</span>
              <span class="optional-hint">automated-covered · collapsed by default</span>
            </summary>
            <ul class="rows">
              {#each group.optional as s (s.id)}
                <li>
                  <button
                    type="button"
                    class="row row-optional"
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
          </details>
        {/if}
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

  /* Optional subsection: collapsed by default. The summary line is a
     non-interactive marker triangle + label + count; clicking the rows
     inside selects scenarios as usual. */
  .optional-group {
    margin: 0.35rem 0.5rem 0;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-bg);
  }

  .optional-summary {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.4rem 0.55rem;
    cursor: pointer;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    list-style: none;
  }

  /* Native disclosure triangle replaced with a CSS marker so the label
     reads cleanly. Keep a fallback marker for assistive tech. */
  .optional-summary::-webkit-details-marker {
    display: none;
  }

  .optional-summary::before {
    content: "▸";
    font-size: 0.7rem;
    color: var(--hub-text-placeholder);
    transition: transform 0.12s ease;
  }

  .optional-group[open] .optional-summary::before {
    transform: rotate(90deg);
  }

  .optional-label {
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--hub-text-muted);
  }

  .optional-count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.1rem;
    height: 1.1rem;
    padding: 0 0.3rem;
    border-radius: 8px;
    background: var(--hub-selected);
    color: var(--hub-text-muted);
    font-size: 0.66rem;
    font-weight: 600;
  }

  .optional-hint {
    font-size: 0.66rem;
    color: var(--hub-text-placeholder);
    margin-left: auto;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .optional-group[open] {
    padding-bottom: 0.2rem;
  }

  .optional-group .rows {
    padding: 0.15rem 0.3rem 0;
  }

  .row-optional {
    padding: 0.4rem 0.5rem;
  }

  .row-optional .row-title {
    font-size: 0.78rem;
  }
</style>
