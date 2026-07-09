<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Choose how the <code>unity-open-mcp</code> MCP server is launched.
    By default the wizard uses the published npm package via
    <code>npx</code> (no repo clone needed); enable
    <strong>Use local checkout</strong> to point at a cloned
    <code>unity-open-mcp</code> monorepo. Project, Unity version, and
    Node.js checks live on the previous step.
  </p>

  <div class="wiz-field">
    <span class="wiz-label">MCP server source</span>
    <label class="wiz-toggle">
      <input
        type="checkbox"
        checked={state.useLocalCheckout}
        onchange={(e) =>
          handlers.onUseLocalCheckoutChange((e.currentTarget as HTMLInputElement).checked)}
      />
      <span>
        <strong>Use local checkout</strong> —
        <small>
          Point at a cloned <code>unity-open-mcp</code> monorepo instead of the
          published npm package. The Configure AI client step then launches
          <code>node &lt;root&gt;/mcp-server/dist/index.js</code>, and the
          Unity packages + skill copy steps use the toolkit root.
        </small>
      </span>
    </label>
    {#if !state.useLocalCheckout}
      <label class="wiz-toggle">
        <input
          type="checkbox"
          checked={state.useGlobalInstall}
          onchange={(e) =>
            handlers.setUseGlobalInstall((e.currentTarget as HTMLInputElement).checked)}
        />
        <span>
          <strong>Use a global install</strong> —
          <small>
            The Configure AI client step launches the bare <code>unity-open-mcp</code> binary
            (assumes <code>npm i -g unity-open-mcp</code>) instead of
            <code>npx -y unity-open-mcp@latest</code>.
          </small>
        </span>
      </label>
      <p class="wiz-hint wiz-hint-ok">
        Default: the wizard writes <code>npx -y unity-open-mcp@latest</code>
        as the MCP launch command. Node {state.nodeMajor ?? "≥"}18 fetches the
        package from npm on first spawn.
      </p>
    {/if}
  </div>

  {#if state.useLocalCheckout}
    <div class="wiz-field">
      <label class="wiz-label" for="wiz-toolkit-root">AI toolkit root</label>
      <div class="wiz-input-row">
        <input
          id="wiz-toolkit-root"
          type="text"
          class="wiz-input"
          placeholder="/Users/you/unity-open-mcp"
          value={state.toolkitRoot}
          oninput={(e) => handlers.onToolkitRootInput((e.currentTarget as HTMLInputElement).value)}
        />
        <Button variant="secondary" onclick={handlers.pickToolkitFolder} disabled={state.pickToolkitInFlight}>
          {state.pickToolkitInFlight ? "Selecting…" : "Browse…"}
        </Button>
        <Button
          variant="secondary"
          onclick={handlers.runToolkitValidation}
          disabled={state.toolkitValidating || !state.toolkitRoot.trim()}
        >
          {state.toolkitValidating ? "Validating…" : state.toolkitRootDirty ? "Validate" : "Re-check"}
        </Button>
      </div>
      {#if state.toolkitError}
        <p class="wiz-hint wiz-hint-warn">{state.toolkitError}</p>
      {/if}
      {#if state.toolkitValidation}
        <ul class="wiz-fingerprints" aria-label="Toolkit root fingerprint checks">
          {#each state.toolkitValidation.fingerprints as fp (fp.relativePath)}
            {@const tone =
              fp.exists && fp.kindOk === true
                ? "ok"
                : fp.exists && fp.kindOk === false
                  ? "warn"
                  : "missing"}
            <li class="wiz-fp wiz-fp-{tone}">
              <span class="wiz-fp-name"><code>{fp.relativePath}</code></span>
              <span class="wiz-fp-status">
                {#if tone === "ok"}
                  ok
                {:else if tone === "warn"}
                  wrong kind
                {:else}
                  missing
                {/if}
              </span>
            </li>
          {/each}
        </ul>
        {#if state.toolkitValidation.mcpDistMissing}
          <p class="wiz-hint wiz-hint-warn">
            <code>mcp-server/dist/index.js</code> is not built.
            Run <code>npm run build</code> in
            <code>{state.toolkitRoot}/mcp-server/</code> and re-check.
          </p>
        {/if}
      {/if}
    </div>
  {/if}

  <details class="wiz-advanced">
    <summary>Advanced — MCP server path override</summary>
    <div class="wiz-field">
      <label class="wiz-label" for="wiz-mcp-override">Custom mcp-server/dist/index.js</label>
      <input
        id="wiz-mcp-override"
        type="text"
        class="wiz-input"
        placeholder="/opt/builds/unity-open-mcp/index.js"
        value={state.mcpIndexOverride}
        oninput={(e) => handlers.setMcpIndexOverride((e.currentTarget as HTMLInputElement).value)}
      />
      <p class="wiz-hint">
        Leave empty to use <code>{state.toolkitRoot || "<toolkit>"}/mcp-server/dist/index.js</code>.
        Unity packages step URLs and skill copy always use the
        toolkit root regardless of this override.
      </p>
    </div>
  </details>
</section>
