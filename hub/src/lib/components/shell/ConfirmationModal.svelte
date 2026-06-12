<script lang="ts">
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";

  function handleCancel() {
    S.resolveConfirmation(false);
  }

  function handleConfirm() {
    S.resolveConfirmation(true);
  }

  function handleOverlayClick() {
    S.resolveConfirmation(false);
  }
</script>

{#if S.showConfirmationModal}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="overlay" onclick={handleOverlayClick}>
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="modal" onclick={(e) => e.stopPropagation()}>
      <div class="modal-header">
        <h2>{S.confirmationTitle}</h2>
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
    z-index: 200;
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
