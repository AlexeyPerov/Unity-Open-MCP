<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import type { BundleStrategy } from "$lib/services/config";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { previewBundleFor } from "./helpers.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();

  let upgradeProject = $derived(
    state.upgradeModalProjectId ? projectsStore.find(state.upgradeModalProjectId) ?? null : null,
  );
  let upgradePreview = $derived(previewBundleFor("0.0.0", state.upgradeStrategy));
</script>

{#if state.upgradeModalProjectId && upgradeProject}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="upgrade-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="upgrade-modal-title"
    onclick={(e) => { if (e.target === e.currentTarget) handlers.closeUpgradeModal(); }}
    onkeydown={(e) => { if (e.key === "Escape" && !state.upgradeLoading) handlers.closeUpgradeModal(); }}
  >
    <div class="upgrade-modal">
      <header class="upgrade-header">
        <h2 id="upgrade-modal-title" class="upgrade-title">Upgrade Unity version</h2>
        {#if !state.upgradeLoading}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close upgrade modal"
            onclick={handlers.closeUpgradeModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="upgrade-body">
        <p class="upgrade-desc">
          Rewrite <code>ProjectSettings/ProjectVersion.txt</code> for
          <strong>{upgradeProject.name}</strong> and bump
          <code>ProjectSettings/ProjectManager.asset</code>'s
          <code>bundleVersion</code> per the strategy below. The
          previous file contents are snapshotted and restored if any
          write fails, so a partial upgrade never leaves the project
          in a mixed state. The exact previous bundleVersion is
          surfaced in the result banner after the upgrade completes.
        </p>

        <section class="upgrade-field">
          <span class="upgrade-label">Current state</span>
          <dl class="upgrade-summary">
            <div>
              <dt>Project path</dt>
              <dd><code title={upgradeProject.path}>{upgradeProject.path}</code></dd>
            </div>
            <div>
              <dt>Current Unity version</dt>
              <dd>
                {#if upgradeProject.unityVersion}
                  <code>{upgradeProject.unityVersion}</code>
                {:else}
                  <em>unknown</em>
                {/if}
              </dd>
            </div>
          </dl>
        </section>

        <section class="upgrade-field">
          <label class="upgrade-label" for="upgrade-target">Target Unity version</label>
          {#if state.upgradeCandidatesList.length === 0}
            <p class="upgrade-empty">
              {#if state.upgradeLoading}
                Loading installed versions…
              {:else}
                No installed Unity version is strictly higher than
                <code>{upgradeProject.unityVersion ?? "unknown"}</code>.
                Install a newer Unity via Unity Hub and click Refresh to
                try again.
              {/if}
            </p>
          {:else}
            <Select
              id="upgrade-target"
              options={state.upgradeCandidatesList.map((v) => ({ value: v, label: v }))}
              value={state.upgradeTargetVersion}
              onchange={(v) => handlers.setUpgradeTargetVersion(v)}
              disabled={state.upgradeLoading}
            />
          {/if}
        </section>

        <section class="upgrade-field">
          <span class="upgrade-label">bundleVersion bump</span>
          <div class="upgrade-strategy" role="radiogroup" aria-label="Bundle version bump strategy">
            <label class="upgrade-strategy-option">
              <input
                type="radio"
                name="upgrade-strategy"
                value="none"
                checked={state.upgradeStrategy === "none"}
                disabled={state.upgradeLoading}
                onchange={() => handlers.setUpgradeStrategy("none" as BundleStrategy)}
              />
              <span>
                <strong>None</strong>
                <span class="upgrade-strategy-hint">Leave bundleVersion untouched (only the project version line is rewritten).</span>
              </span>
            </label>
            <label class="upgrade-strategy-option">
              <input
                type="radio"
                name="upgrade-strategy"
                value="patch"
                checked={state.upgradeStrategy === "patch"}
                disabled={state.upgradeLoading}
                onchange={() => handlers.setUpgradeStrategy("patch" as BundleStrategy)}
              />
              <span>
                <strong>Patch</strong>
                <span class="upgrade-strategy-hint">Bump the patch number (e.g. 1.2.3 → 1.2.4). Default.</span>
              </span>
            </label>
            <label class="upgrade-strategy-option">
              <input
                type="radio"
                name="upgrade-strategy"
                value="minor"
                checked={state.upgradeStrategy === "minor"}
                disabled={state.upgradeLoading}
                onchange={() => handlers.setUpgradeStrategy("minor" as BundleStrategy)}
              />
              <span>
                <strong>Minor</strong>
                <span class="upgrade-strategy-hint">Bump the minor number and zero the patch (e.g. 1.2.3 → 1.3.0).</span>
              </span>
            </label>
            <label class="upgrade-strategy-option">
              <input
                type="radio"
                name="upgrade-strategy"
                value="major"
                checked={state.upgradeStrategy === "major"}
                disabled={state.upgradeLoading}
                onchange={() => handlers.setUpgradeStrategy("major" as BundleStrategy)}
              />
              <span>
                <strong>Major</strong>
                <span class="upgrade-strategy-hint">Bump the major number and zero the rest (e.g. 1.2.3 → 2.0.0).</span>
              </span>
            </label>
          </div>
          <p class="upgrade-preview">
            <span class="upgrade-preview-label">Preview (assuming current = 0.0.0):</span>
            <code>0.0.0</code>
            <span class="upgrade-preview-arrow" aria-hidden="true">→</span>
            <code><strong>{upgradePreview.next || "0.0.0"}</strong></code>
          </p>
        </section>

        {#if state.upgradeError}
          <p class="upgrade-error" role="alert">{state.upgradeError}</p>
        {/if}
      </div>

      <footer class="upgrade-footer">
        <Button variant="secondary" onclick={handlers.closeUpgradeModal} disabled={state.upgradeLoading}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onclick={handlers.submitUpgrade}
          disabled={state.upgradeLoading || state.upgradeCandidatesList.length === 0 || !state.upgradeTargetVersion}
        >
          {state.upgradeLoading ? "Upgrading…" : "Upgrade"}
        </Button>
      </footer>
    </div>
  </div>
{/if}
