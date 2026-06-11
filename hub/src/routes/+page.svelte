<script lang="ts">
  import { S } from "$lib/state.svelte";
  import TopBar from "$lib/components/shell/TopBar.svelte";
  import TabPanel from "$lib/components/shell/TabPanel.svelte";
  import ConfirmationModal from "$lib/components/shell/ConfirmationModal.svelte";
  import StatusDrawer from "$lib/components/shell/StatusDrawer.svelte";
  import ProjectsTab from "$lib/tabs/ProjectsTab.svelte";
  import UnityVersionsTab from "$lib/tabs/UnityVersionsTab.svelte";
  import ToolsTab from "$lib/tabs/ToolsTab.svelte";
  import SettingsTab from "$lib/tabs/SettingsTab.svelte";
</script>

<div class="shell" role="application" aria-label="Unity AI Hub">
  <div class="app">
    <TopBar />

    <TabPanel>
      {#if S.activeTab === "projects"}
        <ProjectsTab />
      {:else if S.activeTab === "unityVersions"}
        <UnityVersionsTab />
      {:else if S.activeTab === "tools"}
        <ToolsTab />
      {:else}
        <SettingsTab />
      {/if}
    </TabPanel>
  </div>

  <StatusDrawer />
  <ConfirmationModal />
</div>

<style>
  /* M1.5-18: theme is driven by [data-theme="dark" | "light"] on
   * <html>. The body / shell uses CSS variables so the same
   * component file can render both palettes without duplicating
   * styles. The defaults below are the dark palette (the Hub
   * shipped dark-only before M1.5-18); the light overrides are
   * applied via the [data-theme="light"] selector. */
  :global(:root) {
    --hub-bg: #14151a;
    --hub-surface: #1e1f26;
    --hub-card: #24252c;
    --hub-border: #34353f;
    --hub-border-light: #3f4150;
    --hub-border-hover: #474957;
    --hub-accent: #5c7cfa;
    --hub-text: #e9e9ef;
    --hub-text-muted: #8b8d9a;
    --hub-text-dim: #a1a3b0;
    --hub-text-bright: #f2f3f7;
    --hub-error: #de3576;
    --hub-warning: #c9a227;
    --hub-success: #2f6f4a;
  }

  :global([data-theme="light"]) {
    --hub-bg: #f5f6f9;
    --hub-surface: #ffffff;
    --hub-card: #ffffff;
    --hub-border: #d4d6dd;
    --hub-border-light: #c2c5cf;
    --hub-border-hover: #a9adba;
    --hub-accent: #3a5bdb;
    --hub-text: #1d1f24;
    --hub-text-muted: #5f636e;
    --hub-text-dim: #4a4d57;
    --hub-text-bright: #0d0e12;
    --hub-error: #c0234f;
    --hub-warning: #a87a14;
    --hub-success: #1f6f4a;
  }

  :global(html),
  :global(body) {
    margin: 0;
    height: 100%;
    overflow: hidden;
    font-family:
      system-ui,
      -apple-system,
      Segoe UI,
      Roboto,
      Helvetica,
      Arial,
      sans-serif;
    background: var(--hub-bg);
    color: var(--hub-text);
  }

  :global(body) {
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  .shell {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
  }

  .app {
    flex: 1;
    display: flex;
    flex-direction: row;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    box-sizing: border-box;
    padding: 0.75rem;
    gap: 0.75rem;
  }
</style>
