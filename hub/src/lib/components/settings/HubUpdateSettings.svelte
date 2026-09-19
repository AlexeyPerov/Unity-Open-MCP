<script lang="ts">
  import { openUrl } from "@tauri-apps/plugin-opener";
  import Button from "$lib/components/shell/Button.svelte";
  import { hubUpdateStore } from "$lib/state/hub_update.svelte";
  import { APP_VERSION } from "$lib/version";

  let statusText = $derived.by(() => {
    if (hubUpdateStore.state === "checking") return "Checking GitHub Releases…";
    if (hubUpdateStore.state === "skipped") return "Automatic checks are off for this local build.";
    if (hubUpdateStore.status?.release) {
      return `Version ${hubUpdateStore.status.release.version} is available.`;
    }
    if (hubUpdateStore.state === "upToDate" || hubUpdateStore.state === "throttled") {
      return "You’re up to date.";
    }
    if (hubUpdateStore.state === "error") return "The last check could not reach GitHub.";
    return "Checks run after startup and no more than once per hour.";
  });
</script>

<div class="update-settings">
  <div class="version-row">
    <span>
      <strong>Installed version</strong>
      <span class="version-copy">v{APP_VERSION}</span>
    </span>
    <Button
      variant="secondary"
      disabled={hubUpdateStore.state === "checking"}
      onclick={() => hubUpdateStore.check(true)}
    >
      {hubUpdateStore.state === "checking" ? "Checking…" : "Check for updates"}
    </Button>
  </div>
  <p class="status-copy">{statusText}</p>
  {#if hubUpdateStore.error}
    <p class="error-copy" role="status">
      {hubUpdateStore.error}
      {#if hubUpdateStore.status?.manualDownloadUrl}
        <button
          type="button"
          class="link-button"
          onclick={() => openUrl(hubUpdateStore.status!.manualDownloadUrl)}
        >Manual download</button>
      {/if}
    </p>
  {/if}
</div>

<style>
  .update-settings {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .version-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
  }

  .version-copy {
    margin-left: 0.5rem;
    color: var(--hub-text-dim);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  .status-copy,
  .error-copy {
    margin: 0;
    color: var(--hub-text-muted);
    font-size: 0.78rem;
    line-height: 1.5;
  }

  .error-copy {
    color: var(--hub-error-fg);
  }

  .link-button {
    border: none;
    padding: 0;
    margin-left: 0.35rem;
    background: transparent;
    color: inherit;
    text-decoration: underline;
    cursor: pointer;
    font: inherit;
  }
</style>
