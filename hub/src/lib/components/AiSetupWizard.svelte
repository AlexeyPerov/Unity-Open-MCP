<script lang="ts">
  /**
   * M4 — AI Setup wizard (M28 Plan 1 modularized).
   *
   * This file is now the thin orchestrator. It owns the shared reactive form
   * state, the async planners/writers (manifest merge, MCP config, skill
   * copy/generate, launch + /ping), draft persistence, and step navigation.
   * All markup lives in step modules under `wizard/`; the shell (overlay,
   * header, progress strip, footer) lives in `wizard/WizardShell.svelte`.
   *
   * Plan 1 is a refactor only — no intentional behavior, copy, or gating
   * change. Step IDs (`step0`…`done`), draft keys, and preset field names are
   * preserved so persisted drafts and presets keep working.
   */
  import { onMount } from "svelte";
  import { S } from "$lib/state.svelte";
  import { projectsStore } from "$lib/state/projects.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import {
    bridgePortFromString,
    checkNodeVersion,
    clearAiSetup,
    copySkillFiles,
    detectProjectState,
    generateProjectSkill,
    launchForVerify,
    planManifestMerge,
    planMcpConfig,
    planSkillCopy,
    pollBridgePing,
    resolveBridgePort,
    validateToolkitRoot,
    writeManifestMerge,
    writeMcpConfig,
    type AiSetupWizardDraft,
  } from "$lib/services/config";
  import {
    collectAiSetupWizardDraft,
    hydrateAiSetupWizardDraft,
    isEmptyDraft,
    serializeDraftSnapshot,
    type AiSetupWizardDraftSnapshot,
    type AiSetupWizardFormState,
  } from "$lib/services/ai_setup_wizard_draft";
  import {
    applyPresetToForm,
    presetById,
    type PresetId,
  } from "$lib/services/wizard_presets";
  import {
    BRIDGE_PACKAGE_ID,
    VERIFY_PACKAGE_ID,
    describeManifestError,
    formatDiffPreview,
    summarizeChanges,
  } from "$lib/services/manifest";
  import { installableEmbeddedDomains } from "$lib/services/extensions";
  import type {
    BridgePingResult,
    BridgeStatusKind,
    ClearAiSetupResult,
    GenerateSkillError,
    GenerateSkillParamsWire,
    GenerateSkillResultWire,
    LaunchForVerifyError,
    ManifestError,
    ManifestMergePlan,
    ManifestWriteResult,
    McpConfigError,
    McpConfigParamsWire,
    McpConfigPlan,
    McpConfigWriteResult,
    NodeProbe,
    ProjectState,
    SkillCopyError,
    SkillCopyParamsWire,
    SkillCopyPlan,
    SkillCopyResult,
    ToolkitValidation,
  } from "$lib/services/config";
  import type { McpClientId } from "$lib/services/ai_toolkit";
  import type { McpLaunchModeWire } from "$lib/services/config";
  import { open as openDialog } from "@tauri-apps/plugin-dialog";
  import { openPath, revealItemInDir } from "@tauri-apps/plugin-opener";

  import "./wizard/wizard.css";
  import WizardShell, {
    type ProgressSegment,
  } from "./wizard/WizardShell.svelte";
  import WizardFooter from "./wizard/WizardFooter.svelte";
  import WizardStep0Preset from "./wizard/WizardStep0Preset.svelte";
  import WizardStep1Detection from "./wizard/WizardStep1Detection.svelte";
  import WizardStep2McpSource from "./wizard/WizardStep2McpSource.svelte";
  import WizardStep3Packages from "./wizard/WizardStep3Packages.svelte";
  import WizardStep4McpClient from "./wizard/WizardStep4McpClient.svelte";
  import WizardStep4bSkill from "./wizard/WizardStep4bSkill.svelte";
  import WizardStep5Launch from "./wizard/WizardStep5Launch.svelte";
  import WizardStepDone from "./wizard/WizardStepDone.svelte";
  import {
    STEP_ORDER,
    stepIndex,
    stepLabel,
    clientToWire,
    clientKind,
    mcpClientLabel,
    type StepId,
  } from "./wizard/constants.ts";
  import { effectiveLaunchMode } from "./wizard/launch_mode.ts";
  import {
    diagnosticsRows,
    mcpConfiguredSummary,
  } from "./wizard/diagnostics.ts";
  import {
    isManifestReady,
    isMcpSourceReady,
    isProjectReady,
    stepPassing,
  } from "./wizard/readiness.ts";
  import {
    describeGenerateSkillError,
    describeLaunchForVerifyError,
    describeMcpConfigError,
    describeSkillCopyError,
    toGenerateSkillError,
    toMcpConfigError,
    toSkillCopyError,
  } from "./wizard/error_descriptors.ts";
  import type { DiagRow, Step5ItemId, Step5ItemState } from "./wizard/state.ts";

  interface Props {
    project: {
      id: string;
      name: string;
      path: string;
      unityVersion: string | null | undefined;
      aiSetupWizard?: AiSetupWizardDraft;
    };
    onClose: () => void;
  }

  let { project, onClose }: Props = $props();

  // Stable for the wizard session. The parent re-renders and may pass a
  // fresh `project` object when the projects store updates; reading
  // `wizardProjectPath` / `wizardProjectName` inside `$effect` or template would
  // restart every async planner and destabilize event bindings on each
  // save. Capture the session-stable fields once.
  // svelte-ignore state_referenced_locally
  const wizardProjectId = project.id;
  // svelte-ignore state_referenced_locally
  const wizardProjectPath = project.path;
  // svelte-ignore state_referenced_locally
  const wizardProjectName = project.name;

  let wizardClosed = $state(false);
  let detectionGeneration = 0;

  let currentStep = $state<StepId>("step0");

  // Step 0 — preset picker.
  let selectedPresetId = $state<string>("");

  // Step 1 — detection snapshot.
  let detection = $state<ProjectState | null>(null);
  let detectionLoading = $state(false);
  let detectionError = $state<string | null>(null);
  let detectToast = $state<string | null>(null);
  let detectToastTimer = $state<ReturnType<typeof setTimeout> | null>(null);

  // Step 2 — environment state.
  let toolkitRoot = $state("");
  let toolkitRootDirty = $state(false);
  let mcpIndexOverride = $state("");
  let toolkitValidation = $state<ToolkitValidation | null>(null);
  let toolkitValidating = $state(false);
  let toolkitError = $state<string | null>(null);
  let nodeProbe = $state<NodeProbe | null>(null);
  let nodeProbing = $state(false);
  let pickToolkitInFlight = $state(false);
  let useLocalCheckout = $state(false);
  let useGlobalInstall = $state(false);

  // Step 3 — packages state.
  let installBridge = $state(true);
  let installVerify = $state(true);
  let installScanner = $state(false);
  let packageVersionPin = $state("");
  let packageCustomUrl = $state("");
  let useLocalPackages = $state(false);
  let useLocalPackagesTouched = $state(false);
  let mergePlan = $state<ManifestMergePlan | null>(null);
  let mergePlanning = $state(false);
  let mergeWriting = $state(false);
  let mergeResult = $state<ManifestWriteResult | null>(null);
  let mergeError = $state<string | null>(null);
  let showDiff = $state(false);
  let upgradeAcknowledged = $state(false);

  let selectedUnityDomainDeps = $state<Set<string>>(new Set());

  function selectedUnityDepInstalls(): { id: string; version: string }[] {
    const installable = new Map(
      installableEmbeddedDomains().map((d) => [d.upmDependency, d]),
    );
    return [...selectedUnityDomainDeps]
      .map((id) => installable.get(id))
      .filter((d): d is NonNullable<typeof d> => Boolean(d))
      .map((d) => ({ id: d.upmDependency, version: d.defaultVersion }));
  }

  // Step 4 — MCP client state.
  let mcpClient = $state<McpClientId>("cursor");
  let cursorProjectScope = $state(true);
  let bridgePort = $state("");
  let resolvedBridgePort = $state<number | null>(null);
  let copyToast = $state<string | null>(null);
  let mcpClientSearch = $state("");
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

  let skillGenResult = $state<GenerateSkillResultWire | null>(null);
  let skillGenRunning = $state(false);
  let skillGenError = $state<GenerateSkillError | null>(null);
  let skillGenPreviewOpen = $state(false);

  // Clear AI Setup.
  let clearInProgress = $state(false);
  let clearResult = $state<ClearAiSetupResult | null>(null);
  let clearError = $state<string | null>(null);

  // Step 5 — launch/verify state.
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
  const DRAFT_PERSIST_DEBOUNCE_MS = 400;

  let draftPersistReady = $state(false);
  let lastPersistedDraftJson = "";
  let draftPersistTimer: ReturnType<typeof setTimeout> | null = null;

  function draftSnapshot(): AiSetupWizardDraftSnapshot {
    return {
      useLocalCheckout,
      useGlobalInstall,
      toolkitRoot,
      mcpIndexOverride,
      installBridge,
      installVerify,
      packageVersionPin,
      packageCustomUrl,
      useLocalPackages,
      selectedUnityDomainDeps,
      upgradeAcknowledged,
      showDiff,
      mcpClient,
      cursorProjectScope,
      bridgePort,
      skillOverwriteAck,
      selectedPresetId,
    };
  }

  function applyHydratedForm(
    state: ReturnType<typeof hydrateAiSetupWizardDraft>,
  ) {
    useLocalCheckout = state.useLocalCheckout;
    useGlobalInstall = state.useGlobalInstall;
    toolkitRoot = state.toolkitRoot;
    mcpIndexOverride = state.mcpIndexOverride;
    installBridge = state.installBridge;
    installVerify = state.installVerify;
    packageVersionPin = state.packageVersionPin;
    packageCustomUrl = state.packageCustomUrl;
    useLocalPackages = state.useLocalPackages;
    selectedUnityDomainDeps = new Set(state.selectedUnityDomainDeps);
    upgradeAcknowledged = state.upgradeAcknowledged;
    showDiff = state.showDiff;
    mcpClient = state.mcpClient;
    cursorProjectScope = state.cursorProjectScope;
    bridgePort = state.bridgePort;
    skillOverwriteAck = state.skillOverwriteAck;
    selectedPresetId = state.selectedPresetId;
  }

  function hydrateFromProject() {
    const entry = projectsStore.find(wizardProjectId);
    const hydrated = hydrateAiSetupWizardDraft(
      entry ?? project,
      settingsStore.aiToolkit,
    );
    applyHydratedForm(hydrated);
    toolkitRootDirty = false;
    lastPersistedDraftJson = serializeDraftSnapshot(draftSnapshot());
  }

  async function persistAiSetupWizardDraft() {
    if (!draftPersistReady || wizardClosed) return;
    const snapshot = draftSnapshot();
    const serialized = serializeDraftSnapshot(snapshot);
    if (serialized === lastPersistedDraftJson) return;

    const draft = collectAiSetupWizardDraft(snapshot);
    const stored = projectsStore.find(wizardProjectId);
    if (!stored) return;

    const next = isEmptyDraft(draft) ? undefined : draft;
    try {
      await projectsStore.updateDraftOnly(wizardProjectId, next);
      lastPersistedDraftJson = serialized;
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save AI Setup wizard draft failed: ${msg}`);
    }
  }

  function scheduleDraftPersist() {
    if (!draftPersistReady || wizardClosed) return;
    if (draftPersistTimer) clearTimeout(draftPersistTimer);
    draftPersistTimer = setTimeout(() => {
      draftPersistTimer = null;
      void persistAiSetupWizardDraft();
    }, DRAFT_PERSIST_DEBOUNCE_MS);
  }

  function flushDraftPersist() {
    if (draftPersistTimer) {
      clearTimeout(draftPersistTimer);
      draftPersistTimer = null;
    }
    void persistAiSetupWizardDraft();
  }

  async function clearPersistedDraft() {
    const stored = projectsStore.find(wizardProjectId);
    if (!stored) return;
    if (stored.aiSetupWizard) {
      try {
        await projectsStore.updateDraftOnly(wizardProjectId, undefined);
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`clear AI Setup wizard draft failed: ${msg}`);
        return;
      }
    }
    hydrateFromProject();
  }

  onMount(() => {
    hydrateFromProject();
    draftPersistReady = true;
    return () => {
      if (!wizardClosed) flushDraftPersist();
    };
  });

  $effect(() => {
    if (!draftPersistReady || wizardClosed) return;
    const serialized = serializeDraftSnapshot(draftSnapshot());
    if (serialized === lastPersistedDraftJson) return;
    scheduleDraftPersist();
  });

  function canGoBack(): boolean {
    return stepIndex(currentStep) > 0;
  }

  function canGoNext(): boolean {
    switch (currentStep) {
      case "step0":
        return true;
      case "step1":
        return isProjectReady({ detection, nodeProbe });
      case "step2":
        return isMcpSourceReady({ useLocalCheckout, toolkitValidation });
      case "step3":
        return isManifestReady({
          installBridge,
          installVerify,
          selectedUnityDomainDeps,
          mergePlan,
        });
      case "step4":
        return true;
      case "step4b":
        return true;
      case "step5":
        return true;
      case "done":
        return false;
    }
  }

  function onUseLocalCheckoutChange(checked: boolean) {
    useLocalCheckout = checked;
    if (!checked) {
      toolkitError = null;
    }
  }

  function canSkipStep3(): boolean {
    if (mergeResult) return true;
    return isManifestReady({
      installBridge,
      installVerify,
      selectedUnityDomainDeps,
      mergePlan,
    });
  }

  function shouldSkipSkillStep(): boolean {
    return (
      selectedPresetId === "team-ci" &&
      presetById(selectedPresetId).values.skillEnabled === false
    );
  }

  function nextStep() {
    if (!canGoNext()) return;
    const i = stepIndex(currentStep);
    if (i >= STEP_ORDER.length - 1) return;
    let next = STEP_ORDER[i + 1];
    if (next === "step4b" && shouldSkipSkillStep()) {
      S.appendDrawerLog(
        `AI Setup: auto-skipped Agent skill step for ${wizardProjectName} (Team CI preset)`,
      );
      const after = STEP_ORDER.indexOf("step5");
      if (after > i) next = STEP_ORDER[after];
    }
    currentStep = next;
  }

  function jumpToStep(id: StepId) {
    currentStep = id;
  }

  function selectPreset(id: PresetId) {
    selectedPresetId = id;
    const preset = presetById(id);
    const patch = applyPresetToForm(preset) as Partial<AiSetupWizardFormState>;
    if (patch.useLocalCheckout !== undefined) {
      onUseLocalCheckoutChange(patch.useLocalCheckout);
    }
    if (patch.useGlobalInstall !== undefined) useGlobalInstall = patch.useGlobalInstall;
    if (patch.useLocalPackages !== undefined) {
      useLocalPackages = patch.useLocalPackages;
      useLocalPackagesTouched = true;
    }
    if (patch.installBridge !== undefined) installBridge = patch.installBridge;
    if (patch.installVerify !== undefined) installVerify = patch.installVerify;
    if (patch.selectedUnityDomainDeps !== undefined) {
      selectedUnityDomainDeps = new Set(patch.selectedUnityDomainDeps);
    }
    if (patch.mcpClient !== undefined) mcpClient = patch.mcpClient;
    S.appendDrawerLog(
      `AI Setup: applied "${preset.label}" preset for ${wizardProjectName}`,
    );
  }

  function backStep() {
    if (!canGoBack()) return;
    const i = stepIndex(currentStep);
    if (i > 0) {
      currentStep = STEP_ORDER[i - 1];
    }
  }

  function closeWizard() {
    if (wizardClosed) return;
    wizardClosed = true;
    detectionGeneration += 1;
    detectionLoading = false;
    if (draftPersistTimer) {
      clearTimeout(draftPersistTimer);
      draftPersistTimer = null;
    }
    void persistAiSetupWizardDraft();
    onClose();
  }

  function cancelWizard() {
    closeWizard();
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
    try {
      await settingsStore.setAiToolkitUseLocalCheckout(useLocalCheckout);
      if (useLocalCheckout && toolkitValidation?.ok) {
        await settingsStore.setAiToolkitRoot(toolkitRoot);
        if (mcpIndexOverride.trim().length > 0) {
          await settingsStore.setAiToolkitMcpIndexOverride(mcpIndexOverride);
        }
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      S.appendErrorLog(`save toolkit root failed: ${msg}`);
    }
  }

  async function refreshDetection() {
    if (wizardClosed) return;
    const gen = ++detectionGeneration;
    detectionLoading = true;
    detectionError = null;
    try {
      const next = await detectProjectState(wizardProjectPath);
      if (wizardClosed || gen !== detectionGeneration) return;
      detection = next;
      detectToast = "Project state refreshed";
      if (detectToastTimer) clearTimeout(detectToastTimer);
      detectToastTimer = setTimeout(() => {
        detectToast = null;
        detectToastTimer = null;
      }, 2200);
    } catch (e) {
      if (wizardClosed || gen !== detectionGeneration) return;
      const msg = e instanceof Error ? e.message : String(e);
      detectionError = `detection failed: ${msg}`;
      detection = null;
      S.appendErrorLog(detectionError);
    } finally {
      if (!wizardClosed && gen === detectionGeneration) {
        detectionLoading = false;
      }
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

  $effect(() => {
    if (currentStep === "step1") {
      if (nodeProbe === null) void runNodeProbe();
      if (toolkitRoot.trim() && toolkitValidation === null && !toolkitValidating) {
        void runToolkitValidation();
      }
    }
  });

  $effect(() => {
    if (currentStep !== "step1" && currentStep !== "done") return;
    void refreshDetection();
    return () => {
      detectionGeneration += 1;
      detectionLoading = false;
    };
  });

  function manifestHasLocalEntries(deps: Record<string, string> | undefined): boolean {
    if (!deps) return false;
    const selectedIds: string[] = [];
    if (installBridge) selectedIds.push(BRIDGE_PACKAGE_ID);
    if (installVerify) selectedIds.push(VERIFY_PACKAGE_ID);
    return selectedIds.some((id) => deps[id]?.trim().startsWith("file:"));
  }

  function onUseLocalPackagesChange(checked: boolean) {
    useLocalPackagesTouched = true;
    useLocalPackages = checked;
  }

  $effect(() => {
    if (currentStep !== "step3") return;
    if (useLocalPackagesTouched) return;
    if (!mergePlan?.manifestRead.dependencies) return;
    if (manifestHasLocalEntries(mergePlan.manifestRead.dependencies)) {
      useLocalPackages = true;
    }
  });

  $effect(() => {
    if (currentStep !== "step3") return;
    if (!toolkitRoot.trim()) return;
    const deps = selectedUnityDepInstalls();
    if (!installBridge && !installVerify && deps.length === 0) {
      mergePlan = null;
      return;
    }
    const localMode = useLocalPackages;
    mergePlanning = true;
    let cancelled = false;
    void (async () => {
      try {
        const plan = await planManifestMerge({
          projectPath: wizardProjectPath,
          toolkitRoot,
          installBridge,
          installVerify,
          versionPin: packageVersionPin,
          customUrl: packageCustomUrl,
          confirmUpgrades: false,
          useLocalPackages: localMode,
          unityDomainDeps: deps,
        });
        if (!cancelled) {
          mergePlan = plan;
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

  $effect(() => {
    if (
      currentStep !== "step4" &&
      currentStep !== "step5" &&
      currentStep !== "done"
    )
      return;
    const projectPath = wizardProjectPath;
    const override = bridgePortFromString(bridgePort);
    if (!projectPath) {
      resolvedBridgePort = null;
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        const port = await resolveBridgePort(projectPath, override);
        if (!cancelled) resolvedBridgePort = port;
      } catch {
        if (!cancelled) resolvedBridgePort = null;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  $effect(() => {
    if (
      currentStep !== "step4" &&
      currentStep !== "step5" &&
      currentStep !== "done"
    )
      return;
    const projectPath = wizardProjectPath;
    const root = toolkitRoot;
    const client = mcpClient;
    const projectScope = cursorProjectScope;
    const port = bridgePort;
    const override = mcpIndexOverride;
    const localCheckout = useLocalCheckout;
    const globalInstall = useGlobalInstall;
    const mode: McpLaunchModeWire = effectiveLaunchMode({
      mcpIndexOverride: override,
      useLocalCheckout: localCheckout,
      useGlobalInstall: globalInstall,
    });
    if (!projectPath || ((mode === "local" || mode === "localOverride") && !root)) {
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
          launchMode: mode,
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

  $effect(() => {
    if (currentStep !== "step4b" && currentStep !== "done") return;
    const root = toolkitRoot;
    const projectPath = wizardProjectPath;
    const client = mcpClient;
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
          mcpClient: clientToWire(client),
        };
        const plan = await planSkillCopy(params);
        if (!cancelled) {
          skillPlan = plan;
          if (!plan.targets.some((t) => t.exists)) {
            skillOverwriteAck = false;
          }
          skillResult = null;
          skillError = null;
          skillGenResult = null;
          skillGenError = null;
          skillGenPreviewOpen = false;
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

  let mcpPreviewText = $derived.by(() => {
    if (!mcpPlan) return "";
    return mcpPlan.command ?? mcpPlan.proposedJson ?? "";
  });

  function canWriteMcpConfig(): boolean {
    if (clientKind(mcpClient) !== "file") return false;
    const mode = effectiveLaunchMode({
      mcpIndexOverride,
      useLocalCheckout,
      useGlobalInstall,
    });
    if (mode === "local" || mode === "localOverride") {
      if (resolvedMcpPathValid !== true) return false;
      if (!toolkitValidation?.ok) return false;
    }
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
    const projectPath = wizardProjectPath;
    const mode = effectiveLaunchMode({
      mcpIndexOverride,
      useLocalCheckout,
      useGlobalInstall,
    });
    if (!projectPath) return;
    if ((mode === "local" || mode === "localOverride") && !root) return;
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
        launchMode: mode,
      };
      const result = await writeMcpConfig(params);
      mcpWriteResult = result;
      copyToast = result.wouldWrite
        ? `Wrote MCP config to ${result.targetPath}`
        : "MCP config already up to date — no write needed";
      S.appendDrawerLog(
        result.wouldWrite
          ? `AI Setup: wrote MCP config to ${result.targetPath} for ${wizardProjectName}`
          : `AI Setup: MCP config already up to date for ${wizardProjectName}`
      );
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

  function skillPlanHasDiffering(): boolean {
    return skillPlan?.targets.some((t) => t.exists && !t.upToDate) ?? false;
  }

  async function copySkillFilesClick() {
    if (skillCopying) return;
    const root = toolkitRoot;
    const projectPath = wizardProjectPath;
    if (!projectPath || !root || !skillPlan) return;
    const needsOverwrite = skillPlanHasDiffering();
    if (needsOverwrite && !skillOverwriteAck) {
      skillError = {
        kind: "overwriteNotConfirmed",
        message:
          "One or more target files already exist and differ from the source. Check the overwrite box to replace them.",
      };
      return;
    }
    skillCopying = true;
    skillError = null;
    try {
      const params: SkillCopyParamsWire = {
        projectPath,
        toolkitRoot: root,
        mcpClient: clientToWire(mcpClient),
      };
      const result = await copySkillFiles(params, skillOverwriteAck);
      skillResult = result;
      S.appendDrawerLog(
        `AI Setup: copied ${result.copied.length} skill file(s), skipped ${result.skipped.length} for ${wizardProjectName}`
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

  function canGenerateSkill(): boolean {
    if (!skillPlan || skillPlan.targets.length === 0) return false;
    if (!toolkitRoot) return false;
    if (toolkitValidation && !toolkitValidation.ok) return false;
    return true;
  }

  async function generateProjectSkillClick() {
    if (skillGenRunning) return;
    const root = toolkitRoot;
    const projectPath = wizardProjectPath;
    if (!projectPath || !root || !skillPlan) return;
    const hasExisting = skillPlan.targets.some((t) => t.exists);
    if (hasExisting && !skillOverwriteAck) {
      skillGenError = {
        kind: "overwriteNotConfirmed",
        message:
          "One or more target files already exist. Check the overwrite box to replace them.",
      };
      return;
    }
    skillGenRunning = true;
    skillGenError = null;
    try {
      const params: GenerateSkillParamsWire = {
        projectPath,
        toolkitRoot: root,
        mcpIndexOverride,
        mcpClient: clientToWire(mcpClient),
      };
      const result = await generateProjectSkill(params);
      skillGenResult = result;
      S.appendDrawerLog(
        `AI Setup: generated project skill for ${wizardProjectName} — ${result.targets.length} target(s), Unity ${result.unityVersion}`
      );
    } catch (e) {
      skillGenError = toGenerateSkillError(e);
      S.appendErrorLog(
        `Skill generation failed: ${describeGenerateSkillError(skillGenError)}`
      );
    } finally {
      skillGenRunning = false;
    }
  }

  async function installManifest() {
    if (!mergePlan || mergeWriting) return;
    if (mergePlan.hasUpgrades && !upgradeAcknowledged) return;
    mergeWriting = true;
    mergeError = null;
    try {
      const result = await writeManifestMerge({
        projectPath: wizardProjectPath,
        toolkitRoot,
        installBridge,
        installVerify,
        versionPin: packageVersionPin,
        customUrl: packageCustomUrl,
        confirmUpgrades: mergePlan.hasUpgrades,
        useLocalPackages,
        unityDomainDeps: selectedUnityDepInstalls(),
      });
      mergeResult = result;
      S.appendDrawerLog(
        `AI Setup: ${summarizeChanges(result.changes)} in ${wizardProjectName}`
      );
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
    const overridePort = bridgePortFromString(String(bridgePort));
    step5BridgePort = resolvedBridgePort ?? overridePort;
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
        projectId: wizardProjectId,
        bridgePort: overridePort ?? 0,
        theme: (settingsStore.current?.theme as "dark" | "light" | "system" | undefined) ?? "system",
      });
      step5BridgePort = result.bridgePort;
      step5LaunchPid = result.pid;
      launched = true;
      step5Items = {
        ...step5Items,
        launch: "ok",
        compile: "running",
      };
      S.appendDrawerLog(
        `AI Setup: launched Unity (pid ${result.pid}) with bridge port ${result.bridgePort} for ${wizardProjectName}`,
      );
      await pollBridgeUntilReady(result.bridgePort);
    } catch (e) {
      const message = describeLaunchForVerifyError(e);
      step5Error = message;
      S.appendErrorLog(`AI Setup: Step 5 launch failed: ${message}`);
      if (launched) {
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
        } else if (result.errorKind === "connectionRefused") {
          step5Items = { ...step5Items, compile: "running" };
        }
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        S.appendErrorLog(`AI Setup: bridge /ping threw: ${msg}`);
      }
      await new Promise((resolve) =>
        setTimeout(resolve, STEP5_POLL_INTERVAL_MS),
      );
    }
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
      `AI Setup: user stopped Step 5 verification for ${wizardProjectName}`,
    );
  }

  function skipStep5Verify() {
    if (step5Running) return;
    resetStep5State();
    step5BridgeStatus = { kind: "notChecked" };
    S.appendDrawerLog(
      `AI Setup: skipped Step 5 verify for ${wizardProjectName} — wizard marked incomplete on Done`,
    );
  }

  function skipSkillStep() {
    S.appendDrawerLog(
      `AI Setup: skipped Agent skill copy for ${wizardProjectName} (${mcpClientLabel(mcpClient)})`,
    );
    nextStep();
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

  async function openProjectFolder() {
    if (!wizardProjectPath) return;
    void openPath(wizardProjectPath);
  }

  async function revealProjectFolder() {
    if (!wizardProjectPath) return;
    void revealItemInDir(wizardProjectPath);
  }

  function reRunWizard() {
    resetStep5State();
    mcpWriteResult = null;
    mcpWriteError = null;
    mcpPlan = null;
    mergeResult = null;
    mergeError = null;
    upgradeAcknowledged = false;
    useLocalPackages = false;
    useLocalPackagesTouched = false;
    selectedUnityDomainDeps = new Set();
    skillResult = null;
    skillError = null;
    skillOverwriteAck = false;
    skillGenResult = null;
    skillGenError = null;
    skillGenPreviewOpen = false;
    selectedPresetId = "";
    step5BridgeStatus = { kind: "notChecked" };
    currentStep = "step0";
    void clearPersistedDraft();
    void refreshDetection();
  }

  async function onClearAiSetup() {
    if (clearInProgress) return;
    const ok = await S.confirm(
      "Clear AI Setup?",
      "This removes the Unity AI agent for this project: the bridge + verify entries from Packages/manifest.json, the unity-open-mcp entry from every known MCP client config (global files only the entries pointing at this project), and the copied agent-skill SKILL.md files. A .bak backup is left next to each changed file. This cannot be undone.",
    );
    if (!ok) return;
    clearInProgress = true;
    clearError = null;
    clearResult = null;
    try {
      const result = await clearAiSetup(wizardProjectPath);
      clearResult = result;
      mcpWriteResult = null;
      mcpWriteError = null;
      mcpPlan = null;
      skillResult = null;
      skillError = null;
      skillGenResult = null;
      skillGenError = null;
      skillGenPreviewOpen = false;
      resetStep5State();
      void refreshDetection();
      void clearPersistedDraft();
      const clearedConfigs = result.clientConfigsCleared.filter((c) => c.removed).length;
      const summary =
        result.errors.length > 0
          ? `Cleared ${clearedConfigs} config(s), ${result.skillsRemoved.length} skill(s); ${result.errors.length} error(s).`
          : `Cleared ${clearedConfigs} config(s), ${result.skillsRemoved.length} skill(s).`;
      detectToast = summary;
      if (detectToastTimer) clearTimeout(detectToastTimer);
      detectToastTimer = setTimeout(() => {
        detectToast = null;
        detectToastTimer = null;
      }, 3500);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      clearError = `Clear AI Setup failed: ${msg}`;
      S.appendErrorLog(clearError);
    } finally {
      clearInProgress = false;
    }
  }

  let canOpenInCursor = $derived(
    mcpClient === "cursor" && Boolean(wizardProjectPath),
  );
  let canOpenInOpencode = $derived(
    (mcpClient === "opencode-global" || mcpClient === "opencode-project") &&
      Boolean(wizardProjectPath),
  );

  // --- Derived view-model for the shell + step modules ----------------

  let progress = $derived.by((): ProgressSegment[] => {
    const passingInput = {
      detection,
      nodeProbe,
      useLocalCheckout,
      toolkitValidation,
      installBridge,
      installVerify,
      selectedUnityDomainDeps,
      mergePlan,
      step5BridgeStatus,
    };
    return STEP_ORDER.map((id, idx) => {
      const currentIdx = stepIndex(currentStep);
      const state: "done" | "current" | "pending" =
        idx < currentIdx ? "done" : idx === currentIdx ? "current" : "pending";
      return {
        id,
        idx,
        label: stepLabel(id),
        state,
        passing: stepPassing(id, passingInput),
      };
    });
  });

  let diagnostics = $derived.by((): DiagRow[] =>
    diagnosticsRows({
      detection,
      nodeProbe,
      nodeProbing,
      step5BridgeStatus,
      step5LaunchPid,
    }),
  );

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

  let showLocalPackagesInfo = $derived.by(() => {
    if (!mergePlan) return false;
    return (
      mergePlan.useLocalPackages ||
      mergePlan.manifestUsesLocalPackages ||
      manifestHasLocalEntries(mergePlan.manifestRead.dependencies)
    );
  });

  let localPackagesInfoText = $derived.by(() => {
    if (!mergePlan) return "";
    const paths = mergePlan.changes
      .filter((c) => c.after.startsWith("file:"))
      .map((c) => c.after);
    if (paths.length > 0) {
      return `Local package paths are in use (${paths.join(", ")}).`;
    }
    const existing = mergePlan.changes
      .map((c) => c.before)
      .filter((v): v is string => !!v && v.startsWith("file:"));
    if (existing.length > 0) {
      return `Local package paths are in use (${existing.join(", ")}).`;
    }
    return "Local package paths are in use.";
  });

  let manifestReady = $derived(
    isManifestReady({
      installBridge,
      installVerify,
      selectedUnityDomainDeps,
      mergePlan,
    }),
  );

  // The state bag passed (read-only) to every step module.
  let wizardState = $derived({
    projectId: wizardProjectId,
    projectPath: wizardProjectPath,
    projectName: wizardProjectName,
    selectedPresetId,
    detection,
    detectionLoading,
    detectionError,
    detectToast,
    nodeProbe,
    nodeProbing,
    diagnostics,
    useLocalCheckout,
    useGlobalInstall,
    toolkitRoot,
    toolkitRootDirty,
    mcpIndexOverride,
    toolkitValidation,
    toolkitValidating,
    toolkitError,
    pickToolkitInFlight,
    nodeMajor: nodeProbe?.major ?? null,
    installBridge,
    installVerify,
    installScanner,
    packageVersionPin,
    packageCustomUrl,
    useLocalPackages,
    mergePlan,
    mergePlanning,
    mergeWriting,
    mergeResult,
    mergeError,
    showDiff,
    upgradeAcknowledged,
    selectedUnityDomainDeps,
    manifestHasLocalEntries:
      !!mergePlan && manifestHasLocalEntries(mergePlan.manifestRead.dependencies),
    showLocalPackagesInfo,
    localPackagesInfoText,
    diffPreviewText,
    hasRealChanges,
    manifestParseError,
    manifestReady,
    canSkipStep3: canSkipStep3(),
    mcpClient,
    cursorProjectScope,
    bridgePort,
    resolvedBridgePort,
    resolvedMcpPath,
    resolvedMcpPathValid,
    copyToast,
    mcpClientSearch,
    mcpPlan,
    mcpPlanning,
    mcpWriteResult,
    mcpWriting,
    mcpWriteError,
    mcpPreviewText,
    canWriteMcpConfig: canWriteMcpConfig(),
    primaryActionLabel: primaryActionLabel(),
    secondaryActionLabel: secondaryActionLabel(),
    skillPlan,
    skillPlanning,
    skillResult,
    skillCopying,
    skillError,
    skillOverwriteAck,
    skillGenResult,
    skillGenRunning,
    skillGenError,
    skillGenPreviewOpen,
    canGenerateSkill: canGenerateSkill(),
    skillPlanHasDiffering: skillPlanHasDiffering(),
    step5Running,
    step5Items,
    step5LaunchPid,
    step5BridgePort,
    step5BridgeStatus,
    step5PingResult,
    step5Error,
    step5LastTick,
    clearInProgress,
    clearResult,
    clearError,
    doneOpenInCursor: canOpenInCursor,
    doneOpenInOpencode: canOpenInOpencode,
  });

  // Handler bag passed to every step module. Stable closures over the
  // reactive state above — re-created on each render is fine because the
  // steps only invoke them on user interaction.
  const handlers = {
    selectPreset: (id: PresetId) => selectPreset(id),
    refreshDetection: () => void refreshDetection(),
    runNodeProbe: () => void runNodeProbe(),
    onUseLocalCheckoutChange,
    onToolkitRootInput,
    pickToolkitFolder: () => void pickToolkitFolder(),
    runToolkitValidation: () => void runToolkitValidation(),
    setMcpIndexOverride: (v: string) => (mcpIndexOverride = v),
    setUseGlobalInstall: (v: boolean) => (useGlobalInstall = v),
    setInstallBridge: (v: boolean) => (installBridge = v),
    setInstallVerify: (v: boolean) => (installVerify = v),
    onUseLocalPackagesChange,
    setPackageVersionPin: (v: string) => (packageVersionPin = v),
    setPackageCustomUrl: (v: string) => (packageCustomUrl = v),
    toggleUnityDomainDep: (upmId: string, checked: boolean) => {
      const next = new Set(selectedUnityDomainDeps);
      if (checked) next.add(upmId);
      else next.delete(upmId);
      selectedUnityDomainDeps = next;
    },
    setUpgradeAcknowledged: (v: boolean) => (upgradeAcknowledged = v),
    setShowDiff: (v: boolean) => (showDiff = v),
    installManifest: () => void installManifest(),
    skipToMcpClient: () => (currentStep = "step4"),
    setMcpClient: (v: McpClientId) => (mcpClient = v),
    setCursorProjectScope: (v: boolean) => (cursorProjectScope = v),
    setBridgePort: (v: string) => (bridgePort = v),
    setMcpClientSearch: (v: string) => (mcpClientSearch = v),
    primaryMcpAction: () => void primaryMcpAction(),
    copyMcpJson: () => void copyMcpJson(),
    copySkillFiles: () => void copySkillFilesClick(),
    generateProjectSkill: () => void generateProjectSkillClick(),
    toggleSkillOverwriteAck: (v: boolean) => (skillOverwriteAck = v),
    toggleSkillGenPreview: () => (skillGenPreviewOpen = !skillGenPreviewOpen),
    skipSkillStep,
    runStep5Verify: () => void runStep5Verify(),
    stopStep5Polling,
    skipStep5Verify,
    openProjectFolder: () => void openProjectFolder(),
    revealProjectFolder: () => void revealProjectFolder(),
    openMcpConfigTarget: () => void openMcpConfigTarget(),
    openToolkitSkill: () => void openToolkitSkill(),
    openCopiedSkill: () => void openCopiedSkill(),
    reRunWizard,
    closeWizard,
    onClearAiSetup: () => void onClearAiSetup(),
  };
</script>

<svelte:window onkeydown={onKeydown} />

<WizardShell
  title={stepLabel(currentStep)}
  subtitle={`${wizardProjectName} · ${wizardProjectPath}`}
  subtitleTitle={wizardProjectPath}
  {progress}
  onOverlayClick={onOverlayClick}
  onClose={closeWizard}
  onJumpTo={jumpToStep}
>
  {#snippet body()}
    {#if clearResult}
      <div class="wiz-block wiz-block-ok" role="status">
        <strong>AI setup cleared.</strong>
        Removed {clearResult.clientConfigsCleared.filter((c) => c.removed).length}
        MCP config entr{clearResult.clientConfigsCleared.filter((c) => c.removed).length === 1 ? "y" : "ies"}
        and {clearResult.skillsRemoved.length} skill file(s)
        {#if clearResult.manifestCleared}
          · bridge + verify removed from <code>Packages/manifest.json</code>
          {#if clearResult.manifestBackupPath}
            (backup: <code>{clearResult.manifestBackupPath}</code>)
          {/if}
        {/if}.
        {#if clearResult.errors.length > 0}
          <br /><small>{clearResult.errors.length} non-fatal error(s) — see the error log.</small>
        {/if}
      </div>
    {/if}
    {#if clearError}
      <div class="wiz-block wiz-block-error" role="alert">{clearError}</div>
    {/if}

    {#if currentStep === "step0"}
      <WizardStep0Preset state={wizardState} {handlers} />
    {:else if currentStep === "step1"}
      <WizardStep1Detection state={wizardState} {handlers} />
    {:else if currentStep === "step2"}
      <WizardStep2McpSource state={wizardState} {handlers} />
    {:else if currentStep === "step3"}
      <WizardStep3Packages state={wizardState} {handlers} bind:showDiff />
    {:else if currentStep === "step4"}
      <WizardStep4McpClient state={wizardState} {handlers} />
    {:else if currentStep === "step4b"}
      <WizardStep4bSkill state={wizardState} {handlers} />
    {:else if currentStep === "step5"}
      <WizardStep5Launch state={wizardState} {handlers} />
    {:else if currentStep === "done"}
      <WizardStepDone state={wizardState} {handlers} />
    {/if}
  {/snippet}

  {#snippet footer()}
    <WizardFooter
      progressLabel={`Step ${stepIndex(currentStep) + 1} of ${STEP_ORDER.length} · ${stepLabel(currentStep)}`}
      canGoBack={canGoBack()}
      canGoNext={canGoNext()}
      isDone={currentStep === "done"}
      nextLabel={nextLabel()}
      clearInProgress={clearInProgress}
      onBack={backStep}
      onNext={onFooterNext}
      onCancel={cancelWizard}
      onClear={() => void onClearAiSetup()}
    />
  {/snippet}
</WizardShell>
