/**
 * Per-(project, panel) command log store for the Open-MCP settings
 * popup. The Rust command runner emits `cmd-log` events line-by-line
 * as the spawned process writes to stdout/stderr; this store routes
 * each line to the right ring buffer and tracks the running/exit
 * state so the UI can show a live badge + scrollable log.
 *
 * Ports the routing shape of vibe-launcher's `AppState.routeLog` but
 * keyed by `projectId` so multiple Open-MCP repos can run commands
 * concurrently without their logs intermixing.
 *
 * Panels: build / test / custom predate the maintainer workflow; the
 * npm-maintainer panels (version / publishDryRun / publish) cover the
 * Open-MCP maintainer panel (npm version / publish dry-run / publish);
 * `sync` covers the repo-wide `scripts/sync-version.mjs` runner.
 */
const MAX_LOG_LINES = 1000;

export interface PanelState {
  lines: string[];
  running: boolean;
  lastExitCode: number | null;
}

export interface ProjectPanels {
  build: PanelState;
  test: PanelState;
  custom: PanelState;
  version: PanelState;
  publishDryRun: PanelState;
  publish: PanelState;
  sync: PanelState;
}

function emptyPanel(): PanelState {
  return { lines: [], running: false, lastExitCode: null };
}

/**
 * Build a fresh, detached `ProjectPanels` object. Used by the Open-MCP
 * settings component as a non-mutating fallback for `$derived` so the
 * derived can read a stable shape before the store-side object is seeded
 * (the store seeds it in an `$effect`, since mutating the store from
 * inside a `$derived` throws `state_unsafe_mutation` in Svelte 5).
 */
export function emptyProjectPanels(): ProjectPanels {
  return {
    build: emptyPanel(),
    test: emptyPanel(),
    custom: emptyPanel(),
    version: emptyPanel(),
    publishDryRun: emptyPanel(),
    publish: emptyPanel(),
    sync: emptyPanel(),
  };
}

class CommandLogsStore {
  projects = $state<Record<string, ProjectPanels>>({});

  /** Returns (creating if needed) the panels object for a project. */
  forProject(projectId: string): ProjectPanels {
    if (!this.projects[projectId]) {
      this.projects[projectId] = {
        build: emptyPanel(),
        test: emptyPanel(),
        custom: emptyPanel(),
        version: emptyPanel(),
        publishDryRun: emptyPanel(),
        publish: emptyPanel(),
        sync: emptyPanel(),
      };
    }
    return this.projects[projectId];
  }

  /** Marks a panel as running (called when a command is spawned). */
  markRunning(projectId: string, panel: keyof ProjectPanels) {
    const p = this.forProject(projectId)[panel];
    p.running = true;
    p.lastExitCode = null;
  }

  /** Appends a log line, capping the buffer to MAX_LOG_LINES. */
  appendLine(projectId: string, panel: keyof ProjectPanels, line: string) {
    const p = this.forProject(projectId)[panel];
    p.lines.push(line);
    if (p.lines.length > MAX_LOG_LINES) {
      p.lines.splice(0, p.lines.length - MAX_LOG_LINES);
    }
  }

  /** Marks a panel as exited with the given code. */
  markExited(projectId: string, panel: keyof ProjectPanels, code: number | null) {
    const p = this.forProject(projectId)[panel];
    p.running = false;
    p.lastExitCode = code;
  }

  /** Clears a panel's log (keeps the running/exit state). */
  clear(projectId: string, panel: keyof ProjectPanels) {
    this.forProject(projectId)[panel].lines = [];
  }
}

export const commandLogsStore = new CommandLogsStore();
