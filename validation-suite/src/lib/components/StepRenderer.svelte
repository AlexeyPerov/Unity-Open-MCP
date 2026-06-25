<script lang="ts">
  import { app } from "../state/app.svelte.ts";
  import StatusBadge from "./StatusBadge.svelte";
  import type { Scenario, ScenarioStep, Status } from "@validation-suite/core";

  let {
    scenario,
    step,
  }: { scenario: Scenario; step: ScenarioStep } = $props();

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
      setTimeout(() => (copied = false), 1400);
    }
  }

  function setStatus(s: Status) {
    void app.setStep(scenario, step.id, s);
  }

  // agent_prompt payload rendered as readable JSON for copy + display.
  const promptText = $derived.by(() => {
    if (step.type !== "agent_prompt") return "";
    const lines: string[] = [];
    if (step.tool) lines.push(`Tool: ${step.tool}`);
    if (step.payload !== undefined) {
      lines.push("Payload:");
      lines.push(JSON.stringify(step.payload, null, 2));
    }
    return lines.join("\n");
  });
</script>

<article class="step {status}">
  <header>
    <span class="type">{step.type}</span>
    {#if step.title}<h4>{step.title}</h4>{/if}
    <span class="status"><StatusBadge status={status} /></span>
  </header>

  <div class="body">
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
      <p class="prose dim">
        Setup runs <strong>{step.actions?.length ?? 0}</strong> action(s):
        {step.actions?.map((a) => a.action).join(", ")}. Execution lands in Phase 2;
        for now, stage the fixture manually and mark the step done.
      </p>
      <ol class="actions">
        {#each step.actions ?? [] as action, i}
          <li>
            <code>{action.action}</code>
            <pre>{JSON.stringify(action, null, 2)}</pre>
          </li>
        {/each}
      </ol>
    {:else if step.type === "agent_prompt"}
      {#if step.tool}
        <p class="prose">Run this in your MCP client:</p>
        <pre class="prompt">{promptText}</pre>
        <button class="copy" onclick={() => copy(promptText)}>
          {copied ? "Copied" : "Copy prompt"}
        </button>
      {/if}
    {:else if step.type === "actual"}
      <p class="prose dim">
        Paste the agent's output here. Payloads persist to
        <code>{scenario.id}-{step.id}.json</code> under the project's
        <code>actuals/</code> dir (Phase 2 wires the save; for now keep
        the paste in your notes).
      </p>
      <textarea placeholder="Paste actual output…" rows="4"></textarea>
    {:else if step.type === "external_doc"}
      <p class="prose">Open and review: <code>{step.docPath}</code></p>
      <button class="copy" onclick={() => window.open(`file://${step.docPath}`, "_blank")}>
        Open doc
      </button>
    {:else if step.type === "mark_done"}
      <p class="prose dim">Confirm this test is complete.</p>
    {/if}
  </div>

  <footer>
    {#if status === "done"}
      <button class="ghost" onclick={() => setStatus("awaiting")}>Mark awaiting</button>
    {:else}
      <button class="ok" onclick={() => setStatus("done")}>Mark done</button>
      {#if status !== "blocked"}
        <button class="ghost" onclick={() => setStatus("blocked")}>Mark blocked</button>
      {/if}
    {/if}
  </footer>
</article>

<style>
  .step {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-elev);
    overflow: hidden;
  }
  .step.done {
    border-color: var(--border);
  }
  .step.done .body {
    opacity: 0.7;
  }
  header {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 9px 14px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-elev-2);
  }
  .type {
    font-family: var(--mono);
    font-size: 11px;
    color: var(--accent);
    text-transform: lowercase;
  }
  h4 {
    margin: 0;
    flex: 1;
    font-size: 13.5px;
    font-weight: 600;
  }
  .status {
    flex: none;
  }
  .body {
    padding: 12px 14px;
  }
  .prose {
    margin: 0 0 8px;
  }
  .prose.dim {
    color: var(--text-dim);
  }
  .items {
    margin: 6px 0 0;
    padding-left: 18px;
  }
  .items li {
    margin-bottom: 4px;
  }
  .actions {
    margin: 8px 0 0;
    padding-left: 18px;
  }
  .actions li {
    margin-bottom: 10px;
  }
  code {
    font-family: var(--mono);
    font-size: 12px;
    color: var(--accent);
    background: var(--bg-elev-2);
    padding: 1px 5px;
    border-radius: 4px;
  }
  pre {
    margin: 6px 0 0;
    padding: 10px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    font-family: var(--mono);
    font-size: 12px;
    overflow-x: auto;
    color: var(--text-dim);
    white-space: pre-wrap;
  }
  .prompt {
    color: var(--text);
  }
  textarea {
    width: 100%;
    resize: vertical;
    background: var(--bg);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    padding: 8px;
    font-family: var(--mono);
    font-size: 12px;
  }
  footer {
    display: flex;
    gap: 8px;
    padding: 9px 14px;
    border-top: 1px solid var(--border);
    background: var(--bg-elev-2);
  }
  .ok {
    background: var(--ok);
    color: #04130a;
    border: none;
    padding: 6px 12px;
    border-radius: var(--radius-sm);
    font-weight: 600;
  }
  .ghost {
    background: transparent;
    color: var(--text-dim);
    border: 1px solid var(--border-strong);
    padding: 6px 12px;
    border-radius: var(--radius-sm);
  }
  .ghost:hover {
    border-color: var(--text-dim);
  }
  .copy {
    margin-top: 6px;
    background: var(--bg-elev-2);
    color: var(--text);
    border: 1px solid var(--border-strong);
    padding: 6px 12px;
    border-radius: var(--radius-sm);
  }
</style>
