<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";

  interface Props {
    /** "Step N of M · <title>". */
    progressLabel: string;
    /** Whether navigation Back is allowed. */
    canGoBack: boolean;
    /** Whether navigation Next is allowed (ignored on Done). */
    canGoNext: boolean;
    /** Whether the current step is the Done screen (Next becomes Close). */
    isDone: boolean;
    /** Next/Finish/Close label. */
    nextLabel: string;
    /** "Clear AI Setup" in-progress flag. */
    clearInProgress: boolean;
    onBack: () => void;
    onNext: () => void;
    onCancel: () => void;
    onClear: () => void;
  }

  let {
    progressLabel,
    canGoBack,
    canGoNext,
    isDone,
    nextLabel,
    clearInProgress,
    onBack,
    onNext,
    onCancel,
    onClear,
  }: Props = $props();
</script>

<footer class="wiz-footer">
  <div class="wiz-footer-left">
    <span class="wiz-footer-progress">
      {progressLabel}
    </span>
    <span class="wiz-footer-clear">
      <Button
        class="wiz-clear-btn"
        variant="secondary"
        onclick={onClear}
        disabled={clearInProgress}
        title="Remove every AI-agent artifact the wizard wrote for this project (manifest entries, MCP client configs, skill files). Backups are created first."
      >
        {clearInProgress ? "Clearing…" : "Clear AI Setup"}
      </Button>
    </span>
  </div>
  <div class="wiz-footer-actions">
    <Button variant="secondary" onclick={onCancel}>Cancel</Button>
    <Button variant="secondary" onclick={onBack} disabled={!canGoBack}>
      Back
    </Button>
    <Button
      variant="primary"
      onclick={onNext}
      disabled={isDone ? false : !canGoNext}
    >
      {nextLabel}
    </Button>
  </div>
</footer>
