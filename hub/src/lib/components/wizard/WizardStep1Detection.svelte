<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import { diagTone, splitDiagnosticsGroups } from "./diagnostics.ts";
  import { mcpConfiguredSummary } from "./diagnostics.ts";
  import WizardExpressPanel from "./WizardExpressPanel.svelte";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Preflight splits diagnostics into two ownership groups: true blockers
  // (gate Next) vs setup-status rows (handled on later steps). `status` rows
  // are labelled "Not yet" so they read as future work, not same-step failures.
  let groups = $derived(splitDiagnosticsGroups(state.diagnostics));
  let hasStatusFailures = $derived(
    groups.status.some((r) => !r.ok),
  );
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Preflight checks the environment before you continue. The
    <strong>Blocking</strong> rows below must pass to proceed; the
    <strong>Setup status</strong> rows report work handled on later steps
    (they read "Not yet" until then). Detection re-runs on every entry so
    the values reflect the on-disk manifest and
    <code>ProjectVersion.txt</code>; the Done screen re-uses this snapshot.
  </p>

  {#if state.alreadyConfigured}
    <div class="wiz-block wiz-block-ok" role="status">
      <strong>You're ready.</strong>
      The bridge, verify, and an MCP client are already configured for this
      project — you can skip ahead to verify the running bridge.
      <div class="wiz-actions-row">
        <Button variant="primary" onclick={() => handlers.jumpToStep("step5")}>
          Go to Verify
        </Button>
        <Button variant="secondary" onclick={() => handlers.jumpToStep("step3")}>
          Review packages
        </Button>
      </div>
    </div>
  {:else if state.expressActive}
    <WizardExpressPanel {state} {handlers} />
  {:else if state.expressEligible}
    <div class="wiz-block wiz-block-ok" role="status">
      <strong>Express setup available.</strong>
      Your environment checks out — set up the recommended packages, MCP
      client, and bridge verification in one pass, or continue step by step.
      <div class="wiz-actions-row">
        <Button variant="primary" onclick={handlers.enterExpress}>
          Express setup
        </Button>
        <Button variant="secondary" onclick={() => handlers.jumpToStep("step3")}>
          Step by step
        </Button>
      </div>
    </div>
  {/if}

  {#if !state.expressActive}

  <div class="wiz-diag-block">
    <div class="wiz-diag-head">
      <span class="wiz-label">Blocking — must pass to continue</span>
      <Button
        variant="secondary"
        onclick={() => { handlers.refreshDetection(); handlers.runNodeProbe(); }}
        disabled={state.detectionLoading || state.nodeProbing}
        title="Re-run project detection + Node probe"
      >
        {state.detectionLoading || state.nodeProbing ? "Checking…" : "Run diagnostics"}
      </Button>
    </div>
    <ul class="wiz-diag" aria-label="Blocking environment checks">
      {#each groups.blocking as row (row.id)}
        {@const tone = diagTone(row)}
        <li class="wiz-diag-row wiz-diag-{tone}">
          <span class="wiz-diag-icon" aria-hidden="true">
            {#if tone === "ok"}✓{:else if tone === "warn"}✗{:else}·{/if}
          </span>
          <span class="wiz-diag-label">{row.label}</span>
          {#if row.detail}<span class="wiz-diag-detail">{row.detail}</span>{/if}
          {#if !row.ok && row.remediation}
            <small class="wiz-diag-fix">{row.remediation}</small>
          {/if}
        </li>
      {/each}
    </ul>
  </div>

  <div class="wiz-diag-block">
    <div class="wiz-diag-head">
      <span class="wiz-label">Setup status — handled on later steps</span>
    </div>
    <ul class="wiz-diag" aria-label="Setup status (informational)">
      {#each groups.status as row (row.id)}
        {@const tone = diagTone(row)}
        <li class="wiz-diag-row wiz-diag-{tone}">
          <span class="wiz-diag-icon" aria-hidden="true">
            {#if tone === "ok"}✓{:else}·{/if}
          </span>
          <span class="wiz-diag-label">{row.label}</span>
          {#if row.detail}<span class="wiz-diag-detail">{row.detail}</span>{/if}
          {#if row.ok}
            <small class="wiz-diag-fix">done</small>
          {:else if row.remediation}
            <small class="wiz-diag-fix">{row.remediation}</small>
          {/if}
        </li>
      {/each}
    </ul>
    {#if hasStatusFailures}
      <p class="wiz-hint">
        These rows are <strong>not</strong> blockers — they become green as you
        complete the packages, client, and verify steps.
      </p>
    {/if}
  </div>

  {#if state.detectionLoading && !state.detection}
    <p class="wiz-hint">Detecting project…</p>
    <p class="wiz-hint wiz-hint-muted">
      You can close the wizard anytime with Cancel, Escape, or the ×
      button — detection will stop in the background.
    </p>
  {/if}
  {#if state.detectionError}
    <p class="wiz-hint wiz-hint-warn">{state.detectionError}</p>
    <div class="wiz-actions-row">
      <Button variant="secondary" onclick={handlers.refreshDetection} disabled={state.detectionLoading}>
        {state.detectionLoading ? "Re-detecting…" : "Re-detect"}
      </Button>
    </div>
  {/if}

  {#if state.detection}
    {#if !state.detection.isValidUnityProject}
      <div class="wiz-block wiz-block-error" role="alert">
        <strong>Not a valid Unity project.</strong>
        The selected path is missing
        <code>Assets/</code> or
        <code>ProjectSettings/</code>. The wizard cannot
        continue.
        <div class="wiz-actions-row">
          <Button variant="secondary" onclick={handlers.refreshDetection} disabled={state.detectionLoading}>
            {state.detectionLoading ? "Re-detecting…" : "Re-detect"}
          </Button>
        </div>
      </div>
    {:else}
      <dl class="wiz-summary">
        <div>
          <dt>Project name</dt>
          <dd>{state.detection.name || state.projectName}</dd>
        </div>
        <div>
          <dt>Path</dt>
          <dd><code title={state.detection.path}>{state.detection.path}</code></dd>
        </div>
        <div>
          <dt>Unity version</dt>
          <dd>
            {#if state.detection.unityVersion}
              <code>{state.detection.unityVersion}</code>
              {#if !state.detection.meetsMinUnityVersion}
                <span class="wiz-tag wiz-tag-warn" title="Unity 2022.3 LTS is the minimum supported version.">below minimum</span>
              {:else if state.detection.meetsRecommendedUnityVersion}
                <span class="wiz-tag wiz-tag-ok" title="Meets the recommended Unity 6+; minimum supported is 2022.3 LTS.">Unity 6+</span>
              {:else}
                <span class="wiz-tag wiz-tag-warn" title="Meets the minimum (2022.3 LTS); uses the legacy toolbar button fallback instead of the native Unity 6 toolbar element.">supported (legacy toolbar)</span>
              {/if}
            {:else}
              <em>unknown</em>
            {/if}
          </dd>
        </div>
        <div>
          <dt>Bridge installed</dt>
          <dd>
            {#if state.detection.bridgeInstalled}
              <span class="wiz-tag wiz-tag-ok">yes</span>
            {:else}
              <span class="wiz-tag wiz-tag-warn">no</span>
            {/if}
          </dd>
        </div>
        <div>
          <dt>Verify installed</dt>
          <dd>
            {#if state.detection.verifyInstalled}
              <span class="wiz-tag wiz-tag-ok">yes</span>
            {:else}
              <span class="wiz-tag wiz-tag-warn">no</span>
            {/if}
          </dd>
        </div>
        <div>
          <dt>MCP configured</dt>
          <dd>
            {mcpConfiguredSummary(state.detection.mcpConfigured)}
          </dd>
        </div>
      </dl>

      {#if !state.detection.meetsMinUnityVersion}
        <div class="wiz-block wiz-block-error" role="alert">
          <strong>Unity {state.detection.unityVersion ?? "unknown"} does not meet the minimum.</strong>
          Unity 2022.3 LTS or newer is required — the bridge + verify
          package manifests declare <code>unity: "2022.3"</code>.
        </div>
      {/if}
      {#if !state.detection.manifestWritable}
        <div class="wiz-block wiz-block-error" role="alert">
          <strong>Cannot write to <code>Packages/manifest.json</code>.</strong>
          The file (or its parent directory) is not user-writable. The
          wizard cannot install packages without write access.
        </div>
      {/if}
      {#if state.detection.hasSpacesInPath}
        <div class="wiz-block wiz-block-warn" role="status">
          <strong>Spaces in project path.</strong>
          Some MCP clients are known to mis-handle paths with spaces.
          This is a warning, not a block.
        </div>
      {/if}

      <div class="wiz-field">
        <span class="wiz-label">Node.js</span>
        {#if state.nodeProbing || state.nodeProbe === null}
          <p class="wiz-hint">Probing…</p>
        {:else if state.nodeProbe.ok}
          <p class="wiz-hint wiz-hint-ok">
            Detected <strong>Node {state.nodeProbe.version}</strong>
            (major {state.nodeProbe.major} ≥ {state.nodeProbe.requiredMajor}).
          </p>
        {:else}
          <div class="wiz-block wiz-block-error" role="alert">
            <strong>Node.js {state.nodeProbe.requiredMajor}+ is required</strong>
            to run <code>unity-open-mcp</code>.
            {#if state.nodeProbe.version}
              Detected <strong>{state.nodeProbe.version}</strong>.
            {:else}
              Not detected on PATH.
            {/if}
            {#if state.nodeProbe.error}
              <br /><small>{state.nodeProbe.error}</small>
            {/if}
          </div>
        {/if}
        <div>
          <Button variant="secondary" onclick={handlers.runNodeProbe} disabled={state.nodeProbing}>
            {state.nodeProbing ? "Checking…" : "Re-check Node"}
          </Button>
        </div>
      </div>

      <div class="wiz-actions-row">
        <Button variant="secondary" onclick={handlers.refreshDetection} disabled={state.detectionLoading}>
          {state.detectionLoading ? "Re-detecting…" : "Re-detect"}
        </Button>
        {#if state.detectToast}
          <span class="wiz-toast" role="status">{state.detectToast}</span>
        {/if}
      </div>
    {/if}
  {/if}
  {/if}
</section>
