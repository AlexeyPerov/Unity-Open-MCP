<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import { describePingErrorMessage } from "./summaries.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  let pingDurationSuffix = $derived(
    state.step5PingResult ? ` (${state.step5PingResult.durationMs}ms)` : "",
  );
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    This step launches Unity with the bridge port pinned via
    <code>-UNITY_OPEN_MCP_BRIDGE_PORT={state.step5BridgePort ?? state.resolvedBridgePort ?? "auto"}</code>
    and polls the bridge HTTP <code>/ping</code> endpoint
    for up to 120 s. The wizard never spawns a separate
    <code>unity-open-mcp</code> subprocess — the wizard
    keeps the verify path to a direct HTTP GET. The
    Done screen re-runs detection on entry and pairs the
    live snapshot with this step's bridge result.
  </p>
  <ol class="wiz-checklist">
    <li class:done={state.step5Items.launch === "ok"} class:running={state.step5Items.launch === "running"}>
      Launch Unity (pid {state.step5LaunchPid ?? "—"})
      {#if state.step5Items.launch === "ok"}<span class="wiz-check-done">— ok</span>{:else if state.step5Items.launch === "running"}<span class="wiz-check-running">— launching…</span>{:else if state.step5Items.launch === "failed"}<span class="wiz-check-failed">— failed</span>{/if}
    </li>
    <li class:done={state.step5Items.compile === "ok"} class:running={state.step5Items.compile === "running"} class:failed={state.step5Items.compile === "failed"}>
      Wait for project compile
      {#if state.step5Items.compile === "ok"}<span class="wiz-check-done">— ok</span>{:else if state.step5Items.compile === "running"}<span class="wiz-check-running">— compiling…</span>{:else if state.step5Items.compile === "failed"}<span class="wiz-check-failed">— compile error</span>{/if}
    </li>
    <li class:done={state.step5Items.ping === "ok"} class:running={state.step5Items.ping === "running"} class:failed={state.step5Items.ping === "failed"}>
      Wait for bridge HTTP <code>/ping</code> (timeout 120s)
      {#if state.step5Items.ping === "ok"}
        <span class="wiz-check-done">— ok{pingDurationSuffix}</span>
      {:else if state.step5Items.ping === "running"}
        <span class="wiz-check-running">— polling…</span>
      {:else if state.step5Items.ping === "failed"}
        <span class="wiz-check-failed">— failed</span>
      {/if}
    </li>
    <li class:done={state.step5Items.confirm === "ok"} class:failed={state.step5Items.confirm === "failed"}>
      Confirm response fields (<code>connected</code>, project path, compile/play state)
      {#if state.step5Items.confirm === "ok" && state.step5BridgeStatus.kind === "ok"}
        <span class="wiz-check-done">
          — connected={state.step5BridgeStatus.connected}{state.step5BridgeStatus.projectPath ? `, project=${state.step5BridgeStatus.projectPath}` : ""}
        </span>
      {:else if state.step5Items.confirm === "failed"}
        <span class="wiz-check-failed">— {describePingErrorMessage(state.step5PingResult)}</span>
      {/if}
    </li>
  </ol>
  {#if state.step5BridgePort !== null}
    <p class="wiz-hint">
      Bridge port: <code>{state.step5BridgePort}</code>
      {#if state.step5LastTick}
        · last poll {Math.max(0, Math.round((Date.now() - state.step5LastTick) / 100) / 10)}s ago
      {/if}
    </p>
  {/if}
  {#if state.step5Error}
    <div class="wiz-block wiz-block-error" role="alert">
      {state.step5Error}
    </div>
  {/if}
  <div class="wiz-actions-row">
    {#if state.step5Items.launch !== "ok"}
      <Button variant="primary" onclick={handlers.runStep5Verify} disabled={state.step5Running}>
        {state.step5Running ? "Launching…" : "Launch Unity"}
      </Button>
    {:else}
      <Button variant="primary" onclick={handlers.runStep5Verify} disabled={state.step5Running}>
        {state.step5Running ? "Re-verifying…" : "Re-verify"}
      </Button>
    {/if}
    {#if state.step5Running}
      <Button variant="secondary" onclick={handlers.stopStep5Polling}>Stop polling</Button>
    {/if}
    <Button variant="secondary" onclick={handlers.skipStep5Verify} disabled={state.step5Running}>
      Skip to Done
    </Button>
  </div>
</section>
