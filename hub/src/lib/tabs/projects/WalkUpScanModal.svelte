<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import { walkUpScanStore } from "$lib/state/walk_up_scan.svelte";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { kindLabel } from "./helpers.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();
</script>

{#if state.walkUpModalOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="walkup-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="walkup-modal-title"
    onclick={(e) => { if (e.target === e.currentTarget) handlers.closeWalkUpModal(); }}
    onkeydown={(e) => { if (e.key === "Escape" && !walkUpScanStore.scanning) handlers.closeWalkUpModal(); }}
  >
    <div class="walkup-modal">
      <header class="walkup-header">
        <h2 id="walkup-modal-title" class="walkup-title">Add Multiple Projects</h2>
        {#if !walkUpScanStore.scanning}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close add multiple projects"
            onclick={handlers.closeWalkUpModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="walkup-body">
        <p class="walkup-desc">
          Hub will recurse into the selected folder and append every
          folder that matches one of the enabled project types below to
          the project list as <code>source: walk-up</code>.
        </p>

        <section class="walkup-config">
          <h3 class="walkup-section-title">Project types to scan</h3>
          <div class="walkup-kinds">
            <label class="walkup-kind-row" class:disabled={walkUpScanStore.scanning}>
              <input
                type="checkbox"
                checked={state.walkUpKinds.unity}
                disabled={walkUpScanStore.scanning}
                onchange={(e) =>
                  settingsStore.setWalkUpKind("unity", (e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="walkup-kind-label">
                <span class="walkup-kind-name">{kindLabel("unity")}</span>
                <span class="walkup-kind-desc">
                  Folders with <code>Assets/</code> and <code>ProjectSettings/</code>.
                </span>
              </span>
            </label>
            <label class="walkup-kind-row" class:disabled={walkUpScanStore.scanning}>
              <input
                type="checkbox"
                checked={state.walkUpKinds.package}
                disabled={walkUpScanStore.scanning}
                onchange={(e) =>
                  settingsStore.setWalkUpKind("package", (e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="walkup-kind-label">
                <span class="walkup-kind-name">{kindLabel("package")}</span>
                <span class="walkup-kind-desc">
                  Folders with a root <code>package.json</code> (UPM packages).
                </span>
              </span>
            </label>
            <label class="walkup-kind-row" class:disabled={walkUpScanStore.scanning}>
              <input
                type="checkbox"
                checked={state.walkUpKinds.openMcp}
                disabled={walkUpScanStore.scanning}
                onchange={(e) =>
                  settingsStore.setWalkUpKind("openMcp", (e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="walkup-kind-label">
                <span class="walkup-kind-name">{kindLabel("openMcp")}</span>
                <span class="walkup-kind-desc">
                  Repos with an <code>mcp-server/</code> directory and a root <code>package.json</code>.
                </span>
              </span>
            </label>
            <label class="walkup-kind-row" class:disabled={walkUpScanStore.scanning}>
              <input
                type="checkbox"
                checked={state.walkUpKinds.custom}
                disabled={walkUpScanStore.scanning}
                onchange={(e) =>
                  settingsStore.setWalkUpKind("custom", (e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="walkup-kind-label">
                <span class="walkup-kind-name">{kindLabel("custom")}</span>
                <span class="walkup-kind-desc">
                  Any other folder. Only leaf folders (no subdirectories) are added to avoid noise.
                </span>
              </span>
            </label>
          </div>
        </section>

        <section class="walkup-config">
          <h3 class="walkup-section-title">Selected Folder</h3>
          {#if settingsStore.current && settingsStore.current.unityDiscovery.walkUpRoots.length > 0}
            <ul class="walkup-roots">
              {#each settingsStore.current.unityDiscovery.walkUpRoots as root (root)}
                <li class="walkup-root" title={root}>{root}</li>
              {/each}
            </ul>
          {:else}
            <p class="walkup-empty">No folder selected</p>
          {/if}
          <dl class="walkup-config-list">
            <div>
              <dt>Max depth</dt>
              <dd>{settingsStore.current?.unityDiscovery.walkUpMaxDepth ?? 4}</dd>
            </div>
            <div>
              <dt>Follow symlinks</dt>
              <dd>
                {settingsStore.current?.unityDiscovery.walkUpFollowSymlinks ? "yes" : "no"}
              </dd>
            </div>
            <div>
              <dt>Keep partial on cancel</dt>
              <dd>
                {settingsStore.current?.unityDiscovery.walkUpKeepPartial ? "yes" : "no"}
              </dd>
            </div>
          </dl>
        </section>

        {#if walkUpScanStore.scanning}
          <section class="walkup-progress" aria-live="polite">
            <h3 class="walkup-section-title">Scanning…</h3>
            <dl class="walkup-progress-list">
              <div>
                <dt>Current root</dt>
                <dd>{walkUpScanStore.currentRoot ?? "—"}</dd>
              </div>
              <div>
                <dt>Current depth</dt>
                <dd>
                  {walkUpScanStore.currentDepth ?? 0} / {walkUpScanStore.maxDepth ?? 0}
                </dd>
              </div>
              <div>
                <dt>Found so far</dt>
                <dd>{walkUpScanStore.foundSoFar}</dd>
              </div>
              <div>
                <dt>Visited dirs</dt>
                <dd>{walkUpScanStore.visitedDirs}</dd>
              </div>
            </dl>
          </section>
        {:else if walkUpScanStore.lastResult}
          <section class="walkup-done" aria-live="polite">
            <h3 class="walkup-section-title">
              {walkUpScanStore.lastResult.status === "cancelled"
                ? "Cancelled"
                : walkUpScanStore.lastResult.status === "failed"
                  ? "Failed"
                  : "Done"}
            </h3>
            {#if handlers.lastScanSummary()}
              {@const s = handlers.lastScanSummary()}
              <p class="walkup-done-line">
                Added <strong>{s?.added}</strong>
                {#if s && s.skipped > 0}
                  , skipped <strong>{s?.skipped}</strong> already in list
                {/if}.
              </p>
              {#if walkUpScanStore.lastResult.error}
                <p class="walkup-error">{walkUpScanStore.lastResult.error}</p>
              {/if}
            {/if}
          </section>
        {/if}

        {#if state.addError && state.walkUpModalOpen}
          <p class="walkup-error" role="alert">{state.addError}</p>
        {/if}
      </div>

      <footer class="walkup-footer">
        {#if walkUpScanStore.scanning}
          <Button variant="destructive" onclick={handlers.cancelWalkUpFromModal}>
            Cancel scan
          </Button>
          <span class="walkup-footer-hint">
            The scan checks the cancel flag at every directory
            boundary — it will stop within a few milliseconds.
          </span>
        {:else}
          <Button variant="secondary" onclick={handlers.closeWalkUpModal}>
            Close
          </Button>
          <Button
            variant="secondary"
            onclick={handlers.handleWalkUpSelectFolder}
            disabled={state.pickingWalkUpFolder}
          >
            {state.pickingWalkUpFolder ? "Selecting…" : "Select folder"}
          </Button>
          <Button
            variant="primary"
            onclick={handlers.startWalkUpFromModal}
            disabled={
              !settingsStore.current ||
              settingsStore.current.unityDiscovery.walkUpRoots.length === 0 ||
              (!state.walkUpKinds.unity &&
                !state.walkUpKinds.package &&
                !state.walkUpKinds.openMcp &&
                !state.walkUpKinds.custom)
            }
          >
            {walkUpScanStore.lastResult ? "Run again" : "Start scan"}
          </Button>
        {/if}
      </footer>
    </div>
  </div>
{/if}
