<script lang="ts">
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";

  let modalEl: HTMLDivElement | null = $state(null);
  let previousFocus: HTMLElement | null = null;

  function handleCancel() {
    S.resolveConfirmation(false);
  }

  function handleConfirm() {
    S.resolveConfirmation(true);
  }

  function handleOverlayClick() {
    S.resolveConfirmation(false);
  }

  // Escape must close the modal regardless of where focus currently
  // lives. The previous version of this file had no `onkeydown` handler
  // at all, which combined with the global `handleGlobalKeydown` in
  // `ProjectsTab` (which has no case for `S.showConfirmationModal`) meant
  // pressing Escape did nothing when the user clicked a table row and
  // the modal opened on top — focus stayed on the row, the keydown
  // never reached the modal, and the user was stuck. Every other modal
  // in the app has the same pattern; this brings the confirmation
  // modal in line.
  function handleKeydown(e: KeyboardEvent) {
    if (e.key === "Escape") {
      e.stopPropagation();
      handleCancel();
    }
  }

  // Move focus into the modal when it opens so keyboard-only users have
  // an entry point (and so the Escape handler above reliably fires — a
  // Svelte `onkeydown` on a non-focused div is otherwise a no-op). On
  // close, restore focus to the element that opened the modal so the
  // user's tab order does not jump.
  $effect(() => {
    if (S.showConfirmationModal) {
      previousFocus = (document.activeElement as HTMLElement | null) ?? null;
      // Defer to the next microtask so the modal element exists in the
      // DOM and Svelte's bindings have populated the ref.
      queueMicrotask(() => {
        modalEl?.focus();
      });
    } else if (previousFocus && typeof previousFocus.focus === "function") {
      try {
        previousFocus.focus();
      } catch {
        // The element may have been unmounted between the modal open
        // and close (e.g. a tab switch); silently swallow.
      }
      previousFocus = null;
    }
  });
</script>

{#if S.showConfirmationModal}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="overlay" onclick={handleOverlayClick} onkeydown={handleKeydown}>
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div
      class="modal"
      role="dialog"
      aria-modal="true"
      aria-labelledby="confirmation-title"
      tabindex="-1"
      bind:this={modalEl}
      onclick={(e) => e.stopPropagation()}
      onkeydown={handleKeydown}
    >
      <div class="modal-header">
        <h2 id="confirmation-title">{S.confirmationTitle}</h2>
        <button
          type="button"
          class="modal-close-btn"
          aria-label="Close"
          onclick={handleCancel}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
          </svg>
        </button>
      </div>
      <div class="modal-body">
        <p class="modal-message">{S.confirmationMessage}</p>
        <div class="modal-actions">
          <Button variant="secondary" onclick={handleCancel}>Cancel</Button>
          <Button variant="destructive" onclick={handleConfirm}>Confirm</Button>
        </div>
      </div>
    </div>
  </div>
{/if}

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 260;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .modal {
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 12px;
    width: min(28rem, 90vw);
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.45);
    outline: none;
  }

  .modal:focus-visible {
    border-color: var(--hub-accent);
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.45), 0 0 0 2px var(--hub-accent);
  }

  .modal-header {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    padding: 0.85rem 1rem;
    border-bottom: 1px solid var(--hub-border);
  }

  .modal-header h2 {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    color: var(--hub-text-bright);
  }

  .modal-close-btn {
    padding: 0.3rem;
    border-radius: 4px;
    border: 1px solid transparent;
    background: transparent;
    color: var(--hub-text-muted);
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    line-height: 1;
  }

  .modal-close-btn:hover {
    color: var(--hub-text-bright);
    border-color: var(--hub-border-hover);
    background: var(--hub-selected);
  }

  .modal-body {
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
  }

  .modal-message {
    margin: 0;
    font-size: 0.88rem;
    line-height: 1.5;
    color: var(--hub-text);
  }

  .modal-actions {
    display: flex;
    flex-direction: row;
    justify-content: flex-end;
    gap: 0.5rem;
  }
</style>
