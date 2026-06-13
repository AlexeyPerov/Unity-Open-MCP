<script lang="ts">
  /**
   * M4 — AI Setup wizard shell (Plan 2).
   *
   * Modal-first flow container attached to the Projects tab. Renders
   * Steps 1-5 plus a Done screen with a sticky footer that owns the
   * Back/Next/Cancel navigation. The component owns all step state
   * in local `$state` — nothing is persisted to `settings.json` /
   * `projects.json`, and a fresh `openAiSetup()` always starts at
   * Step 1 (per questions-4 Q11 = A and Plan 2 Task 3).
   *
   * Step bodies are intentionally light in this plan:
   *   - Plan 3 (Detect + Packages) fills in Steps 1, 2, 3.
   *   - Plan 4 (MCP config) fills in Step 4.
   *   - Plan 5 (Launch + Done) fills in Step 5 and the Done screen.
   * The shell only commits to the navigation + state-preservation
   * contract that the later plans rely on.
   */
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import {
    checkNodeVersion,
    validateToolkitRoot,
    type NodeProbe,
    type ToolkitValidation,
  } from "$lib/services/config";
  import {
    buildCursorMcpEntry,
    buildMcpEnv,
    buildOpenCodeMcpEntry,
    DEFAULT_BRIDGE_PORT,
    MCP_SERVER_KEY,
    type McpClientId,
  } from "$lib/services/ai_toolkit";
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";

  type StepId = "step1" | "step2" | "step3" | "step4" | "step5" | "done";

  const STEP_ORDER: StepId[] = [
    "step1",
    "step2",
    "step3",
    "step4",
    "step5",
    "done",
  ];

  const STEP_TITLES: Record<StepId, string> = {
    step1: "Project detection",
    step2: "Environment",
    step3: "Install Unity packages",
    step4: "Configure AI client",
    step5: "Launch Unity and verify bridge",
    done: "Setup complete",
  };

  const MCP_CLIENT_OPTIONS: { id: McpClientId; label: string; kind: "file" | "cli" | "clipboard" }[] = [
    { id: "cursor", label: "Cursor", kind: "file" },
    { id: "claude-desktop", label: "Claude Desktop", kind: "file" },
    { id: "claude-code", label: "Claude Code (CLI only)", kind: "cli" },
    { id: "opencode-global", label: "OpenCode (global)", kind: "file" },
    { id: "opencode-project", label: "OpenCode (project)", kind: "file" },
    { id: "manual", label: "Manual / copy JSON", kind: "clipboard" },
  ];

  interface Props {
    project: {
      id: string;
      name: string;
      path: string;
      unityVersion: string | null | undefined;
    };
    onClose: () => void;
  }

  let { project, onClose }: Props = $props();

  // Wizard state is local — never written to settings.json /
  // projects.json. A fresh open always lands on Step 1 by way of
  // the parent unmounting the component on close, which discards
  // the `$state` below.
  let currentStep = $state<StepId>("step1");

  // Step 2 — environment state. Prefilled from `settings.aiToolkit`
  // when the user has run the wizard before, but kept in local
  // `$state` until validation passes — only then does the parent
  // call `settingsStore.setAiToolkitRoot` from the Save action.
  let toolkitRoot = $state("");
  let toolkitRootDirty = $state(false);
  let mcpIndexOverride = $state("");
  let toolkitValidation = $state<ToolkitValidation | null>(null);
  let toolkitValidating = $state(false);
  let toolkitError = $state<string | null>(null);
  let nodeProbe = $state<NodeProbe | null>(null);
  let nodeProbing = $state(false);
  let pickToolkitInFlight = $state(false);

  // Step 3 — packages state.
  let installBridge = $state(true);
  let installVerify = $state(true);
  let installScanner = $state(false);
  let packageVersionPin = $state("");
  let packageCustomUrl = $state("");

  // Step 4 — MCP client state.
  let mcpClient = $state<McpClientId>("cursor");
  let cursorProjectScope = $state(false);
  let bridgePort = $state(DEFAULT_BRIDGE_PORT);
  let copyToast = $state<string | null>(null);

  // Step 5 — launch/verify state.
  let launchInFlight = $state(false);
  let launchStatus = $state<"idle" | "launched" | "skipped" | "failed">("idle");
  let launchMessage = $state<string | null>(null);

  onMount(() => {
    // Prefill Step 2 from the persisted toolkit root, but treat the
    // incoming `toolkitRoot` as authoritative for this run — saving
    // back to settings happens in the Done screen's save action.
    const stored = settingsStore.aiToolkit;
    toolkitRoot = stored.rootPath ?? "";
    mcpIndexOverride = stored.mcpIndexOverride ?? "";
    toolkitRootDirty = false;
  });

  function currentStepIndex(): number {
    return STEP_ORDER.indexOf(currentStep);
  }

  function stepLabel(id: StepId): string {
    return STEP_TITLES[id];
  }

  function canGoBack(): boolean {
    return currentStepIndex() > 0;
  }

  function canGoNext(): boolean {
    switch (currentStep) {
      case "step1":
        return true;
      case "step2":
        return !!toolkitValidation?.ok && !!nodeProbe?.ok;
      case "step3":
        return installBridge || installVerify;
      case "step4":
        return true;
      case "step5":
        return true;
      case "done":
        return false;
    }
  }

  function nextStep() {
    if (!canGoNext()) return;
    const i = currentStepIndex();
    if (i < STEP_ORDER.length - 1) {
      currentStep = STEP_ORDER[i + 1];
    }
  }

  function backStep() {
    if (!canGoBack()) return;
    const i = currentStepIndex();
    if (i > 0) {
      currentStep = STEP_ORDER[i - 1];
    }
  }

  function cancelWizard() {
    onClose();
  }

  function isLastFormStep(): boolean {
    return currentStep === "step5";
  }

  function nextLabel(): string {
    if (currentStep === "done") return "Close";
    if (isLastFormStep()) return "Finish";
    return "Next";
  }

  function onFooterNext() {
    if (currentStep === "done") {
      cancelWizard();
      return;
    }
    if (isLastFormStep()) {
      // Persist the validated toolkit root on finish. The shell's
      // own state is local; only the root path crosses the boundary
      // into `settings.aiToolkit.rootPath`. M4-3 already owns the
      // Step 2 save behavior, but the wizard needs to commit on
      // finish so the user does not have to revisit Step 2.
      void persistToolkitRoot();
      cancelWizard();
      return;
    }
    nextStep();
  }

  function onFooterBack() {
    backStep();
  }

  function onOverlayClick(e: MouseEvent) {
    if (e.target === e.currentTarget) cancelWizard();
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key === "Escape") {
      e.preventDefault();
      cancelWizard();
    }
  }

  async function persistToolkitRoot() {
    if (!toolkitValidation?.ok) return;
    try {
      await settingsStore.setAiToolkitRoot(toolkitRoot);
      if (mcpIndexOverride.trim().length > 0) {
        await settingsStore.setAiToolkitMcpIndexOverride(mcpIndexOverride);
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save toolkit root failed: ${msg}`);
    }
  }

  async function pickToolkitFolder() {
    if (pickToolkitInFlight) return;
    pickToolkitInFlight = true;
    try {
      const picked = await openDialog({
        directory: true,
        multiple: false,
        title: "Select Unity-AI-Hub toolkit root",
      });
      if (typeof picked === "string") {
        toolkitRoot = picked;
        toolkitRootDirty = true;
        await runToolkitValidation();
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      toolkitError = `folder picker failed: ${msg}`;
      S.appendErrorLog(toolkitError);
    } finally {
      pickToolkitInFlight = false;
    }
  }

  function onToolkitRootInput(value: string) {
    toolkitRoot = value;
    toolkitRootDirty = true;
  }

  async function runToolkitValidation() {
    if (!toolkitRoot.trim()) {
      toolkitValidation = null;
      toolkitError = "Pick a folder or type a path first.";
      return;
    }
    toolkitValidating = true;
    toolkitError = null;
    try {
      const v = await validateToolkitRoot(toolkitRoot);
      toolkitValidation = v;
      if (!v.ok) {
        toolkitError = "Toolkit root validation failed — see the failed checks below.";
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      toolkitError = `validation failed: ${msg}`;
      toolkitValidation = null;
      S.appendErrorLog(toolkitError);
    } finally {
      toolkitValidating = false;
    }
  }

  async function runNodeProbe() {
    nodeProbing = true;
    try {
      nodeProbe = await checkNodeVersion();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      nodeProbe = {
        ok: false,
        version: null,
        major: null,
        requiredMajor: 18,
        error: msg,
      };
      S.appendErrorLog(`node probe failed: ${msg}`);
    } finally {
      nodeProbing = false;
    }
  }

  // Run both checks when Step 2 first becomes active. The shell
  // calls this on every `onMount`/step entry so the user sees fresh
  // results without having to click a refresh button.
  $effect(() => {
    if (currentStep === "step2") {
      if (nodeProbe === null) void runNodeProbe();
      if (toolkitRoot.trim() && toolkitValidation === null && !toolkitValidating) {
        void runToolkitValidation();
      }
    }
  });

  // Derived: resolved MCP index path honoring the advanced override.
  let resolvedMcpPath = $derived.by(() => {
    const override = mcpIndexOverride.trim();
    if (override.length > 0) return override;
    if (!toolkitRoot.trim()) return null;
    return `${toolkitRoot.replace(/[\\/]+$/, "")}/mcp-server/dist/index.js`;
  });

  let resolvedMcpPathValid = $state<boolean | null>(null);

  // Re-probe filesystem only when the resolved path actually changes.
  $effect(() => {
    const path = resolvedMcpPath;
    if (!path) {
      resolvedMcpPathValid = null;
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        const v = await validateToolkitRoot(toolkitRoot);
        if (cancelled) return;
        const mcpEntry = v.fingerprints.find((f) =>
          f.relativePath === "mcp-server/dist/index.js"
        );
        resolvedMcpPathValid = !!mcpEntry && mcpEntry.exists && mcpEntry.kindOk === true;
      } catch {
        if (!cancelled) resolvedMcpPathValid = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  let generatedMcpJson = $derived.by(() => {
    if (!resolvedMcpPath) return "";
    const env = buildMcpEnv({
      unityProjectPath: project.path,
      bridgePort,
    });
    if (mcpClient === "opencode-global" || mcpClient === "opencode-project") {
      const entry = buildOpenCodeMcpEntry(resolvedMcpPath, {
        unityProjectPath: project.path,
        bridgePort,
      });
      return JSON.stringify(
        {
          mcp: {
            [MCP_SERVER_KEY]: entry,
          },
        },
        null,
        2
      );
    }
    const entry = buildCursorMcpEntry(resolvedMcpPath, {
      unityProjectPath: project.path,
      bridgePort,
    });
    return JSON.stringify(
      {
        mcpServers: {
          [MCP_SERVER_KEY]: entry,
        },
      },
      null,
      2
    );
  });

  function clientKind(id: McpClientId): "file" | "cli" | "clipboard" {
    return MCP_CLIENT_OPTIONS.find((o) => o.id === id)?.kind ?? "file";
  }

  function canWriteMcpConfig(): boolean {
    if (clientKind(mcpClient) !== "file") return false;
    if (resolvedMcpPathValid !== true) return false;
    if (!toolkitValidation?.ok) return false;
    return true;
  }

  async function copyMcpJson() {
    if (typeof navigator === "undefined" || !navigator.clipboard) {
      copyToast = "clipboard unavailable";
      return;
    }
    try {
      await navigator.clipboard.writeText(generatedMcpJson);
      copyToast = "Copied MCP config JSON to clipboard";
      S.appendDrawerLog("copied MCP config JSON to clipboard");
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      copyToast = `copy failed: ${msg}`;
    } finally {
      setTimeout(() => (copyToast = null), 2000);
    }
  }

  function fakeLaunch() {
    if (launchInFlight) return;
    launchInFlight = true;
    launchStatus = "launched";
    launchMessage = "Launch is a placeholder in this milestone — Plan 5 wires the real Unity + bridge ping.";
    S.appendDrawerLog(`AI Setup: simulated launch for ${project.name}`);
    setTimeout(() => {
      launchInFlight = false;
    }, 400);
  }

  function skipVerify() {
    launchStatus = "skipped";
    launchMessage = "Skipped — you can re-run the wizard and retry from Step 5.";
  }

  // Re-render the wizard's progress segments. We highlight the
  // current step and dim completed/pending steps so the user has a
  // single glance at "where am I" + "what's left".
  let progress = $derived.by(() => {
    return STEP_ORDER.map((id, idx) => {
      const currentIdx = currentStepIndex();
      const state: "done" | "current" | "pending" =
        idx < currentIdx ? "done" : idx === currentIdx ? "current" : "pending";
      return { id, idx, label: stepLabel(id), state };
    });
  });

  // Bump wizardKey on every close so the next open re-mounts the
  // component and local state resets to Step 1 with a fresh Step 2
  // prefill. (Task 3: restart from Step 1 on each new AI Setup.)
  function handleClose() {
    onClose();
  }
</script>

<svelte:window onkeydown={onKeydown} />

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
  class="wiz-overlay"
  role="dialog"
  tabindex="-1"
  aria-modal="true"
  aria-labelledby="wiz-title"
  onclick={onOverlayClick}
>
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="wiz-shell" onclick={(e) => e.stopPropagation()}>
    <header class="wiz-header">
      <div class="wiz-header-titles">
        <span class="wiz-eyebrow">AI Setup</span>
        <h2 id="wiz-title" class="wiz-title">
          {stepLabel(currentStep)}
        </h2>
        <span class="wiz-subtitle" title={project.path}>
          {project.name} · {project.path}
        </span>
      </div>
      <button
        type="button"
        class="wiz-close"
        aria-label="Close AI Setup"
        title="Cancel and close the wizard"
        onclick={handleClose}
      >
        ×
      </button>
    </header>

    <ol class="wiz-progress" aria-label="Wizard progress">
      {#each progress as seg (seg.id)}
        <li class="wiz-seg wiz-seg-{seg.state}" aria-current={seg.state === "current" ? "step" : undefined}>
          <span class="wiz-seg-num">{seg.idx + 1}</span>
          <span class="wiz-seg-label">{seg.label}</span>
        </li>
      {/each}
    </ol>

    <div class="wiz-body">
      {#if currentStep === "step1"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 1 detects the current state of the project. Plan 3
            (Detect + Packages) will add the live manifest /
            <code>ProjectVersion.txt</code> / MCP heuristic read;
            this step is the navigation anchor for now.
          </p>
          <dl class="wiz-summary">
            <div>
              <dt>Project name</dt>
              <dd>{project.name}</dd>
            </div>
            <div>
              <dt>Path</dt>
              <dd><code title={project.path}>{project.path}</code></dd>
            </div>
            <div>
              <dt>Unity version</dt>
              <dd>
                {#if project.unityVersion}
                  <code>{project.unityVersion}</code>
                {:else}
                  <em>unknown</em>
                {/if}
              </dd>
            </div>
            <div>
              <dt>Bridge installed</dt>
              <dd><em>not checked</em></dd>
            </div>
            <div>
              <dt>Verify installed</dt>
              <dd><em>not checked</em></dd>
            </div>
            <div>
              <dt>MCP configured</dt>
              <dd><em>not checked</em></dd>
            </div>
          </dl>
        </section>
      {:else if currentStep === "step2"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 2 validates Node.js and the cloned Unity-AI-Hub
            toolkit root. Both checks must pass before you can
            continue to Step 3.
          </p>

          <div class="wiz-field">
            <span class="wiz-label">Node.js</span>
            {#if nodeProbing || nodeProbe === null}
              <p class="wiz-hint">Probing…</p>
            {:else if nodeProbe.ok}
              <p class="wiz-hint wiz-hint-ok">
                Detected <strong>Node {nodeProbe.version}</strong>
                (major {nodeProbe.major} ≥ {nodeProbe.requiredMajor}).
              </p>
            {:else}
              <p class="wiz-hint wiz-hint-warn">
                Node.js {nodeProbe.requiredMajor}+ is required to run
                <code>unity-agent-mcp</code>.
                {#if nodeProbe.version}
                  Detected <strong>{nodeProbe.version}</strong>.
                {:else}
                  Not detected on PATH.
                {/if}
                {#if nodeProbe.error}
                  <br /><small>{nodeProbe.error}</small>
                {/if}
              </p>
            {/if}
            <div>
              <Button variant="secondary" onclick={runNodeProbe} disabled={nodeProbing}>
                {nodeProbing ? "Checking…" : "Re-check"}
              </Button>
            </div>
          </div>

          <div class="wiz-field">
            <label class="wiz-label" for="wiz-toolkit-root">AI toolkit root</label>
            <div class="wiz-input-row">
              <input
                id="wiz-toolkit-root"
                type="text"
                class="wiz-input"
                placeholder="/Users/you/Unity-AI-Hub"
                value={toolkitRoot}
                oninput={(e) => onToolkitRootInput((e.currentTarget as HTMLInputElement).value)}
              />
              <Button variant="secondary" onclick={pickToolkitFolder} disabled={pickToolkitInFlight}>
                {pickToolkitInFlight ? "Selecting…" : "Browse…"}
              </Button>
              <Button
                variant="secondary"
                onclick={runToolkitValidation}
                disabled={toolkitValidating || !toolkitRoot.trim()}
              >
                {toolkitValidating ? "Validating…" : toolkitRootDirty ? "Validate" : "Re-check"}
              </Button>
            </div>
            {#if toolkitError}
              <p class="wiz-hint wiz-hint-warn">{toolkitError}</p>
            {/if}
            {#if toolkitValidation}
              <ul class="wiz-fingerprints" aria-label="Toolkit root fingerprint checks">
                {#each toolkitValidation.fingerprints as fp (fp.relativePath)}
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
              {#if toolkitValidation.mcpDistMissing}
                <p class="wiz-hint wiz-hint-warn">
                  <code>mcp-server/dist/index.js</code> is not built.
                  Run <code>npm run build</code> in
                  <code>{toolkitRoot}/mcp-server/</code> and re-check.
                </p>
              {/if}
            {/if}
          </div>

          <details class="wiz-advanced">
            <summary>Advanced — MCP server path override</summary>
            <div class="wiz-field">
              <label class="wiz-label" for="wiz-mcp-override">Custom mcp-server/dist/index.js</label>
              <input
                id="wiz-mcp-override"
                type="text"
                class="wiz-input"
                placeholder="/opt/builds/unity-agent-mcp/index.js"
                value={mcpIndexOverride}
                oninput={(e) => (mcpIndexOverride = (e.currentTarget as HTMLInputElement).value)}
              />
              <p class="wiz-hint">
                Leave empty to use <code>{toolkitRoot || "<toolkit>"}/mcp-server/dist/index.js</code>.
                Step 3 package URLs and skill copy always use the
                toolkit root regardless of this override.
              </p>
            </div>
          </details>
        </section>
      {:else if currentStep === "step3"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 3 adds bridge + verify packages to the project's
            <code>Packages/manifest.json</code>. Plan 3 owns the
            actual merge + write behavior; this shell step keeps the
            current form draft so toggles survive Back / Next.
          </p>

          <div class="wiz-field">
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installBridge} />
              <span><strong>Install Unity Agent Bridge</strong> — required for live MCP tooling</span>
            </label>
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installVerify} />
              <span>
                <strong>Install Unity Agent Verify</strong> —
                <small>Scoped health checks for AI gates — not the full Unity Scanner window.</small>
              </span>
            </label>
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installScanner} />
              <span>
                <strong>Also install Unity Scanner</strong> —
                <small>Full upstream product for human inspection in the Editor (advanced, off by default).</small>
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
                value={packageVersionPin}
                oninput={(e) => (packageVersionPin = (e.currentTarget as HTMLInputElement).value)}
              />
            </div>
            <div class="wiz-field">
              <label class="wiz-label" for="wiz-pkg-url">Custom git URL (dev builds)</label>
              <input
                id="wiz-pkg-url"
                type="text"
                class="wiz-input"
                placeholder="https://github.com/your-fork/Unity-AI-Hub.git"
                value={packageCustomUrl}
                oninput={(e) => (packageCustomUrl = (e.currentTarget as HTMLInputElement).value)}
              />
            </div>
          </details>
        </section>
      {:else if currentStep === "step4"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 4 writes a <code>unity-agent</code> MCP server
            entry to your client config. Plan 4 owns the actual file
            merge; this shell step previews the JSON the wizard will
            write and exposes the write / copy actions.
          </p>

          <div class="wiz-field">
            <span class="wiz-label">MCP client</span>
            <div class="wiz-radio-grid" role="radiogroup" aria-label="MCP client">
              {#each MCP_CLIENT_OPTIONS as opt (opt.id)}
                <label class="wiz-radio">
                  <input
                    type="radio"
                    name="wiz-mcp-client"
                    value={opt.id}
                    bind:group={mcpClient}
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

          {#if mcpClient === "cursor"}
            <div class="wiz-field">
              <label class="wiz-toggle">
                <input type="checkbox" bind:checked={cursorProjectScope} />
                <span>
                  <strong>Use project-scoped config</strong> —
                  <small>write to <code>{project.path}/.cursor/mcp.json</code> instead of <code>~/.cursor/mcp.json</code>.</small>
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
              value={bridgePort}
              oninput={(e) => (bridgePort = (e.currentTarget as HTMLInputElement).value)}
            />
          </div>

          <div class="wiz-field">
            <span class="wiz-label">Generated config</span>
            <pre class="wiz-codeblock" aria-label="Generated MCP config">{generatedMcpJson || "—"}</pre>
            {#if !resolvedMcpPath}
              <p class="wiz-hint wiz-hint-warn">
                Set the toolkit root in Step 2 to generate a config.
              </p>
            {:else if resolvedMcpPathValid === false}
              <p class="wiz-hint wiz-hint-warn">
                Resolved MCP path does not exist on disk:
                <code>{resolvedMcpPath}</code>.
                Run <code>npm run build</code> in
                <code>{toolkitRoot}/mcp-server/</code>.
              </p>
            {/if}
          </div>

          <div class="wiz-actions-row">
            <Button
              variant="primary"
              disabled={!canWriteMcpConfig()}
              title={canWriteMcpConfig() ? "Write config" : "Pick a client + valid MCP path first"}
            >
              {clientKind(mcpClient) === "file" ? "Write config" : "Copy to clipboard"}
            </Button>
            <Button variant="secondary" onclick={copyMcpJson} disabled={!generatedMcpJson}>
              Copy JSON
            </Button>
            {#if copyToast}
              <span class="wiz-toast" role="status">{copyToast}</span>
            {/if}
          </div>
        </section>
      {:else if currentStep === "step5"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 5 launches Unity for this project and confirms the
            bridge HTTP <code>/ping</code> endpoint. Plan 5 wires the
            real launch + polling; this shell step renders the
            checklist and surfaces the placeholder status.
          </p>
          <ol class="wiz-checklist">
            <li class:done={launchStatus !== "idle"}>
              Launch Unity (Hub launcher flow)
              {#if launchStatus !== "idle"}<span class="wiz-check-done">— ok</span>{/if}
            </li>
            <li>Wait for project compile</li>
            <li>Wait for bridge HTTP <code>/ping</code> (timeout 120s)</li>
            <li>Confirm response fields (<code>connected</code>, project path, compile/play state)</li>
          </ol>
          {#if launchMessage}
            <p class="wiz-hint">{launchMessage}</p>
          {/if}
          <div class="wiz-actions-row">
            <Button variant="primary" onclick={fakeLaunch} disabled={launchInFlight}>
              {launchInFlight ? "Launching…" : "Launch Unity"}
            </Button>
            <Button variant="secondary" onclick={skipVerify}>Skip to Done</Button>
          </div>
        </section>
      {:else if currentStep === "done"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Wizard complete. Plan 5 (Launch + Done) will expand this
            screen with the live detection results, the
            <code>{toolkitRoot || "<toolkit>"}/skills/unity-agent/SKILL.md</code>
            copy, and the per-client open actions.
          </p>
          <dl class="wiz-summary">
            <div>
              <dt>Packages</dt>
              <dd>
                bridge: {installBridge ? "will install" : "skipped"}<br />
                verify: {installVerify ? "will install" : "skipped"}
              </dd>
            </div>
            <div>
              <dt>MCP client</dt>
              <dd>{MCP_CLIENT_OPTIONS.find((o) => o.id === mcpClient)?.label ?? mcpClient}</dd>
            </div>
            <div>
              <dt>Toolkit root</dt>
              <dd><code>{toolkitRoot || "<not set>"}</code></dd>
            </div>
            <div>
              <dt>Launch</dt>
              <dd>
                {#if launchStatus === "launched"}ok{:else if (launchStatus as string) === "skipped"}skipped{:else}not run{/if}
              </dd>
            </div>
          </dl>
          <div class="wiz-actions-row">
            <Button variant="primary" onclick={handleClose}>Close</Button>
            <Button variant="secondary" onclick={handleClose}>Re-run wizard</Button>
          </div>
        </section>
      {/if}
    </div>

    <footer class="wiz-footer">
      <div class="wiz-footer-progress">
        Step {currentStepIndex() + 1} of {STEP_ORDER.length} · {stepLabel(currentStep)}
      </div>
      <div class="wiz-footer-actions">
        <Button variant="secondary" onclick={cancelWizard}>Cancel</Button>
        <Button variant="secondary" onclick={onFooterBack} disabled={!canGoBack()}>
          Back
        </Button>
        <Button
          variant="primary"
          onclick={onFooterNext}
          disabled={currentStep === "done" ? false : !canGoNext()}
        >
          {nextLabel()}
        </Button>
      </div>
    </footer>
  </div>
</div>

<style>
  .wiz-overlay {
    position: fixed;
    inset: 0;
    z-index: 250;
    background: rgba(8, 9, 13, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1.5rem;
  }

  .wiz-shell {
    background: var(--hub-bg);
    border: 1px solid var(--hub-border);
    border-radius: 12px;
    width: 100%;
    max-width: 980px;
    max-height: calc(100vh - 3rem);
    display: flex;
    flex-direction: column;
    box-shadow: 0 12px 48px rgba(0, 0, 0, 0.55);
  }

  .wiz-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
    padding: 1rem 1.25rem 0.5rem;
    border-bottom: 1px solid var(--hub-border-light);
  }

  .wiz-header-titles {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    min-width: 0;
  }

  .wiz-eyebrow {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--hub-accent);
    font-weight: 600;
  }

  .wiz-title {
    margin: 0;
    font-size: 1.1rem;
    font-weight: 600;
    color: var(--hub-text-bright);
  }

  .wiz-subtitle {
    font-size: 0.78rem;
    color: var(--hub-text-muted);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .wiz-close {
    background: transparent;
    border: 1px solid transparent;
    color: var(--hub-text-muted);
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
    padding: 0 0.5rem;
    border-radius: 4px;
  }

  .wiz-close:hover {
    color: var(--hub-text-bright);
    border-color: var(--hub-border-hover);
    background: var(--hub-selected);
  }

  .wiz-progress {
    list-style: none;
    margin: 0;
    padding: 0.6rem 1.25rem 0.7rem;
    display: grid;
    grid-template-columns: repeat(6, 1fr);
    gap: 0.5rem;
    border-bottom: 1px solid var(--hub-border-light);
  }

  .wiz-seg {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    background: var(--hub-card);
    min-width: 0;
  }

  .wiz-seg-done {
    color: var(--hub-text-dim);
    border-color: var(--hub-border-hover);
  }

  .wiz-seg-current {
    color: var(--hub-text-bright);
    border-color: var(--hub-accent);
    background: rgba(92, 124, 250, 0.12);
  }

  .wiz-seg-pending {
    opacity: 0.75;
  }

  .wiz-seg-num {
    flex: 0 0 1.4rem;
    height: 1.4rem;
    border-radius: 50%;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: var(--hub-bg);
    color: var(--hub-text-muted);
    font-size: 0.7rem;
    font-weight: 600;
    border: 1px solid var(--hub-border-light);
  }

  .wiz-seg-current .wiz-seg-num {
    color: var(--hub-text-bright);
    border-color: var(--hub-accent);
  }

  .wiz-seg-label {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .wiz-body {
    flex: 1;
    overflow-y: auto;
    padding: 1rem 1.25rem 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
  }

  .wiz-section {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }

  .wiz-desc {
    margin: 0;
    font-size: 0.84rem;
    line-height: 1.5;
    color: var(--hub-text-muted);
  }

  .wiz-desc code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.78rem;
    background: var(--hub-card);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text);
  }

  .wiz-field {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .wiz-label {
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
  }

  .wiz-input-row {
    display: flex;
    gap: 0.4rem;
    align-items: stretch;
    flex-wrap: wrap;
  }

  .wiz-input {
    flex: 1;
    min-width: 0;
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    color: var(--hub-text);
    font-size: 0.86rem;
    padding: 0.4rem 0.5rem;
    box-sizing: border-box;
    font-family: inherit;
  }

  .wiz-input:focus {
    outline: 2px solid var(--hub-accent);
    border-color: var(--hub-accent);
  }

  .wiz-input-small {
    max-width: 9rem;
  }

  .wiz-hint {
    margin: 0;
    font-size: 0.76rem;
    color: var(--hub-text-muted);
    line-height: 1.4;
  }

  .wiz-hint code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    background: var(--hub-card);
    padding: 0 0.25rem;
    border-radius: 3px;
    color: var(--hub-text);
  }

  .wiz-hint-ok { color: #4ade80; }
  .wiz-hint-warn { color: var(--hub-error-fg); }

  .wiz-fingerprints {
    list-style: none;
    margin: 0.3rem 0 0;
    padding: 0.4rem 0.5rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-card);
  }

  .wiz-fp {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.76rem;
  }

  .wiz-fp-name code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text);
  }

  .wiz-fp-status {
    font-size: 0.72rem;
    color: var(--hub-text-muted);
  }

  .wiz-fp-ok .wiz-fp-status { color: #4ade80; }
  .wiz-fp-warn .wiz-fp-status { color: #fbbf24; }
  .wiz-fp-missing .wiz-fp-status { color: var(--hub-error-fg); }

  .wiz-advanced {
    border: 1px dashed var(--hub-border-light);
    border-radius: 6px;
    padding: 0.4rem 0.6rem;
  }

  .wiz-advanced summary {
    cursor: pointer;
    font-size: 0.78rem;
    color: var(--hub-text-dim);
  }

  .wiz-toggle {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.4rem;
    align-items: start;
    font-size: 0.84rem;
    color: var(--hub-text);
    cursor: pointer;
  }

  .wiz-toggle input[type="checkbox"] {
    margin-top: 0.2rem;
  }

  .wiz-toggle small {
    display: block;
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    margin-top: 0.1rem;
  }

  .wiz-toggle code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.7rem;
  }

  .wiz-radio-grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.35rem;
  }

  .wiz-radio {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.4rem;
    align-items: start;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-card);
    cursor: pointer;
    font-size: 0.82rem;
  }

  .wiz-radio:has(input:checked) {
    border-color: var(--hub-accent);
    background: rgba(92, 124, 250, 0.08);
  }

  .wiz-radio small {
    display: block;
    font-size: 0.7rem;
    color: var(--hub-text-muted);
    margin-top: 0.1rem;
  }

  .wiz-codeblock {
    margin: 0;
    padding: 0.6rem 0.7rem;
    background: var(--hub-card);
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text);
    max-height: 16rem;
    overflow: auto;
    white-space: pre;
  }

  .wiz-actions-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .wiz-toast {
    font-size: 0.74rem;
    color: var(--hub-accent);
  }

  .wiz-summary {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 0.25rem 0.85rem;
    margin: 0;
    font-size: 0.82rem;
  }

  .wiz-summary dt {
    color: var(--hub-text-muted);
  }

  .wiz-summary dd {
    margin: 0;
    color: var(--hub-text);
  }

  .wiz-summary code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    color: var(--hub-text);
  }

  .wiz-checklist {
    margin: 0;
    padding: 0;
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .wiz-checklist li {
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-card);
    font-size: 0.82rem;
    color: var(--hub-text-dim);
  }

  .wiz-checklist li.done {
    color: var(--hub-text);
  }

  .wiz-check-done {
    color: #4ade80;
    font-size: 0.74rem;
    margin-left: 0.4rem;
  }

  .wiz-footer {
    position: sticky;
    bottom: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    padding: 0.7rem 1.25rem;
    border-top: 1px solid var(--hub-border-light);
    background: var(--hub-bg);
  }

  .wiz-footer-progress {
    font-size: 0.74rem;
    color: var(--hub-text-muted);
  }

  .wiz-footer-actions {
    display: flex;
    gap: 0.4rem;
    align-items: center;
  }

  @media (max-width: 720px) {
    .wiz-progress { grid-template-columns: repeat(3, 1fr); }
    .wiz-radio-grid { grid-template-columns: 1fr; }
  }
</style>
