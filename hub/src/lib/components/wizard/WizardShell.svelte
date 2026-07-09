<script lang="ts">
  import type { Snippet } from "svelte";
  import type { StepId } from "./constants.ts";

  /** A progress segment rendered in the strip. Exported for the orchestrator. */
  export interface ProgressSegment {
    id: StepId;
    idx: number;
    label: string;
    state: "done" | "current" | "pending";
    passing: boolean;
    /** Plan 2 — optional/advanced segments (MCP source, Agent skill) are
     *  demoted out of the visible progress strip. They are still navigable
     *  via step flow + Customize, but no longer advertised as peer segments. */
    optional?: boolean;
  }

  interface Props {
    /** Current step title (the <h2>). */
    title: string;
    /** Subtitle line (project name · path). */
    subtitle: string;
    /** `title` attribute on the subtitle (full project path). */
    subtitleTitle: string;
    /** Progress segments. */
    progress: ProgressSegment[];
    /** Overlay click handler (closes when the click hit the backdrop). */
    onOverlayClick: (e: MouseEvent) => void;
    /** Close (×) button handler. */
    onClose: () => void;
    /** Jump-to-step handler from the progress strip. */
    onJumpTo: (id: StepId) => void;
    /** Body snippet (the step switch). */
    body: Snippet;
    /** Footer snippet (Back / Next / Cancel / Clear). */
    footer: Snippet;
  }

  let {
    title,
    subtitle,
    subtitleTitle,
    progress,
    onOverlayClick,
    onClose,
    onJumpTo,
    body,
    footer,
  }: Props = $props();

  // Plan 2 — the visible progress strip shows core segments only. Optional
  // segments (MCP source, Agent skill) are demoted out of the strip unless
  // they are the current step, so the user always sees where they are. The
  // hidden segments are still fully navigable via the step flow.
  let visibleProgress = $derived(
    progress.filter(
      (seg) => !seg.optional || seg.state === "current",
    ),
  );
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
  class="wiz-overlay"
  role="dialog"
  tabindex="-1"
  aria-modal="true"
  aria-labelledby="wiz-title"
  onclick={onOverlayClick}
>
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="wiz-shell" onclick={(e) => e.stopPropagation()}>
    <header class="wiz-header">
      <div class="wiz-header-titles">
        <span class="wiz-eyebrow">AI Setup</span>
        <h2 id="wiz-title" class="wiz-title">
          {title}
        </h2>
        <span class="wiz-subtitle" title={subtitleTitle}>
          {subtitle}
        </span>
      </div>
      <button
        type="button"
        class="wiz-close"
        aria-label="Close AI Setup"
        title="Cancel and close the wizard"
        onclick={onClose}
      >
        ×
      </button>
    </header>

    <ol class="wiz-progress" aria-label="Wizard progress">
      {#each visibleProgress as seg, displayIdx (seg.id)}
        <!-- svelte-ignore a11y_no_static_element_interactions, a11y_click_events_have_key_events, a11y_interactive_supports_focus, a11y_no_noninteractive_element_to_interactive_role -->
        <li
          class="wiz-seg wiz-seg-{seg.state}{seg.passing ? ' wiz-seg-passing' : ''}{seg.optional ? ' wiz-seg-optional' : ''}"
          aria-current={seg.state === "current" ? "step" : undefined}
          role="button"
          tabindex="0"
          aria-label={`Jump to ${seg.label}`}
          title={`Jump to ${seg.label}`}
          onclick={() => onJumpTo(seg.id)}
          onkeydown={(e) => {
            if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              onJumpTo(seg.id);
            }
          }}
        >
          <span class="wiz-seg-num">{displayIdx + 1}</span>
          <span class="wiz-seg-label">{seg.label}</span>
        </li>
      {/each}
    </ol>

    <div class="wiz-body">
      {@render body()}
    </div>

    {@render footer()}
  </div>
</div>
