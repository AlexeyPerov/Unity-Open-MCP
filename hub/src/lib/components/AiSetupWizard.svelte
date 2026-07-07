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
    type BridgePingResult,
    type BridgeStatusKind,
    type ClearAiSetupResult,
    type GenerateSkillError,
    type GenerateSkillParamsWire,
    type GenerateSkillResultWire,
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
    WIZARD_PRESETS,
    applyPresetToForm,
    presetById,
    type PresetId,
    type WizardPreset,
  } from "$lib/services/wizard_presets";
  import {
    BRIDGE_PACKAGE_ID,
    VERIFY_PACKAGE_ID,
    changeKindLabel,
    changeKindTone,
    describeManifestError,
    formatChangeLine,
    formatDiffPreview,
    shortPackageName,
    summarizeChanges,
  } from "$lib/services/manifest";
  import {
    EMBEDDED_DOMAINS,
    builtinEmbeddedDomains,
    installableEmbeddedDomains,
  } from "$lib/services/extensions";
  import {
    type McpClientId,
  } from "$lib/services/ai_toolkit";
  import type { McpLaunchModeWire } from "$lib/services/config";
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

  type StepId =
    | "step0"
    | "step1"
    | "step2"
    | "step3"
    | "step4"
    | "step4b"
    | "step5"
    | "done";

  const STEP_ORDER: StepId[] = [
    "step0",
    "step1",
    "step2",
    "step3",
    "step4",
    "step4b",
    "step5",
    "done",
  ];

  const STEP_TITLES: Record<StepId, string> = {
    step0: "Setup preset",
    step1: "Project detection",
    step2: "MCP server source",
    step3: "Unity packages",
    step4: "Configure AI client",
    step4b: "Agent skill (optional)",
    step5: "Launch Unity and verify bridge",
    done: "Setup complete",
  };

  const MCP_CLIENT_OPTIONS: {
    id: McpClientId;
    label: string;
    kind: "file" | "cli" | "clipboard";
    /** Category for the grouped picker: IDE-backed agents, CLI
     *  agents, or the manual/clipboard fallbacks. */
    category: "ide" | "cli" | "manual";
    /** Tooltip describing the config format family + popular agents
     *  that share it, so users picking one client understand the
     *  format they are committing to. */
    sharedWith?: string;
  }[] = [
    // --- IDE / editor agents (file-backed) ---
    {
      id: "cursor",
      label: "Cursor",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON at ~/.cursor/mcp.json (global) or .cursor/mcp.json (project).",
    },
    {
      id: "claude-desktop",
      label: "Claude Desktop",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON (Claude Desktop config). Same envelope as Cursor; shared by Claude Desktop and Cursor.",
    },
    {
      id: "cline",
      label: "Cline (VS Code)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON in VS Code globalStorage (cline_mcp_settings.json). Skill installs to .cline/skills/.",
    },
    {
      id: "vscode-copilot",
      label: "VS Code Copilot",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: servers JSON at .vscode/mcp.json (project). Uses the `servers` key, not `mcpServers`.",
    },
    {
      id: "vs-copilot",
      label: "Visual Studio Copilot",
      kind: "file",
      category: "ide",
      sharedWith: "Format: servers JSON at .vs/mcp.json (project).",
    },
    {
      id: "zoocode",
      label: "ZooCode",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON at .roo/mcp.json (project). Skill installs to .roo/skills/.",
    },
    {
      id: "kilo-code",
      label: "Kilo Code",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON at .kilocode/mcp.json (project). Skill installs to .kilocode/skills/.",
    },
    {
      id: "rider",
      label: "Rider (Junie)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON at .junie/mcp/mcp.json (project). Skill installs to .junie/skills/.",
    },
    {
      id: "unity-ai",
      label: "Unity AI",
      kind: "file",
      category: "ide",
      sharedWith: "Format: mcpServers JSON at UserSettings/mcp.json (project).",
    },
    {
      id: "antigravity",
      label: "Antigravity",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcpServers JSON at ~/.gemini/antigravity/mcp_config.json (global). Skill installs to .agent/skills/.",
    },
    {
      id: "gemini",
      label: "Gemini CLI",
      kind: "file",
      category: "cli",
      sharedWith:
        "Format: mcpServers JSON at .gemini/settings.json (project). Skill installs to .gemini/skills/.",
    },
    {
      id: "codex",
      label: "Codex",
      kind: "file",
      category: "cli",
      sharedWith:
        "Format: TOML at .codex/config.toml (project). Emits a [mcp_servers.unity-open-mcp] table.",
    },
    {
      id: "opencode-global",
      label: "OpenCode (global)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcp + $schema JSON (~/.config/opencode/opencode.json). Shared by: OpenCode and Opencode.",
    },
    {
      id: "opencode-project",
      label: "OpenCode (project)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcp + $schema JSON (project-local opencode.json). Shared by: OpenCode and Opencode.",
    },
    {
      id: "zcode-global",
      label: "ZCode (global)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcp.servers + type:stdio JSON (~/.zcode/cli/config.json). Skill installs to .agents/skills/. Shared by: ZCode.",
    },
    {
      id: "zcode-project",
      label: "ZCode (project)",
      kind: "file",
      category: "ide",
      sharedWith:
        "Format: mcp.servers + type:stdio JSON (project-local .zcode/cli/config.json). Skill installs to .agents/skills/. Shared by: ZCode.",
    },
    // --- CLI agents ---
    {
      id: "claude-code",
      label: "Claude Code (CLI only)",
      kind: "cli",
      category: "cli",
      sharedWith:
        "CLI-only: renders a `claude mcp add` command (no config file is written). Skill installs to .claude/skills/.",
    },
    {
      id: "github-copilot-cli",
      label: "GitHub Copilot CLI",
      kind: "file",
      category: "cli",
      sharedWith:
        "Format: mcpServers JSON at .mcp.json (project, shared with Claude Code). Run `copilot` from the project root.",
    },
    // --- Manual / clipboard fallbacks ---
    {
      id: "manual",
      label: "Manual / copy JSON",
      kind: "clipboard",
      category: "manual",
      sharedWith:
        "Copy a JSON snippet to paste into any MCP client manually. No file is written by the wizard.",
    },
    {
      id: "custom",
      label: "Custom / other",
      kind: "clipboard",
      category: "manual",
      sharedWith:
        "Copy a JSON snippet for any MCP client not listed above. Installs the skill into every known client folder.",
    },
  ];

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

  // Stable for the wizard session. Parent re-renders from draft persist pass
  // a fresh `project` object each time; reading `project.path` inside `$effect`
  // would restart every async planner on each save.
  // svelte-ignore state_referenced_locally
  const wizardProjectId = project.id;
  // svelte-ignore state_referenced_locally
  const wizardProjectPath = project.path;

  let wizardClosed = $state(false);
  let detectionGeneration = 0;

  let currentStep = $state<StepId>("step0");

  // Step 0 — preset picker. `selectedPresetId` is the persisted choice
  // (empty = Custom / skip). Selecting a card hydrates the relevant form
  // fields immediately so Steps 1–5 reflect the choice on entry.
  let selectedPresetId = $state<string>("");

  // Step 1 — detection snapshot. Refreshed on mount and on every
  // entry to Step 1 / Done so the UI matches live disk state.
  let detection = $state<ProjectState | null>(null);
  let detectionLoading = $state(false);
  let detectionError = $state<string | null>(null);
  // Transient "project state refreshed" confirmation shown next to the
  // Re-detect button after a successful refresh. Auto-clears on a timer.
  let detectToast = $state<string | null>(null);
  let detectToastTimer = $state<ReturnType<typeof setTimeout> | null>(null);

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
  // Step 2 toggle: `true` to onboard against a local toolkit checkout
  // (contributor / clone path); `false` (default) to use the bundled npm
  // package via `npx -y unity-open-mcp@latest`. Auto-enables when a
  // `rootPath` is already saved so existing M4 onboarding keeps resolving
  // to the local launch command without a forced migration.
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

  // Step 3 — optional Unity domain dependencies (opt-in toggle group).
  // M18 Plan 4: domain tools are bundled inside the bridge, so the wizard
  // no longer installs separate `com.alexeyperov.unity-open-mcp-ext-*`
  // packs. Instead, the user opts into the Unity package that activates
  // each embedded domain (e.g. `com.unity.ai.navigation`). Selected deps
  // flow through the same merge planner as bridge + verify: each one
  // becomes a UPM version entry written to `Packages/manifest.json` on
  // Install, with the same backup / upgrade-confirmation envelope.
  // Built-in module domains (Particle System, Animation) have no UPM
  // id and never reach this set — they render as info-only cards.
  let selectedUnityDomainDeps = $state<Set<string>>(new Set());

  // Resolve the selected UPM ids to (id, version) pairs the Rust merge
  // planner expects. Sourced from the TS catalog; the merge writer just
  // carries the version string into `Packages/manifest.json`.
  function selectedUnityDepInstalls(): { id: string; version: string }[] {
    const installable = new Map(
      installableEmbeddedDomains().map((d) => [d.upmDependency, d]),
    );
    return [...selectedUnityDomainDeps]
      .map((id) => installable.get(id))
      .filter((d): d is NonNullable<typeof d> => Boolean(d))
      .map((d) => ({ id: d.upmDependency, version: d.defaultVersion }));
  }

  // Static catalog snapshot — embedded domains advertised by the wizard.
  // `installable` domains get a toggle; `builtin` ones render as info-only
  // "always-on" cards because the Unity module ships with the Editor.
  const installableDomains = installableEmbeddedDomains();
  const builtinDomains = builtinEmbeddedDomains();

  // Step 4 — MCP client state. `bridgePort` is an OPTIONAL override:
  // blank means "derive from the project path" (the per-project hash shared
  // with the bridge + MCP server). The effective port is resolved via
  // `resolveBridgePort` and surfaced in `resolvedBridgePort` so the UI and
  // Step 5 always work with the real number.
  let mcpClient = $state<McpClientId>("cursor");
  let cursorProjectScope = $state(true);
  let bridgePort = $state("");
  let resolvedBridgePort = $state<number | null>(null);
  let copyToast = $state<string | null>(null);
  // Step 4 client-picker search filter. Empty shows the full grouped
  // catalog; typing filters by label / id so the 18-entry list stays
  // navigable. The selected client is always visible even when it does
  // not match the filter (so the preview + write actions stay reachable).
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

  // "Generate project skill" — runs unity_open_mcp_generate_skill via
  // the local CLI (no live bridge needed) and merges the template
  // workflow playbook with this project's inventory into the same
  // client skill dirs the template copy writes. Shares the overwrite
  // confirmation toggle with the template-copy flow (generate
  // overwrites the same paths).
  let skillGenResult = $state<GenerateSkillResultWire | null>(null);
  let skillGenRunning = $state(false);
  let skillGenError = $state<GenerateSkillError | null>(null);
  let skillGenPreviewOpen = $state(false);

  // "Clear AI Setup" — destructive inverse of the wizard. The yellow
  // footer button runs it after a confirmation modal; the result is
  // surfaced inline + via the Re-detect toast slot so the user sees
  // exactly what was removed.
  let clearInProgress = $state(false);
  let clearResult = $state<ClearAiSetupResult | null>(null);
  let clearError = $state<string | null>(null);

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
  const DRAFT_PERSIST_DEBOUNCE_MS = 400;

  let draftPersistReady = $state(false);
  let lastPersistedDraftJson = $state("");
  let draftPersistTimer = $state<ReturnType<typeof setTimeout> | null>(null);

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

    const updated = {
      ...stored,
      aiSetupWizard: isEmptyDraft(draft) ? undefined : draft,
    };

    try {
      await projectsStore.update(updated);
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
        await projectsStore.update({ ...stored, aiSetupWizard: undefined });
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
      case "step0":
        // Step 1 (preset picker) always passes — no gate.
        return true;
      case "step1":
        return isProjectReady();
      case "step2":
        return isMcpSourceReady();
      case "step3":
        return isManifestReady();
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

  function isProjectReady(): boolean {
    // Step 1 is the environment gate: a valid Unity project that meets
    // the minimum version, has a writable manifest, and Node.js ≥18
    // available (the wizard launches `npx unity-open-mcp` / a local
    // `node` build, so Node is a hard requirement surfaced here rather
    // than on a separate Environment step).
    if (!detection) return false;
    if (!detection.isValidUnityProject) return false;
    if (!detection.meetsMinUnityVersion) return false;
    if (!detection.manifestWritable) return false;
    if (!nodeProbe?.ok) return false;
    return true;
  }

  function isMcpSourceReady(): boolean {
    // Step 2 only configures the MCP-server launch source. The default
    // `npx` path needs nothing; the local-checkout path needs a
    // validated toolkit root. Unity version / Node / manifest-writable
    // checks live on Step 1 now.
    if (useLocalCheckout && !toolkitValidation?.ok) return false;
    return true;
  }

  // The launch mode the wizard passes to `plan_mcp_config` /
  // `write_mcp_config`. Derived from the Step 2 toggle + the global
  // install option + the Step 4 advanced override field: the override
  // always wins (it is the explicit "custom mcp-server/dist/index.js"
  // escape hatch), then the toggle picks local vs npx, then the global
  // install option refines npx → global. Kept as a function so Step 4's
  // two `$effect` blocks re-read it on every form-state change.
  function effectiveLaunchMode(): McpLaunchModeWire {
    if (mcpIndexOverride.trim().length > 0) return "localOverride";
    if (useLocalCheckout) return "local";
    return useGlobalInstall ? "global" : "npx";
  }

  function onUseLocalCheckoutChange(checked: boolean) {
    useLocalCheckout = checked;
    // Never wipe `toolkitRoot` on toggle change — keep reading it for
    // back-compat so a user who flips back and forth does not lose the
    // path. Clearing the validation error lets the UI re-run the
    // fingerprint check when the toggle is re-enabled.
    if (!checked) {
      toolkitError = null;
    }
  }

  function isManifestReady(): boolean {
    // A Unity-dep-only selection (no bridge/verify) is a valid flow when
    // the selected deps are already present in the manifest.
    const depIds = [...selectedUnityDomainDeps];
    if (!installBridge && !installVerify && depIds.length === 0) return false;
    if (!mergePlan) return false;
    if (mergePlan.manifestRead.parseError) return false;
    // Allow Next whenever the selected packages already exist in the
    // manifest — whether via a local `file:` path (demo project) or a
    // remote git URL. The upgrade-acknowledgement toggle only gates the
    // "Install / Upgrade" action, not navigation.
    const selectedIds: string[] = [];
    if (installBridge) selectedIds.push(BRIDGE_PACKAGE_ID);
    if (installVerify) selectedIds.push(VERIFY_PACKAGE_ID);
    selectedIds.push(...depIds);
    return mergePlan.changes.some(
      (c) =>
        selectedIds.includes(c.id) &&
        (c.kind === "unchanged" || c.kind === "upgrade"),
    );
  }

  function canSkipStep3(): boolean {
    if (mergeResult) return true;
    return isManifestReady();
  }

  // Team CI preset advertises `skillEnabled: false` — CI agents typically
  // don't need a desktop skill file, so the wizard auto-skips the Agent
  // skill step when that preset is active.
  function shouldSkipSkillStep(): boolean {
    return (
      selectedPresetId === "team-ci" &&
      presetById(selectedPresetId).values.skillEnabled === false
    );
  }

  function nextStep() {
    if (!canGoNext()) return;
    const i = currentStepIndex();
    if (i >= STEP_ORDER.length - 1) return;
    let next = STEP_ORDER[i + 1];
    // Auto-skip the optional Agent skill step for presets that disable it.
    if (next === "step4b" && shouldSkipSkillStep()) {
      S.appendDrawerLog(
        `AI Setup: auto-skipped Agent skill step for ${project.name} (Team CI preset)`,
      );
      const after = STEP_ORDER.indexOf("step5");
      if (after > i) next = STEP_ORDER[after];
    }
    currentStep = next;
  }

  // Free navigation: jump straight to any step from the progress list.
  // The destination step's own UI continues to show its blocks/gating;
  // the footer Back/Next still respects sequential `canGoNext`. No
  // prerequisite enforcement on jump — least surprising for "let me
  // peek at a later step."
  function jumpToStep(id: StepId) {
    currentStep = id;
  }

  // Step 0 — apply a preset to the wizard form. Selecting a card both
  // records the choice (`selectedPresetId`, persisted in the draft) and
  // hydrates the relevant fields immediately so Steps 1–5 reflect the
  // choice on entry. Re-selecting on Back navigation re-applies the
  // preset's values in full (the simpler branch from the plan).
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
      `AI Setup: applied "${preset.label}" preset for ${project.name}`,
    );
  }

  function backStep() {
    if (!canGoBack()) return;
    const i = currentStepIndex();
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
    try {
      // Always persist the local-checkout toggle so the next wizard run
      // remembers the onboarding path the user picked — even on the npx
      // path where no toolkit root is ever collected.
      await settingsStore.setAiToolkitUseLocalCheckout(useLocalCheckout);
      // Root + override only persist when the local-checkout path
      // validated successfully. The npx path leaves them untouched.
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
      // Surface a small confirmation so the user sees the refresh did
      // something (Re-detect is otherwise silent when nothing changed).
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

  // Re-run environment checks whenever the user enters Step 1 (the
  // wizard's environment gate). Node is probed here so the popup
  // launch immediately surfaces a missing/too-old Node, and the
  // toolkit re-validates when a saved root is present.
  $effect(() => {
    if (currentStep === "step1") {
      if (nodeProbe === null) void runNodeProbe();
      if (toolkitRoot.trim() && toolkitValidation === null && !toolkitValidating) {
        void runToolkitValidation();
      }
    }
  });

  // Re-detect project on every Step 1 / Done entry so the table reflects
  // the latest manifest state. Cancel in-flight work when the step changes
  // or the wizard closes so stale results cannot block the UI.
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

  // Live plan: re-compute the merge plan whenever Step 3 form
  // state (or the toolkit root) changes. The diff preview is
  // always live — the user does not have to click a refresh.
  $effect(() => {
    if (currentStep !== "step3") return;
    if (!toolkitRoot.trim()) return;
    // Reading `selectedUnityDomainDeps` here makes the plan re-run
    // whenever a Unity-dep toggle changes. A Unity-dep-only selection
    // (no bridge/verify) still produces a valid merge plan.
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

  // Resolve the effective bridge port whenever the project path or the
  // optional override changes. Blank override → the per-project hash
  // (computed server-side in Rust); a valid override wins. Surfaced in
  // `resolvedBridgePort` so the UI and Step 5 always use the real number.
  $effect(() => {
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

  // Live Step 4 plan: re-call `plan_mcp_config` whenever the
  // form state changes so the diff preview + write button
  // always reflect what the Rust writer would produce. The
  // MCP path is the upstream gate; the planner still runs
  // with a stale path so the UI can surface a focused
  // `mcpPathInvalid` error rather than a blank preview.
  $effect(() => {
    const projectPath = wizardProjectPath;
    const root = toolkitRoot;
    const client = mcpClient;
    const projectScope = cursorProjectScope;
    const port = bridgePort;
    const override = mcpIndexOverride;
    const localCheckout = useLocalCheckout;
    const globalInstall = useGlobalInstall;
    const mode: McpLaunchModeWire = override.trim().length > 0
      ? "localOverride"
      : localCheckout
        ? "local"
        : globalInstall
          ? "global"
          : "npx";
    // The local + localOverride launch modes need a toolkit root to
    // resolve the on-disk index; the npm modes (npx / global) resolve
    // the published binary and never touch disk, so they run without one.
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

  // Live skill-copy plan: recomputed whenever the user enters the
  // dedicated Agent skill step (step4b) OR the Done screen, so the
  // per-target preview + "file exists" flags are always fresh. The
  // targets come from the single-source manifest keyed off the
  // selected MCP client (Cursor → .cursor/skills/, ZCode →
  // .agents/skills/, etc.) — never from ad-hoc booleans.
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
          // Reset confirmation + result so the user re-acks
          // the overwrite toggle when the plan shape changes.
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
      case "zcode-global":
        return "zcodeGlobal";
      case "zcode-project":
        return "zcodeProject";
      case "manual":
        return "manual";
      case "cline":
        return "cline";
      case "codex":
        return "codex";
      case "gemini":
        return "gemini";
      case "github-copilot-cli":
        return "githubCopilotCli";
      case "kilo-code":
        return "kiloCode";
      case "rider":
        return "rider";
      case "unity-ai":
        return "unityAi";
      case "vscode-copilot":
        return "vscodeCopilot";
      case "vs-copilot":
        return "vsCopilot";
      case "zoocode":
        return "zoocode";
      case "antigravity":
        return "antigravity";
      case "custom":
        return "custom";
    }
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
      case "manifestMissing":
      case "manifestInvalid":
        return `Skill client-paths manifest problem: ${err.message}. Make sure your toolkit root is the unity-open-mcp monorepo checkout.`;
      case "writeFailed":
        return `Failed to copy skill: ${err.message}. Check folder permissions.`;
      case "backupFailed":
        return `Cannot create backup: ${err.message}`;
      case "notAUnityProject":
        return `Project path is not a directory.`;
      case "overwriteNotConfirmed":
        return err.message;
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

  // Filtered + grouped view of the client picker. The search matches
  // label or id (case-insensitive); the currently-selected client is
  // always kept visible so the preview/write actions stay reachable.
  let filteredClientOptions = $derived.by(() => {
    const q = mcpClientSearch.trim().toLowerCase();
    if (!q) return MCP_CLIENT_OPTIONS;
    return MCP_CLIENT_OPTIONS.filter(
      (o) =>
        o.label.toLowerCase().includes(q) ||
        o.id.toLowerCase().includes(q) ||
        o.id === mcpClient,
    );
  });

  const CLIENT_CATEGORY_LABELS: Record<"ide" | "cli" | "manual", string> = {
    ide: "Editor / IDE agents",
    cli: "CLI agents",
    manual: "Manual",
  };

  // Group the filtered options by category, preserving the catalog order.
  let groupedClientOptions = $derived.by(() => {
    const groups: { category: "ide" | "cli" | "manual"; items: typeof MCP_CLIENT_OPTIONS }[] = [];
    for (const cat of ["ide", "cli", "manual"] as const) {
      const items = filteredClientOptions.filter((o) => o.category === cat);
      if (items.length > 0) groups.push({ category: cat, items });
    }
    return groups;
  });

  function canWriteMcpConfig(): boolean {
    if (clientKind(mcpClient) !== "file") return false;
    // The local + localOverride launch modes need a validated on-disk
    // index path; the npm modes (npx / global) write the
    // published-binary launch command and skip path validation.
    const mode = effectiveLaunchMode();
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
    const mode = effectiveLaunchMode();
    // Local modes need a toolkit root to resolve the on-disk index; the
    // npm modes (npx / global) write the published-binary launch command
    // and never touch disk.
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
    const projectPath = wizardProjectPath;
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
        mcpClient: clientToWire(mcpClient),
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

  // "Generate project skill" — runs the local MCP server's
  // unity_open_mcp_generate_skill tool via the CLI (no live bridge
  // needed) and writes a project-specific SKILL.md that merges the
  // template workflow playbook with this project's inventory into the
  // same client skill dirs. Requires the built MCP server entry
  // (`mcp-server/dist/index.js`); same prerequisite as the MCP config
  // step's local launch mode.
  function toGenerateSkillError(e: unknown): GenerateSkillError {
    if (e && typeof e === "object" && "kind" in e && "message" in e) {
      return e as GenerateSkillError;
    }
    return { kind: "unknown", message: e instanceof Error ? e.message : String(e) };
  }

  function describeGenerateSkillError(err: GenerateSkillError): string {
    switch (err.kind) {
      case "notAUnityProject":
        return `Project path is not a directory.`;
      case "mcpPathInvalid":
        return `MCP server entry not found. Run \`npm run build\` in the toolkit's mcp-server/ folder. (${err.message})`;
      case "noClientTargets":
        return `No skill folder is mapped for the selected client. Pick a different client or use Manual.`;
      case "spawnFailed":
        return `Failed to run node: ${err.message}. Check Node.js is installed and on PATH.`;
      case "cliError":
        return `Skill generator CLI failed: ${err.message}`;
      case "toolError":
        return `Skill generation tool error: ${err.message}`;
      case "manifestInvalid":
        return `Skill client-paths manifest problem: ${err.message}.`;
      default:
        return `${err.kind}: ${err.message}`;
    }
  }

  function canGenerateSkill(): boolean {
    // Same prerequisite as the MCP config step's local launch mode:
    // the built MCP entry must resolve. Reuses the existing fingerprint
    // validation when available.
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
        `AI Setup: generated project skill for ${project.name} — ${result.targets.length} target(s), Unity ${result.unityVersion}`
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
    // `bridgePort` is an optional override; pass 0 when blank so the Rust
    // launch path derives the per-project hash. The effective port is echoed
    // back in the launch result (`result.bridgePort`).
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
      // The launch result carries the effective port (override or computed
      // hash), so surface that everywhere downstream.
      step5BridgePort = result.bridgePort;
      step5LaunchPid = result.pid;
      launched = true;
      step5Items = {
        ...step5Items,
        launch: "ok",
        compile: "running",
      };
      S.appendDrawerLog(
        `AI Setup: launched Unity (pid ${result.pid}) with bridge port ${result.bridgePort} for ${project.name}`,
      );
      // Phase 2 — poll the bridge `/ping` endpoint every
      // STEP5_POLL_INTERVAL_MS until the bridge responds 200
      // with a parseable body, or the 120 s overall budget
      // elapses, or the user clicks Stop. The compile step is
      // considered "ok" the moment the bridge reports
      // `compiling: false` (or once we've seen at least one
      // response, since the spec treats compile errors as
      // `ping: failed` rather than `compile: failed`).
      await pollBridgeUntilReady(result.bridgePort);
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

  // Skip the optional Agent skill step without copying. Logs the
  // decision so the Done-screen summary is consistent.
  function skipSkillStep() {
    S.appendDrawerLog(
      `AI Setup: skipped Agent skill copy for ${project.name} (${mcpClientLabel(mcpClient)})`,
    );
    nextStep();
  }

  // Human-readable label for the MCP client radio selection. Used by
  // the Agent skill step so the user sees which client the skill
  // folder is derived from.
  function mcpClientLabel(id: McpClientId): string {
    return MCP_CLIENT_OPTIONS.find((o) => o.id === id)?.label ?? id;
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
    if (
      h.cursor ||
      h.claudeDesktop ||
      h.opencodeGlobal ||
      h.opencodeProject ||
      h.zcodeGlobal ||
      h.zcodeProject
    ) {
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
    // Reset transient per-step state to Step 0 (preset picker). Form
    // fields reload from global settings + defaults after clearing the
    // per-project draft.
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

  // Clear AI Setup — removes the manifest entries, MCP client configs
  // (project-scoped unconditionally; global only the entries whose
  // `UNITY_PROJECT_PATH` matches this project), and the agent-skill
  // files the wizard wrote. All mutations are best-effort with `.bak`
  // backups; per-target failures land in `result.errors` and are shown
  // inline rather than aborting the whole pass.
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
      // Refresh the live snapshot so Step 1 / Done reflect the cleared
      // state, and reset the transient Step 4/4b/5 state so stale
      // "config written" / bridge-verified chips do not linger.
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
  // single glance at "where am I" + "what's left". A segment also gets
  // a green `passing` accent when its readiness check holds against the
  // live detection / probe state — so on popup launch, segments whose
  // prerequisites are already satisfied light up immediately.
  function stepPassing(id: StepId): boolean {
    switch (id) {
      case "step0":
        // Preset picker has no readiness check — always neutral.
        return false;
      case "step1":
        return isProjectReady();
      case "step2":
        return isMcpSourceReady();
      case "step3":
        return (
          !!detection &&
          detection.bridgeInstalled &&
          detection.verifyInstalled
        );
      case "step4": {
        const h = detection?.mcpConfigured;
        return (
          !!h &&
          (h.cursor ||
            h.claudeDesktop ||
            h.opencodeGlobal ||
            h.opencodeProject ||
            h.zcodeGlobal ||
            h.zcodeProject)
        );
      }
      case "step4b":
        return !!detection?.anySkillInstalled;
      case "step5":
        return step5BridgeStatus.kind === "ok";
      case "done":
        return false;
    }
  }

  let progress = $derived.by(() => {
    return STEP_ORDER.map((id, idx) => {
      const currentIdx = currentStepIndex();
      const state: "done" | "current" | "pending" =
        idx < currentIdx ? "done" : idx === currentIdx ? "current" : "pending";
      return { id, idx, label: stepLabel(id), state, passing: stepPassing(id) };
    });
  });

  function handleClose() {
    closeWizard();
  }

  // --- Step 1 derived display helpers -------------------------------
  function mcpConfiguredSummary(h: McpConfigHeuristic): string {
    const any =
      h.cursor ||
      h.claudeDesktop ||
      h.opencodeGlobal ||
      h.opencodeProject ||
      h.zcodeGlobal ||
      h.zcodeProject;
    if (!any) return "not detected";
    const clients: string[] = [];
    if (h.cursor) clients.push("Cursor");
    if (h.claudeDesktop) clients.push("Claude Desktop");
    if (h.opencodeGlobal) clients.push("OpenCode (global)");
    if (h.opencodeProject) clients.push("OpenCode (project)");
    if (h.zcodeGlobal) clients.push("ZCode (global)");
    if (h.zcodeProject) clients.push("ZCode (project)");
    return `yes (${clients.join(", ")})`;
  }

  // Diagnostics-first panel (Step 1, project detection). Reuses the live
  // detection + node probe + Step 5 ping result — no new Tauri command.
  // Each row carries a one-line remediation hint so the user can act on a
  // failure without reading the full detection table.
  interface DiagRow {
    id: string;
    label: string;
    ok: boolean;
    /** When `true` the row is informational (not a pass/fail gate). */
    info?: boolean;
    detail?: string;
    remediation?: string;
  }

  function diagnosticsRows(): DiagRow[] {
    const rows: DiagRow[] = [];
    const d = detection;
    // Project layout + Unity version.
    rows.push({
      id: "unity-project",
      label: "Valid Unity project layout",
      ok: !!d?.isValidUnityProject,
      remediation: d?.isValidUnityProject
        ? undefined
        : "Add the project via the Projects tab; the folder needs Assets/ and ProjectSettings/.",
    });
    rows.push({
      id: "unity-version",
      label: "Unity version meets minimum (2022.3 LTS)",
      ok: !!d?.meetsMinUnityVersion,
      detail: d?.unityVersion ?? "unknown",
      remediation: d?.meetsMinUnityVersion
        ? undefined
        : "Install Unity 2022.3 LTS or newer from the Installs tab.",
    });
    // Node.js.
    rows.push({
      id: "node",
      label: `Node.js ${nodeProbe?.requiredMajor ?? 18}+`,
      ok: !!nodeProbe?.ok,
      detail: nodeProbe?.version ?? (nodeProbing ? "probing…" : "not detected"),
      remediation: nodeProbe?.ok
        ? undefined
        : "Install the LTS from https://nodejs.org/ and restart the Hub.",
    });
    // Manifest writable.
    rows.push({
      id: "manifest-writable",
      label: "Writable Packages/manifest.json",
      ok: !!d?.manifestWritable,
      remediation: d?.manifestWritable
        ? undefined
        : "Check write permissions on the project's Packages/ folder.",
    });
    // Packages installed (informational — installed on Step 3).
    rows.push({
      id: "bridge-installed",
      label: "Bridge package installed",
      ok: !!d?.bridgeInstalled,
      info: true,
      remediation: d?.bridgeInstalled
        ? undefined
        : "Install the bridge on the Unity packages step.",
    });
    rows.push({
      id: "verify-installed",
      label: "Verify package installed",
      ok: !!d?.verifyInstalled,
      info: true,
      remediation: d?.verifyInstalled
        ? undefined
        : "Install verify on the Unity packages step.",
    });
    // MCP configured (informational — configured on Step 4).
    const mcpAny = !!d?.mcpConfigured && (() => {
      const h = d!.mcpConfigured;
      return h.cursor || h.claudeDesktop || h.opencodeGlobal ||
        h.opencodeProject || h.zcodeGlobal || h.zcodeProject;
    })();
    rows.push({
      id: "mcp-configured",
      label: "MCP client configured",
      ok: mcpAny,
      info: true,
      remediation: mcpAny
        ? undefined
        : "Configure an MCP client on the Configure AI client step.",
    });
    // Bridge reachable — only when Step 5 has run.
    if (step5BridgeStatus.kind !== "notChecked" || step5LaunchPid !== null) {
      rows.push({
        id: "bridge-reachable",
        label: "Bridge reachable (/ping)",
        ok: step5BridgeStatus.kind === "ok",
        info: true,
        detail:
          step5BridgeStatus.kind === "ok"
            ? "connected"
            : step5BridgeStatus.kind === "failed"
              ? step5BridgeStatus.message
              : "pending",
        remediation:
          step5BridgeStatus.kind === "ok"
            ? undefined
            : "Run the Launch and verify step; check the launch log for errors.",
      });
    }
    return rows;
  }

  function diagTone(row: DiagRow): "ok" | "warn" | "muted" {
    if (row.info && row.ok) return "ok";
    if (row.info && !row.ok) return "muted";
    return row.ok ? "ok" : "warn";
  }

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
        <!-- svelte-ignore a11y_no_static_element_interactions, a11y_click_events_have_key_events, a11y_interactive_supports_focus, a11y_no_noninteractive_element_to_interactive_role -->
        <li
          class="wiz-seg wiz-seg-{seg.state}{seg.passing ? ' wiz-seg-passing' : ''}"
          aria-current={seg.state === "current" ? "step" : undefined}
          role="button"
          tabindex="0"
          aria-label={`Jump to Step ${seg.idx + 1}: ${seg.label}`}
          title={`Jump to Step ${seg.idx + 1}: ${seg.label}`}
          onclick={() => jumpToStep(seg.id)}
          onkeydown={(e) => {
            if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              jumpToStep(seg.id);
            }
          }}
        >
          <span class="wiz-seg-num">{seg.idx + 1}</span>
          <span class="wiz-seg-label">{seg.label}</span>
        </li>
      {/each}
    </ol>

    <div class="wiz-body">
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
        <section class="wiz-section">
          <p class="wiz-desc">
            Pick a preset to pre-fill the rest of the wizard, or choose
            <strong>Custom / skip</strong> to configure every step manually.
            Presets are starting points, not locks — you can change any
            field on later steps. The recommended preset covers most users.
          </p>

          <div class="wiz-preset-grid" role="radiogroup" aria-label="Setup preset">
            {#each WIZARD_PRESETS as preset (preset.id)}
              <button
                type="button"
                class="wiz-preset{selectedPresetId === preset.id ? ' wiz-preset-selected' : ''}{preset.recommended ? ' wiz-preset-recommended' : ''}"
                role="radio"
                aria-checked={selectedPresetId === preset.id}
                title={preset.tooltip}
                onclick={() => selectPreset(preset.id)}
              >
                <div class="wiz-preset-head">
                  <strong>{preset.label}</strong>
                  {#if preset.recommended}
                    <span class="wiz-preset-badge">Recommended</span>
                  {/if}
                </div>
                <p class="wiz-preset-desc">{preset.description}</p>
                {#if preset.id === "secure-remote"}
                  <small class="wiz-preset-note">
                    Token auth, remote bind, and restricted tool groups are
                    configured on the bridge window after onboarding.
                  </small>
                {/if}
                {#if preset.id === "team-ci"}
                  <small class="wiz-preset-note">
                    Configure token auth on the bridge for headless CI use.
                  </small>
                {/if}
              </button>
            {/each}
          </div>

          {#if selectedPresetId && selectedPresetId !== "custom"}
            <p class="wiz-hint wiz-hint-ok">
              Applied the <strong>{presetById(selectedPresetId).label}</strong>
              preset. Steps 2–7 now reflect its defaults — adjust any field
              as needed. Pick <strong>Custom / skip</strong> to clear the
              pre-fills.
            </p>
          {/if}
        </section>
      {:else if currentStep === "step1"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Step 2 detects the current state of the project and runs a
            diagnostics pass. The detection is re-run on every entry so the
            values below always reflect the on-disk manifest and
            <code>ProjectVersion.txt</code>; the Done screen
            re-uses the same snapshot.
          </p>

          <div class="wiz-diag-block">
            <div class="wiz-diag-head">
              <span class="wiz-label">Diagnostics</span>
              <Button
                variant="secondary"
                onclick={() => { void refreshDetection(); void runNodeProbe(); }}
                disabled={detectionLoading || nodeProbing}
                title="Re-run project detection + Node probe"
              >
                {detectionLoading || nodeProbing ? "Checking…" : "Run diagnostics"}
              </Button>
            </div>
            <ul class="wiz-diag" aria-label="Project diagnostics">
              {#each diagnosticsRows() as row (row.id)}
                {@const tone = diagTone(row)}
                <li class="wiz-diag-row wiz-diag-{tone}">
                  <span class="wiz-diag-icon" aria-hidden="true">
                    {#if tone === "ok"}✓{:else if tone === "warn"}✗{:else}·{/if}
                  </span>
                  <span class="wiz-diag-label">{row.label}</span>
                  {#if row.detail}<span class="wiz-diag-detail">{row.detail}</span>{/if}
                  {#if !row.ok && row.remediation}
                    <small class="wiz-diag-fix">{row.remediation}</small>
                  {/if}
                </li>
              {/each}
            </ul>
          </div>

          {#if detectionLoading && !detection}
            <p class="wiz-hint">Detecting project…</p>
            <p class="wiz-hint wiz-hint-muted">
              You can close the wizard anytime with Cancel, Escape, or the ×
              button — detection will stop in the background.
            </p>
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
                      {#if !detection.meetsMinUnityVersion}
                        <span class="wiz-tag wiz-tag-warn" title="Unity 2022.3 LTS is the minimum supported version.">below minimum</span>
                      {:else if detection.meetsRecommendedUnityVersion}
                        <span class="wiz-tag wiz-tag-ok" title="Meets the recommended Unity 6+; minimum supported is 2022.3 LTS.">Unity 6+</span>
                      {:else}
                        <span class="wiz-tag wiz-tag-warn" title="Meets the minimum (2022.3 LTS); uses the legacy toolbar button fallback instead of the native Unity 6 toolbar element.">supported (legacy toolbar)</span>
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
              </dl>

              {#if !detection.meetsMinUnityVersion}
                <div class="wiz-block wiz-block-error" role="alert">
                  <strong>Unity {detection.unityVersion ?? "unknown"} does not meet the minimum.</strong>
                  Unity 2022.3 LTS or newer is required — the bridge + verify
                  package manifests declare <code>unity: "2022.3"</code>.
                </div>
              {/if}
              {#if !detection.manifestWritable}
                <div class="wiz-block wiz-block-error" role="alert">
                  <strong>Cannot write to <code>Packages/manifest.json</code>.</strong>
                  The file (or its parent directory) is not user-writable. The
                  wizard cannot install packages without write access.
                </div>
              {/if}
              {#if detection.hasSpacesInPath}
                <div class="wiz-block wiz-block-warn" role="status">
                  <strong>Spaces in project path.</strong>
                  Some MCP clients are known to mis-handle paths with spaces.
                  This is a warning, not a block.
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
                    {nodeProbing ? "Checking…" : "Re-check Node"}
                  </Button>
                </div>
              </div>

              <div class="wiz-actions-row">
                <Button variant="secondary" onclick={refreshDetection} disabled={detectionLoading}>
                  {detectionLoading ? "Re-detecting…" : "Re-detect"}
                </Button>
                {#if detectToast}
                  <span class="wiz-toast" role="status">{detectToast}</span>
                {/if}
              </div>
            {/if}
          {/if}
        </section>
      {:else if currentStep === "step2"}
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
                checked={useLocalCheckout}
                onchange={(e) =>
                  onUseLocalCheckoutChange((e.currentTarget as HTMLInputElement).checked)}
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
            {#if !useLocalCheckout}
              <label class="wiz-toggle">
                <input type="checkbox" bind:checked={useGlobalInstall} />
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
                as the MCP launch command. Node {nodeProbe?.major ?? "≥"}18 fetches the
                package from npm on first spawn.
              </p>
            {/if}
          </div>

          {#if useLocalCheckout}
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
                Unity packages step URLs and skill copy always use the
                toolkit root regardless of this override.
              </p>
            </div>
          </details>
        </section>
      {:else if currentStep === "step3"}
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
              <input
                type="checkbox"
                checked={useLocalPackages}
                onchange={(e) =>
                  onUseLocalPackagesChange((e.currentTarget as HTMLInputElement).checked)}
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
              <input type="checkbox" bind:checked={installScanner} disabled />
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
                value={packageVersionPin}
                disabled={useLocalPackages}
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
                disabled={useLocalPackages}
                oninput={(e) => (packageCustomUrl = (e.currentTarget as HTMLInputElement).value)}
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
                  {@const checked = selectedUnityDomainDeps.has(dep.upmDependency)}
                  <li class="wiz-extension-pack">
                    <label class="wiz-toggle">
                      <input
                        type="checkbox"
                        checked={checked}
                        onchange={(e) => {
                          const next = new Set(selectedUnityDomainDeps);
                          if ((e.currentTarget as HTMLInputElement).checked) {
                            next.add(dep.upmDependency);
                          } else {
                            next.delete(dep.upmDependency);
                          }
                          selectedUnityDomainDeps = next;
                        }}
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
            {#if !installBridge && !installVerify && selectedUnityDomainDeps.size === 0}
              <p class="wiz-hint wiz-hint-warn">
                Pick at least one package to install.
              </p>
            {:else if !toolkitRoot.trim() || !toolkitValidation?.ok}
              <p class="wiz-hint wiz-hint-warn">
                Validate the toolkit root on the MCP server source step first.
              </p>
            {:else if mergePlanning && !mergePlan}
              <p class="wiz-hint">Planning merge…</p>
            {:else if mergePlan}
              {#if showLocalPackagesInfo}
                <p class="wiz-hint wiz-hint-info">{localPackagesInfoText}</p>
              {/if}
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
              disabled={
                !isManifestReady() ||
                mergeWriting ||
                Boolean(mergePlan?.hasUpgrades && hasRealChanges && !upgradeAcknowledged)
              }
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
              disabled={!canSkipStep3()}
              title={canSkipStep3()
                ? "Skip to MCP client config"
                : "Validate the toolkit root and resolve manifest blocks first"}
            >
              Skip to MCP client config
            </Button>
          </div>
        </section>
      {:else if currentStep === "step4"}
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
                value={mcpClientSearch}
                oninput={(e) =>
                  (mcpClientSearch = (e.currentTarget as HTMLInputElement).value)}
                aria-label="Filter MCP clients"
              />
            </div>
            <div role="radiogroup" aria-label="MCP client">
              {#each groupedClientOptions as group (group.category)}
                <div class="wiz-client-group">
                  <p class="wiz-client-group-label">{CLIENT_CATEGORY_LABELS[group.category]}</p>
                  <div class="wiz-radio-grid">
                    {#each group.items as opt (opt.id)}
                      <label class="wiz-radio" title={opt.sharedWith}>
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
              placeholder="(auto)"
              value={bridgePort}
              oninput={(e) => (bridgePort = (e.currentTarget as HTMLInputElement).value)}
            />
            {#if !bridgePort.trim() && resolvedBridgePort != null}
              <p class="wiz-hint">
                Auto-derived from project path: <code>{resolvedBridgePort}</code>.
                Override only if you pin a specific port.
              </p>
            {/if}
          </div>

          <div class="wiz-field">
            <span class="wiz-label">
              {mcpPlan?.command ? "Claude Code command" : "Generated config"}
            </span>
            {#if mcpPlanning && !mcpPlan}
              <p class="wiz-hint">Planning…</p>
            {:else if !mcpPlan}
              <p class="wiz-hint wiz-hint-warn">
                {#if useLocalCheckout}
                  Set and validate the toolkit root on the MCP server source step to generate a config.
                {:else}
                  Waiting for the planner — the default <code>npx</code> launch
                  command needs no toolkit root.
                {/if}
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
            {#if resolvedMcpPathValid === false && useLocalCheckout}
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
      {:else if currentStep === "step4b"}
        <section class="wiz-section">
          <p class="wiz-desc">
            Install the Unity Open MCP <strong>agent skill</strong> so your AI
            client gets workflow guidance for the Unity MCP tools — the
            mutate→gate→fix loop, capabilities-first discovery, and the agent
            senses (tests, profiler, screenshots). This step is
            <strong>optional</strong>: skip it if you manage skills yourself.
          </p>
          <p class="wiz-hint">
            Two options write to the same project-relative skill folder(s) for
            the MCP client you picked on the Configure AI client step
            (<strong>{mcpClientLabel(mcpClient)}</strong>):
            <strong>Copy skill</strong> installs the template playbook (the same
            workflow guidance for every project), and
            <strong>Generate project skill</strong> produces a project-specific
            file that merges that playbook with this project's inventory (Unity
            version, installed packages, key MonoBehaviour / ScriptableObject
            types). Generate needs the built MCP server
            (<code>mcp-server/dist/index.js</code>); copy does not.
            Paths are derived from a single manifest
            (<code>skills/client-paths.json</code>), so ZCode writes
            <code>.agents/skills/</code> and Cursor writes
            <code>.cursor/skills/</code> — never an unconditional
            <code>.claude/skills/</code>.
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
            {:else if skillPlan.targets.length === 0}
              <p class="wiz-hint wiz-hint-warn">
                No skill folder is mapped for <strong>{mcpClientLabel(mcpClient)}</strong>.
                Use the **Skip** button to continue, or pick a different MCP client on the Configure AI client step.
              </p>
            {:else}
              <ul class="wiz-fingerprints" aria-label="Agent skill copy targets">
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
          {/if}

          {#if skillGenError}
            <div class="wiz-block wiz-block-error" role="alert">
              {describeGenerateSkillError(skillGenError)}
            </div>
          {/if}
          {#if skillGenResult}
            <div class="wiz-block wiz-block-ok" role="status">
              <strong>Project skill generated.</strong>
              Wrote {skillGenResult.targets.length} file(s) — Unity {skillGenResult.unityVersion}
              {#if skillGenResult.bridgeVersion}
                · bridge {skillGenResult.bridgeVersion}
              {/if}
              .
              <button
                type="button"
                class="wiz-link-button"
                onclick={() => (skillGenPreviewOpen = !skillGenPreviewOpen)}
              >
                {skillGenPreviewOpen ? "Hide" : "Show"} preview
              </button>
            </div>
            {#if skillGenPreviewOpen}
              <details class="wiz-preview" open>
                <summary>Generated skill preview</summary>
                <pre>{skillGenResult.inventoryPreview}</pre>
              </details>
            {/if}
          {/if}

          <div class="wiz-actions-row">
            <Button
              variant="primary"
              onclick={copySkillFilesClick}
              disabled={
                !skillPlan ||
                skillPlan.targets.length === 0 ||
                !skillPlan.sourcePath ||
                skillCopying ||
                (skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck)
              }
              title={
                skillPlan && skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck
                  ? "Confirm overwrite first"
                  : "Copy the template skill into the client's skill folder"
              }
            >
              {skillCopying ? "Copying…" : skillResult?.copied.length ? "Copy again" : "Copy skill"}
            </Button>
            <Button
              variant="secondary"
              onclick={generateProjectSkillClick}
              disabled={
                !canGenerateSkill() ||
                skillGenRunning ||
                (skillPlan?.targets.some((t) => t.exists) && !skillOverwriteAck)
              }
              title={
                skillPlan?.targets.some((t) => t.exists) && !skillOverwriteAck
                  ? "Confirm overwrite first"
                  : !canGenerateSkill()
                    ? "Build the MCP server (mcp-server/dist/index.js) first"
                    : "Generate a project-specific skill that merges the playbook with this project's inventory"
              }
            >
              {skillGenRunning ? "Generating…" : skillGenResult?.targets.length ? "Regenerate" : "Generate project skill"}
            </Button>
            <Button variant="secondary" onclick={skipSkillStep}>
              Skip
            </Button>
          </div>
        </section>
      {:else if currentStep === "step5"}
        <section class="wiz-section">
          <p class="wiz-desc">
            This step launches Unity with the bridge port pinned via
            <code>-UNITY_OPEN_MCP_BRIDGE_PORT={step5BridgePort ?? resolvedBridgePort ?? "auto"}</code>
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
            the live project state. Wizard form choices are saved per
            project, but step navigation always restarts at the preset
            picker when you reopen the wizard. Re-run or Clear AI Setup
            resets those saved choices. The Done screen always reflects the
            latest on-disk manifest. The
            bridge <code>/ping</code> row carries the launch + verify
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
                  {#if !detection.meetsMinUnityVersion}
                    <StatusChip tone="warn" label="below minimum" />
                  {:else if detection.meetsRecommendedUnityVersion}
                    <StatusChip tone="ok" label="Unity 6+" />
                  {:else}
                    <StatusChip tone="warn" label="supported (legacy toolbar)" />
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
                    {mcpConfiguredSummary(detection?.mcpConfigured ?? { cursor: false, claudeDesktop: false, opencodeGlobal: false, opencodeProject: false, zcodeGlobal: false, zcodeProject: false })}
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
              the batch criteria are met). Baseline creation is also
              deferred; the wizard renders only Steps 1–7 plus this Done screen.
            </p>
          </div>

          <div class="wiz-field">
            <span class="wiz-label">Skill copy</span>
            <p class="wiz-desc">
              {#if skillResult && skillResult.copied.length > 0}
                The wizard copied
                <code>{toolkitRoot || "<toolkit>"}/skills/unity-open-mcp/SKILL.md</code>
                into the project-relative skill folder(s) for your selected client ({skillResult.copied.map((t) => t.relativePath).join(", ")}).
              {:else}
                The wizard copies
                <code>{toolkitRoot || "<toolkit>"}/skills/unity-open-mcp/SKILL.md</code>
                into the project-relative skill folder(s) for your selected client
                ({#each skillPlan?.targets ?? [] as t, i}{#if i > 0}, {/if}<code>{t.relativePath}</code>{/each}{#if !skillPlan || skillPlan.targets.length === 0}<em>none for this client</em>{/if}).
                You can also do this from the dedicated **Agent skill** step.
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
              {:else if skillPlan.targets.length === 0}
                <p class="wiz-hint wiz-hint-warn">
                  No skill targets are mapped for the selected client. Pick a different MCP client on the Configure AI client step or use the Manual option to install into all known client folders.
                </p>
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
                    {skillCopying ? "Copying…" : skillResult?.copied.length ? "Copy again" : "Copy skill files"}
                  </Button>
                  <Button
                    variant="secondary"
                    onclick={generateProjectSkillClick}
                    disabled={
                      !canGenerateSkill() ||
                      skillGenRunning ||
                      (skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck)
                    }
                    title={
                      skillPlan.targets.some((t) => t.exists) && !skillOverwriteAck
                        ? "Confirm overwrite first"
                        : !canGenerateSkill()
                          ? "Build the MCP server (mcp-server/dist/index.js) first"
                          : "Generate a project-specific skill that merges the playbook with this project's inventory"
                    }
                  >
                    {skillGenRunning ? "Generating…" : skillGenResult?.targets.length ? "Regenerate skill" : "Generate project skill"}
                  </Button>
                </div>
              {/if}
            {/if}
            {#if skillGenError}
              <div class="wiz-block wiz-block-error" role="alert">
                {describeGenerateSkillError(skillGenError)}
              </div>
            {/if}
            {#if skillGenResult}
              <div class="wiz-block wiz-block-ok" role="status">
                <strong>Project skill generated.</strong>
                Wrote {skillGenResult.targets.length} file(s) — Unity {skillGenResult.unityVersion}
                {#if skillGenResult.bridgeVersion}
                  · bridge {skillGenResult.bridgeVersion}
                {/if}
                .
                <button
                  type="button"
                  class="wiz-link-button"
                  onclick={() => (skillGenPreviewOpen = !skillGenPreviewOpen)}
                >
                  {skillGenPreviewOpen ? "Hide" : "Show"} preview
                </button>
              </div>
              {#if skillGenResult.targets.length > 0}
                <ul class="wiz-fingerprints" aria-label="Generated skill files">
                  {#each skillGenResult.targets as t (t.absolutePath)}
                    <li class="wiz-fp wiz-fp-ok">
                      <span class="wiz-fp-name"><code>{t.relativePath}</code></span>
                      <span class="wiz-fp-status">{t.existed ? "overwritten" : "created"}</span>
                    </li>
                  {/each}
                </ul>
              {/if}
              {#if skillGenPreviewOpen}
                <details class="wiz-preview" open>
                  <summary>Generated skill preview</summary>
                  <pre>{skillGenResult.inventoryPreview}</pre>
                </details>
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
      <div class="wiz-footer-left">
        <span class="wiz-footer-progress">
          Step {currentStepIndex() + 1} of {STEP_ORDER.length} · {stepLabel(currentStep)}
        </span>
        <span class="wiz-footer-clear">
          <Button
            class="wiz-clear-btn"
            variant="secondary"
            onclick={onClearAiSetup}
            disabled={clearInProgress}
            title="Remove every AI-agent artifact the wizard wrote for this project (manifest entries, MCP client configs, skill files). Backups are created first."
          >
            {clearInProgress ? "Clearing…" : "Clear AI Setup"}
          </Button>
        </span>
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
    /* dvh accounts for the titleBarStyle "Overlay" drag region so the modal
     * never overhangs the visible viewport on constrained heights. */
    max-height: calc(100dvh - 3rem);
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
    grid-template-columns: repeat(7, 1fr);
    gap: 0.5rem;
    border-bottom: 1px solid var(--hub-border-light);
  }

  .wiz-seg {
    display: flex;
    align-items: flex-start;
    gap: 0.45rem;
    padding: 0.5rem 0.45rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    font-size: 0.74rem;
    color: var(--hub-text-muted);
    background: var(--hub-card);
    min-width: 0;
    min-height: 3.2rem;
    cursor: pointer;
    transition: border-color 0.12s ease, background 0.12s ease;
  }

  .wiz-seg:hover {
    border-color: var(--hub-border-hover);
  }

  .wiz-seg:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
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

  /* Green accent for a segment whose readiness check already holds.
     Uses --hub-success to match StatusChip's "ok" tone. */
  .wiz-seg-passing {
    border-color: var(--hub-success);
  }

  .wiz-seg-passing .wiz-seg-num {
    background: rgba(47, 111, 74, 0.28);
    border-color: var(--hub-success);
    color: var(--hub-text-bright);
  }

  .wiz-seg-current.wiz-seg-passing {
    border-color: var(--hub-success);
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
    flex: 1;
    min-width: 0;
    white-space: normal;
    line-height: 1.25;
    word-break: break-word;
  }

  .wiz-body {
    flex: 1;
    /* min-height: 0 lets this flex child shrink below its content height so
     * it scrolls instead of overflowing the shell and shoving the footer out
     * of view. Without it, the default min-height: auto prevents shrinking. */
    min-height: 0;
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

  .wiz-label-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .wiz-client-search {
    width: auto;
    min-width: 12rem;
  }

  .wiz-client-group {
    margin-top: 0.4rem;
  }

  .wiz-client-group-label {
    margin: 0 0 0.25rem;
    font-size: 0.68rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: var(--hub-text-muted);
    font-weight: 600;
    opacity: 0.85;
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
  .wiz-hint-info { color: #22d3ee; }
  .wiz-hint-muted {
    color: var(--hub-text-muted);
    font-size: 0.85em;
  }

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

  .wiz-extension-packs {
    list-style: none;
    padding: 0;
    margin: 0.5rem 0 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .wiz-extension-pack {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-card);
  }

  .wiz-extension-pack .wiz-toggle {
    align-items: flex-start;
  }

  .wiz-extension-pack .wiz-toggle small {
    display: block;
    color: var(--hub-text-muted);
    font-size: 0.72rem;
    margin-top: 0.15rem;
  }

  .wiz-extension-pack-entry {
    display: block;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 0.72rem;
    color: var(--hub-text);
    background: var(--hub-card-inset, rgba(0, 0, 0, 0.04));
    padding: 0.3rem 0.4rem;
    border-radius: 4px;
    word-break: break-all;
    user-select: all;
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

  /* Inline link-styled button used inside result blocks (e.g. "Show preview"). */
  .wiz-link-button {
    background: none;
    border: none;
    padding: 0;
    color: inherit;
    text-decoration: underline;
    cursor: pointer;
    font: inherit;
  }

  /* Collapsible preview of a generated artifact (skill markdown). */
  .wiz-preview {
    margin-top: 8px;
    border: 1px solid rgba(255, 255, 255, 0.12);
    border-radius: 6px;
    background: rgba(0, 0, 0, 0.25);
  }
  .wiz-preview summary {
    cursor: pointer;
    padding: 6px 10px;
    font-size: 0.85em;
    opacity: 0.85;
  }
  .wiz-preview pre {
    margin: 0;
    padding: 10px 12px;
    max-height: 320px;
    overflow: auto;
    font-size: 0.78em;
    line-height: 1.4;
    white-space: pre-wrap;
    word-break: break-word;
  }

  /* Amber soft warning — mirrors .wiz-tag-warn tokens. Never blocks;
   * used for "meets the supported minimum but below recommended". */
  .wiz-block-warn {
    border-color: rgba(251, 191, 36, 0.35);
    background: rgba(251, 191, 36, 0.08);
    color: #fbbf24;
  }

  .wiz-block-warn strong {
    color: #fde68a;
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
    /* In-flow flex child of .wiz-shell. Now that .wiz-body has min-height: 0
     * it shrinks and scrolls, so this footer sits naturally at the shell's
     * bottom as a pinned action bar — no sticky needed (sticky had no effect
     * here anyway: the footer is a sibling of the scroll container, not a
     * descendant, so it had no sticky scroll context). */
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    padding: 0.7rem 1.25rem;
    border-top: 1px solid var(--hub-border-light);
    background: var(--hub-bg);
    border-radius: 0 0 12px 12px;
  }

  .wiz-footer-left {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    min-width: 0;
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

  /* Destructive "Clear AI Setup" — muted gray so it does not attract
     attention. The footer Back/Next/Finish group stays in the accent
     family; this button is for users who know they want it. */
  .wiz-footer-clear {
    display: flex;
    align-items: center;
  }

  :global(.btn.wiz-clear-btn) {
    border-color: var(--hub-border-light) !important;
    color: var(--hub-text-dim) !important;
    background: var(--hub-card) !important;
  }

  :global(.btn.wiz-clear-btn:hover:not(:disabled)) {
    border-color: var(--hub-border-hover) !important;
    color: var(--hub-text) !important;
    background: var(--hub-selected) !important;
  }

  /* --- Step 0 — preset picker ----------------------------------------- */
  .wiz-preset-grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
  }

  .wiz-preset {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    padding: 0.6rem 0.7rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 8px;
    background: var(--hub-card);
    color: var(--hub-text);
    text-align: left;
    cursor: pointer;
    font: inherit;
    transition: border-color 0.12s ease, background 0.12s ease;
  }

  .wiz-preset:hover {
    border-color: var(--hub-border-hover);
  }

  .wiz-preset:focus-visible {
    outline: 2px solid var(--hub-accent);
    outline-offset: 1px;
  }

  .wiz-preset-selected {
    border-color: var(--hub-accent);
    background: rgba(92, 124, 250, 0.1);
  }

  .wiz-preset-recommended {
    border-color: var(--hub-success);
  }

  .wiz-preset-head {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .wiz-preset-head strong {
    font-size: 0.88rem;
    color: var(--hub-text-bright);
  }

  .wiz-preset-badge {
    font-size: 0.62rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    font-weight: 600;
    padding: 0.05rem 0.4rem;
    border-radius: 999px;
    color: #bbf7d0;
    background: rgba(74, 222, 128, 0.16);
    border: 1px solid rgba(74, 222, 128, 0.35);
  }

  .wiz-preset-desc {
    margin: 0;
    font-size: 0.78rem;
    line-height: 1.4;
    color: var(--hub-text-muted);
  }

  .wiz-preset-note {
    font-size: 0.7rem;
    color: var(--hub-text-dim);
    line-height: 1.35;
  }

  /* --- Step 1 — diagnostics panel ------------------------------------- */
  .wiz-diag-block {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--hub-border-light);
    border-radius: 6px;
    background: var(--hub-card);
  }

  .wiz-diag-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .wiz-diag {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .wiz-diag-row {
    display: grid;
    grid-template-columns: auto 1fr auto;
    grid-template-rows: auto auto;
    align-items: baseline;
    gap: 0 0.4rem;
    padding: 0.3rem 0.35rem;
    border-radius: 4px;
    font-size: 0.8rem;
    color: var(--hub-text);
    background: var(--hub-bg);
  }

  .wiz-diag-row .wiz-diag-fix {
    grid-column: 2 / span 2;
    grid-row: 2;
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    margin-top: 0.1rem;
  }

  .wiz-diag-icon {
    font-weight: 700;
    width: 1rem;
    text-align: center;
  }

  .wiz-diag-ok .wiz-diag-icon { color: #4ade80; }
  .wiz-diag-warn .wiz-diag-icon { color: var(--hub-error-fg); }
  .wiz-diag-warn { background: rgba(248, 113, 113, 0.06); }
  .wiz-diag-muted .wiz-diag-icon { color: var(--hub-text-dim); }

  .wiz-diag-detail {
    font-size: 0.72rem;
    color: var(--hub-text-muted);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  }

  @media (max-width: 720px) {
    .wiz-progress { grid-template-columns: repeat(4, 1fr); }
    .wiz-preset-grid { grid-template-columns: 1fr; }
    .wiz-radio-grid { grid-template-columns: 1fr; }
    .wiz-footer { flex-wrap: wrap; }
    .wiz-footer-clear { width: 100%; justify-content: flex-start; }
  }
</style>
