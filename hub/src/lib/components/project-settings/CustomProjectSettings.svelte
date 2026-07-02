<script lang="ts">
  import type { ProjectEntry } from "$lib/services/config";
  import LineCounterPanel from "$lib/components/project-settings/LineCounterPanel.svelte";

  let {
    project,
    onMutated,
  }: {
    project: ProjectEntry;
    onMutated: (updated: ProjectEntry) => void;
  } = $props();

  type Tab = "info" | "lineCounter";
  let activeTab = $state<Tab>("info");
</script>

<div class="custom-settings">
  <nav class="popup-tabs">
    <button class="popup-tab" class:active={activeTab === "info"} onclick={() => (activeTab = "info")}>Info</button>
    <button class="popup-tab" class:active={activeTab === "lineCounter"} onclick={() => (activeTab = "lineCounter")}>Line counter</button>
  </nav>

  {#if activeTab === "info"}
    <section class="info-block">
      <p class="info-text">
        This folder is tracked as a <strong>Custom</strong> project. Launch and AI
        setup are unavailable for custom folders; use the <strong>Line counter</strong>
        tab to inspect code volume.
      </p>
    </section>
  {:else if activeTab === "lineCounter"}
    <LineCounterPanel {project} />
  {/if}
</div>

<style>
  .custom-settings {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .popup-tabs {
    display: flex;
    gap: 0.25rem;
    border-bottom: 1px solid var(--hub-border);
  }
  .popup-tab {
    padding: 0.4rem 0.8rem;
    background: transparent;
    border: none;
    border-bottom: 2px solid transparent;
    color: var(--hub-text-dim);
    font-size: 0.8rem;
    cursor: pointer;
  }
  .popup-tab.active {
    color: var(--hub-text);
    border-bottom-color: var(--hub-accent, #5c7cfa);
  }
  .info-block {
    padding: 0.6rem 0.8rem;
    border-radius: 0.5rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border);
  }
  .info-text {
    margin: 0;
    font-size: 0.8rem;
    line-height: 1.5;
    color: var(--hub-text-dim);
  }
</style>
