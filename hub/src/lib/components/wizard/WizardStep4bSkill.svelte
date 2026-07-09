<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import { mcpClientLabel } from "./constants.ts";
  import {
    describeGenerateSkillError,
    describeSkillCopyError,
  } from "./error_descriptors.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Install the Unity Open MCP <strong>agent skill</strong> so your AI
    client gets workflow guidance for the Unity MCP tools — the
    mutate→gate→fix loop, capabilities-first discovery, and the agent
    senses (tests, profiler, screenshots). This step is
    <strong>optional</strong>: skip it if you manage skills yourself.
  </p>
  <p class="wiz-hint">
    Two options write to the same project-relative skill folder(s) for
    the MCP client you picked on the Configure AI client step
    (<strong>{mcpClientLabel(state.mcpClient)}</strong>):
    <strong>Copy skill</strong> installs the template playbook (the same
    workflow guidance for every project), and
    <strong>Generate project skill</strong> produces a project-specific
    file that merges that playbook with this project's inventory (Unity
    version, installed packages, key MonoBehaviour / ScriptableObject
    types). Generate needs the built MCP server
    (<code>mcp-server/dist/index.js</code>); copy does not.
    Paths are derived from a single manifest
    (<code>skills/client-paths.json</code>), so ZCode writes
    <code>.agents/skills/</code> and Cursor writes
    <code>.cursor/skills/</code> — never an unconditional
    <code>.claude/skills/</code>.
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
        No skill folder is mapped for <strong>{mcpClientLabel(state.mcpClient)}</strong>.
        Use the **Skip** button to continue, or pick a different MCP client on the Configure AI client step.
      </p>
    {:else}
      <ul class="wiz-fingerprints" aria-label="Agent skill copy targets">
        {#each state.skillPlan.targets as target (target.targetPath)}
          {@const needsOverwrite = target.exists && !target.upToDate}
          {@const tone = needsOverwrite ? "warn" : "ok"}
          <li class="wiz-fp wiz-fp-{tone}">
            <span class="wiz-fp-name">
              <code>{target.relativePath}</code>
            </span>
            <span class="wiz-fp-status">
              {#if target.upToDate}
                already up to date — no write needed
              {:else if target.exists}
                exists — will be overwritten only with confirmation
              {:else}
                will create
              {/if}
            </span>
          </li>
        {/each}
      </ul>
      {#if state.skillPlan.targets.some((t) => t.exists && !t.upToDate)}
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
      {#if state.skillPlan.targets.length > 0 && state.skillPlan.targets.every((t) => !t.exists || t.upToDate) && state.skillPlan.targets.some((t) => t.upToDate)}
        <div class="wiz-block wiz-block-ok" role="status">
          <strong>Already up to date.</strong>
          The existing skill file(s) already match the toolkit
          template — no copy or backup is needed. Use
          <strong>Generate project skill</strong> to produce a
          project-specific file instead.
        </div>
      {/if}
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
    {#if state.skillGenPreviewOpen}
      <details class="wiz-preview" open>
        <summary>Generated skill preview</summary>
        <pre>{state.skillGenResult.inventoryPreview}</pre>
      </details>
    {/if}
  {/if}

  <div class="wiz-actions-row">
    <Button
      variant="primary"
      onclick={handlers.copySkillFiles}
      disabled={
        !state.skillPlan ||
        state.skillPlan.targets.length === 0 ||
        !state.skillPlan.sourcePath ||
        state.skillCopying ||
        (state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck)
      }
      title={
        state.skillPlan && state.skillPlan.targets.some((t) => t.exists) && !state.skillOverwriteAck
          ? "Confirm overwrite first"
          : "Copy the template skill into the client's skill folder"
      }
    >
      {state.skillCopying ? "Copying…" : state.skillResult?.copied.length ? "Copy again" : "Copy skill"}
    </Button>
    <Button
      variant="secondary"
      onclick={handlers.generateProjectSkill}
      disabled={
        !state.canGenerateSkill ||
        state.skillGenRunning ||
        (state.skillPlan?.targets.some((t) => t.exists) && !state.skillOverwriteAck)
      }
      title={
        state.skillPlan?.targets.some((t) => t.exists) && !state.skillOverwriteAck
          ? "Confirm overwrite first"
          : !state.canGenerateSkill
            ? "Build the MCP server (mcp-server/dist/index.js) first"
            : "Generate a project-specific skill that merges the playbook with this project's inventory"
      }
    >
      {state.skillGenRunning ? "Generating…" : state.skillGenResult?.targets.length ? "Regenerate" : "Generate project skill"}
    </Button>
    <Button variant="secondary" onclick={handlers.skipSkillStep}>
      Skip
    </Button>
  </div>
</section>
