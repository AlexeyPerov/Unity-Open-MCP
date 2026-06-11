<script lang="ts">
  import { S } from "$lib/state.svelte";

  let {
    children,
  }: {
    children?: import("svelte").Snippet;
  } = $props();
</script>

<div class="tab-panel" role="tabpanel" aria-label={S.activeTab}>
  {@render children?.()}
</div>

<style>
  .tab-panel {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
  }
</style>
