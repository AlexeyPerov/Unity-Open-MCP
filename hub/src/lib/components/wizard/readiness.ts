import {
  BRIDGE_PACKAGE_ID,
  VERIFY_PACKAGE_ID,
} from "../../services/manifest.ts";
import type {
  BridgeStatusKind,
  ManifestMergePlan,
  NodeProbe,
  ProjectState,
  ToolkitValidation,
} from "../../services/config.ts";
import { mcpHeuristicAny } from "./diagnostics.ts";
import type { McpLaunchSourceMode } from "./launch_mode.ts";
import type { StepId } from "./constants.ts";

/** Inputs that gate Step 1 (project detection readiness). */
export interface ProjectReadyInput {
  detection: ProjectState | null;
  nodeProbe: NodeProbe | null;
}

/** Step 1 is the environment gate: a valid Unity project that meets the
 *  minimum version, has a writable manifest, and Node.js ≥18 available. */
export function isProjectReady(input: ProjectReadyInput): boolean {
  const { detection, nodeProbe } = input;
  if (!detection) return false;
  if (!detection.isValidUnityProject) return false;
  if (!detection.meetsMinUnityVersion) return false;
  if (!detection.manifestWritable) return false;
  if (!nodeProbe?.ok) return false;
  return true;
}

/** Inputs that gate Step 2 (MCP server source readiness). */
export interface McpSourceReadyInput {
  useLocalCheckout: boolean;
  toolkitValidation: ToolkitValidation | null;
}

/** Step 2 only configures the MCP-server launch source. The default `npx`
 *  path needs nothing; the local-checkout path needs a validated toolkit root.
 *  Unity version / Node / manifest-writable checks live on Step 1. */
export function isMcpSourceReady(input: McpSourceReadyInput): boolean {
  if (input.useLocalCheckout && !input.toolkitValidation?.ok) return false;
  return true;
}

/** Inputs that gate Step 2 under the Plan 2 exclusive-mode model. */
export interface McpSourceModeReadyInput {
  /** The exclusive launch-source mode selected on Step 2. */
  sourceMode: McpLaunchSourceMode;
  /** Validated toolkit root (only required for local + custom modes). */
  toolkitValidation: ToolkitValidation | null;
}

/** Step 2 readiness under the exclusive-mode model. `npx` and `global` need
 *  nothing; `local` and `custom` both require a validated toolkit root so the
 *  custom-entrypoint path cannot bypass checks. Pure given the inputs. */
export function isMcpSourceModeReady(input: McpSourceModeReadyInput): boolean {
  if (input.sourceMode === "npx" || input.sourceMode === "global") return true;
  return !!input.toolkitValidation?.ok;
}

/** Inputs that gate Step 3 (manifest readiness). */
export interface ManifestReadyInput {
  installBridge: boolean;
  installVerify: boolean;
  /** Selected Unity domain dependency UPM ids. */
  selectedUnityDomainDeps: Set<string>;
  mergePlan: ManifestMergePlan | null;
}

/** Whether the Step 3 selection can advance. A Unity-dep-only selection (no
 *  bridge/verify) is a valid flow when the selected deps are already present
 *  in the manifest. Allows Next whenever the selected packages already exist
 *  (unchanged or upgrade) — the upgrade ack only gates the Install action. */
export function isManifestReady(input: ManifestReadyInput): boolean {
  const { installBridge, installVerify, selectedUnityDomainDeps, mergePlan } =
    input;
  const depIds = [...selectedUnityDomainDeps];
  if (!installBridge && !installVerify && depIds.length === 0) return false;
  if (!mergePlan) return false;
  if (mergePlan.manifestRead.parseError) return false;
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

/** Inputs that determine whether a step's progress segment is "passing". */
export interface StepPassingInput {
  detection: ProjectState | null;
  nodeProbe: NodeProbe | null;
  useLocalCheckout: boolean;
  toolkitValidation: ToolkitValidation | null;
  installBridge: boolean;
  installVerify: boolean;
  selectedUnityDomainDeps: Set<string>;
  mergePlan: ManifestMergePlan | null;
  step5BridgeStatus: BridgeStatusKind;
}

/** Whether a step's readiness check already holds, so its progress segment
 *  gets a green accent. Pure given the live state. */
export function stepPassing(
  id: StepId,
  input: StepPassingInput,
): boolean {
  switch (id) {
    case "step0":
      // Preset picker has no readiness check — always neutral.
      return false;
    case "step1":
      return isProjectReady({
        detection: input.detection,
        nodeProbe: input.nodeProbe,
      });
    case "step2":
      return isMcpSourceReady({
        useLocalCheckout: input.useLocalCheckout,
        toolkitValidation: input.toolkitValidation,
      });
    case "step3": {
      // The progress strip highlights green when the bridge + verify packages
      // are already installed (the merge-plan-based gate is a navigation gate,
      // not a "passing" highlight). This matches the pre-refactor behavior.
      return (
        !!input.detection &&
        input.detection.bridgeInstalled &&
        input.detection.verifyInstalled
      );
    }
    case "step4": {
      const h = input.detection?.mcpConfigured;
      return !!h && mcpHeuristicAny(h);
    }
    case "step4b":
      return !!input.detection?.anySkillInstalled;
    case "step5":
      return input.step5BridgeStatus.kind === "ok";
    case "done":
      return false;
  }
}

/** Inputs to the already-configured short-circuit check. */
export interface AlreadyConfiguredInput {
  detection: ProjectState | null;
}

/** Whether a project is already fully configured on wizard open: bridge +
 *  verify packages installed AND an MCP client config already written. When
 *  true, the Preflight step offers a "You're ready" banner that jumps past
 *  the Apply steps straight to Verify. Skill state is not part of the gate —
 *  the skill step is optional. Pure given the live detection. */
export function isAlreadyConfigured(input: AlreadyConfiguredInput): boolean {
  const d = input.detection;
  if (!d) return false;
  if (!d.bridgeInstalled || !d.verifyInstalled) return false;
  return !!d.mcpConfigured && mcpHeuristicAny(d.mcpConfigured);
}
