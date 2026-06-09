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

<div class="shell" role="application" aria-label="Unity Agent Hub">
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
    background: #14151a;
    color: #e9e9ef;
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
    flex-direction: column;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
    box-sizing: border-box;
    padding: 1rem 1.25rem 0.5rem;
    gap: 1rem;
  }
</style>
