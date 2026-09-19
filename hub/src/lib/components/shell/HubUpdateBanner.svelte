<script lang="ts">
  import { openUrl } from "@tauri-apps/plugin-opener";
  import { hubUpdateStore } from "$lib/state/hub_update.svelte";
  import Button from "$lib/components/shell/Button.svelte";
  import { relaunchHub } from "$lib/services/hub_update";

  let release = $derived(hubUpdateStore.status?.release ?? null);

  function openReleaseNotes() {
    if (release) void openUrl(release.releaseNotesUrl);
  }

  function openManualDownload() {
    const url =
      hubUpdateStore.applyResult?.manualDownloadUrl ??
      hubUpdateStore.status?.manualDownloadUrl;
    if (url) void openUrl(url);
  }
</script>

{#if release || hubUpdateStore.applyResult || (hubUpdateStore.error && hubUpdateStore.state === "available")}
  <section class="update-banner" aria-label="Hub update" aria-live="polite">
    <div class="update-copy">
      {#if hubUpdateStore.applyResult}
        <strong>Update installer opened</strong>
        <span>{hubUpdateStore.applyResult.message}</span>
      {:else if release}
        <strong>Update available: v{release.version}</strong>
        <button class="notes-link" type="button" onclick={openReleaseNotes}>
          Release notes
        </button>
        <span class="coordination-note">MCP and Unity packages update separately.</span>
      {/if}
      {#if hubUpdateStore.error}
        <span class="update-error">{hubUpdateStore.error}</span>
      {/if}
    </div>
    <div class="update-actions">
      {#if hubUpdateStore.applyResult}
        <Button variant="primary" onclick={() => relaunchHub()}>Relaunch</Button>
        <Button variant="secondary" onclick={openManualDownload}>Release page</Button>
        <Button variant="secondary" onclick={() => hubUpdateStore.clearApplyResult()}>Close</Button>
      {:else if release}
        <Button
          variant="primary"
          disabled={hubUpdateStore.applying}
          onclick={() => hubUpdateStore.apply(release!)}
        >
          {hubUpdateStore.applying ? "Downloading…" : "Update"}
        </Button>
        <Button variant="secondary" onclick={() => hubUpdateStore.suppress("remindLater")}>
          Remind later
        </Button>
        <Button variant="secondary" onclick={() => hubUpdateStore.suppress("dismiss")}>
          Dismiss
        </Button>
      {/if}
    </div>
  </section>
{/if}

<style>
  .update-banner {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 0.65rem 0.8rem;
    border: 1px solid var(--hub-accent);
    border-radius: 8px;
    background: var(--hub-info-bg);
    color: var(--hub-text);
  }

  .update-copy,
  .update-actions {
    display: flex;
    align-items: center;
    gap: 0.65rem;
    flex-wrap: wrap;
  }

  .update-copy {
    min-width: 0;
    font-size: 0.82rem;
  }

  .update-copy strong {
    color: var(--hub-text-bright);
  }

  .notes-link {
    border: none;
    padding: 0;
    background: transparent;
    color: var(--hub-info-fg);
    text-decoration: underline;
    cursor: pointer;
    font: inherit;
  }

  .coordination-note {
    color: var(--hub-text-muted);
  }

  .update-error {
    color: var(--hub-error-fg);
  }

  .update-actions {
    flex-shrink: 0;
  }

  @media (max-width: 760px) {
    .update-banner {
      align-items: flex-start;
      flex-direction: column;
    }
  }
</style>
