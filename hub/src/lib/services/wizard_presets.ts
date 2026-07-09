/**
 * Wizard setup presets — named bundles of pre-filled wizard form values.
 *
 * Each preset is a starting point, not a lock: the user can still override
 * any toggle on later steps. Selecting a preset on Step 1 hydrates the
 * relevant fields on Steps 2–6; re-selecting on Back navigation re-applies
 * the preset's values (full re-apply on preset change).
 *
 * Every value maps onto a field that already exists in the wizard form
 * state — presets never introduce new fields. Where a preset's documented
 * behavior depends on a control that lives outside the wizard (e.g. token
 * auth + remote bind for the Secure / remote preset, which are bridge-side
 * concerns), the preset surfaces a helper note instead of pretending to
 * set a field the wizard does not own.
 */

import type { McpClientId } from "./ai_toolkit";
import type { AiSetupWizardFormState } from "./ai_setup_wizard_draft";

/** Preset identifier. `"custom"` is the skip / no-pre-fill entry. */
export type PresetId =
  | "custom"
  | "regular-npm"
  | "contributor"
  | "team-ci"
  | "secure-remote";

/** The subset of wizard form fields a preset may pre-fill. Omitted keys
 *  are left untouched when the preset is applied. */
export interface WizardPresetValues {
  /** Step 2 — MCP server source. */
  useLocalCheckout?: boolean;
  useGlobalInstall?: boolean;
  /** Step 3 — Unity packages. */
  useLocalPackages?: boolean;
  installBridge?: boolean;
  installVerify?: boolean;
  selectedUnityDomainDeps?: string[];
  /** Step 4 — MCP client. */
  mcpClient?: McpClientId;
  /** Advisory: when `false`, the wizard auto-skips the Agent skill step
   *  (Team CI agents typically don't need a desktop skill file). */
  skillEnabled?: boolean;
}

/** A single named preset entry in the catalog. */
export interface WizardPreset {
  id: PresetId;
  /** Card title. */
  label: string;
  /** One-line description shown under the title. */
  description: string;
  /** Expanded summary of the pre-filled choices (card tooltip). */
  tooltip: string;
  /** Highlights the recommended default card. */
  recommended?: boolean;
  /** Display tier. `"primary"` presets render in the first viewport grid;
   *  `"more"` presets are demoted behind a "More presets" disclosure so they
   *  stay reachable without competing with the common-path choices. Niche
   *  presets (Team CI, Secure / remote) opt into `"more"`. Defaults to
   *  `"primary"`. */
  tier?: "primary" | "more";
  /** Pre-filled values applied when the user picks this preset. */
  values: WizardPresetValues;
}

/** The full preset catalog. Order is the display order on Step 1. */
export const WIZARD_PRESETS: readonly WizardPreset[] = [
  {
    id: "regular-npm",
    label: "Regular user (npm)",
    description:
      "Published npm package via npx. No monorepo checkout needed — the fastest standard path.",
    tooltip:
      "MCP server: npx -y unity-open-mcp@latest. Packages: bridge + verify from published sources, domain deps off. Client: not pre-selected. Skill: on. Launch: standard verify, no auth.",
    recommended: true,
    values: {
      useLocalCheckout: false,
      useGlobalInstall: false,
      useLocalPackages: false,
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: [],
      skillEnabled: true,
    },
  },
  {
    id: "contributor",
    label: "Contributor (local checkout)",
    description:
      "Hack on bridge / verify / MCP server locally. Uses file: packages from a cloned monorepo.",
    tooltip:
      "MCP server: local checkout (build mcp-server first). Packages: bridge + verify via file: URLs from the checkout, domain deps off. Client: not pre-selected. Skill: on. See the development setup guide.",
    values: {
      useLocalCheckout: true,
      useGlobalInstall: false,
      useLocalPackages: true,
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: [],
      skillEnabled: true,
    },
  },
  {
    id: "team-ci",
    label: "Team CI",
    description:
      "Headless automation: global install, manual client config, no desktop skill, token auth on the bridge.",
    tier: "more",
    tooltip:
      "MCP server: global npm install (stable path for CI images). Packages: bridge + verify, domain deps off. Client: manual / CLI snippet. Skill: skipped. Configure token auth on the bridge for CI.",
    values: {
      useLocalCheckout: false,
      useGlobalInstall: true,
      useLocalPackages: false,
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: [],
      mcpClient: "manual",
      skillEnabled: false,
    },
  },
  {
    id: "secure-remote",
    label: "Secure / remote",
    description:
      "Remote-bridge mode for non-localhost access. Configure token auth + restricted tool groups on the bridge.",
    tier: "more",
    tooltip:
      "MCP server: npx or global per environment. Packages: bridge + verify, domain deps off. Skill: on. Token auth, remote bind, and restricted tool groups are bridge-side controls — configure them from the bridge window after onboarding.",
    values: {
      useLocalCheckout: false,
      useGlobalInstall: false,
      useLocalPackages: false,
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: [],
      skillEnabled: true,
    },
  },
  {
    id: "custom",
    label: "Custom / skip",
    description:
      "No pre-fills — configure every step manually. Identical to the wizard's built-in defaults.",
    tooltip:
      "Skip the preset picker and walk the wizard with its default toggles. You can still change any field on later steps.",
    values: {},
  },
];

/** Look up a preset by id. Falls back to `"custom"` for unknown / empty ids
 *  so a stale persisted `selectedPresetId` never breaks the wizard. */
export function presetById(id: PresetId | string | undefined): WizardPreset {
  if (!id) return customPreset();
  const found = WIZARD_PRESETS.find((p) => p.id === id);
  return found ?? customPreset();
}

/** The Custom / skip preset (no pre-fills). */
export function customPreset(): WizardPreset {
  return WIZARD_PRESETS[WIZARD_PRESETS.length - 1];
}

/** Convert a preset's pre-fill values into a partial wizard form state.
 *  Only keys the preset explicitly sets are included; the caller merges
 *  them onto the current form. `skillEnabled` is advisory and does not
 *  appear in the form state — the wizard reads it off the preset directly
 *  to decide whether to auto-skip the Agent skill step. */
export function applyPresetToForm(
  preset: WizardPreset,
): Partial<AiSetupWizardFormState> {
  const v = preset.values;
  const out: Partial<AiSetupWizardFormState> = {};
  if (v.useLocalCheckout !== undefined) out.useLocalCheckout = v.useLocalCheckout;
  if (v.useGlobalInstall !== undefined) out.useGlobalInstall = v.useGlobalInstall;
  if (v.useLocalPackages !== undefined) out.useLocalPackages = v.useLocalPackages;
  if (v.installBridge !== undefined) out.installBridge = v.installBridge;
  if (v.installVerify !== undefined) out.installVerify = v.installVerify;
  if (v.selectedUnityDomainDeps !== undefined) {
    out.selectedUnityDomainDeps = [...v.selectedUnityDomainDeps];
  }
  if (v.mcpClient !== undefined) out.mcpClient = v.mcpClient;
  return out;
}
