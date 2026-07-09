<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import {
    builtinEmbeddedDomains,
    installableEmbeddedDomains,
  } from "$lib/services/extensions";
  import {
    changeKindLabel,
    changeKindTone,
    shortPackageName,
    summarizeChanges,
  } from "$lib/services/manifest";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Static catalog snapshot — embedded domains advertised by the wizard.
  // `installable` domains get a toggle; `builtin` ones render as info-only
  // "always-on" cards because the Unity module ships with the Editor.
  const installableDomains = installableEmbeddedDomains();
  const builtinDomains = builtinEmbeddedDomains();
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    This step adds bridge + verify packages to the project's
    <code>Packages/manifest.json</code>. Domain tools (NavMesh,
    Input System, ProBuilder, Particle System, Animation) are
    <strong>bundled with the bridge</strong> — they activate
    automatically once the matching Unity package is present, so
    you install the bridge once and toggle Unity domain deps in
    the section below. The diff preview is live — it re-computes
    whenever you change a toggle, version pin, custom URL, or
    local-package mode. Enable <strong>Use local packages</strong>
    to install via <code>file:</code> paths from the toolkit root
    (typical for projects inside this monorepo). An upgrade
    (existing entry with a different URL or tag) always requires
    explicit confirmation before the wizard will write. Unrelated
    dependency entries are preserved verbatim.
  </p>

  <div class="wiz-field">
    <label class="wiz-toggle">
      <input
        type="checkbox"
        checked={state.installBridge}
        onchange={(e) => handlers.setInstallBridge((e.currentTarget as HTMLInputElement).checked)}
      />
      <span><strong>Install Unity Open MCP Bridge</strong> — required for live MCP tooling</span>
    </label>
    <label class="wiz-toggle">
      <input
        type="checkbox"
        checked={state.installVerify}
        onchange={(e) => handlers.setInstallVerify((e.currentTarget as HTMLInputElement).checked)}
      />
      <span>
        <strong>Install Unity Open MCP Verify</strong> —
        <small>Scoped health checks for AI gates — not the full Unity Scanner window.</small>
      </span>
    </label>
    <label class="wiz-toggle">
      <input
        type="checkbox"
        checked={state.useLocalPackages}
        onchange={(e) =>
          handlers.onUseLocalPackagesChange((e.currentTarget as HTMLInputElement).checked)}
      />
      <span>
        <strong>Use local packages</strong> —
        <small>
          Install via <code>file:</code> paths relative to the toolkit root
          (e.g. <code>file:../../packages/bridge</code>).
        </small>
      </span>
    </label>
    <label class="wiz-toggle">
      <input type="checkbox" checked={state.installScanner} disabled />
      <span>
        <strong>Also install Unity Scanner</strong> —
        <small>Full upstream product for inspection in the Editor (advanced, off by default).</small>
      </span>
    </label>
  </div>

  <details class="wiz-advanced">
    <summary>Advanced package options</summary>
    <div class="wiz-field">
      <label class="wiz-label" for="wiz-pkg-pin">Package version pin (tag)</label>
      <input
        id="wiz-pkg-pin"
        type="text"
        class="wiz-input"
        placeholder="bridge-v1.0.0"
        value={state.packageVersionPin}
        disabled={state.useLocalPackages}
        oninput={(e) => handlers.setPackageVersionPin((e.currentTarget as HTMLInputElement).value)}
      />
      <p class="wiz-hint">
        Applied to both packages. Leave empty to use the
        default tag from the toolkit root
        (e.g. <code>bridge-v1.0.0</code>).
      </p>
    </div>
    <div class="wiz-field">
      <label class="wiz-label" for="wiz-pkg-url">Custom git URL (dev builds)</label>
      <input
        id="wiz-pkg-url"
        type="text"
        class="wiz-input"
        placeholder="https://github.com/your-fork/unity-open-mcp.git"
        value={state.packageCustomUrl}
        disabled={state.useLocalPackages}
        oninput={(e) => handlers.setPackageCustomUrl((e.currentTarget as HTMLInputElement).value)}
      />
      <p class="wiz-hint">
        Replaces the toolkit root's git remote. Useful for
        testing against a fork; not required for the
        standard monorepo flow.
      </p>
    </div>
  </details>

  <details class="wiz-advanced">
    <summary>Unity domain dependencies (optional)</summary>
    <p class="wiz-hint">
      Domain tools (NavMesh, Input System, ProBuilder, Particle System,
      Animation) are <strong>bundled with the bridge</strong> — there is
      no separate install. They activate automatically once the matching
      Unity package is present in the project. Toggle the dependencies
      you want the wizard to add to <code>Packages/manifest.json</code>;
      the bridge's embedded tools compile in after Unity re-imports the
      manifest. Built-in Unity modules (Particle System, Animation) ship
      with the Editor and need no manifest entry — they are listed
      below for visibility.
    </p>

    {#if installableDomains.length === 0}
      <p class="wiz-hint">No installable Unity domain dependencies shipped with this toolkit version.</p>
    {:else}
      <ul class="wiz-extension-packs">
        {#each installableDomains as dep (dep.upmDependency)}
          {@const checked = state.selectedUnityDomainDeps.has(dep.upmDependency)}
          <li class="wiz-extension-pack">
            <label class="wiz-toggle">
              <input
                type="checkbox"
                checked={checked}
                onchange={(e) =>
                  handlers.toggleUnityDomainDep(dep.upmDependency, (e.currentTarget as HTMLInputElement).checked)}
              />
              <span>
                <strong>{dep.displayName}</strong>
                <small>
                  installs <code>{dep.upmDependency}@{dep.defaultVersion}</code>
                  · {dep.toolIds.length} tool(s)
                </small>
                <small>{dep.description}</small>
              </span>
            </label>
          </li>
        {/each}
      </ul>
    {/if}

    {#if builtinDomains.length > 0}
      <p class="wiz-hint wiz-hint-info">
        Always-on (built-in Unity module, no install needed):
        {builtinDomains.map((d) => d.displayName).join(", ")}.
      </p>
    {/if}

    <p class="wiz-hint">
      Contributor / community-pack path: the legacy
      <code>com.alexeyperov.unity-open-mcp-ext-*</code> UPM packages
      are no longer required for shipped domains (M18 Plan 4) — they
      remain in <code>packages/extensions/</code> for third-party
      packs only. See the manual setup guide for the
      <code>file:</code> workflow.
    </p>
  </details>

  <div class="wiz-field">
    <span class="wiz-label">Manifest status</span>
    {#if !state.installBridge && !state.installVerify && state.selectedUnityDomainDeps.size === 0}
      <p class="wiz-hint wiz-hint-warn">
        Pick at least one package to install.
      </p>
    {:else if !state.toolkitRoot.trim() || !state.toolkitValidation?.ok}
      <p class="wiz-hint wiz-hint-warn">
        Validate the toolkit root on the MCP server source step first.
      </p>
    {:else if state.mergePlanning && !state.mergePlan}
      <p class="wiz-hint">Planning merge…</p>
    {:else if state.mergePlan}
      {#if state.showLocalPackagesInfo}
        <p class="wiz-hint wiz-hint-info">{state.localPackagesInfoText}</p>
      {/if}
      {#if state.manifestParseError}
        <div class="wiz-block wiz-block-error" role="alert">
          <strong>Cannot parse <code>Packages/manifest.json</code>.</strong>
          {state.manifestParseError} Fix the JSON by hand and re-run.
        </div>
      {:else if !state.mergePlan.manifestRead.present}
        <p class="wiz-hint">
          No <code>Packages/manifest.json</code> on disk yet — the
          wizard will create one with the selected entries.
        </p>
      {/if}

      {#if !state.manifestParseError}
        <ul class="wiz-fingerprints" aria-label="Merge plan">
          {#each state.mergePlan.changes as change (change.id)}
            <li class="wiz-fp wiz-fp-{changeKindTone(change.kind)}">
              <span class="wiz-fp-name">
                <code>{shortPackageName(change.id)}</code>
              </span>
              <span class="wiz-fp-status">
                {changeKindLabel(change.kind)}
              </span>
            </li>
          {/each}
        </ul>

        {#if state.mergePlan.hasUpgrades}
          <label class="wiz-toggle wiz-toggle-confirm">
            <input
              type="checkbox"
              checked={state.upgradeAcknowledged}
              onchange={(e) => handlers.setUpgradeAcknowledged((e.currentTarget as HTMLInputElement).checked)}
            />
            <span>
              <strong>I understand the manifest will be upgraded.</strong>
              <small>The existing bridge/verify entries differ from the proposed values (different tag or git remote). The wizard will overwrite them only after this confirmation.</small>
            </span>
          </label>
        {/if}

        <details
          class="wiz-advanced"
          open={state.showDiff}
          ontoggle={(e) => handlers.setShowDiff((e.currentTarget as HTMLDetailsElement).open)}
        >
          <summary>Preview manifest diff</summary>
          <pre class="wiz-codeblock" aria-label="Manifest diff">{state.diffPreviewText}</pre>
          {#if state.mergePlan.derivedUrls.gitRemote}
            <p class="wiz-hint">
              Using git remote <code>{state.mergePlan.derivedUrls.gitRemote}</code>
              derived from the toolkit root
              {#if state.packageCustomUrl.trim()}(overridden by the custom URL field){/if}.
            </p>
          {/if}
        </details>
      {/if}
    {/if}
  </div>

  {#if state.mergeResult}
    <div class="wiz-block wiz-block-ok" role="status">
      <strong>Manifest written.</strong>
      {summarizeChanges(state.mergeResult.changes)}.
      {#if state.mergeResult.backupPath}
        Backup saved to <code>{state.mergeResult.backupPath}</code>.
      {/if}
    </div>
  {/if}
  {#if state.mergeError}
    <div class="wiz-block wiz-block-error" role="alert">
      {state.mergeError}
    </div>
  {/if}

  <div class="wiz-actions-row">
    <Button
      variant="primary"
      onclick={handlers.installManifest}
      disabled={
        !state.manifestReady ||
        state.mergeWriting ||
        Boolean(state.mergePlan?.hasUpgrades && state.hasRealChanges && !state.upgradeAcknowledged)
      }
    >
      {state.mergeWriting
        ? "Installing…"
        : state.hasRealChanges
          ? state.mergePlan?.hasUpgrades
            ? "Upgrade manifest"
            : "Install"
          : "Already installed"}
    </Button>
    <Button
      variant="secondary"
      onclick={handlers.skipToMcpClient}
      disabled={!state.canSkipStep3}
      title={state.canSkipStep3
        ? "Skip to MCP client config"
        : "Validate the toolkit root and resolve manifest blocks first"}
    >
      Skip to MCP client config
    </Button>
  </div>
</section>
