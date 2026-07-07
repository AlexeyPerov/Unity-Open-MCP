import type { McpClientId } from "./ai_toolkit";
import type {
  AiSetupWizardDraft,
  AiToolkitSettings,
  ProjectEntry,
} from "./config";

export type { AiSetupWizardDraft };

/** Canonical empty draft — used to decide whether to omit `aiSetupWizard`. */
export const EMPTY_AI_SETUP_WIZARD_DRAFT: Readonly<AiSetupWizardDraft> = {
  useLocalCheckout: false,
  useGlobalInstall: false,
  toolkitRoot: "",
  mcpIndexOverride: "",
  installBridge: true,
  installVerify: true,
  packageVersionPin: "",
  packageCustomUrl: "",
  useLocalPackages: false,
  selectedUnityDomainDeps: [],
  upgradeAcknowledged: false,
  showDiff: false,
  mcpClient: "cursor",
  cursorProjectScope: true,
  bridgePort: "",
  skillOverwriteAck: false,
  selectedPresetId: "",
};

/** Hydrated wizard form values after merge precedence. */
export interface AiSetupWizardFormState {
  useLocalCheckout: boolean;
  useGlobalInstall: boolean;
  toolkitRoot: string;
  mcpIndexOverride: string;
  installBridge: boolean;
  installVerify: boolean;
  packageVersionPin: string;
  packageCustomUrl: string;
  useLocalPackages: boolean;
  selectedUnityDomainDeps: string[];
  upgradeAcknowledged: boolean;
  showDiff: boolean;
  mcpClient: McpClientId;
  cursorProjectScope: boolean;
  bridgePort: string;
  skillOverwriteAck: boolean;
  /** Step 1 preset id (empty = Custom / skip). */
  selectedPresetId: string;
}

export interface AiSetupWizardDraftSnapshot {
  useLocalCheckout: boolean;
  useGlobalInstall: boolean;
  toolkitRoot: string;
  mcpIndexOverride: string;
  installBridge: boolean;
  installVerify: boolean;
  packageVersionPin: string;
  packageCustomUrl: string;
  useLocalPackages: boolean;
  selectedUnityDomainDeps: Set<string>;
  upgradeAcknowledged: boolean;
  showDiff: boolean;
  mcpClient: McpClientId;
  cursorProjectScope: boolean;
  bridgePort: string;
  skillOverwriteAck: boolean;
  selectedPresetId: string;
}

function globalToolkitDefaults(settings: AiToolkitSettings): Pick<
  AiSetupWizardFormState,
  "toolkitRoot" | "mcpIndexOverride" | "useLocalCheckout"
> {
  const toolkitRoot = settings.rootPath ?? "";
  const mcpIndexOverride = settings.mcpIndexOverride ?? "";
  const useLocalCheckout =
    settings.useLocalCheckout ?? toolkitRoot.trim().length > 0;
  return { toolkitRoot, mcpIndexOverride, useLocalCheckout };
}

/** Merge saved per-project draft → global `aiToolkit` → component defaults. */
export function hydrateAiSetupWizardDraft(
  project: Pick<ProjectEntry, "aiSetupWizard">,
  settings: AiToolkitSettings,
): AiSetupWizardFormState {
  const saved = project.aiSetupWizard;
  const global = globalToolkitDefaults(settings);
  const empty = EMPTY_AI_SETUP_WIZARD_DRAFT;

  return {
    useLocalCheckout: saved?.useLocalCheckout ?? global.useLocalCheckout,
    useGlobalInstall: saved?.useGlobalInstall ?? empty.useGlobalInstall!,
    toolkitRoot: saved?.toolkitRoot ?? global.toolkitRoot,
    mcpIndexOverride: saved?.mcpIndexOverride ?? global.mcpIndexOverride,
    installBridge: saved?.installBridge ?? empty.installBridge!,
    installVerify: saved?.installVerify ?? empty.installVerify!,
    packageVersionPin: saved?.packageVersionPin ?? empty.packageVersionPin!,
    packageCustomUrl: saved?.packageCustomUrl ?? empty.packageCustomUrl!,
    useLocalPackages: saved?.useLocalPackages ?? empty.useLocalPackages!,
    selectedUnityDomainDeps:
      saved?.selectedUnityDomainDeps?.slice() ??
      empty.selectedUnityDomainDeps!.slice(),
    upgradeAcknowledged:
      saved?.upgradeAcknowledged ?? empty.upgradeAcknowledged!,
    showDiff: saved?.showDiff ?? empty.showDiff!,
    mcpClient: saved?.mcpClient ?? empty.mcpClient!,
    cursorProjectScope:
      saved?.cursorProjectScope ?? empty.cursorProjectScope!,
    bridgePort: saved?.bridgePort ?? empty.bridgePort!,
    skillOverwriteAck: saved?.skillOverwriteAck ?? empty.skillOverwriteAck!,
    selectedPresetId: saved?.selectedPresetId ?? empty.selectedPresetId!,
  };
}

/** Snapshot the persisted subset of wizard form state. */
export function collectAiSetupWizardDraft(
  state: AiSetupWizardDraftSnapshot,
): AiSetupWizardDraft {
  return {
    useLocalCheckout: state.useLocalCheckout,
    useGlobalInstall: state.useGlobalInstall,
    toolkitRoot: state.toolkitRoot,
    mcpIndexOverride: state.mcpIndexOverride,
    installBridge: state.installBridge,
    installVerify: state.installVerify,
    packageVersionPin: state.packageVersionPin,
    packageCustomUrl: state.packageCustomUrl,
    useLocalPackages: state.useLocalPackages,
    selectedUnityDomainDeps: [...state.selectedUnityDomainDeps].sort(),
    upgradeAcknowledged: state.upgradeAcknowledged,
    showDiff: state.showDiff,
    mcpClient: state.mcpClient,
    cursorProjectScope: state.cursorProjectScope,
    bridgePort: state.bridgePort,
    skillOverwriteAck: state.skillOverwriteAck,
    selectedPresetId: state.selectedPresetId,
  };
}

function draftFieldEquals(
  key: keyof AiSetupWizardDraft,
  draft: AiSetupWizardDraft,
  empty: AiSetupWizardDraft,
): boolean {
  const left = draft[key];
  const right = empty[key];
  if (Array.isArray(left) && Array.isArray(right)) {
    if (left.length !== right.length) return false;
    return left.every((v, i) => v === right[i]);
  }
  return left === right;
}

/** True when the draft matches canonical defaults (omit from `projects.json`). */
export function isEmptyDraft(draft: AiSetupWizardDraft): boolean {
  const keys = Object.keys(EMPTY_AI_SETUP_WIZARD_DRAFT) as Array<
    keyof AiSetupWizardDraft
  >;
  return keys.every((key) =>
    draftFieldEquals(key, draft, EMPTY_AI_SETUP_WIZARD_DRAFT),
  );
}

/** Stable JSON for change detection before debounced persist. */
export function serializeDraftSnapshot(
  state: AiSetupWizardDraftSnapshot,
): string {
  return JSON.stringify(collectAiSetupWizardDraft(state));
}
