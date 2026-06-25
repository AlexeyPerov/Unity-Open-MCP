/**
 * Shared log drawer store (Svelte 5 runes).
 *
 * Mirrors the Hub's `state.svelte.ts` drawer-log pattern: a bounded
 * ring of timestamped lines plus an `expanded` flag. `appendError`
 * forces the drawer open so failures are visible. Everything that the
 * backend surfaces (scenario load errors, persistence failures, project
 * selection) flows through here so the operator has one place to look.
 */

const MAX_LINES = 500;

class LogsStore {
  lines = $state<string[]>([]);
  expanded = $state(false);

  private append(line: string, openOnError: boolean) {
    const ts = new Date().toLocaleTimeString();
    this.lines = [...this.lines, `${ts}  ${line}`].slice(-MAX_LINES);
    if (openOnError) this.expanded = true;
  }

  /** Append an informational line. */
  log(line: string) {
    this.append(line, false);
  }

  /** Append an error line and force the drawer open. */
  error(line: string) {
    this.append(`[error] ${line}`, true);
  }

  clear() {
    this.lines = [];
  }
}

export const logs = new LogsStore();
