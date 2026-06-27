<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import { logs } from "../state/logs.svelte";
  import ActionLog from "./ActionLog.svelte";
  import StatusBadge from "./StatusBadge.svelte";
  import Button from "./shell/Button.svelte";
  import {
    expandValue,
    type ExpandContext,
    type Scenario,
    type ScenarioStep,
    type SetupAction,
    type Status,
  } from "@validation-suite/core";

  let {
    scenario,
    step,
    ctx = null,
  }: { scenario: Scenario; step: ScenarioStep; ctx?: ExpandContext | null } = $props();

  /** Collapse state — all steps expanded by default. Pure UI, not persisted. */
  let open = $state(true);

  // Step status is read from the suite state; defaults to awaiting.
  const status = $derived(
    (app.suite?.tests[scenario.id]?.stepStatus[step.id] as Status | undefined) ?? "awaiting",
  );

  // A copy "done" affordance per copyable step; resets when the step
  // status changes. Pure UI feedback, not persisted.
  let copied = $state(false);
  $effect(() => {
    status; // re-run when status changes
    copied = false;
  });

  async function copy(text: string) {
    const ok = await app.copy(text);
    if (ok) {
      copied = true;
      logs.log(`copied prompt for ${scenario.id} › ${step.id}`);
      setTimeout(() => (copied = false), 1400);
    }
  }

  function setStatus(s: Status) {
    void app.setStep(scenario, step.id, s);
  }

  // Phase 2: setup steps are run through the action executor; their
  // status is set by run/reset rather than the manual mark-done controls.
  const isSetup = $derived(step.type === "setup");
  async function runSetup() {
    await app.runStep(scenario, step.id);
  }
  async function resetSetup() {
    await app.resetStep(scenario, step.id);
  }

  // agent_prompt payload rendered as readable JSON for copy + display.
  // Placeholders (`{fixtureRoot}` / `{projectRoot}`) are expanded against
  // the resolved context when available, so the operator sees + copies the
  // real paths. Falls back to raw tokens before the context resolves.
  const promptText = $derived.by(() => {
    if (step.type !== "agent_prompt") return "";
    const lines: string[] = [];
    if (step.tool) lines.push(`Tool: ${step.tool}`);
    if (step.payload !== undefined) {
      lines.push("Payload:");
      lines.push(JSON.stringify(ctx ? expandValue(step.payload, ctx) : step.payload, null, 2));
    }
    return lines.join("\n");
  });

  /** Expand a setup action's placeholders for display (raw when no ctx). */
  function expandAction(action: SetupAction): unknown {
    return ctx ? expandValue(action, ctx) : action;
  }
</script>

<article class="step" class:step-done={status === "done"}>
  <button
    type="button"
    class="step-head"
    class:step-head-collapsed={!open}
    aria-expanded={open}
    onclick={() => (open = !open)}
  >
    <span class="step-chevron" class:step-chevron-open={open} aria-hidden="true">▸</span>
    <span class="step-type">{step.type}</span>
    {#if step.title}<span class="step-title">{step.title}</span>{/if}
    <span class="step-status"><StatusBadge status={status} /></span>
  </button>

  {#if open}
    <div class="step-body">
      {#if step.type === "info" || step.type === "expected"}
        {#if step.body}
          <p class="prose">{step.body}</p>
        {/if}
        {#if step.items?.length}
          <ul class="items">
            {#each step.items as item}
              <li>{item}</li>
            {/each}
          </ul>
        {/if}
      {:else if step.type === "setup"}
        <p class="prose muted">
          Runs <strong>{step.actions?.length ?? 0}</strong> setup action(s):
          {step.actions?.map((a) => a.action).join(", ")}.
        </p>
        <ol class="actions">
          {#each step.actions ?? [] as action}
            <li>
              <code class="verb">{action.action}</code>
              <pre>{JSON.stringify(expandAction(action), null, 2)}</pre>
            </li>
          {/each}
        </ol>
        <ActionLog scenarioId={scenario.id} stepId={step.id} />
      {:else if step.type === "agent_prompt"}
        {#if step.tool}
          <p class="prose">Run this in your MCP client:</p>
          <pre class="prompt">{promptText}</pre>
          <Button variant="secondary" onclick={() => copy(promptText)}>
            {copied ? "Copied ✓" : "Copy prompt"}
          </Button>
        {/if}
      {:else if step.type === "actual"}
        <p class="prose muted">
          Paste the agent's output here. Payloads persist to
          <code>{scenario.id}-{step.id}.json</code> under the project's
          <code>actuals/</code> dir (Phase 2 wires the save; for now keep the paste in your notes).
        </p>
        <textarea placeholder="Paste actual output…" rows="4"></textarea>
      {:else if step.type === "external_doc"}
        <p class="prose">Open and review: <code>{step.docPath}</code></p>
        <Button
          variant="secondary"
          onclick={() => window.open(`file://${step.docPath}`, "_blank")}
        >
          Open doc
        </Button>
      {:else if step.type === "mark_done"}
        <p class="prose muted">Confirm this test is complete.</p>
      {/if}
    </div>

    <footer class="step-foot">
      {#if isSetup}
        {#if status === "done"}
          <Button variant="secondary" onclick={resetSetup} disabled={app.busy}>Re-run setup</Button>
          <Button variant="secondary" onclick={() => setStatus("awaiting")} disabled={app.busy}>
            Clear status
          </Button>
        {:else}
          <Button variant="primary" onclick={runSetup} disabled={app.busy}>Run setup</Button>
          {#if status !== "blocked"}
            <Button variant="secondary" onclick={() => setStatus("blocked")} disabled={app.busy}>
              Mark blocked
            </Button>
          {/if}
        {/if}
      {:else if status === "done"}
        <Button variant="secondary" onclick={() => setStatus("awaiting")}>Mark awaiting</Button>
      {:else}
        <Button variant="primary" onclick={() => setStatus("done")}>Mark done</Button>
        {#if status !== "blocked"}
          <Button variant="secondary" onclick={() => setStatus("blocked")}>Mark blocked</Button>
        {/if}
      {/if}
    </footer>
  {/if}
</article>

<style>
  .step {
    border: 1px solid var(--hub-border);
    border-radius: 8px;
    background: var(--hub-bg);
    overflow: hidden;
  }

  .step-done {
    border-color: var(--hub-success);
  }

  .step-done .step-body {
    opacity: 0.7;
  }

  .step-head {
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.6rem;
    padding: 0.6rem 0.85rem;
    border: none;
    border-bottom: 1px solid var(--hub-card);
    background: var(--hub-surface);
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    transition: background 0.1s ease;
  }

  .step-head:hover {
    background: var(--hub-card);
  }

  .step-head:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: -2px;
  }

  /* Collapsed head is a clean single row — drop the divider. */
  .step-head-collapsed {
    border-bottom: none;
  }

  .step-chevron {
    display: inline-block;
    flex-shrink: 0;
    width: 0.9rem;
    color: var(--hub-text-muted);
    font-size: 0.7rem;
    transition: transform 0.15s ease;
  }

  .step-chevron-open {
    transform: rotate(90deg);
  }

  .step-type {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    color: var(--hub-accent);
    background: var(--hub-bg);
    padding: 0.1rem 0.4rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-light);
    flex-shrink: 0;
  }

  .step-title {
    flex: 1;
    min-width: 0;
    font-size: 0.85rem;
    font-weight: 600;
    color: var(--hub-text-bright);
  }

  .step-status {
    flex: none;
  }

  .step-body {
    padding: 0.7rem 0.95rem 0.85rem;
  }

  .prose {
    margin: 0 0 0.5rem;
    line-height: 1.5;
  }

  .prose.muted {
    color: var(--hub-text-muted);
  }

  .prose code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    background: var(--hub-surface);
    padding: 0 0.3rem;
    border-radius: 3px;
    color: var(--hub-text);
  }

  .items {
    margin: 0.4rem 0 0;
    padding-left: 1.1rem;
  }

  .items li {
    margin-bottom: 0.3rem;
  }

  .actions {
    margin: 0.5rem 0 0;
    padding-left: 1.1rem;
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }

  .actions li {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .verb {
    align-self: flex-start;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-accent);
    background: var(--hub-surface);
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
    border: 1px solid var(--hub-border-light);
  }

  pre {
    margin: 0;
    padding: 0.55rem 0.7rem;
    background: var(--hub-surface);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    line-height: 1.45;
    overflow-x: auto;
    color: var(--hub-text-dim);
    white-space: pre-wrap;
  }

  .prompt {
    color: var(--hub-text);
    margin-bottom: 0.5rem;
  }

  textarea {
    width: 100%;
    resize: vertical;
    background: var(--hub-surface);
    color: var(--hub-text);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    padding: 0.5rem 0.65rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    line-height: 1.45;
  }

  textarea:focus {
    outline: none;
    border-color: var(--hub-accent);
  }

  .step-foot {
    display: flex;
    gap: 0.5rem;
    padding: 0.55rem 0.95rem;
    border-top: 1px solid var(--hub-card);
    background: var(--hub-surface);
  }
</style>
