/**
 * Shared reactive state + handler surface for the AI Setup wizard.
 *
 * The orchestrator (`AiSetupWizard.svelte`) owns every `$state` field and the
 * async planners/writers. Per-step modules receive this bag read-only (state)
 * plus the callbacks they need — there is no second store. Keeping the shape
 * in one typed interface lets Plan 2+ rewire step flow without hunting through
 * the orchestrator script.
 *
 * Re-exported service types are intentionally concrete (not `unknown`) so the
 * step modules get full IntelliSense + type-checking on the values they render.
 */
import type {
  BridgePingResult,
  BridgeStatusKind,
  ClearAiSetupResult,
  GenerateSkillError,
  GenerateSkillResultWire,
  LaunchForVerifyError,
  ManifestError,
  ManifestMergePlan,
  ManifestWriteResult,
  McpConfigError,
  McpConfigHeuristic,
  McpConfigPlan,
  McpConfigWriteResult,
  NodeProbe,
  ProjectState,
  SkillCopyError,
  SkillCopyPlan,
  SkillCopyResult,
  ToolkitValidation,
} from "$lib/services/config";
import type { McpClientId } from "$lib/services/ai_toolkit";
import type { DiagRow } from "./diagnostics.ts";

// Re-exported so step modules + the orchestrator can import it from one place.
export type { DiagRow };

/** Step 5 per-item state + ids. */
export type Step5ItemId = "launch" | "compile" | "ping" | "confirm";
export type Step5ItemState = "pending" | "running" | "ok" | "failed";

/** The full wizard state surface passed to every step module. Fields are the
 *  reactive values the orchestrator owns; steps read them but do not mutate
 *  them directly (mutations go through {@link WizardHandlers}). */
export interface WizardState {
  // --- session ---
  projectId: string;
  projectPath: string;
  projectName: string;

  // --- Step 0 — preset ---
  selectedPresetId: string;

  // --- Step 1 — detection ---
  detection: ProjectState | null;
  detectionLoading: boolean;
  detectionError: string | null;
  detectToast: string | null;
  nodeProbe: NodeProbe | null;
  nodeProbing: boolean;
  diagnostics: DiagRow[];

  // --- Step 2 — MCP server source ---
  useLocalCheckout: boolean;
  useGlobalInstall: boolean;
  toolkitRoot: string;
  toolkitRootDirty: boolean;
  mcpIndexOverride: string;
  toolkitValidation: ToolkitValidation | null;
  toolkitValidating: boolean;
  toolkitError: string | null;
  pickToolkitInFlight: boolean;
  nodeMajor: number | null;

  // --- Step 3 — packages ---
  installBridge: boolean;
  installVerify: boolean;
  installScanner: boolean;
  packageVersionPin: string;
  packageCustomUrl: string;
  useLocalPackages: boolean;
  mergePlan: ManifestMergePlan | null;
  mergePlanning: boolean;
  mergeWriting: boolean;
  mergeResult: ManifestWriteResult | null;
  mergeError: string | null;
  showDiff: boolean;
  upgradeAcknowledged: boolean;
  selectedUnityDomainDeps: Set<string>;
  manifestHasLocalEntries: boolean;
  showLocalPackagesInfo: boolean;
  localPackagesInfoText: string;
  diffPreviewText: string;
  hasRealChanges: boolean;
  manifestParseError: string | null;
  manifestReady: boolean;
  canSkipStep3: boolean;

  // --- Step 4 — MCP client ---
  mcpClient: McpClientId;
  cursorProjectScope: boolean;
  bridgePort: string;
  resolvedBridgePort: number | null;
  resolvedMcpPath: string | null;
  resolvedMcpPathValid: boolean | null;
  copyToast: string | null;
  mcpClientSearch: string;
  mcpPlan: McpConfigPlan | null;
  mcpPlanning: boolean;
  mcpWriteResult: McpConfigWriteResult | null;
  mcpWriting: boolean;
  mcpWriteError: McpConfigError | null;
  mcpPreviewText: string;
  canWriteMcpConfig: boolean;
  primaryActionLabel: string;
  secondaryActionLabel: string;

  // --- Step 4b / Done — skill ---
  skillPlan: SkillCopyPlan | null;
  skillPlanning: boolean;
  skillResult: SkillCopyResult | null;
  skillCopying: boolean;
  skillError: SkillCopyError | null;
  skillOverwriteAck: boolean;
  skillGenResult: GenerateSkillResultWire | null;
  skillGenRunning: boolean;
  skillGenError: GenerateSkillError | null;
  skillGenPreviewOpen: boolean;
  canGenerateSkill: boolean;
  skillPlanHasDiffering: boolean;

  // --- Step 5 — launch / verify ---
  step5Running: boolean;
  step5Items: Record<Step5ItemId, Step5ItemState>;
  step5LaunchPid: number | null;
  step5BridgePort: number | null;
  step5BridgeStatus: BridgeStatusKind;
  step5PingResult: BridgePingResult | null;
  step5Error: string | null;
  step5LastTick: number | null;

  // --- Clear AI Setup ---
  clearInProgress: boolean;
  clearResult: ClearAiSetupResult | null;
  clearError: string | null;

  // --- Done-screen derived summaries (precomputed by the orchestrator) ---
  doneOpenInCursor: boolean;
  doneOpenInOpencode: boolean;
}

/** Callback surface passed to every step module. The orchestrator implements
 *  every function; steps call them on user interaction. */
export interface WizardHandlers {
  // Step 0
  selectPreset: (id: import("../../services/wizard_presets.ts").PresetId) => void;
  // Step 1
  refreshDetection: () => void;
  runNodeProbe: () => void;
  // Step 2
  onUseLocalCheckoutChange: (checked: boolean) => void;
  onToolkitRootInput: (value: string) => void;
  pickToolkitFolder: () => void;
  runToolkitValidation: () => void;
  setMcpIndexOverride: (value: string) => void;
  setUseGlobalInstall: (value: boolean) => void;
  // Step 3
  setInstallBridge: (value: boolean) => void;
  setInstallVerify: (value: boolean) => void;
  onUseLocalPackagesChange: (checked: boolean) => void;
  setPackageVersionPin: (value: string) => void;
  setPackageCustomUrl: (value: string) => void;
  toggleUnityDomainDep: (upmId: string, checked: boolean) => void;
  setUpgradeAcknowledged: (value: boolean) => void;
  setShowDiff: (value: boolean) => void;
  installManifest: () => void;
  skipToMcpClient: () => void;
  // Step 4
  setMcpClient: (value: McpClientId) => void;
  setCursorProjectScope: (value: boolean) => void;
  setBridgePort: (value: string) => void;
  setMcpClientSearch: (value: string) => void;
  primaryMcpAction: () => void;
  copyMcpJson: () => void;
  // Step 4b / Done — skill
  copySkillFiles: () => void;
  generateProjectSkill: () => void;
  toggleSkillOverwriteAck: (value: boolean) => void;
  toggleSkillGenPreview: () => void;
  skipSkillStep: () => void;
  // Step 5
  runStep5Verify: () => void;
  stopStep5Polling: () => void;
  skipStep5Verify: () => void;
  // Done — links
  openProjectFolder: () => void;
  revealProjectFolder: () => void;
  openMcpConfigTarget: () => void;
  openToolkitSkill: () => void;
  openCopiedSkill: () => void;
  reRunWizard: () => void;
  closeWizard: () => void;
  onClearAiSetup: () => void;
}

// Re-export the service types so step modules can import them from one place.
export type {
  BridgePingResult,
  BridgeStatusKind,
  ClearAiSetupResult,
  GenerateSkillError,
  GenerateSkillResultWire,
  LaunchForVerifyError,
  ManifestError,
  ManifestMergePlan,
  ManifestWriteResult,
  McpConfigError,
  McpConfigHeuristic,
  McpConfigPlan,
  McpConfigWriteResult,
  NodeProbe,
  ProjectState,
  SkillCopyError,
  SkillCopyPlan,
  SkillCopyResult,
  ToolkitValidation,
};
