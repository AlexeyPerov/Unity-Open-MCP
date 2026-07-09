<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import {
    CLIENT_CATEGORY_LABELS,
    MCP_CLIENT_OPTIONS,
    clientKind,
    type McpClientOption,
  } from "./constants.ts";
  import { describeMcpConfigError } from "./error_descriptors.ts";
  import type { WizardState, WizardHandlers } from "./state.ts";

  interface Props {
    state: WizardState;
    handlers: WizardHandlers;
  }

  let { state, handlers }: Props = $props();

  // Plan 3 — Popular clients show in the first viewport; the full catalog
  // stays behind a "Show all clients" disclosure. The currently-selected
  // client is always visible in Popular (pinned when it isn't a popular one)
  // so the preview/write actions stay reachable without expanding.
  let popular = $derived(
    MCP_CLIENT_OPTIONS.filter((o) => o.popular),
  );
  let pinnedSelected = $derived.by(() => {
    const sel = MCP_CLIENT_OPTIONS.find((o) => o.id === state.mcpClient);
    if (!sel) return null;
    return sel.popular ? null : sel;
  });
  let popularList = $derived.by(() => {
    if (pinnedSelected) return [pinnedSelected, ...popular];
    return popular;
  });

  // Filtered + grouped view of the full client catalog (shown when "Show all
  // clients" is expanded). The search matches label or id (case-insensitive);
  // the currently-selected client is always kept visible.
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
    const groups: { category: "ide" | "cli" | "manual"; items: McpClientOption[] }[] = [];
    for (const cat of ["ide", "cli", "manual"] as const) {
      const items = filtered.filter((o) => o.category === cat);
      if (items.length > 0) groups.push({ category: cat, items });
    }
    return groups;
  });
</script>

<section class="wiz-section">
  <p class="wiz-desc">
    Choose the AI client you want to connect. The wizard writes a
    <code>unity-open-mcp</code> entry to its config — other servers
    are left untouched.
  </p>

  <div class="wiz-field">
    <span class="wiz-label">MCP client</span>
    <div role="radiogroup" aria-label="MCP client">
      {#if popularList.length > 0}
        <div class="wiz-client-group">
          <p class="wiz-client-group-label">Popular</p>
          <div class="wiz-radio-grid">
            {#each popularList as opt (opt.id)}
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
      {/if}

      <details class="wiz-advanced wiz-client-all">
        <summary>Show all clients</summary>
        <div class="wiz-label-row">
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
      </details>
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

  <details class="wiz-advanced">
    <summary>Advanced (optional)</summary>
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
  </details>

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
