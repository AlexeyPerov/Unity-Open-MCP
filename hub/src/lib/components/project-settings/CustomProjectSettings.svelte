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
</script>

<div class="custom-settings">
  <section class="info-block">
    <p class="info-text">
      This folder is tracked as a <strong>Custom</strong> project. Launch and AI
      setup are unavailable for custom folders; use the line counter below to
      inspect code volume.
    </p>
  </section>
  <LineCounterPanel {project} />
</div>

<style>
  .custom-settings {
    display: flex;
    flex-direction: column;
    gap: 1rem;
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
