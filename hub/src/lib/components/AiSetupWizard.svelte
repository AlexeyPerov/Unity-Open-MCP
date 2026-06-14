<script lang="ts">
  /**
   * M4 — AI Setup wizard.
   *
   * Plan 2 contributed the modal shell + step navigation contract.
   * Plan 3 fills in Steps 1-3 with live detection + manifest
   * merge, plus orders Step 2's hard blocks per `hub-wizard.md`:
   *   - Unity 6 (6000.0+) hard block (questions-4 Q10 = A)
   *   - Node.js 18+ hard block (Q3 = A, before the toolkit picker)
   *   - AI toolkit root hard block (Plan 1 fingerprint validation)
   *   - Write access to `Packages/manifest.json` hard block
   *   - Path with spaces / MCP client warnings only
   * Plan 4 owns Step 4. Plan 5 wires Step 5's real
   * `launch_for_verify` + `poll_bridge_ping` flow plus the
   * StatusChip-driven Done screen.
   */
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import {
    bridgePortFromString,
    checkNodeVersion,
    copySkillFiles,
    detectProjectState,
    launchForVerify,
    planManifestMerge,
    planMcpConfig,
    planSkillCopy,
    pollBridgePing,
    validateToolkitRoot,
    writeManifestMerge,
    writeMcpConfig,
    type BridgePingResult,
    type BridgeStatusKind,
    type LaunchForVerifyError,
    type ManifestError,
    type ManifestMergePlan,
    type ManifestWriteResult,
    type McpConfigError,
    type McpConfigHeuristic,
    type McpConfigParamsWire,
    type McpConfigPlan,
    type McpConfigWriteResult,
    type NodeProbe,
    type ProjectState,
    type SkillCopyError,
    type SkillCopyParamsWire,
    type SkillCopyPlan,
    type SkillCopyResult,
    type ToolkitValidation,
  } from "$lib/services/config";
  import {
    changeKindLabel,
    changeKindTone,
    describeManifestError,
    formatChangeLine,
    formatDiffPreview,
    shortPackageName,
    summarizeChanges,
  } from "$lib/services/manifest";
  import {
    DEFAULT_BRIDGE_PORT,
    type McpClientId,
  } from "$lib/services/ai_toolkit";
  // The pure-function `buildCursorMcpEntry` / `buildOpenCodeMcpEntry` /
  // `buildMcpEnv` helpers in `ai_toolkit.ts` still back the
  // `ai_toolkit.test.ts` Node:test suite. The wizard Step 4
  // now calls `plan_mcp_config` / `write_mcp_config` from the
  // Rust backend instead of building the JSON client-side, so
  // the live preview is guaranteed to match what the writer
  // will emit (the `mcpServers.unity-open-mcp` merge key, the
  // OpenCode `mcp.unity-open-mcp` + `environment` envelope, the
  // OS-resolved Claude Desktop path, and the `claude mcp add`
  // command for Claude Code all live in Rust).
  import Button from "$lib/components/shell/Button.svelte";
  import StatusChip from "$lib/components/StatusChip.svelte";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, revealItemInDir } from "@tauri-apps/plugin-opener";

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

  let currentStep = $state<StepId>("step1");

  // Step 1 — detection snapshot. Refreshed on mount and on every
  // entry to Step 1 / Done so the UI matches live disk state.
  let detection = $state<ProjectState | null>(null);
  let detectionLoading = $state(false);
  let detectionError = $state<string | null>(null);

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
  let mergePlan = $state<ManifestMergePlan | null>(null);
  let mergePlanning = $state(false);
  let mergeWriting = $state(false);
  let mergeResult = $state<ManifestWriteResult | null>(null);
  let mergeError = $state<string | null>(null);
  let showDiff = $state(false);
  let upgradeAcknowledged = $state(false);

  // Step 4 — MCP client state.
  let mcpClient = $state<McpClientId>("cursor");
  let cursorProjectScope = $state(false);
  let bridgePort = $state(DEFAULT_BRIDGE_PORT);
  let copyToast = $state<string | null>(null);
  let mcpPlan = $state<McpConfigPlan | null>(null);
  let mcpPlanning = $state(false);
  let mcpWriteResult = $state<McpConfigWriteResult | null>(null);
  let mcpWriting = $state(false);
  let mcpWriteError = $state<McpConfigError | null>(null);

  // Done — skill copy state.
  let skillPlan = $state<SkillCopyPlan | null>(null);
  let skillPlanning = $state(false);
  let skillResult = $state<SkillCopyResult | null>(null);
  let skillCopying = $state(false);
  let skillError = $state<SkillCopyError | null>(null);
  let skillOverwriteAck = $state(false);

  // Step 5 — launch/verify state. The wizard drives the
  // launch + `/ping` polling itself; the Done screen reads
  // these values (plus the live detection snapshot) to render
  // the StatusChip checklist.
  type Step5ItemId = "launch" | "compile" | "ping" | "confirm";
  type Step5ItemState = "pending" | "running" | "ok" | "failed";

  let step5Running = $state(false);
  let step5Items = $state<Record<Step5ItemId, Step5ItemState>>({
    launch: "pending",
    compile: "pending",
    ping: "pending",
    confirm: "pending",
  });
  let step5LaunchPid = $state<number | null>(null);
  let step5BridgePort = $state<number | null>(null);
  let step5BridgeStatus = $state<BridgeStatusKind>({ kind: "notChecked" });
  let step5PingResult = $state<BridgePingResult | null>(null);
  let step5Error = $state<string | null>(null);
  let step5StartedAt = $state<number | null>(null);
  let step5DeadlineAt = $state<number | null>(null);
  let step5LastTick = $state<number | null>(null);

  const STEP5_TOTAL_BUDGET_MS = 120_000;
  const STEP5_POLL_INTERVAL_MS = 2_000;
  const STEP5_PING_TIMEOUT_MS = 5_000;

  onMount(() => {
    const stored = settingsStore.aiToolkit;
    toolkitRoot = stored.rootPath ?? "";
    mcpIndexOverride = stored.mcpIndexOverride ?? "";
    toolkitRootDirty = false;
    void refreshDetection();
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
        return isProjectReady();
      case "step2":
        return isEnvironmentReady();
      case "step3":
        return isManifestReady();
      case "step4":
        return true;
      case "step5":
        return true;
      case "done":
        return false;
    }
  }

  function isProjectReady(): boolean {
    if (!detection) return false;
    return detection.isValidUnityProject;
  }

  function isEnvironmentReady(): boolean {
    if (!nodeProbe?.ok) return false;
    if (!toolkitValidation?.ok) return false;
    if (!detection?.meetsMinUnityVersion) return false;
    if (!detection?.manifestWritable) return false;
    return true;
  }

  function isManifestReady(): boolean {
    if (!installBridge && !installVerify) return false;
    if (!mergePlan) return false;
    if (mergePlan.manifestRead.parseError) return false;
    // Allow Next whenever the selected packages already exist in the
    // manifest — whether via a local `file:` path (demo project) or a
    // remote git URL. The upgrade-acknowledgement toggle only gates the
    // "Install / Upgrade" action, not navigation.
    const selectedIds: string[] = [];
    if (installBridge) selectedIds.push("com.unity.ai-agent-bridge");
    if (installVerify) selectedIds.push("com.unity.ai-agent-verify");
    return mergePlan.changes.some(
      (c) =>
        selectedIds.includes(c.id) &&
        (c.kind === "unchanged" || c.kind === "upgrade"),
    );
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
      void persistToolkitRoot();
      currentStep = "done";
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

  async function refreshDetection() {
    detectionLoading = true;
    detectionError = null;
    try {
      const next = await detectProjectState(project.path);
      detection = next;
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      detectionError = `detection failed: ${msg}`;
      detection = null;
      S.appendErrorLog(detectionError);
    } finally {
      detectionLoading = false;
    }
  }

  async function pickToolkitFolder() {
    if (pickToolkitInFlight) return;
    pickToolkitInFlight = true;
    try {
      const picked = await openDialog({
        directory: true,
        multiple: false,
        title: "Select unity-open-mcp toolkit root",
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

  // Re-run environment checks whenever the user enters Step 2.
  $effect(() => {
    if (currentStep === "step2") {
      if (nodeProbe === null) void runNodeProbe();
      if (toolkitRoot.trim() && toolkitValidation === null && !toolkitValidating) {
        void runToolkitValidation();
      }
    }
  });

  // Re-detect project on every Step 1 entry so the table reflects
  // the latest manifest state. Plan 5's Step 5 /ping will mutate
  // `bridgeStatus` here in a later plan.
  $effect(() => {
    if (currentStep === "step1" || currentStep === "done") {
      void refreshDetection();
    }
  });

  // Live plan: re-compute the merge plan whenever Step 3 form
  // state (or the toolkit root) changes. The diff preview is
  // always live — the user does not have to click a refresh.
  $effect(() => {
    if (currentStep !== "step3") return;
    if (!toolkitRoot.trim()) return;
    if (!installBridge && !installVerify) {
      mergePlan = null;
      return;
    }
    mergePlanning = true;
    let cancelled = false;
    void (async () => {
      try {
        const plan = await planManifestMerge({
          projectPath: project.path,
          toolkitRoot,
          installBridge,
          installVerify,
          versionPin: packageVersionPin,
          customUrl: packageCustomUrl,
          confirmUpgrades: false,
        });
        if (!cancelled) {
          mergePlan = plan;
          // Reset the upgrade ack whenever the plan changes
          // shape — the user is making a new decision.
          upgradeAcknowledged = false;
          mergeResult = null;
          mergeError = null;
        }
      } catch (e) {
        if (!cancelled) {
          const msg = e instanceof Error ? e.message : String(e);
          mergeError = `plan failed: ${msg}`;
          mergePlan = null;
        }
      } finally {
        if (!cancelled) mergePlanning = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Derived: resolved MCP index path honoring the advanced override.
  let resolvedMcpPath = $derived.by(() => {
    const override = mcpIndexOverride.trim();
    if (override.length > 0) return override;
    if (!toolkitRoot.trim()) return null;
    return `${toolkitRoot.replace(/[\\/]+$/, "")}/mcp-server/dist/index.js`;
  });

  let resolvedMcpPathValid = $state<boolean | null>(null);

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

  // Live Step 4 plan: re-call `plan_mcp_config` whenever the
  // form state changes so the diff preview + write button
  // always reflect what the Rust writer would produce. The
  // MCP path is the upstream gate; the planner still runs
  // with a stale path so the UI can surface a focused
  // `mcpPathInvalid` error rather than a blank preview.
  $effect(() => {
    const projectPath = project.path;
    const root = toolkitRoot;
    const client = mcpClient;
    const projectScope = cursorProjectScope;
    const port = bridgePort;
    const override = mcpIndexOverride;
    if (!projectPath || !root) {
      mcpPlan = null;
      return;
    }
    mcpPlanning = true;
    let cancelled = false;
    void (async () => {
      try {
        const params: McpConfigParamsWire = {
          projectPath,
          toolkitRoot: root,
          mcpIndexOverride: override,
          unityProjectPath: projectPath,
          bridgePort: port,
          includeUnityPath: false,
          unityPath: "",
          client: clientToWire(client),
          cursorProjectScope: projectScope,
        };
        const plan = await planMcpConfig(params);
        if (!cancelled) {
          mcpPlan = plan;
          mcpWriteResult = null;
          mcpWriteError = null;
        }
      } catch (e) {
        if (!cancelled) {
          mcpWriteError = toMcpConfigError(e);
          mcpPlan = null;
        }
      } finally {
        if (!cancelled) mcpPlanning = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Live Step 4 Skill copy plan: runs on every Done entry so
  // the wizard surfaces the per-target preview + "file
  // exists" flags before the user clicks the copy action.
  $effect(() => {
    if (currentStep !== "done") return;
    const root = toolkitRoot;
    const projectPath = project.path;
    if (!projectPath || !root) {
      skillPlan = null;
      return;
    }
    skillPlanning = true;
    let cancelled = false;
    void (async () => {
      try {
        const params: SkillCopyParamsWire = {
          projectPath,
          toolkitRoot: root,
          opencodeSelected: isOpencodeClient(mcpClient),
        };
        const plan = await planSkillCopy(params);
        if (!cancelled) {
          skillPlan = plan;
          // Reset confirmation + result so the user re-acks
          // the overwrite toggle when the plan shape changes.
          if (!plan.targets.some((t) => t.exists)) {
            skillOverwriteAck = false;
          }
          skillResult = null;
          skillError = null;
        }
      } catch (e) {
        if (!cancelled) {
          skillError = toSkillCopyError(e);
          skillPlan = null;
        }
      } finally {
        if (!cancelled) skillPlanning = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  function clientToWire(id: McpClientId): McpConfigParamsWire["client"] {
    switch (id) {
      case "cursor":
        return "cursor";
      case "claude-desktop":
        return "claudeDesktop";
      case "claude-code":
        return "claudeCode";
      case "opencode-global":
        return "opencodeGlobal";
      case "opencode-project":
        return "opencodeProject";
      case "manual":
        return "manual";
    }
  }

  function isOpencodeClient(id: McpClientId): boolean {
    return id === "opencode-global" || id === "opencode-project";
  }

  function toMcpConfigError(e: unknown): McpConfigError {
    if (e && typeof e === "object" && "kind" in e && "message" in e) {
      return e as McpConfigError;
    }
    return { kind: "unknown", message: e instanceof Error ? e.message : String(e) };
  }

  function toSkillCopyError(e: unknown): SkillCopyError {
    if (e && typeof e === "object" && "kind" in e && "message" in e) {
      return e as SkillCopyError;
    }
    return { kind: "unknown", message: e instanceof Error ? e.message : String(e) };
  }

  function describeMcpConfigError(err: McpConfigError): string {
    switch (err.kind) {
      case "mcpPathInvalid":
        return `MCP server entry point does not exist on disk. Run \`npm run build\` in the toolkit's mcp-server/ folder.`;
      case "homeMissing":
        return "Cannot resolve the home directory for a global MCP config target.";
      case "noFileTarget":
        return "This client does not back a writable config file.";
      case "invalidJson":
        return `Existing config is not valid JSON: ${err.message}`;
      case "readFailed":
        return `Cannot read existing config: ${err.message}`;
      case "writeFailed":
        return `Failed to write config: ${err.message}. Check folder permissions.`;
      case "backupFailed":
        return `Cannot create backup: ${err.message}`;
      default:
        return `${err.kind}: ${err.message}`;
    }
  }

  function describeSkillCopyError(err: SkillCopyError): string {
    switch (err.kind) {
      case "sourceMissing":
        return `Toolkit source skill file is missing. Run the wizard with a valid toolkit root.`;
      case "writeFailed":
        return `Failed to copy skill: ${err.message}. Check folder permissions.`;
      case "backupFailed":
        return `Cannot create backup: ${err.message}`;
      case "notAUnityProject":
        return `Project path is not a directory.`;
      default:
        return `${err.kind}: ${err.message}`;
    }
  }

  // Display text: prefer the `claude mcp add` command for
  // Claude Code (which never touches a file), the merged
  // JSON proposal for every other client.
  let mcpPreviewText = $derived.by(() => {
    if (!mcpPlan) return "";
    return mcpPlan.command ?? mcpPlan.proposedJson ?? "";
  });

  function clientKind(id: McpClientId): "file" | "cli" | "clipboard" {
    return MCP_CLIENT_OPTIONS.find((o) => o.id === id)?.kind ?? "file";
  }

  function canWriteMcpConfig(): boolean {
    if (clientKind(mcpClient) !== "file") return false;
    if (resolvedMcpPathValid !== true) return false;
    if (!toolkitValidation?.ok) return false;
    if (!mcpPlan?.targetPath) return false;
    return true;
  }

  function primaryActionLabel(): string {
    if (clientKind(mcpClient) === "file") return "Write config";
    if (clientKind(mcpClient) === "cli") return "Copy command";
    return "Copy to clipboard";
  }

  function secondaryActionLabel(): string {
    if (mcpPlan?.command) return "Copy command";
    return "Copy JSON";
  }

  async function copyMcpJson() {
    const text = mcpPreviewText;
    if (!text) {
      copyToast = "nothing to copy yet";
      return;
    }
    if (typeof navigator === "undefined" || !navigator.clipboard) {
      copyToast = "clipboard unavailable";
      return;
    }
    try {
      await navigator.clipboard.writeText(text);
      copyToast = mcpPlan?.command
        ? "Copied claude mcp add command to clipboard"
        : "Copied MCP config JSON to clipboard";
      S.appendDrawerLog(
        mcpPlan?.command
          ? "copied claude mcp add command to clipboard"
          : "copied MCP config JSON to clipboard"
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      copyToast = `copy failed: ${msg}`;
    } finally {
      setTimeout(() => (copyToast = null), 2000);
    }
  }

  async function primaryMcpAction() {
    if (clientKind(mcpClient) === "file") {
      await writeMcpConfigClick();
    } else {
      await copyMcpJson();
    }
  }

  async function writeMcpConfigClick() {
    if (!canWriteMcpConfig() || mcpWriting) return;
    const root = toolkitRoot;
    const projectPath = project.path;
    if (!projectPath || !root) return;
    mcpWriting = true;
    mcpWriteError = null;
    try {
      const params: McpConfigParamsWire = {
        projectPath,
        toolkitRoot: root,
        mcpIndexOverride: mcpIndexOverride,
        unityProjectPath: projectPath,
        bridgePort,
        includeUnityPath: false,
        unityPath: "",
        client: clientToWire(mcpClient),
        cursorProjectScope,
      };
      const result = await writeMcpConfig(params);
      mcpWriteResult = result;
      copyToast = result.wouldWrite
        ? `Wrote MCP config to ${result.targetPath}`
        : "MCP config already up to date — no write needed";
      S.appendDrawerLog(
        result.wouldWrite
          ? `AI Setup: wrote MCP config to ${result.targetPath} for ${project.name}`
          : `AI Setup: MCP config already up to date for ${project.name}`
      );
      // Refresh Step 1 detection so the Done screen reflects
      // the freshly-merged MCP heuristic flag.
      void refreshDetection();
    } catch (e) {
      mcpWriteError = toMcpConfigError(e);
      S.appendErrorLog(
        `MCP config write failed: ${describeMcpConfigError(mcpWriteError)}`
      );
    } finally {
      mcpWriting = false;
      setTimeout(() => (copyToast = null), 2500);
    }
  }

  async function copySkillFilesClick() {
    if (skillCopying) return;
    const root = toolkitRoot;
    const projectPath = project.path;
    if (!projectPath || !root || !skillPlan) return;
    const hasExisting = skillPlan.targets.some((t) => t.exists);
    if (hasExisting && !skillOverwriteAck) {
      skillError = {
        kind: "overwriteNotConfirmed",
        message:
          "One or more target files already exist. Check the overwrite box to replace them.",
      };
      return;
    }
    skillCopying = true;
    skillError = null;
    try {
      const params: SkillCopyParamsWire = {
        projectPath,
        toolkitRoot: root,
        opencodeSelected: isOpencodeClient(mcpClient),
      };
      const result = await copySkillFiles(params, skillOverwriteAck);
      skillResult = result;
      S.appendDrawerLog(
        `AI Setup: copied ${result.copied.length} skill file(s), skipped ${result.skipped.length} for ${project.name}`
      );
    } catch (e) {
      skillError = toSkillCopyError(e);
      S.appendErrorLog(
        `Skill copy failed: ${describeSkillCopyError(skillError)}`
      );
    } finally {
      skillCopying = false;
    }
  }

  async function installManifest() {
    if (!mergePlan || mergeWriting) return;
    if (mergePlan.hasUpgrades && !upgradeAcknowledged) return;
    mergeWriting = true;
    mergeError = null;
    try {
      const result = await writeManifestMerge({
        projectPath: project.path,
        toolkitRoot,
        installBridge,
        installVerify,
        versionPin: packageVersionPin,
        customUrl: packageCustomUrl,
        confirmUpgrades: mergePlan.hasUpgrades,
      });
      mergeResult = result;
      S.appendDrawerLog(
        `AI Setup: ${summarizeChanges(result.changes)} in ${project.name}`
      );
      // Refresh detection so the Done screen reflects the
      // freshly-installed packages.
      void refreshDetection();
    } catch (e) {
      const raw = e as { kind?: string; message?: string } | Error;
      const errShape: ManifestError =
        "kind" in (raw as ManifestError) && typeof (raw as ManifestError).kind === "string"
          ? (raw as ManifestError)
          : {
              kind: "unknown",
              message: raw instanceof Error ? raw.message : String(raw),
            };
      mergeError = describeManifestError(errShape);
      S.appendErrorLog(`manifest write failed: ${mergeError}`);
    } finally {
      mergeWriting = false;
    }
  }

  function resetStep5State() {
    step5Items = {
      launch: "pending",
      compile: "pending",
      ping: "pending",
      confirm: "pending",
    };
    step5LaunchPid = null;
    step5BridgePort = null;
    step5BridgeStatus = { kind: "notChecked" };
    step5PingResult = null;
    step5Error = null;
    step5StartedAt = null;
    step5DeadlineAt = null;
    step5LastTick = null;
  }

  async function runStep5Verify() {
    if (step5Running) return;
    resetStep5State();
    step5Running = true;
    const port = bridgePortFromString(String(bridgePort));
    step5BridgePort = port;
    step5StartedAt = Date.now();
    step5DeadlineAt = step5StartedAt + STEP5_TOTAL_BUDGET_MS;
    step5Items = {
      launch: "running",
      compile: "pending",
      ping: "pending",
      confirm: "pending",
    };
    let launched = false;
    try {
      const result = await launchForVerify({
        projectId: project.id,
        bridgePort: port,
        theme: (settingsStore.current?.theme as "dark" | "light" | "system" | undefined) ?? "system",
      });
      step5LaunchPid = result.pid;
      launched = true;
      step5Items = {
        ...step5Items,
        launch: "ok",
        compile: "running",
      };
      S.appendDrawerLog(
        `AI Setup: launched Unity (pid ${result.pid}) with bridge port ${port} for ${project.name}`,
      );
      // Phase 2 — poll the bridge `/ping` endpoint every
      // STEP5_POLL_INTERVAL_MS until the bridge responds 200
      // with a parseable body, or the 120 s overall budget
      // elapses, or the user clicks Stop. The compile step is
      // considered "ok" the moment the bridge reports
      // `compiling: false` (or once we've seen at least one
      // response, since the spec treats compile errors as
      // `ping: failed` rather than `compile: failed`).
      await pollBridgeUntilReady(port);
    } catch (e) {
      const message = describeLaunchForVerifyError(e);
      step5Error = message;
      S.appendErrorLog(`AI Setup: Step 5 launch failed: ${message}`);
      if (launched) {
        // Launch succeeded but the poll loop never returned
        // (probably a programming error); still mark the
        // individual steps accordingly.
        step5Items = {
          ...step5Items,
          launch: "ok",
          compile: "failed",
          ping: "failed",
        };
        step5BridgeStatus = { kind: "failed", message };
      } else {
        step5Items = {
          ...step5Items,
          launch: "failed",
          compile: "failed",
          ping: "failed",
        };
        step5BridgeStatus = { kind: "failed", message };
      }
    } finally {
      step5Running = false;
    }
  }

  async function pollBridgeUntilReady(port: number) {
    while (Date.now() < (step5DeadlineAt ?? Date.now())) {
      step5LastTick = Date.now();
      try {
        const result = await pollBridgePing(port, STEP5_PING_TIMEOUT_MS);
        step5PingResult = result;
        if (result.ok) {
          step5Items = {
            ...step5Items,
            compile: result.compiling ? "running" : "ok",
            ping: "ok",
            confirm: "ok",
          };
          step5BridgeStatus = {
            kind: "ok",
            connected: result.connected,
            projectPath: result.projectPath ?? null,
            compiling: result.compiling,
            isPlaying: result.isPlaying,
          };
          S.appendDrawerLog(
            `AI Setup: bridge /ping ok on port ${port} (connected=${result.connected})`,
          );
          return;
        }
        // Got an HTTP response but not 2xx — treat as compile
        // error / bridge not ready. Per the spec table, this
        // is a "Compile errors" row in the user-visible UX.
        if (result.errorKind === "httpError" || result.errorKind === "malformedBody") {
          step5Items = {
            ...step5Items,
            compile: "failed",
            ping: "failed",
          };
          step5BridgeStatus = {
            kind: "failed",
            message: result.errorMessage,
          };
          // Do not return — keep polling until the bridge
          // recovers (Unity may be mid-recompile).
        } else if (result.errorKind === "connectionRefused") {
          // Bridge not yet up; just keep the compile step
          // running and try again.
          step5Items = { ...step5Items, compile: "running" };
        } else {
          // timeout / unreachable — keep the steps pending
          // so the user sees a "still waiting" state, not a
          // hard failure.
        }
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`AI Setup: bridge /ping threw: ${msg}`);
      }
      await new Promise((resolve) =>
        setTimeout(resolve, STEP5_POLL_INTERVAL_MS),
      );
    }
    // Deadline elapsed without a successful ping.
    if (step5BridgeStatus.kind !== "ok") {
      step5Items = {
        ...step5Items,
        compile: "failed",
        ping: "failed",
      };
      step5BridgeStatus = {
        kind: "failed",
        message: `Bridge /ping did not respond within ${STEP5_TOTAL_BUDGET_MS / 1000}s on port ${port}.`,
      };
    }
  }

  function stopStep5Polling() {
    if (!step5Running) return;
    step5DeadlineAt = Date.now();
    S.appendDrawerLog(
      `AI Setup: user stopped Step 5 verification for ${project.name}`,
    );
  }

  function skipStep5Verify() {
    if (step5Running) return;
    resetStep5State();
    step5BridgeStatus = { kind: "notChecked" };
    S.appendDrawerLog(
      `AI Setup: skipped Step 5 verify for ${project.name} — wizard marked incomplete on Done`,
    );
  }

  function describeLaunchForVerifyError(e: unknown): string {
    if (
      e &&
      typeof e === "object" &&
      "kind" in e &&
      typeof (e as LaunchForVerifyError).kind === "string"
    ) {
      const err = e as LaunchForVerifyError;
      switch (err.kind) {
        case "projectNotFound":
          return `Project ${err.projectId} is no longer in the Hub project list. Reopen the wizard.`;
        case "pathInvalid":
          return `Project path is invalid: ${err.path}.`;
        case "versionMissing":
          return `Unity version is unknown. Open the project once in the Editor to refresh the version, then retry.`;
        case "installNotFound":
          return `Unity ${err.version} is not installed on this machine. Open the Installs tab to add it.`;
        case "launchFailed":
          return `Failed to launch Unity: ${err.message}. Open the launch log from the Status drawer.`;
        case "portInvalid":
          return `Bridge port ${err.port} is not a valid TCP port. Pick a port in 1..65535.`;
        default:
          return `${(e as { kind?: string }).kind ?? "unknown"}: ${(e as { message?: string }).message ?? "unknown error"}`;
      }
    }
    return e instanceof Error ? e.message : String(e);
  }

  function describePingErrorMessage(result: BridgePingResult | null): string {
    if (!result) return "—";
    if (result.ok) return `connected=${result.connected} in ${result.durationMs} ms`;
    if (result.errorMessage) return `${result.errorKind}: ${result.errorMessage}`;
    return result.errorKind || "failed";
  }

  function pingStatusTone(
    state: Step5ItemState
  ): "ok" | "warn" | "muted" | "info" {
    if (state === "ok") return "ok";
    if (state === "failed") return "warn";
    if (state === "running") return "info";
    return "muted";
  }

  function pingDurationSuffix(): string {
    if (!step5PingResult) return "";
    return ` (${step5PingResult.durationMs}ms)`;
  }

  function openProjectFolder() {
    if (!project.path) return;
    void openPath(project.path);
  }

  function revealProjectFolder() {
    if (!project.path) return;
    void revealItemInDir(project.path);
  }

  async function openMcpConfigTarget() {
    if (!mcpPlan?.targetPath) return;
    try {
      await openPath(mcpPlan.targetPath);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`AI Setup: cannot open ${mcpPlan.targetPath}: ${msg}`);
    }
  }

  async function openToolkitSkill() {
    if (!toolkitRoot.trim()) return;
    const target = `${toolkitRoot.replace(/[\\/]+$/, "")}/skills/unity-open-mcp/SKILL.md`;
    try {
      await openPath(target);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`AI Setup: cannot open ${target}: ${msg}`);
    }
  }

  async function openCopiedSkill() {
    if (!skillResult || skillResult.copied.length === 0) return;
    const target = skillResult.copied[0].targetPath;
    try {
      await openPath(target);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`AI Setup: cannot open ${target}: ${msg}`);
    }
  }

  function packagesSummary(d: ProjectState | null): {
    tone: "ok" | "warn" | "missing";
    label: string;
  } {
    if (!d) return { tone: "missing", label: "unknown" };
    if (d.bridgeInstalled && d.verifyInstalled) {
      return { tone: "ok", label: "installed" };
    }
    if (d.bridgeInstalled || d.verifyInstalled) {
      return { tone: "warn", label: "partial" };
    }
    return { tone: "missing", label: "not installed" };
  }

  function mcpSummary(): { tone: "ok" | "warn" | "muted"; label: string } {
    const h = detection?.mcpConfigured;
    if (!h) return { tone: "muted", label: "not detected" };
    if (h.cursor || h.claudeDesktop || h.opencodeGlobal || h.opencodeProject) {
      return { tone: "ok", label: "configured" };
    }
    if (mcpWriteResult?.wouldWrite) {
      return { tone: "ok", label: "written" };
    }
    if (mcpClient === "claude-code") {
      return { tone: "warn", label: "cli command" };
    }
    if (mcpClient === "manual") {
      return { tone: "warn", label: "manual" };
    }
    return { tone: "warn", label: "not configured" };
  }

  function launchSummary(): { tone: "ok" | "warn" | "muted"; label: string } {
    if (step5Items.launch === "ok" && step5Items.ping === "ok") {
      return { tone: "ok", label: "ok" };
    }
    if (step5Items.launch === "ok") {
      return { tone: "warn", label: "ok · bridge pending" };
    }
    if (step5Items.launch === "failed") {
      return { tone: "warn", label: "failed" };
    }
    if (step5BridgeStatus.kind === "notChecked" && step5LaunchPid === null) {
      return { tone: "muted", label: "not run" };
    }
    return { tone: "muted", label: "pending" };
  }

  function bridgeSummary(): { tone: "ok" | "warn" | "muted"; label: string } {
    if (step5BridgeStatus.kind === "ok") {
      return {
        tone: "ok",
        label: step5BridgeStatus.connected ? "connected" : "responded",
      };
    }
    if (step5BridgeStatus.kind === "failed") {
      return { tone: "warn", label: "failed" };
    }
    return { tone: "muted", label: "not checked" };
  }

  function reRunWizard() {
    // Reset the per-step state to Step 1 without touching the
    // persisted settings (toolkit root + MCP override stay
    // pre-filled in Step 2). Per questions-4 Q11 = A, no
    // wizard progress is persisted, so the next entry would
    // already start at Step 1 — this function just makes the
    // reset explicit and immediate.
    resetStep5State();
    mcpWriteResult = null;
    mcpWriteError = null;
    mcpPlan = null;
    mergeResult = null;
    mergeError = null;
    upgradeAcknowledged = false;
    skillResult = null;
    skillError = null;
    skillOverwriteAck = false;
    step5BridgeStatus = { kind: "notChecked" };
    currentStep = "step1";
    void refreshDetection();
  }

  function openInCursor() {
    if (!project.path) return;
    // The Hub doesn't track a Cursor install path; the
    // `code`-style CLI behavior on macOS / Linux is to launch
    // Cursor with the project folder. We use the OS opener so
    // the platform decides how to handle the .app / .exe
    // association — same approach as the regular project
    // toolbar's "Open" action.
    void openPath(project.path);
  }

  function openInOpencode() {
    if (!project.path) return;
    // OpenCode's TUI is CLI-first; opening the project folder
    // via the OS opener is the lowest-friction path that does
    // not require us to ship an OpenCode install detection
    // step in M4. A future M7+ task can wire `opencode <path>`
    // once we have an OpenCode install probe.
    void openPath(project.path);
  }

  let canOpenInCursor = $derived(
    mcpClient === "cursor" && Boolean(project.path),
  );
  let canOpenInOpencode = $derived(
    (mcpClient === "opencode-global" || mcpClient === "opencode-project") &&
      Boolean(project.path),
  );

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

  function handleClose() {
    onClose();
  }

  // --- Step 1 derived display helpers -------------------------------
  function mcpConfiguredSummary(h: McpConfigHeuristic): string {
    const any =
      h.cursor || h.claudeDesktop || h.opencodeGlobal || h.opencodeProject;
    if (!any) return "not detected";
    const clients: string[] = [];
    if (h.cursor) clients.push("Cursor");
    if (h.claudeDesktop) clients.push("Claude Desktop");
    if (h.opencodeGlobal) clients.push("OpenCode (global)");
    if (h.opencodeProject) clients.push("OpenCode (project)");
    return `yes (${clients.join(", ")})`;
  }

  // --- Step 2 derived display helpers -------------------------------
  let unityBlockReason = $derived.by(() => {
    if (!detection) return null;
    if (detection.isValidUnityProject && !detection.unityVersion) {
      return "ProjectSettings/ProjectVersion.txt is missing or empty.";
    }
    if (!detection.meetsMinUnityVersion) {
      return `Detected Unity ${detection.unityVersion ?? "unknown"} — Unity Open MCP Bridge requires Unity 6 (6000.0+).`;
    }
    return null;
  });

  let manifestWriteBlockReason = $derived.by(() => {
    if (!detection) return null;
    if (!detection.manifestWritable) {
      return "Packages/manifest.json (or its parent) is not user-writable.";
    }
    return null;
  });

  // --- Step 3 derived display helpers -------------------------------
  let diffPreviewText = $derived.by(() => {
    if (!mergePlan) return "";
    return formatDiffPreview(mergePlan.changes);
  });

  let hasRealChanges = $derived.by(() => {
    if (!mergePlan) return false;
    return mergePlan.changes.some((c) => c.kind !== "unchanged");
  });

  let manifestParseError = $derived.by(() => {
    if (!mergePlan) return null;
    return mergePlan.manifestRead.parseError;
  });
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
            Step 1 detects the current state of the project. The
            detection is re-run on every Step 1 entry so the
            values below always reflect the on-disk manifest and
            <code>ProjectVersion.txt</code>; the Done screen
            re-uses the same snapshot.
          </p>

          {#if detectionLoading && !detection}
            <p class="wiz-hint">Detecting project…</p>
          {/if}
          {#if detectionError}
            <p class="wiz-hint wiz-hint-warn">{detectionError}</p>
            <div class="wiz-actions-row">
              <Button variant="secondary" onclick={refreshDetection} disabled={detectionLoading}>
                {detectionLoading ? "Re-detecting…" : "Re-detect"}
              </Button>
            </div>
          {/if}

          {#if detection}
            {#if !detection.isValidUnityProject}
              <div class="wiz-block wiz-block-error" role="alert">
                <strong>Not a valid Unity project.</strong>
                The selected path is missing
                <code>Assets/</code> or
                <code>ProjectSettings/</code>. The wizard cannot
                continue.
                <div class="wiz-actions-row">
                  <Button variant="secondary" onclick={refreshDetection} disabled={detectionLoading}>
                    {detectionLoading ? "Re-detecting…" : "Re-detect"}
                  </Button>
                </div>
              </div>
            {:else}
              <dl class="wiz-summary">
                <div>
                  <dt>Project name</dt>
                  <dd>{detection.name || project.name}</dd>
                </div>
                <div>
                  <dt>Path</dt>
                  <dd><code title={detection.path}>{detection.path}</code></dd>
                </div>
                <div>
                  <dt>Unity version</dt>
                  <dd>
                    {#if detection.unityVersion}
                      <code>{detection.unityVersion}</code>
                      {#if detection.meetsMinUnityVersion}
                        <span class="wiz-tag wiz-tag-ok">Unity 6+</span>
                      {:else}
                        <span class="wiz-tag wiz-tag-warn">below Unity 6</span>
                      {/if}
                    {:else}
                      <em>unknown</em>
                    {/if}
                  </dd>
                </div>
                <div>
                  <dt>Bridge installed</dt>
                  <dd>
                    {#if detection.bridgeInstalled}
                      <span class="wiz-tag wiz-tag-ok">yes</span>
                    {:else}
                      <span class="wiz-tag wiz-tag-warn">no</span>
                    {/if}
                  </dd>
                </div>
                <div>
                  <dt>Verify installed</dt>
                  <dd>
                    {#if detection.verifyInstalled}
                      <span class="wiz-tag wiz-tag-ok">yes</span>
                    {:else}
                      <span class="wiz-tag wiz-tag-warn">no</span>
                    {/if}
                  </dd>
                </div>
                <div>
                  <dt>MCP configured</dt>
                  <dd>
                    {mcpConfiguredSummary(detection.mcpConfigured)}
                  </dd>
                </div>
                <div>
                  <dt>Bridge status</dt>
                  <dd><em>not checked</em></dd>
                </div>
              </dl>
              <div class="wiz-actions-row">
                <Button variant="secondary" onclick={refreshDetection} disabled={detectionLoading}>
                  {detectionLoading ? "Re-detecting…" : "Re-detect"}
                </Button>
              </div>
            {/if}
          {/if}
        </section>
      {:else if currentStep === "step2"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 2 validates Unity version, Node.js, the cloned
            unity-open-mcp toolkit root, and write access to
            <code>Packages/manifest.json</code>. The first three
            checks must pass before you can continue to Step 3.
          </p>

          {#if detection}
            <div class="wiz-field">
              <span class="wiz-label">Unity version</span>
              {#if unityBlockReason}
                <div class="wiz-block wiz-block-error" role="alert">
                  <strong>Unity {detection.unityVersion ?? "unknown"} does not meet the minimum.</strong>
                  {unityBlockReason}
                </div>
              {:else if detection.meetsMinUnityVersion}
                <p class="wiz-hint wiz-hint-ok">
                  Detected <strong>Unity {detection.unityVersion}</strong> — meets the
                  <strong>Unity 6 (6000.0+)</strong> requirement.
                </p>
              {/if}
            </div>
          {/if}

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
              <div class="wiz-block wiz-block-error" role="alert">
                <strong>Node.js {nodeProbe.requiredMajor}+ is required</strong>
                to run <code>unity-open-mcp</code>.
                {#if nodeProbe.version}
                  Detected <strong>{nodeProbe.version}</strong>.
                {:else}
                  Not detected on PATH.
                {/if}
                {#if nodeProbe.error}
                  <br /><small>{nodeProbe.error}</small>
                {/if}
              </div>
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
                placeholder="/Users/you/unity-open-mcp"
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

          {#if detection && !detection.manifestWritable}
            <div class="wiz-field">
              <span class="wiz-label">Manifest write access</span>
              <div class="wiz-block wiz-block-error" role="alert">
                <strong>Cannot write to <code>Packages/manifest.json</code>.</strong>
                {manifestWriteBlockReason} The wizard cannot install packages
                without write access.
              </div>
            </div>
          {/if}

          {#if detection?.hasSpacesInPath}
            <div class="wiz-field">
              <span class="wiz-label">Warning</span>
              <p class="wiz-hint wiz-hint-warn">
                The project path contains a space. Some MCP clients are known
                to mis-handle paths with spaces. This is a warning, not a
                block.
              </p>
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
            <code>Packages/manifest.json</code>. The diff preview
            below is live — it re-computes whenever you change a
            toggle, version pin, or custom URL. When the project is
            the demo project bundled inside the unity-open-mcp repo
            itself, the wizard detects this and uses a local
            <code>file:</code> path instead of a remote git URL —
            no network fetch required. An upgrade (existing entry
            with a different URL or tag) always requires explicit
            confirmation before the wizard will write. Unrelated
            dependency entries are preserved verbatim.
          </p>

          <div class="wiz-field">
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installBridge} />
              <span><strong>Install Unity Open MCP Bridge</strong> — required for live MCP tooling</span>
            </label>
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installVerify} />
              <span>
                <strong>Install Unity Open MCP Verify</strong> —
                <small>Scoped health checks for AI gates — not the full Unity Scanner window.</small>
              </span>
            </label>
            <label class="wiz-toggle">
              <input type="checkbox" bind:checked={installScanner} disabled />
              <span>
                <strong>Also install Unity Scanner</strong> —
                <small>Full upstream product for human inspection in the Editor (advanced, off by default; not wired in v1).</small>
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
                value={packageCustomUrl}
                oninput={(e) => (packageCustomUrl = (e.currentTarget as HTMLInputElement).value)}
              />
              <p class="wiz-hint">
                Replaces the toolkit root's git remote. Useful for
                testing against a fork; not required for the
                standard monorepo flow.
              </p>
            </div>
          </details>

          <div class="wiz-field">
            <span class="wiz-label">Manifest status</span>
            {#if !installBridge && !installVerify}
              <p class="wiz-hint wiz-hint-warn">
                Pick at least one package to install.
              </p>
            {:else if !toolkitRoot.trim() || !toolkitValidation?.ok}
              <p class="wiz-hint wiz-hint-warn">
                Validate the toolkit root in Step 2 first.
              </p>
            {:else if mergePlanning && !mergePlan}
              <p class="wiz-hint">Planning merge…</p>
            {:else if mergePlan}
              {#if manifestParseError}
                <div class="wiz-block wiz-block-error" role="alert">
                  <strong>Cannot parse <code>Packages/manifest.json</code>.</strong>
                  {manifestParseError} Fix the JSON by hand and re-run.
                </div>
              {:else if !mergePlan.manifestRead.present}
                <p class="wiz-hint">
                  No <code>Packages/manifest.json</code> on disk yet — the
                  wizard will create one with the selected entries.
                </p>
              {/if}

              {#if !manifestParseError}
                <ul class="wiz-fingerprints" aria-label="Merge plan">
                  {#each mergePlan.changes as change (change.id)}
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

                {#if mergePlan.hasUpgrades}
                  <label class="wiz-toggle wiz-toggle-confirm">
                    <input type="checkbox" bind:checked={upgradeAcknowledged} />
                    <span>
                      <strong>I understand the manifest will be upgraded.</strong>
                      <small>The existing bridge/verify entries differ from the proposed values (different tag or git remote). The wizard will overwrite them only after this confirmation.</small>
                    </span>
                  </label>
                {/if}

                <details class="wiz-advanced" bind:open={showDiff}>
                  <summary>Preview manifest diff</summary>
                  <pre class="wiz-codeblock" aria-label="Manifest diff">{diffPreviewText}</pre>
                  {#if mergePlan.derivedUrls.gitRemote}
                    <p class="wiz-hint">
                      Using git remote <code>{mergePlan.derivedUrls.gitRemote}</code>
                      derived from the toolkit root
                      {#if packageCustomUrl.trim()}(overridden by the custom URL field){/if}.
                    </p>
                  {/if}
                </details>
              {/if}
            {/if}
          </div>

          {#if mergeResult}
            <div class="wiz-block wiz-block-ok" role="status">
              <strong>Manifest written.</strong>
              {summarizeChanges(mergeResult.changes)}.
              {#if mergeResult.backupPath}
                Backup saved to <code>{mergeResult.backupPath}</code>.
              {/if}
            </div>
          {/if}
          {#if mergeError}
            <div class="wiz-block wiz-block-error" role="alert">
              {mergeError}
            </div>
          {/if}

          <div class="wiz-actions-row">
            <Button
              variant="primary"
              onclick={installManifest}
              disabled={!isManifestReady() || mergeWriting}
            >
              {mergeWriting
                ? "Installing…"
                : hasRealChanges
                  ? mergePlan?.hasUpgrades
                    ? "Upgrade manifest"
                    : "Install"
                  : "Already installed"}
            </Button>
            <Button
              variant="secondary"
              onclick={() => {
                currentStep = "step4";
              }}
              disabled={!mergeResult}
              title={mergeResult ? "Skip to MCP client config" : "Run the install at least once"}
            >
              Skip to Step 4
            </Button>
          </div>
        </section>
      {:else if currentStep === "step4"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 4 writes a <code>unity-open-mcp</code> MCP server
            entry to your client config. The wizard calls the
            Rust planner on every form-state change so the live
            preview matches exactly what the writer will emit:
            <code>mcpServers.unity-open-mcp</code> for Cursor /
            Claude Desktop, <code>mcp.unity-open-mcp</code> for
            OpenCode, a <code>claude mcp add</code> command for
            Claude Code, and a copyable snippet for Manual.
            Unrelated MCP servers are merged through unchanged.
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
            <span class="wiz-label">
              {mcpPlan?.command ? "Claude Code command" : "Generated config"}
            </span>
            {#if mcpPlanning && !mcpPlan}
              <p class="wiz-hint">Planning…</p>
            {:else if !mcpPlan}
              <p class="wiz-hint wiz-hint-warn">
                Set the toolkit root in Step 2 to generate a config.
              </p>
            {:else}
              <pre class="wiz-codeblock" aria-label={mcpPlan.command ? "Claude Code command" : "Generated MCP config"}>{mcpPreviewText || "—"}</pre>
              {#if mcpPlan.targetPath}
                <p class="wiz-hint">
                  Target: <code>{mcpPlan.targetPath}</code>
                  {#if mcpPlan.fileExists}
                    <span class="wiz-tag wiz-tag-warn">file exists</span>
                  {:else}
                    <span class="wiz-tag wiz-tag-ok">new file</span>
                  {/if}
                  {#if !mcpPlan.wouldWrite && mcpPlan.fileExists}
                    <span class="wiz-tag wiz-tag-ok">already up to date</span>
                  {/if}
                </p>
              {/if}
              {#if mcpPlan.preservedKeys.length > 0}
                <p class="wiz-hint">
                  Preserved top-level keys: {mcpPlan.preservedKeys
                    .filter((k) => !["mcpServers", "mcp"].includes(k))
                    .map((k) => `<code>${k}</code>`)
                    .join(", ") || "<em>none</em>"}
                  {#if mcpPlan.preservedKeys.some((k) => ["mcpServers", "mcp"].includes(k))}
                    ; other servers under <code>mcpServers</code> / <code>mcp</code> are also kept.
                  {/if}
                </p>
              {/if}
              {#if mcpPlan.command}
                <p class="wiz-hint">
                  Claude Code is CLI-only — the wizard
                  renders the <code>claude mcp add</code> command
                  and never writes a config file.
                </p>
              {/if}
            {/if}
            {#if resolvedMcpPathValid === false}
              <p class="wiz-hint wiz-hint-warn">
                Resolved MCP path does not exist on disk:
                <code>{resolvedMcpPath}</code>.
                Run <code>npm run build</code> in
                <code>{toolkitRoot}/mcp-server/</code>.
              </p>
            {/if}
          </div>

          {#if mcpWriteResult?.wouldWrite}
            <div class="wiz-block wiz-block-ok" role="status">
              <strong>MCP config written.</strong>
              Saved to <code>{mcpWriteResult.targetPath}</code>.
              {#if mcpWriteResult.backupPath}
                Backup at <code>{mcpWriteResult.backupPath}</code>.
              {/if}
            </div>
          {:else if mcpWriteResult && !mcpWriteResult.wouldWrite}
            <div class="wiz-block wiz-block-ok" role="status">
              <strong>Already up to date.</strong>
              Existing <code>{mcpWriteResult.targetPath}</code> already
              matches the proposed <code>unity-open-mcp</code> entry — no
              write or backup was needed.
            </div>
          {/if}
          {#if mcpWriteError}
            <div class="wiz-block wiz-block-error" role="alert">
              {describeMcpConfigError(mcpWriteError)}
            </div>
          {/if}

          <div class="wiz-actions-row">
            <Button
              variant="primary"
              onclick={primaryMcpAction}
              disabled={
                (clientKind(mcpClient) === "file" && (!canWriteMcpConfig() || mcpWriting)) ||
                (clientKind(mcpClient) !== "file" && !mcpPreviewText)
              }
              title={
                clientKind(mcpClient) === "file" && canWriteMcpConfig()
                  ? "Write config"
                  : clientKind(mcpClient) === "file"
                    ? "Pick a client + valid MCP path first"
                    : "Copy to clipboard"
              }
            >
              {mcpWriting ? "Writing…" : primaryActionLabel()}
            </Button>
            {#if clientKind(mcpClient) === "file" && mcpPlan?.command === null}
              <!-- noop placeholder so the next button is the only secondary when CLI/Manual -->
            {/if}
            <Button variant="secondary" onclick={copyMcpJson} disabled={!mcpPreviewText}>
              {secondaryActionLabel()}
            </Button>
            {#if copyToast}
              <span class="wiz-toast" role="status">{copyToast}</span>
            {/if}
          </div>
        </section>
      {:else if currentStep === "step5"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 5 launches Unity with the bridge port pinned via
            <code>-UNITY_OPEN_MCP_BRIDGE_PORT={step5BridgePort ?? bridgePortFromString(String(bridgePort))}</code>
            and polls the bridge HTTP <code>/ping</code> endpoint
            for up to 120 s. The wizard never spawns a separate
            <code>unity-open-mcp</code> subprocess — the wizard
            keeps the verify path to a direct HTTP GET. The
            Done screen re-runs detection on entry and pairs the
            live snapshot with this step's bridge result.
          </p>
          <ol class="wiz-checklist">
            <li class:done={step5Items.launch === "ok"} class:running={step5Items.launch === "running"}>
              Launch Unity (pid {step5LaunchPid ?? "—"})
              {#if step5Items.launch === "ok"}<span class="wiz-check-done">— ok</span>{:else if step5Items.launch === "running"}<span class="wiz-check-running">— launching…</span>{:else if step5Items.launch === "failed"}<span class="wiz-check-failed">— failed</span>{/if}
            </li>
            <li class:done={step5Items.compile === "ok"} class:running={step5Items.compile === "running"} class:failed={step5Items.compile === "failed"}>
              Wait for project compile
              {#if step5Items.compile === "ok"}<span class="wiz-check-done">— ok</span>{:else if step5Items.compile === "running"}<span class="wiz-check-running">— compiling…</span>{:else if step5Items.compile === "failed"}<span class="wiz-check-failed">— compile error</span>{/if}
            </li>
            <li class:done={step5Items.ping === "ok"} class:running={step5Items.ping === "running"} class:failed={step5Items.ping === "failed"}>
              Wait for bridge HTTP <code>/ping</code> (timeout 120s)
              {#if step5Items.ping === "ok"}
                <span class="wiz-check-done">— ok{pingDurationSuffix()}</span>
              {:else if step5Items.ping === "running"}
                <span class="wiz-check-running">— polling…</span>
              {:else if step5Items.ping === "failed"}
                <span class="wiz-check-failed">— failed</span>
              {/if}
            </li>
            <li class:done={step5Items.confirm === "ok"} class:failed={step5Items.confirm === "failed"}>
              Confirm response fields (<code>connected</code>, project path, compile/play state)
              {#if step5Items.confirm === "ok" && step5BridgeStatus.kind === "ok"}
                <span class="wiz-check-done">
                  — connected={step5BridgeStatus.connected}{step5BridgeStatus.projectPath ? `, project=${step5BridgeStatus.projectPath}` : ""}
                </span>
              {:else if step5Items.confirm === "failed"}
                <span class="wiz-check-failed">— {describePingErrorMessage(step5PingResult)}</span>
              {/if}
            </li>
          </ol>
          {#if step5BridgePort !== null}
            <p class="wiz-hint">
              Bridge port: <code>{step5BridgePort}</code>
              {#if step5LastTick}
                · last poll {Math.max(0, Math.round((Date.now() - step5LastTick) / 100) / 10)}s ago
              {/if}
            </p>
          {/if}
          {#if step5Error}
            <div class="wiz-block wiz-block-error" role="alert">
              {step5Error}
            </div>
          {/if}
          <div class="wiz-actions-row">
            {#if step5Items.launch !== "ok"}
              <Button variant="primary" onclick={runStep5Verify} disabled={step5Running}>
                {step5Running ? "Launching…" : "Launch Unity"}
              </Button>
            {:else}
              <Button variant="primary" onclick={runStep5Verify} disabled={step5Running}>
                {step5Running ? "Re-verifying…" : "Re-verify"}
              </Button>
            {/if}
            {#if step5Running}
              <Button variant="secondary" onclick={stopStep5Polling}>Stop polling</Button>
            {/if}
            <Button variant="secondary" onclick={skipStep5Verify} disabled={step5Running}>
              Skip to Done
            </Button>
          </div>
        </section>
      {:else if currentStep === "done"}
        <section class="wiz-section">
          <div class="wiz-block wiz-block-ok wiz-done-banner" role="status">
            <strong>Setup complete!</strong>
            The Unity AI agent is installed and configured. You can now
            use MCP tools from your AI client to inspect and drive the
            Unity Editor. Examples you can try:
            <ul class="wiz-done-examples">
              <li><code>"List all scenes in the project"</code></li>
              <li><code>"What is the current build target?"</code></li>
              <li><code>"Run a health check on the project"</code></li>
              <li><code>"Show me all installed packages"</code></li>
            </ul>
          </div>

          <p class="wiz-desc">
            The checklist below is computed from
            the live project state — no per-step progress is
            persisted, so re-running the
            wizard always restarts at Step 1 and the Done screen
            always reflects the latest on-disk manifest. The
            bridge <code>/ping</code> row carries the Step 5
            result; the live detection snapshot below it shows
            the freshest manifest / MCP heuristic the wizard
            could read.
          </p>
          <dl class="wiz-summary">
            <div>
              <dt>Project</dt>
              <dd>
                {detection?.name ?? project.name} · {detection?.path ?? project.path}
              </dd>
            </div>
            <div>
              <dt>Unity</dt>
              <dd>
                {#if detection?.unityVersion}
                  <code>{detection.unityVersion}</code>
                  {#if detection.meetsMinUnityVersion}
                    <StatusChip tone="ok" label="meets minimum" />
                  {:else}
                    <StatusChip tone="warn" label="below minimum" />
                  {/if}
                {:else}
                  <em>unknown</em>
                {/if}
              </dd>
            </div>
            <div>
              <dt>Packages installed</dt>
              <dd>
                {#if detection}
                  {@const pkg = packagesSummary(detection)}
                  <StatusChip tone={pkg.tone} label={pkg.label} />
                  <small class="wiz-summary-small">
                    bridge: {detection.bridgeInstalled ? "yes" : "no"} · verify: {detection.verifyInstalled ? "yes" : "no"}
                  </small>
                {:else}
                  <StatusChip tone="muted" label="unknown" />
                {/if}
                {#if mergeResult}
                  <br />
                  <small>Last install: {summarizeChanges(mergeResult.changes)}</small>
                {/if}
              </dd>
            </div>
            <div>
              <dt>MCP configured</dt>
              <dd>
                {#if true}
                  {@const mcp = mcpSummary()}
                  <StatusChip tone={mcp.tone} label={mcp.label} />
                  <small class="wiz-summary-small">
                    {mcpConfiguredSummary(detection?.mcpConfigured ?? { cursor: false, claudeDesktop: false, opencodeGlobal: false, opencodeProject: false })}
                  </small>
                {/if}
              </dd>
            </div>
            <div>
              <dt>Unity launched</dt>
              <dd>
                {#if true}
                  {@const launch = launchSummary()}
                  <StatusChip tone={launch.tone} label={launch.label} />
                  {#if step5LaunchPid !== null}
                    <small class="wiz-summary-small">pid {step5LaunchPid}</small>
                  {/if}
                {/if}
              </dd>
            </div>
            <div>
              <dt>Bridge verified</dt>
              <dd>
                {#if true}
                  {@const br = bridgeSummary()}
                  <StatusChip tone={br.tone} label={br.label} />
                  {#if step5BridgeStatus.kind === "ok"}
                    <small class="wiz-summary-small">
                      {#if step5BridgeStatus.projectPath}project: {step5BridgeStatus.projectPath}{/if}
                      {#if step5BridgeStatus.isPlaying} · in play mode{/if}
                    </small>
                  {:else if step5BridgeStatus.kind === "failed"}
                    <small class="wiz-summary-small">{step5BridgeStatus.message}</small>
                  {/if}
                {/if}
              </dd>
            </div>
            <div>
              <dt>Toolkit root</dt>
              <dd><code>{toolkitRoot || "<not set>"}</code></dd>
            </div>
          </dl>

          <div class="wiz-field">
            <span class="wiz-label">Links</span>
            <div class="wiz-actions-row">
              <Button variant="secondary" onclick={openProjectFolder} disabled={!project.path}>
                Open project folder
              </Button>
              <Button variant="secondary" onclick={revealProjectFolder} disabled={!project.path}>
                Reveal in Finder / Explorer
              </Button>
              <Button
                variant="secondary"
                onclick={openMcpConfigTarget}
                disabled={!mcpPlan?.targetPath}
                title={mcpPlan?.targetPath ? `Open ${mcpPlan.targetPath}` : "No MCP config target was written"}
              >
                Open MCP config file
              </Button>
              <Button variant="secondary" onclick={openToolkitSkill} disabled={!toolkitRoot.trim()}>
                Open toolkit skill
              </Button>
              <Button
                variant="secondary"
                onclick={openCopiedSkill}
                disabled={!skillResult || skillResult.copied.length === 0}
              >
                Open copied skill
              </Button>
            </div>
            <p class="wiz-hint">
              The Advanced BYO-bridge section is intentionally
              hidden in v1 (it will be re-enabled after
              the batch criteria are met). Step 6 baseline
              creation is also deferred; the wizard renders
              only Steps 1-5 plus this Done screen.
            </p>
          </div>

          <div class="wiz-field">
            <span class="wiz-label">Skill copy</span>
            <p class="wiz-desc">
              On Done, the wizard copies
              <code>{toolkitRoot || "<toolkit>"}/skills/unity-open-mcp/SKILL.md</code>
              into the project's Claude-compatible skill folder, and
              {#if isOpencodeClient(mcpClient)}
                (because OpenCode was selected) the OpenCode mirror too.
              {:else}
                also into the OpenCode mirror when OpenCode is the selected client.
              {/if}
              Existing files are only overwritten after you tick the
              confirmation box.
            </p>
            {#if skillPlanning && !skillPlan}
              <p class="wiz-hint">Planning skill copy…</p>
            {:else if skillError}
              <div class="wiz-block wiz-block-error" role="alert">
                {describeSkillCopyError(skillError)}
              </div>
            {:else if skillPlan}
              {#if !skillPlan.sourcePath}
                <div class="wiz-block wiz-block-error" role="alert">
                  Source skill file is missing in the toolkit root. Run
                  the wizard again with a valid toolkit checkout.
                </div>
              {:else}
                <ul class="wiz-fingerprints" aria-label="Skill copy targets">
                  {#each skillPlan.targets as target (target.targetPath)}
                    {@const tone = target.exists ? "warn" : "ok"}
                    <li class="wiz-fp wiz-fp-{tone}">
                      <span class="wiz-fp-name">
                        <code>{target.relativePath}</code>
                      </span>
                      <span class="wiz-fp-status">
                        {#if target.exists}exists — will be overwritten only with confirmation{:else}will create{/if}
                      </span>
                    </li>
                  {/each}
                </ul>
                {#if skillPlan.targets.some((t) => t.exists)}
                  <label class="wiz-toggle wiz-toggle-confirm">
                    <input type="checkbox" bind:checked={skillOverwriteAck} />
                    <span>
                      <strong>Overwrite existing skill files.</strong>
                      <small>The targets above already exist; the wizard will back them up to <code>*.bak</code> and replace them only when this is checked.</small>
                    </span>
                  </label>
                {/if}
                <div class="wiz-actions-row">
                  <Button
                    variant="primary"
                    onclick={copySkillFilesClick}
                    disabled={
                      skillCopying ||
                      (skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck)
                    }
                    title={
                      skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck
                        ? "Confirm overwrite first"
                        : "Copy skill files"
                    }
                  >
                    {skillCopying ? "Copying…" : "Copy skill files"}
                  </Button>
                </div>
              {/if}
            {/if}
            {#if skillResult}
              <div class="wiz-block wiz-block-ok" role="status">
                <strong>Skill copy complete.</strong>
                Copied {skillResult.copied.length} file(s)
                {#if skillResult.overwritten.length > 0}
                  ({skillResult.overwritten.length} replaced existing)
                {/if}
                {#if skillResult.skipped.length > 0}
                  · skipped {skillResult.skipped.length} (already present)
                {/if}
              </div>
              {#if skillResult.copied.length > 0}
                <ul class="wiz-fingerprints" aria-label="Copied skill files">
                  {#each skillResult.copied as t (t.targetPath)}
                    <li class="wiz-fp wiz-fp-ok">
                      <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
                      <span class="wiz-fp-status">copied</span>
                    </li>
                  {/each}
                </ul>
              {/if}
              {#if skillResult.skipped.length > 0}
                <ul class="wiz-fingerprints" aria-label="Skipped skill files">
                  {#each skillResult.skipped as t (t.targetPath)}
                    <li class="wiz-fp wiz-fp-warn">
                      <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
                      <span class="wiz-fp-status">skipped (not overwritten)</span>
                    </li>
                  {/each}
                </ul>
              {/if}
            {/if}
          </div>

          <div class="wiz-actions-row">
            <Button variant="primary" onclick={handleClose}>Close</Button>
            <Button variant="secondary" onclick={reRunWizard}>Re-run wizard</Button>
            {#if canOpenInCursor}
              <Button variant="secondary" onclick={openInCursor}>
                Open in Cursor
              </Button>
            {/if}
            {#if canOpenInOpencode}
              <Button variant="secondary" onclick={openInOpencode}>
                Open in OpenCode
              </Button>
            {/if}
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

  .wiz-tag {
    display: inline-block;
    margin-left: 0.3rem;
    padding: 0.05rem 0.4rem;
    border-radius: 999px;
    font-size: 0.7rem;
    font-weight: 600;
    border: 1px solid var(--hub-border-light);
    background: var(--hub-card);
    color: var(--hub-text-muted);
    vertical-align: middle;
  }

  .wiz-tag-ok {
    color: #4ade80;
    border-color: rgba(74, 222, 128, 0.35);
    background: rgba(74, 222, 128, 0.08);
  }

  .wiz-tag-warn {
    color: #fbbf24;
    border-color: rgba(251, 191, 36, 0.35);
    background: rgba(251, 191, 36, 0.08);
  }

  .wiz-block {
    padding: 0.55rem 0.7rem;
    border-radius: 6px;
    border: 1px solid var(--hub-border-light);
    background: var(--hub-card);
    font-size: 0.82rem;
    color: var(--hub-text);
    line-height: 1.45;
  }

  .wiz-block code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.74rem;
    background: rgba(255, 255, 255, 0.06);
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .wiz-block-error {
    border-color: rgba(248, 113, 113, 0.35);
    background: rgba(248, 113, 113, 0.08);
    color: var(--hub-error-fg);
  }

  .wiz-block-error strong {
    color: #fca5a5;
  }

  .wiz-block-ok {
    border-color: rgba(74, 222, 128, 0.35);
    background: rgba(74, 222, 128, 0.08);
    color: #4ade80;
  }

  .wiz-block-ok strong {
    color: #bbf7d0;
  }

  .wiz-done-banner {
    padding: 0.7rem 0.85rem;
    line-height: 1.6;
  }

  .wiz-done-examples {
    margin: 0.5rem 0 0;
    padding-left: 1.2rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .wiz-done-examples li {
    font-size: 0.8rem;
    color: var(--hub-text);
  }

  .wiz-done-examples code {
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.76rem;
    background: rgba(255, 255, 255, 0.06);
    padding: 0 0.25rem;
    border-radius: 3px;
  }

  .wiz-toggle-confirm {
    border: 1px dashed rgba(251, 191, 36, 0.4);
    border-radius: 6px;
    padding: 0.4rem 0.5rem;
    background: rgba(251, 191, 36, 0.05);
  }

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

  .wiz-check-running {
    color: #9bb3ff;
    font-size: 0.74rem;
    margin-left: 0.4rem;
  }

  .wiz-check-failed {
    color: var(--hub-error-fg);
    font-size: 0.74rem;
    margin-left: 0.4rem;
  }

  .wiz-checklist li.running {
    border-color: rgba(92, 124, 250, 0.45);
    background: rgba(92, 124, 250, 0.08);
  }

  .wiz-checklist li.failed {
    border-color: rgba(248, 113, 113, 0.45);
    background: rgba(248, 113, 113, 0.08);
    color: var(--hub-error-fg);
  }

  .wiz-summary-small {
    display: block;
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    margin-top: 0.15rem;
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
