<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StepRenderer from "./StepRenderer.svelte";
  import StatusBadge from "./StatusBadge.svelte";
  import TierBadge from "./TierBadge.svelte";
</script>

<section class="runner">
  {#if app.warning}
    <div class="banner warn">
      <strong>{app.warning.title}.</strong> {app.warning.body}
      <button class="reset" onclick={() => app.resetAll()} disabled={app.busy}>
        Reset local data
      </button>
    </div>
  {/if}

  {#if app.readErrors.length > 0}
    <div class="banner bad">
      <strong>Some scenario files could not be read:</strong>
      <ul>
        {#each app.readErrors as err}
          <li><code>{err.source}</code> — {err.message}</li>
        {/each}
      </ul>
    </div>
  {/if}

  {#if app.loadErrors.length > 0}
    <div class="banner warn">
      <strong>Some scenario files failed validation and were skipped:</strong>
      <ul>
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
        <button class="reset-test" onclick={() => app.resetTest(scenario)}>Reset test</button>
      </div>
    </header>

    <div class="steps">
      {#each scenario.steps as step (step.id)}
        <StepRenderer {scenario} {step} />
      {/each}
    </div>
  {:else if app.scenarios.length === 0 && app.activeProject}
    <div class="placeholder">
      No scenarios found for <code>{app.profile?.displayName ?? "this engine"}</code>.
      Drop scenario JSON under <code>scenarios/{app.profile?.id ?? "engine"}/</code>.
    </div>
  {:else}
    <div class="placeholder">Select a scenario from the left.</div>
  {/if}
</section>

<style>
  .runner {
    flex: 1;
    overflow-y: auto;
    padding: 20px 24px 40px;
  }
  .head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 18px;
  }
  .titles {
    min-width: 0;
  }
  h1 {
    margin: 0;
    font-size: 19px;
    font-weight: 600;
  }
  .id {
    font-family: var(--mono);
    font-size: 12px;
    color: var(--text-faint);
  }
  .headmeta {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: none;
  }
  .reset-test {
    background: transparent;
    color: var(--text-dim);
    border: 1px solid var(--border-strong);
    padding: 5px 10px;
    border-radius: var(--radius-sm);
    font-size: 12px;
  }
  .steps {
    display: flex;
    flex-direction: column;
    gap: 12px;
    max-width: 860px;
  }
  .banner {
    margin-bottom: 16px;
    padding: 12px 14px;
    border-radius: var(--radius);
    font-size: 13px;
    line-height: 1.5;
  }
  .banner.warn {
    background: var(--warn-soft);
    border: 1px solid var(--warn);
    color: var(--text);
  }
  .banner.bad {
    background: var(--bad-soft);
    border: 1px solid var(--bad);
    color: var(--text);
  }
  .banner ul {
    margin: 6px 0 0;
    padding-left: 18px;
  }
  .banner li {
    margin-bottom: 3px;
  }
  .banner code {
    font-family: var(--mono);
    font-size: 12px;
    background: rgba(0, 0, 0, 0.25);
    padding: 1px 5px;
    border-radius: 4px;
  }
  .reset {
    margin-left: 10px;
    background: var(--warn);
    color: #1a1404;
    border: none;
    padding: 5px 11px;
    border-radius: var(--radius-sm);
    font-weight: 600;
  }
  .placeholder {
    color: var(--text-faint);
    font-size: 14px;
    padding: 40px 0;
  }
  .placeholder code {
    font-family: var(--mono);
    font-size: 12px;
  }
</style>
