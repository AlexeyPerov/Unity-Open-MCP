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

<div class="shell" role="application" aria-label="Unity Hub Pro">
  <div class="titlebar" data-tauri-drag-region></div>
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
    --hub-text-placeholder: #6f7280;
    --hub-text-disabled: #555;
    --hub-selected: #32343f;
    --hub-error: #de3576;
    --hub-warning: #c9a227;
    --hub-success: #2f6f4a;
    --hub-info-bg: #1e2a4a;
    --hub-info-fg: #7c9cfa;
    --hub-warn-bg: #4a3a1e;
    --hub-warn-fg: #f0c87a;
    --hub-error-bg: #2a1320;
    --hub-error-fg: #f0a8b8;
    --hub-branch-chip-bg: #1d2330;
    --hub-branch-chip-border: #3a4255;
    --hub-branch-chip-fg: #b6c2d6;
    --hub-branch-detached-bg: #2a200f;
    --hub-branch-detached-border: #6b4f1a;
    --hub-branch-detached-fg: #d8b86a;
    --hub-source-walkup-fg: #9bb3ff;
    --hub-source-seed-fg: #b4b8c5;
    --hub-relink-fg: #c8d3ff;
    --hub-relink-hover-bg: #243056;
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
    --hub-text-placeholder: #8b8d9a;
    --hub-text-disabled: #9aa0ad;
    --hub-selected: #e8eaf0;
    --hub-error: #c0234f;
    --hub-warning: #a87a14;
    --hub-success: #1f6f4a;
    --hub-info-bg: #e6ecff;
    --hub-info-fg: #3a5bdb;
    --hub-warn-bg: #fdf3dc;
    --hub-warn-fg: #8a6d2b;
    --hub-error-bg: #fde7ee;
    --hub-error-fg: #c0234f;
    --hub-branch-chip-bg: #e8eaf2;
    --hub-branch-chip-border: #b0b8cc;
    --hub-branch-chip-fg: #4a5568;
    --hub-branch-detached-bg: #fdf3dc;
    --hub-branch-detached-border: #d4a84b;
    --hub-branch-detached-fg: #8a6d2b;
    --hub-source-walkup-fg: #3a5bdb;
    --hub-source-seed-fg: #5f636e;
    --hub-relink-fg: #3a5bdb;
    --hub-relink-hover-bg: #e6ecff;
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

  .titlebar {
    flex-shrink: 0;
    height: 32px;
    background: var(--hub-surface);
    -webkit-app-region: drag;
    app-region: drag;
  }

  .app {
    flex: 1;
    display: flex;
    flex-direction: row;
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    box-sizing: border-box;
    padding: 0 0.75rem 0.75rem 0.75rem;
    gap: 0.75rem;
  }
</style>
