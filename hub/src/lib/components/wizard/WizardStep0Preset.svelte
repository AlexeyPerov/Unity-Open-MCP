<script lang="ts">
  import {
    WIZARD_PRESETS,
    presetById,
    type PresetId,
    type WizardPreset,
  } from "$lib/services/wizard_presets";
  import type { WizardHandlers, WizardState } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Plan 3 — first viewport shows the primary presets only (Recommended,
  // Contributor, Custom). Niche presets (Team CI, Secure / remote) are
  // demoted behind a "More presets" disclosure so they stay reachable
  // without competing with the common-path choices.
  let primaryPresets = $derived(
    WIZARD_PRESETS.filter((p) => (p.tier ?? "primary") === "primary"),
  );
  let morePresets = $derived(
    WIZARD_PRESETS.filter((p) => p.tier === "more"),
  );
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Pick a preset to pre-fill the rest of the wizard. Presets are
    starting points, not locks — you can change any field on later
    steps. The recommended preset covers most users.
  </p>

  <div class="wiz-preset-grid" role="radiogroup" aria-label="Setup preset">
    {#each primaryPresets as preset (preset.id)}
      {@render presetCard(preset)}
    {/each}
  </div>

  {#if morePresets.length > 0}
    <details class="wiz-advanced">
      <summary>More presets</summary>
      <div class="wiz-preset-grid" role="radiogroup" aria-label="More setup presets">
        {#each morePresets as preset (preset.id)}
          {@render presetCard(preset)}
        {/each}
      </div>
    </details>
  {/if}

  {#if state.selectedPresetId && state.selectedPresetId !== "custom"}
    <p class="wiz-hint wiz-hint-ok">
      Applied the <strong>{presetById(state.selectedPresetId as PresetId).label}</strong>
      preset. Later steps now reflect its defaults — adjust any field
      as needed. Pick <strong>Custom / skip</strong> to clear the
      pre-fills.
    </p>
  {/if}
</section>

{#snippet presetCard(preset: WizardPreset)}
  <button
    type="button"
    class="wiz-preset{state.selectedPresetId === preset.id ? ' wiz-preset-selected' : ''}"
    role="radio"
    aria-checked={state.selectedPresetId === preset.id}
    title={preset.tooltip}
    onclick={() => handlers.selectPreset(preset.id as PresetId)}
  >
    <div class="wiz-preset-head">
      <strong>{preset.label}</strong>
      {#if preset.recommended}
        <span class="wiz-preset-badge">Recommended</span>
      {/if}
    </div>
    <p class="wiz-preset-desc">{preset.description}</p>
    {#if preset.id === "secure-remote"}
      <small class="wiz-preset-note">
        Token auth, remote bind, and restricted tool groups are
        configured on the bridge window after onboarding.
      </small>
    {/if}
    {#if preset.id === "team-ci"}
      <small class="wiz-preset-note">
        Configure token auth on the bridge for headless CI use.
      </small>
    {/if}
  </button>
{/snippet}
