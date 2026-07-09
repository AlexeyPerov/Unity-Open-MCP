<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import { diagTone } from "./diagnostics.ts";
  import { mcpConfiguredSummary } from "./diagnostics.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Step 2 detects the current state of the project and runs a
    diagnostics pass. The detection is re-run on every entry so the
    values below always reflect the on-disk manifest and
    <code>ProjectVersion.txt</code>; the Done screen
    re-uses the same snapshot.
  </p>

  <div class="wiz-diag-block">
    <div class="wiz-diag-head">
      <span class="wiz-label">Diagnostics</span>
      <Button
        variant="secondary"
        onclick={() => { handlers.refreshDetection(); handlers.runNodeProbe(); }}
        disabled={state.detectionLoading || state.nodeProbing}
        title="Re-run project detection + Node probe"
      >
        {state.detectionLoading || state.nodeProbing ? "Checking…" : "Run diagnostics"}
      </Button>
    </div>
    <ul class="wiz-diag" aria-label="Project diagnostics">
      {#each state.diagnostics as row (row.id)}
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
</section>
