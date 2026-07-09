<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import {
    CLIENT_CATEGORY_LABELS,
    MCP_CLIENT_OPTIONS,
    clientKind,
  } from "./constants.ts";
  import { describeMcpConfigError } from "./error_descriptors.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Filtered + grouped view of the client picker. The search matches
  // label or id (case-insensitive); the currently-selected client is
  // always kept visible so the preview/write actions stay reachable.
  let filtered = $derived.by(() => {
    const q = state.mcpClientSearch.trim().toLowerCase();
    if (!q) return MCP_CLIENT_OPTIONS;
    return MCP_CLIENT_OPTIONS.filter(
      (o) =>
        o.label.toLowerCase().includes(q) ||
        o.id.toLowerCase().includes(q) ||
        o.id === state.mcpClient,
    );
  });

  let grouped = $derived.by(() => {
    const groups: { category: "ide" | "cli" | "manual"; items: typeof MCP_CLIENT_OPTIONS }[] = [];
    for (const cat of ["ide", "cli", "manual"] as const) {
      const items = filtered.filter((o) => o.category === cat);
      if (items.length > 0) groups.push({ category: cat, items });
    }
    return groups;
  });
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    This step writes a <code>unity-open-mcp</code> MCP server
    entry to your client config. The launch command comes from your
    MCP server source choice — <code>npx -y unity-open-mcp@latest</code> by
    default, or <code>node &lt;root&gt;/mcp-server/dist/index.js</code>
    when <strong>Use local checkout</strong> is on. The wizard calls
    the Rust planner on every form-state change so the live preview
    matches exactly what the writer will emit:
    <code>mcpServers.unity-open-mcp</code> for Cursor / Claude Desktop,
    <code>mcp.unity-open-mcp</code> for OpenCode, a
    <code>claude mcp add</code> command for Claude Code, and a copyable
    snippet for Manual. Unrelated MCP servers are merged through
    unchanged.
  </p>

  <div class="wiz-field">
    <div class="wiz-label-row">
      <span class="wiz-label">MCP client</span>
      <input
        type="search"
        class="wiz-input wiz-input-small wiz-client-search"
        placeholder="Filter clients…"
        value={state.mcpClientSearch}
        oninput={(e) =>
          handlers.setMcpClientSearch((e.currentTarget as HTMLInputElement).value)}
        aria-label="Filter MCP clients"
      />
    </div>
    <div role="radiogroup" aria-label="MCP client">
      {#each grouped as group (group.category)}
        <div class="wiz-client-group">
          <p class="wiz-client-group-label">{CLIENT_CATEGORY_LABELS[group.category]}</p>
          <div class="wiz-radio-grid">
            {#each group.items as opt (opt.id)}
              <label class="wiz-radio" title={opt.sharedWith}>
                <input
                  type="radio"
                  name="wiz-mcp-client"
                  value={opt.id}
                  checked={state.mcpClient === opt.id}
                  onchange={() => handlers.setMcpClient(opt.id)}
                />
                <span>
                  <strong>{opt.label}</strong>
                  <small>
                    {#if opt.kind === "file"}writes config file{/if}
                    {#if opt.kind === "cli"}CLI command only{/if}
                    {#if opt.kind === "clipboard"}copy JSON to clipboard{/if}
                  </small>
                </span>
              </label>
            {/each}
          </div>
        </div>
      {/each}
    </div>
  </div>

  {#if state.mcpClient === "cursor"}
    <div class="wiz-field">
      <label class="wiz-toggle">
        <input
          type="checkbox"
          checked={state.cursorProjectScope}
          onchange={(e) => handlers.setCursorProjectScope((e.currentTarget as HTMLInputElement).checked)}
        />
        <span>
          <strong>Use project-scoped config</strong> —
          <small>write to <code>{state.projectPath}/.cursor/mcp.json</code> instead of <code>~/.cursor/mcp.json</code>.</small>
        </span>
      </label>
    </div>
  {/if}

  <div class="wiz-field">
    <label class="wiz-label" for="wiz-bridge-port">Bridge HTTP port</label>
    <input
      id="wiz-bridge-port"
      type="text"
      class="wiz-input wiz-input-small"
      placeholder="(auto)"
      value={state.bridgePort}
      oninput={(e) => handlers.setBridgePort((e.currentTarget as HTMLInputElement).value)}
    />
    {#if !state.bridgePort.trim() && state.resolvedBridgePort != null}
      <p class="wiz-hint">
        Auto-derived from project path: <code>{state.resolvedBridgePort}</code>.
        Override only if you pin a specific port.
      </p>
    {/if}
  </div>

  <div class="wiz-field">
    <span class="wiz-label">
      {state.mcpPlan?.command ? "Claude Code command" : "Generated config"}
    </span>
    {#if state.mcpPlanning && !state.mcpPlan}
      <p class="wiz-hint">Planning…</p>
    {:else if !state.mcpPlan}
      <p class="wiz-hint wiz-hint-warn">
        {#if state.useLocalCheckout}
          Set and validate the toolkit root on the MCP server source step to generate a config.
        {:else}
          Waiting for the planner — the default <code>npx</code> launch
          command needs no toolkit root.
        {/if}
      </p>
    {:else}
      <pre class="wiz-codeblock" aria-label={state.mcpPlan.command ? "Claude Code command" : "Generated MCP config"}>{state.mcpPreviewText || "—"}</pre>
      {#if state.mcpPlan.targetPath}
        <p class="wiz-hint">
          Target: <code>{state.mcpPlan.targetPath}</code>
          {#if state.mcpPlan.fileExists}
            <span class="wiz-tag wiz-tag-warn">file exists</span>
          {:else}
            <span class="wiz-tag wiz-tag-ok">new file</span>
          {/if}
          {#if !state.mcpPlan.wouldWrite && state.mcpPlan.fileExists}
            <span class="wiz-tag wiz-tag-ok">already up to date</span>
          {/if}
        </p>
      {/if}
      {#if state.mcpPlan.preservedKeys.length > 0}
        <p class="wiz-hint">
          Preserved top-level keys: {state.mcpPlan.preservedKeys
            .filter((k) => !["mcpServers", "mcp"].includes(k))
            .map((k) => `<code>${k}</code>`)
            .join(", ") || "<em>none</em>"}
          {#if state.mcpPlan.preservedKeys.some((k) => ["mcpServers", "mcp"].includes(k))}
            ; other servers under <code>mcpServers</code> / <code>mcp</code> are also kept.
          {/if}
        </p>
      {/if}
      {#if state.mcpPlan.command}
        <p class="wiz-hint">
          Claude Code is CLI-only — the wizard
          renders the <code>claude mcp add</code> command
          and never writes a config file.
        </p>
      {/if}
    {/if}
    {#if state.resolvedMcpPathValid === false && state.useLocalCheckout}
      <p class="wiz-hint wiz-hint-warn">
        Resolved MCP path does not exist on disk:
        <code>{state.resolvedMcpPath}</code>.
        Run <code>npm run build</code> in
        <code>{state.toolkitRoot}/mcp-server/</code>.
      </p>
    {/if}
  </div>

  {#if state.mcpWriteResult?.wouldWrite}
    <div class="wiz-block wiz-block-ok" role="status">
      <strong>MCP config written.</strong>
      Saved to <code>{state.mcpWriteResult.targetPath}</code>.
      {#if state.mcpWriteResult.backupPath}
        Backup at <code>{state.mcpWriteResult.backupPath}</code>.
      {/if}
    </div>
  {:else if state.mcpWriteResult && !state.mcpWriteResult.wouldWrite}
    <div class="wiz-block wiz-block-ok" role="status">
      <strong>Already up to date.</strong>
      Existing <code>{state.mcpWriteResult.targetPath}</code> already
      matches the proposed <code>unity-open-mcp</code> entry — no
      write or backup was needed.
    </div>
  {/if}
  {#if state.mcpWriteError}
    <div class="wiz-block wiz-block-error" role="alert">
      {describeMcpConfigError(state.mcpWriteError)}
    </div>
  {/if}

  <div class="wiz-actions-row">
    <Button
      variant="primary"
      onclick={handlers.primaryMcpAction}
      disabled={
        (clientKind(state.mcpClient) === "file" && (!state.canWriteMcpConfig || state.mcpWriting)) ||
        (clientKind(state.mcpClient) !== "file" && !state.mcpPreviewText)
      }
      title={
        clientKind(state.mcpClient) === "file" && state.canWriteMcpConfig
          ? "Write config"
          : clientKind(state.mcpClient) === "file"
            ? "Pick a client + valid MCP path first"
            : "Copy to clipboard"
      }
    >
      {state.mcpWriting ? "Writing…" : state.primaryActionLabel}
    </Button>
    <Button variant="secondary" onclick={handlers.copyMcpJson} disabled={!state.mcpPreviewText}>
      {state.secondaryActionLabel}
    </Button>
    {#if state.copyToast}
      <span class="wiz-toast" role="status">{state.copyToast}</span>
    {/if}
  </div>
</section>
