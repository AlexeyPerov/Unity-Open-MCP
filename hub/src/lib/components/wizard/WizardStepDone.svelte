<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import { mcpConfiguredSummary } from "./diagnostics.ts";
  import {
    bridgeSummary,
    launchSummary,
    mcpSummary,
    packagesSummary,
  } from "./summaries.ts";
  import {
    describeGenerateSkillError,
    describeSkillCopyError,
  } from "./error_descriptors.ts";
  import { summarizeChanges } from "$lib/services/manifest";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  const EMPTY_HEURISTIC = {
    cursor: false,
    claudeDesktop: false,
    opencodeGlobal: false,
    opencodeProject: false,
    zcodeGlobal: false,
    zcodeProject: false,
  };

  // Precompute the Done-screen summaries (pure given the state bag).
  let mcp = $derived(
    mcpSummary({
      detection: state.detection,
      mcpWritten: Boolean(state.mcpWriteResult?.wouldWrite),
      mcpClient: state.mcpClient,
    }),
  );
  let launch = $derived(
    launchSummary({
      launch: state.step5Items.launch,
      ping: state.step5Items.ping,
      bridgeStatus: state.step5BridgeStatus,
      launchPid: state.step5LaunchPid,
    }),
  );
  let br = $derived(bridgeSummary(state.step5BridgeStatus));
</script>

<section class="wiz-section">
  <div class="wiz-block wiz-block-ok wiz-done-banner" role="status">
    <strong>Setup complete!</strong>
    The Unity AI agent is installed and configured. You can now
    use MCP tools from your AI client to inspect and drive the
    Unity Editor. Examples you can try:
    <ul class="wiz-done-examples">
      <li><code>"List all scenes in the project"</code></li>
      <li><code>"What is the current build target?"</code></li>
      <li><code>"Run a health check on the project"</code></li>
      <li><code>"Show me all installed packages"</code></li>
    </ul>
  </div>

  <p class="wiz-desc">
    The checklist below is computed from
    the live project state. Wizard form choices are saved per
    project, but step navigation always restarts at the preset
    picker when you reopen the wizard. Re-run or Clear AI setup
    resets those saved choices. The Done screen always reflects the
    latest on-disk manifest. The
    bridge <code>/ping</code> row carries the launch + verify
    result; the live detection snapshot below it shows
    the freshest manifest / MCP heuristic the wizard
    could read.
  </p>
  <dl class="wiz-summary">
    <div>
      <dt>Project</dt>
      <dd>
        {state.detection?.name ?? state.projectName} · {state.detection?.path ?? state.projectPath}
      </dd>
    </div>
    <div>
      <dt>Unity</dt>
      <dd>
        {#if state.detection?.unityVersion}
          <code>{state.detection.unityVersion}</code>
          {#if !state.detection.meetsMinUnityVersion}
            <StatusChip tone="warn" label="below minimum" />
          {:else if state.detection.meetsRecommendedUnityVersion}
            <StatusChip tone="ok" label="Unity 6+" />
          {:else}
            <StatusChip tone="warn" label="supported (legacy toolbar)" />
          {/if}
        {:else}
          <em>unknown</em>
        {/if}
      </dd>
    </div>
    <div>
      <dt>Packages installed</dt>
      <dd>
        {#if state.detection}
          {@const pkg = packagesSummary(state.detection)}
          <StatusChip tone={pkg.tone} label={pkg.label} />
          <small class="wiz-summary-small">
            bridge: {state.detection.bridgeInstalled ? "yes" : "no"} · verify: {state.detection.verifyInstalled ? "yes" : "no"}
          </small>
        {:else}
          <StatusChip tone="muted" label="unknown" />
        {/if}
        {#if state.mergeResult}
          <br />
          <small>Last install: {summarizeChanges(state.mergeResult.changes)}</small>
        {/if}
      </dd>
    </div>
    <div>
      <dt>MCP configured</dt>
      <dd>
        <StatusChip tone={mcp.tone} label={mcp.label} />
        <small class="wiz-summary-small">
          {mcpConfiguredSummary(state.detection?.mcpConfigured ?? EMPTY_HEURISTIC)}
        </small>
      </dd>
    </div>
    <div>
      <dt>Unity launched</dt>
      <dd>
        <StatusChip tone={launch.tone} label={launch.label} />
        {#if state.step5LaunchPid !== null}
          <small class="wiz-summary-small">pid {state.step5LaunchPid}</small>
        {/if}
      </dd>
    </div>
    <div>
      <dt>Bridge verified</dt>
      <dd>
        <StatusChip tone={br.tone} label={br.label} />
        {#if state.step5BridgeStatus.kind === "ok"}
          <small class="wiz-summary-small">
            {#if state.step5BridgeStatus.projectPath}project: {state.step5BridgeStatus.projectPath}{/if}
            {#if state.step5BridgeStatus.isPlaying} · in play mode{/if}
          </small>
        {:else if state.step5BridgeStatus.kind === "failed"}
          <small class="wiz-summary-small">{state.step5BridgeStatus.message}</small>
        {/if}
      </dd>
    </div>
    <div>
      <dt>Toolkit root</dt>
      <dd><code>{state.toolkitRoot || "<not set>"}</code></dd>
    </div>
  </dl>

  <div class="wiz-field">
    <span class="wiz-label">Links</span>
    <div class="wiz-actions-row">
      <Button variant="secondary" onclick={handlers.openProjectFolder} disabled={!state.projectPath}>
        Open project folder
      </Button>
      <Button variant="secondary" onclick={handlers.revealProjectFolder} disabled={!state.projectPath}>
        Reveal in Finder / Explorer
      </Button>
      <Button
        variant="secondary"
        onclick={handlers.openMcpConfigTarget}
        disabled={!state.mcpPlan?.targetPath}
        title={state.mcpPlan?.targetPath ? `Open ${state.mcpPlan.targetPath}` : "No MCP config target was written"}
      >
        Open MCP config file
      </Button>
      <Button variant="secondary" onclick={handlers.openToolkitSkill} disabled={!state.toolkitRoot.trim()}>
        Open toolkit skill
      </Button>
      <Button
        variant="secondary"
        onclick={handlers.openCopiedSkill}
        disabled={!state.skillResult || state.skillResult.copied.length === 0}
      >
        Open copied skill
      </Button>
    </div>
    <p class="wiz-hint">
      The Advanced BYO-bridge section is intentionally
      hidden in v1 (it will be re-enabled after
      the batch criteria are met). Baseline creation is also
      deferred; the wizard renders only Steps 1–7 plus this Done screen.
    </p>
  </div>

  <div class="wiz-field">
    <span class="wiz-label">Skill copy</span>
    <p class="wiz-desc">
      {#if state.skillResult && state.skillResult.copied.length > 0}
        The wizard copied
        <code>{state.toolkitRoot || "<toolkit>"}/skills/unity-open-mcp/SKILL.md</code>
        into the project-relative skill folder(s) for your selected client ({state.skillResult.copied.map((t) => t.relativePath).join(", ")}).
      {:else}
        The wizard copies
        <code>{state.toolkitRoot || "<toolkit>"}/skills/unity-open-mcp/SKILL.md</code>
        into the project-relative skill folder(s) for your selected client
        ({#each state.skillPlan?.targets ?? [] as t, i}{#if i > 0}, {/if}<code>{t.relativePath}</code>{/each}{#if !state.skillPlan || state.skillPlan.targets.length === 0}<em>none for this client</em>{/if}).
        You can also do this from the dedicated **Agent skill** step.
      {/if}
      Existing files are only overwritten after you tick the
      confirmation box.
    </p>
    {#if state.skillPlanning && !state.skillPlan}
      <p class="wiz-hint">Planning skill copy…</p>
    {:else if state.skillError}
      <div class="wiz-block wiz-block-error" role="alert">
        {describeSkillCopyError(state.skillError)}
      </div>
    {:else if state.skillPlan}
      {#if !state.skillPlan.sourcePath}
        <div class="wiz-block wiz-block-error" role="alert">
          Source skill file is missing in the toolkit root. Run
          the wizard again with a valid toolkit checkout.
        </div>
      {:else if state.skillPlan.targets.length === 0}
        <p class="wiz-hint wiz-hint-warn">
          No skill targets are mapped for the selected client. Pick a different MCP client on the Configure AI client step or use the Manual option to install into all known client folders.
        </p>
      {:else}
        <ul class="wiz-fingerprints" aria-label="Skill copy targets">
          {#each state.skillPlan.targets as target (target.targetPath)}
            {@const tone = target.exists ? "warn" : "ok"}
            <li class="wiz-fp wiz-fp-{tone}">
              <span class="wiz-fp-name">
                <code>{target.relativePath}</code>
              </span>
              <span class="wiz-fp-status">
                {#if target.exists}exists — will be overwritten only with confirmation{:else}will create{/if}
              </span>
            </li>
          {/each}
        </ul>
        {#if state.skillPlan.targets.some((t) => t.exists)}
          <label class="wiz-toggle wiz-toggle-confirm">
            <input
              type="checkbox"
              checked={state.skillOverwriteAck}
              onchange={(e) => handlers.toggleSkillOverwriteAck((e.currentTarget as HTMLInputElement).checked)}
            />
            <span>
              <strong>Overwrite existing skill files.</strong>
              <small>The targets above already exist; the wizard will back them up to <code>*.bak</code> and replace them only when this is checked.</small>
            </span>
          </label>
        {/if}
        <div class="wiz-actions-row">
          <Button
            variant="primary"
            onclick={handlers.copySkillFiles}
            disabled={
              state.skillCopying ||
              (state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck)
            }
            title={
              state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck
                ? "Confirm overwrite first"
                : "Copy skill files"
            }
          >
            {state.skillCopying ? "Copying…" : state.skillResult?.copied.length ? "Copy again" : "Copy skill files"}
          </Button>
          <Button
            variant="secondary"
            onclick={handlers.generateProjectSkill}
            disabled={
              !state.canGenerateSkill ||
              state.skillGenRunning ||
              (state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck)
            }
            title={
              state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck
                ? "Confirm overwrite first"
                : !state.canGenerateSkill
                  ? "Build the MCP server (mcp-server/dist/index.js) first"
                  : "Generate a project-specific skill that merges the playbook with this project's inventory"
            }
          >
            {state.skillGenRunning ? "Generating…" : state.skillGenResult?.targets.length ? "Regenerate skill" : "Generate project skill"}
          </Button>
        </div>
      {/if}
    {/if}
    {#if state.skillGenError}
      <div class="wiz-block wiz-block-error" role="alert">
        {describeGenerateSkillError(state.skillGenError)}
      </div>
    {/if}
    {#if state.skillGenResult}
      <div class="wiz-block wiz-block-ok" role="status">
        <strong>Project skill generated.</strong>
        Wrote {state.skillGenResult.targets.length} file(s) — Unity {state.skillGenResult.unityVersion}
        {#if state.skillGenResult.bridgeVersion}
          · bridge {state.skillGenResult.bridgeVersion}
        {/if}
        .
        <button
          type="button"
          class="wiz-link-button"
          onclick={handlers.toggleSkillGenPreview}
        >
          {state.skillGenPreviewOpen ? "Hide" : "Show"} preview
        </button>
      </div>
      {#if state.skillGenResult.targets.length > 0}
        <ul class="wiz-fingerprints" aria-label="Generated skill files">
          {#each state.skillGenResult.targets as t (t.absolutePath)}
            <li class="wiz-fp wiz-fp-ok">
              <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
              <span class="wiz-fp-status">{t.existed ? "overwritten" : "created"}</span>
            </li>
          {/each}
        </ul>
      {/if}
      {#if state.skillGenPreviewOpen}
        <details class="wiz-preview" open>
          <summary>Generated skill preview</summary>
          <pre>{state.skillGenResult.inventoryPreview}</pre>
        </details>
      {/if}
    {/if}
    {#if state.skillResult}
      <div class="wiz-block wiz-block-ok" role="status">
        <strong>Skill copy complete.</strong>
        Copied {state.skillResult.copied.length} file(s)
        {#if state.skillResult.overwritten.length > 0}
          ({state.skillResult.overwritten.length} replaced existing)
        {/if}
        {#if state.skillResult.skipped.length > 0}
          · skipped {state.skillResult.skipped.length} (already present)
        {/if}
      </div>
      {#if state.skillResult.copied.length > 0}
        <ul class="wiz-fingerprints" aria-label="Copied skill files">
          {#each state.skillResult.copied as t (t.targetPath)}
            <li class="wiz-fp wiz-fp-ok">
              <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
              <span class="wiz-fp-status">copied</span>
            </li>
          {/each}
        </ul>
      {/if}
      {#if state.skillResult.skipped.length > 0}
        <ul class="wiz-fingerprints" aria-label="Skipped skill files">
          {#each state.skillResult.skipped as t (t.targetPath)}
            <li class="wiz-fp wiz-fp-warn">
              <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
              <span class="wiz-fp-status">skipped (not overwritten)</span>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <div class="wiz-actions-row">
    <Button variant="primary" onclick={handlers.closeWizard}>Close</Button>
    <Button variant="secondary" onclick={handlers.reRunWizard}>Re-run wizard</Button>
    {#if state.doneOpenInCursor}
      <Button variant="secondary" onclick={handlers.openProjectFolder}>
        Open in Cursor
      </Button>
    {/if}
    {#if state.doneOpenInOpencode}
      <Button variant="secondary" onclick={handlers.openProjectFolder}>
        Open in OpenCode
      </Button>
    {/if}
  </div>
</section>
