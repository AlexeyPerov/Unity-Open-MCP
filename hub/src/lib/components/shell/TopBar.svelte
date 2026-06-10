<script lang="ts">
  import { S, type Tab } from "$lib/state.svelte";
  import { APP_NAME } from "$lib/tokens";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { discoveryStore } from "$lib/state/discovery.svelte";

  const tabs: { id: Tab; label: string }[] = [
    { id: "projects", label: "Projects" },
    { id: "unityVersions", label: "Unity Versions" },
    { id: "tools", label: "Tools" },
    { id: "settings", label: "Settings" },
  ];

  async function handleRefresh() {
    S.appendDrawerLog("refreshing projects and Unity discovery…");
    try {
      await discoveryStore.refresh();
      S.appendDrawerLog(
        `discovery: ${discoveryStore.installations.length} install${discoveryStore.installations.length === 1 ? "" : "s"}${discoveryStore.errors.length ? `, ${discoveryStore.errors.length} error(s)` : ""}`
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`discovery refresh failed: ${msg}`);
    }
    try {
      await projectsStore.load();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`projects reload failed: ${msg}`);
    }
  }
</script>

<header class="topbar">
  <div class="topbar-left">
    <h1 class="topbar-title">{APP_NAME}</h1>
    <div class="tabs" role="tablist" aria-label="Hub sections">
      {#each tabs as tab}
        <button
          type="button"
          role="tab"
          class="tab"
          class:tab-active={S.activeTab === tab.id}
          aria-selected={S.activeTab === tab.id}
          onclick={() => (S.activeTab = tab.id)}
        >{tab.label}</button>
      {/each}
    </div>
  </div>
  <button
    type="button"
    class="refresh-btn"
    title="Refresh"
    aria-label="Refresh"
    onclick={handleRefresh}
  >
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <polyline points="23 4 23 10 17 10"/>
      <polyline points="1 20 1 14 7 14"/>
      <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/>
    </svg>
  </button>
</header>

<style>
  .topbar {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .topbar-left {
    flex: 1;
    min-width: 0;
  }

  .topbar-title {
    margin: 0 0 0.35rem;
    font-size: 1.35rem;
    font-weight: 650;
    letter-spacing: -0.02em;
    color: #e9e9ef;
  }

  .tabs {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.25rem;
    padding: 0.2rem;
    border: 1px solid #3f4150;
    border-radius: 8px;
    background: #1e1f26;
  }

  .tab {
    border: 1px solid transparent;
    border-radius: 6px;
    background: transparent;
    color: #a1a3b0;
    font-size: 0.82rem;
    padding: 0.3rem 0.65rem;
    cursor: pointer;
    line-height: 1.4;
  }

  .tab:hover {
    color: #fff;
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

  .refresh-btn {
    flex-shrink: 0;
    margin-top: 0.15rem;
    padding: 0.45rem;
    border-radius: 6px;
    border: 1px solid #474957;
    background: #32343f;
    color: #d7d8e0;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    line-height: 1;
  }

  .refresh-btn:hover {
    border-color: #5c7cfa;
    color: #fff;
  }

  .refresh-btn:focus-visible {
    outline: 2px solid #5c7cfa;
    outline-offset: 1px;
  }
</style>
