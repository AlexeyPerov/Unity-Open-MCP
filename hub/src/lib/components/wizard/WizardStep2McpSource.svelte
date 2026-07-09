<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import type { WizardState, WizardHandlers } from "./state.ts";
  import type { McpLaunchSourceMode } from "./launch_mode.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // The exclusive launch-source options. Plan 2 collapsed the checkbox stack
  // + hidden override into one radio selector; only inputs for the selected
  // mode are shown below it.
  const MODES: {
    id: McpLaunchSourceMode;
    label: string;
    blurb: string;
  }[] = [
    {
      id: "npx",
      label: "npx (published npm)",
      blurb:
        "Fetches the latest unity-open-mcp from npm on first spawn. No repo clone needed — the default for most users.",
    },
    {
      id: "global",
      label: "Global install",
      blurb:
        "Launches the bare unity-open-mcp binary (assumes npm i -g unity-open-mcp). Stable path for CI images.",
    },
    {
      id: "local",
      label: "Local checkout",
      blurb:
        "Points at a cloned unity-open-mcp monorepo. Packages + skill copy use the toolkit root; the client launches node <root>/mcp-server/dist/index.js.",
    },
    {
      id: "custom",
      label: "Custom entrypoint (advanced)",
      blurb:
        "A specific mcp-server/dist/index.js path. Owns the former override escape hatch — custom is a mode, not a hidden switch.",
    },
  ];
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Choose how the <code>unity-open-mcp</code> MCP server is launched. Pick
    one source — only the inputs for your choice are shown. Project, Unity
    version, and Node.js checks live on the Preflight step.
  </p>

  <div class="wiz-field">
    <span class="wiz-label">MCP server source</span>
    <div role="radiogroup" aria-label="MCP server source">
      {#each MODES as mode (mode.id)}
        <label class="wiz-radio wiz-radio-block" title={mode.blurb}>
          <input
            type="radio"
            name="wiz-mcp-source"
            value={mode.id}
            checked={state.mcpSourceMode === mode.id}
            onchange={() => handlers.setMcpSourceMode(mode.id)}
          />
          <span>
            <strong>{mode.label}</strong>
            <small>{mode.blurb}</small>
          </span>
        </label>
      {/each}
    </div>
    {#if !state.mcpSourceReady}
      <p class="wiz-hint wiz-hint-warn">
        {#if state.mcpSourceMode === "local" || state.mcpSourceMode === "custom"}
          Validate the toolkit root below to continue.
        {:else}
          Resolve the blocks on the Preflight step first.
        {/if}
      </p>
    {/if}
  </div>

  {#if state.mcpSourceMode === "local" || state.mcpSourceMode === "custom"}
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

  {#if state.mcpSourceMode === "custom"}
    <div class="wiz-field">
      <label class="wiz-label" for="wiz-mcp-override">Custom mcp-server/dist/index.js path</label>
      <input
        id="wiz-mcp-override"
        type="text"
        class="wiz-input"
        placeholder="/opt/builds/unity-open-mcp/index.js"
        value={state.mcpIndexOverride}
        oninput={(e) => handlers.setMcpIndexOverride((e.currentTarget as HTMLInputElement).value)}
      />
      <p class="wiz-hint">
        The exact entrypoint the client launches. Defaults to
        <code>{state.toolkitRoot || "<toolkit>"}/mcp-server/dist/index.js</code>
        when empty; Unity packages step URLs and skill copy always use the
        toolkit root regardless of this path.
      </p>
    </div>
  {/if}
</section>
