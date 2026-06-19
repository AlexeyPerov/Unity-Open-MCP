<script lang="ts">
  import type { ProjectEntry } from "$lib/services/config";
  import {
    runProjectBuild,
    runProjectTest,
    runProjectCustom,
    stopProjectCommand,
    type CommandPanel,
  } from "$lib/services/config";
  import { commandLogsStore, type ProjectPanels } from "$lib/state/command_logs.svelte";
  import { S } from "$lib/state.svelte";
  import Button from "$lib/components/shell/Button.svelte";
  import Console from "$lib/components/project-settings/Console.svelte";
  import LineCounterPanel from "$lib/components/project-settings/LineCounterPanel.svelte";

  let {
    project,
    onMutated,
  }: {
    project: ProjectEntry;
    onMutated: (updated: ProjectEntry) => void;
  } = $props();

  // The store lazily creates the panels object on first access.
  let panels = $derived<ProjectPanels>(commandLogsStore.forProject(project.id));

  let customArgs = $state("run lint");

  function badgeClass(running: boolean, exitCode: number | null): string {
    if (running) return "badge-running";
    if (exitCode === null) return "badge-idle";
    return exitCode === 0 ? "badge-ok" : "badge-fail";
  }
  function badgeLabel(running: boolean, exitCode: number | null): string {
    if (running) return "running";
    if (exitCode === null) return "idle";
    return exitCode === 0 ? "passed" : `failed (${exitCode})`;
  }

  async function start(panel: CommandPanel) {
    commandLogsStore.markRunning(project.id, panel);
    commandLogsStore.clear(project.id, panel);
    try {
      if (panel === "build") await runProjectBuild(project.id, project.path);
      else if (panel === "test") await runProjectTest(project.id, project.path);
      else {
        const args = customArgs.trim().split(/\s+/).filter((a) => a.length > 0);
        await runProjectCustom(project.id, project.path, args);
      }
      S.appendDrawerLog(`started ${panel} for ${project.name}`);
    } catch (e) {
      commandLogsStore.markExited(project.id, panel, 1);
      S.appendErrorLog(`${panel} failed to start: ${e}`);
    }
  }

  async function stop(panel: CommandPanel) {
    try {
      await stopProjectCommand(project.id, panel);
      commandLogsStore.markExited(project.id, panel, null);
      S.appendDrawerLog(`stopped ${panel} for ${project.name}`);
    } catch (e) {
      S.appendErrorLog(`stop ${panel} failed: ${e}`);
    }
  }
</script>

<div class="openmcp-settings">
  <section class="info-block">
    <p class="info-text">
      This folder is tracked as an <strong>Open-MCP repository</strong>. Run
      the build / test suite, or a custom npm script, and watch the output
      stream live. Logs are capped to the last 1000 lines.
    </p>
  </section>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Build</span>
      <span class={`status-badge ${badgeClass(panels.build.running, panels.build.lastExitCode)}`}>
        {badgeLabel(panels.build.running, panels.build.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.build.running}
          <Button variant="secondary" onclick={() => stop("build")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("build")}>Run npm build</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "build")}>Clear</button>
      </div>
    </div>
    <Console lines={panels.build.lines} title="npm run build" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Tests</span>
      <span class={`status-badge ${badgeClass(panels.test.running, panels.test.lastExitCode)}`}>
        {badgeLabel(panels.test.running, panels.test.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.test.running}
          <Button variant="secondary" onclick={() => stop("test")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("test")}>Run npm test</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "test")}>Clear</button>
      </div>
    </div>
    <Console lines={panels.test.lines} title="npm run test" />
  </div>

  <div class="panel-row">
    <div class="panel-head">
      <span class="panel-label">Custom</span>
      <span class={`status-badge ${badgeClass(panels.custom.running, panels.custom.lastExitCode)}`}>
        {badgeLabel(panels.custom.running, panels.custom.lastExitCode)}
      </span>
      <div class="panel-actions">
        {#if panels.custom.running}
          <Button variant="secondary" onclick={() => stop("custom")}>Stop</Button>
        {:else}
          <Button variant="primary" onclick={() => start("custom")}>Run</Button>
        {/if}
        <button type="button" class="link-btn" onclick={() => commandLogsStore.clear(project.id, "custom")}>Clear</button>
      </div>
    </div>
    <input
      class="custom-input"
      bind:value={customArgs}
      placeholder="run lint  (npm args; empty = npm install)"
      spellcheck="false"
    />
    <Console lines={panels.custom.lines} title="npm (custom)" />
  </div>

  <LineCounterPanel {project} />
</div>

<style>
  .openmcp-settings {
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
  .panel-row {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }
  .panel-head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }
  .panel-label {
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--hub-text);
    min-width: 4rem;
  }
  .status-badge {
    font-size: 0.65rem;
    font-weight: 700;
    text-transform: uppercase;
    padding: 0.1rem 0.4rem;
    border-radius: 3px;
  }
  .badge-idle { background: var(--hub-card); color: var(--hub-text-dim); }
  .badge-running { background: rgba(92, 124, 250, 0.2); color: #5c7cfa; }
  .badge-ok { background: rgba(86, 180, 130, 0.2); color: #56b482; }
  .badge-fail { background: rgba(224, 86, 86, 0.2); color: #e05656; }
  .panel-actions {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin-left: auto;
  }
  .custom-input {
    padding: 0.3rem 0.4rem;
    border: 1px solid var(--hub-border);
    border-radius: 0.3rem;
    background: var(--hub-bg);
    color: var(--hub-text);
    font-size: 0.78rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }
  .link-btn {
    background: transparent;
    border: none;
    color: var(--hub-accent, #5c7cfa);
    cursor: pointer;
    font-size: 0.7rem;
  }
</style>
