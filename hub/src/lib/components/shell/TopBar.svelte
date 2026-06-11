<script lang="ts">
  import { S, type Tab } from "$lib/state.svelte";

  const tabs: { id: Tab; label: string }[] = [
    { id: "projects", label: "Projects" },
    { id: "unityVersions", label: "Installs" },
    { id: "tools", label: "Tools" },
    { id: "settings", label: "Settings" },
  ];
</script>

<aside class="sidebar" aria-label="Hub navigation">
  <!-- svelte-ignore a11y_no_noninteractive_element_to_interactive_role -->
  <nav class="tabs" role="tablist" aria-label="Hub sections">
    {#each tabs.filter(t => t.id !== "settings") as tab}
      <button
        type="button"
        role="tab"
        class="tab"
        class:tab-active={S.activeTab === tab.id}
        aria-selected={S.activeTab === tab.id}
        onclick={() => (S.activeTab = tab.id)}
      >{tab.label}</button>
    {/each}
  </nav>
  <div class="spacer"></div>
  <button
    type="button"
    role="tab"
    class="tab"
    class:tab-active={S.activeTab === "settings"}
    aria-selected={S.activeTab === "settings"}
    onclick={() => (S.activeTab = "settings")}
  >Settings</button>
</aside>

<style>
  .sidebar {
    flex-shrink: 0;
    width: 11rem;
    display: flex;
    flex-direction: column;
    min-height: 0;
    padding: 0.4rem;
    border: 1px solid #34353f;
    border-radius: 8px;
    background: #1a1b21;
  }

  .tabs {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    min-height: 0;
  }

  .spacer {
    flex: 1;
    min-height: 0.5rem;
  }

  .tab {
    width: 100%;
    border: 1px solid transparent;
    border-radius: 6px;
    background: transparent;
    color: #a1a3b0;
    font-size: 0.85rem;
    padding: 0.55rem 0.65rem;
    cursor: pointer;
    line-height: 1.3;
    text-align: left;
  }

  .tab:hover {
    color: #fff;
    background: #1e1f26;
  }

  .tab:focus-visible {
    outline: 2px solid #5c7cfa;
    outline-offset: 1px;
  }

  .tab.tab-active {
    background: #32343f;
    color: #f2f3f7;
    border-color: #474957;
  }
</style>
