<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StepRenderer from "./StepRenderer.svelte";
  import StatusBadge from "./StatusBadge.svelte";
  import TierBadge from "./TierBadge.svelte";
  import Button from "./shell/Button.svelte";
</script>

<section class="runner">
  {#if app.warning}
    <div class="banner banner-warn" role="alert">
      <div class="banner-head">
        <span class="banner-label">⚠ {app.warning.title}</span>
        <button
          type="button"
          class="banner-close"
          title="Dismiss"
          aria-label="Dismiss warning"
          onclick={() => app.dismissWarning()}
        >×</button>
      </div>
      <p class="banner-body">{app.warning.body}</p>
      <div class="banner-actions">
        <Button variant="destructive" onclick={() => app.resetAll()} disabled={app.busy}>
          Reset local data
        </Button>
      </div>
    </div>
  {/if}

  {#if app.readErrors.length > 0}
    <div class="banner banner-error" role="alert">
      <div class="banner-head">
        <span class="banner-label">Some scenario files could not be read</span>
        <button
          type="button"
          class="banner-close"
          title="Dismiss"
          aria-label="Dismiss read errors"
          onclick={() => app.dismissReadErrors()}
        >×</button>
      </div>
      <ul class="banner-list">
        {#each app.readErrors as err}
          <li><code>{err.source}</code> — {err.message}</li>
        {/each}
      </ul>
    </div>
  {/if}

  {#if app.loadErrors.length > 0}
    <div class="banner banner-warn">
      <div class="banner-head">
        <span class="banner-label">Some scenario files failed validation and were skipped</span>
        <button
          type="button"
          class="banner-close"
          title="Dismiss"
          aria-label="Dismiss validation errors"
          onclick={() => app.dismissLoadErrors()}
        >×</button>
      </div>
      <ul class="banner-list">
        {#each app.loadErrors as err}
          <li><code>{err.source}</code> — {err.message}</li>
        {/each}
      </ul>
    </div>
  {/if}

  {#if app.selected}
    {@const scenario = app.selected}
    <header class="head">
      <div class="titles">
        <h1>{scenario.title}</h1>
        <span class="id">{scenario.id}</span>
      </div>
      <div class="headmeta">
        <TierBadge level={scenario.requirementLevel} automated={(scenario.automatedCoverage?.length ?? 0) > 0} />
        <StatusBadge status={app.statusOf(scenario.id)} />
        <Button variant="secondary" onclick={() => app.resetTest(scenario)}>Reset test</Button>
      </div>
    </header>

    <div class="steps">
      {#each scenario.steps as step (step.id)}
        <StepRenderer {scenario} {step} ctx={app.expandContext} />
      {/each}
    </div>
  {:else if app.scenarios.length === 0 && app.activeProject}
    <div class="placeholder">
      No scenarios found for <code>{app.profile?.displayName ?? "this engine"}</code>. Drop scenario
      JSON under <code>scenarios/{app.profile?.id ?? "engine"}/</code>.
    </div>
  {:else if app.activeProject}
    <div class="placeholder">Select a scenario from the left.</div>
  {/if}
</section>

<style>
  .runner {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
    overflow-y: auto;
    padding: 0.25rem 0.5rem 1.5rem 0.25rem;
    gap: 0.75rem;
  }

  .head {
    flex-shrink: 0;
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.25rem 0.5rem;
  }

  .titles {
    min-width: 0;
  }

  h1 {
    margin: 0;
    font-size: 1.1rem;
    font-weight: 600;
    color: var(--hub-text-bright);
  }

  .id {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text-placeholder);
  }

  .headmeta {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    flex: none;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  .steps {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    padding: 0 0.5rem;
    max-width: 56rem;
  }

  .banner {
    flex-shrink: 0;
    margin: 0 0.5rem;
    padding: 0.6rem 0.8rem;
    border-radius: 6px;
    font-size: 0.82rem;
    line-height: 1.5;
  }

  .banner-warn {
    border: 1px solid var(--hub-warn-fg);
    background: var(--hub-warn-bg);
    color: var(--hub-text);
  }

  .banner-error {
    border: 1px solid var(--hub-error-fg);
    background: var(--hub-error-bg);
    color: var(--hub-text);
  }

  .banner-label {
    display: block;
    font-weight: 600;
    color: var(--hub-warn-fg);
    margin-bottom: 0.25rem;
  }

  .banner-error .banner-label {
    color: var(--hub-error-fg);
  }

  /* Header row: label on the left, dismiss × on the right. The × is a
     low-affordance dismiss (it only hides the banner locally); destructive
     actions like Reset local data stay as explicit buttons. */
  .banner-head {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .banner-head .banner-label {
    margin-bottom: 0;
  }

  .banner-close {
    flex-shrink: 0;
    width: 1.4rem;
    height: 1.4rem;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--hub-border-light);
    border-radius: 4px;
    background: transparent;
    color: var(--hub-text-muted);
    font-size: 1rem;
    line-height: 1;
    padding: 0;
    cursor: pointer;
    transition: background 0.1s ease, color 0.1s ease;
  }

  .banner-close:hover {
    background: var(--hub-bg);
    color: var(--hub-text-bright);
  }

  .banner-close:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .banner-body {
    margin: 0 0 0.5rem;
  }

  .banner-list {
    margin: 0.35rem 0 0;
    padding-left: 1.1rem;
  }

  .banner-list li {
    margin-bottom: 0.2rem;
  }

  .banner code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: rgba(0, 0, 0, 0.25);
    padding: 0 0.3rem;
    border-radius: 3px;
  }

  .banner-actions {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.5rem;
  }

  .placeholder {
    color: var(--hub-text-placeholder);
    font-size: 0.88rem;
    padding: 2rem 0.5rem;
    margin: auto;
  }

  .placeholder code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
  }
</style>
