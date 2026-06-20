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
 * Plan 3 maintainer panel.
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
}

function emptyPanel(): PanelState {
  return { lines: [], running: false, lastExitCode: null };
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
