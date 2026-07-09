<script lang="ts">
  /**
   * Express setup panel (Plan 2 T28.2.4).
   *
   * Shown on the Preflight step for the Recommended path after a green
   * environment check. Offers a single confirm screen — pick a client (or keep
   * the last-used), then **Set up** runs packages → MCP write → launch/verify
   * with a live progress list. The full multi-step wizard stays available via
   * the Customize / Custom presets and the progress strip.
   */
  import Button from "$lib/components/shell/Button.svelte";
  import {
    CLIENT_CATEGORY_LABELS,
    MCP_CLIENT_OPTIONS,
  } from "./constants.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Phase labels for the live progress list.
  const PHASE_LABELS: Record<typeof state.expressPhase, string> = {
    idle: "Ready",
    packages: "Installing Unity packages",
    mcp: "Writing MCP client config",
    launch: "Launching Unity and verifying bridge",
    done: "Setup complete",
    error: "Setup failed",
  };

  let phaseList = $derived([
    { id: "packages", label: PHASE_LABELS.packages },
    { id: "mcp", label: PHASE_LABELS.mcp },
    { id: "launch", label: PHASE_LABELS.launch },
  ] as const);

  function phaseState(
    phaseId: string,
  ): "pending" | "running" | "done" | "failed" {
    if (state.expressPhase === "error") {
      if (phaseId === currentPhaseId()) return "failed";
      if (isBefore(phaseId, currentPhaseId())) return "done";
      return "pending";
    }
    if (state.expressPhase === "done") return "done";
    if (phaseId === currentPhaseId()) return "running";
    if (isBefore(phaseId, currentPhaseId())) return "done";
    return "pending";
  }

  function currentPhaseId(): string {
    return state.expressPhase;
  }

  const ORDER = ["idle", "packages", "mcp", "launch", "done", "error"];

  function isBefore(a: string, b: string): boolean {
    return ORDER.indexOf(a) < ORDER.indexOf(b);
  }
</script>

<div class="wiz-express">
  <div class="wiz-express-head">
    <span class="wiz-eyebrow">Express setup</span>
    <h3 class="wiz-express-title">Set up in one click</h3>
    <p class="wiz-desc">
      Picks the recommended packages, writes the MCP client config, and
      launches Unity to verify the bridge — all in one pass. Want control over
      each step? Choose <strong>Customize</strong> on the preset step, or use
      the progress strip to jump to any step.
    </p>
  </div>

  {#if state.expressPhase === "idle"}
    <div class="wiz-field">
      <span class="wiz-label">MCP client</span>
      <p class="wiz-hint">
        Pick the AI client you use — the express path writes its MCP config.
        Default: <strong>{state.mcpClient}</strong> (change on the Configure AI
        client step later if needed).
      </p>
      <select
        class="wiz-input"
        value={state.mcpClient}
        onchange={(e) =>
          handlers.setMcpClient((e.currentTarget as HTMLSelectElement).value as never)}
      >
        {#each Object.keys(CLIENT_CATEGORY_LABELS) as cat}
          <optgroup label={CLIENT_CATEGORY_LABELS[cat as keyof typeof CLIENT_CATEGORY_LABELS]}>
            {#each MCP_CLIENT_OPTIONS.filter((o) => o.category === cat) as opt (opt.id)}
              <option value={opt.id}>{opt.label}</option>
            {/each}
          </optgroup>
        {/each}
      </select>
    </div>

    {#if state.expressError}
      <div class="wiz-block wiz-block-error" role="alert">{state.expressError}</div>
    {/if}

    <div class="wiz-actions-row">
      <Button
        variant="primary"
        onclick={handlers.runExpressSetup}
        disabled={state.expressRunning}
      >
        {state.expressRunning ? "Setting up…" : "Set up"}
      </Button>
      <Button variant="secondary" onclick={handlers.exitExpress} disabled={state.expressRunning}>
        Back to Preflight
      </Button>
    </div>
  {:else}
    <ol class="wiz-checklist">
      {#each phaseList as phase (phase.id)}
        {@const ps = phaseState(phase.id)}
        <li class:done={ps === "done"} class:running={ps === "running"} class:failed={ps === "failed"}>
          {phase.label}
          {#if ps === "done"}<span class="wiz-check-done">— ok</span>
          {:else if ps === "running"}<span class="wiz-check-running">— running…</span>
          {:else if ps === "failed"}<span class="wiz-check-failed">— failed</span>
          {/if}
        </li>
      {/each}
    </ol>

    {#if state.expressError}
      <div class="wiz-block wiz-block-error" role="alert">{state.expressError}</div>
    {/if}

    {#if state.expressPhase === "error"}
      <div class="wiz-actions-row">
        <Button variant="secondary" onclick={handlers.exitExpress}>
          Back to Preflight
        </Button>
      </div>
    {/if}
  {/if}
</div>
