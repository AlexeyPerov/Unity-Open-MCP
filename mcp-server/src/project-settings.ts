// TS-side reader for `.unity-open-mcp/settings.json`.
//
// The same settings file is already read by C# on the Unity side
// (`VerifyProjectSettings.cs`, `BridgeProjectSettings.cs`) for verify + bridge
// tunables. Those readers use `JsonUtility`, which silently IGNORES unknown
// keys — so a TS-read slice in the same file does not conflict. This module is
// the TS-side mirror: it reads keys that are consumed entirely inside the MCP
// server (Node.js) by tools that deliberately do NOT depend on the bridge.
//
// The motivating consumer is `resource_pressure`, a server-side fd probe whose
// ceiling must be overridable for runtimes whose internal limit differs from
// Mono's classic 1024 (e.g. Unity 6 / CoreCLR). Because `resource_pressure`
// runs server-side and the bridge is the thing that dies on fd-exhaustion, the
// ceiling CANNOT be resolved through the bridge — it must be read here.
//
// Robustness mirrors the C# pattern: missing file / section / invalid value →
// defaults, never throws.

import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";

import { STATUS_DIR_NAME } from "./constants.js";
import { FD_CEILING_DEFAULT } from "./process-diagnostics.js";

/** File name inside the `.unity-open-mcp/` directory. */
const SETTINGS_FILE_NAME = "settings.json";

/** Below this configured ceiling the value is rejected → default. */
export const FD_CEILING_MIN = 64;
/** Above this configured ceiling the value is clamped down. */
export const FD_CEILING_MAX = 1_000_000;

/**
 * The `resourcePressure` slice. `JsonUtility` on the C# side ignores it; this
 * module is the only reader. Optional end-to-end so a missing slice falls back
 * to the default ceiling.
 */
export interface ResourcePressureSettings {
  fdCeiling?: number;
}

interface ProjectSettingsFile {
  resourcePressure?: ResourcePressureSettings;
}

/** Result of resolving the fd ceiling for a project. */
export interface FdCeilingResolution {
  /** The fd ceiling to apply (already clamped into range). */
  ceiling: number;
  /** `"config"` when a valid in-range value was read; `"default"` otherwise. */
  source: "default" | "config";
}

/**
 * Resolve the settings file path for a project root. Exported for tests.
 * `resolve` (not `join`) anchors a RELATIVE project path against the server's
 * cwd instead of producing a relative path that would be re-interpreted by
 * later read calls — matching how every other project-rooted path is resolved.
 */
export function settingsFilePath(projectPath: string): string {
  return resolve(projectPath, STATUS_DIR_NAME, SETTINGS_FILE_NAME);
}

/**
 * Clamp a configured ceiling following the C# `ClampBatchExecuteMaxCommands`
 * pattern: below the min → default (a nonsensically-low ceiling is treated as
 * "not configured"); above the max → the max. An in-range value is kept as-is.
 * The `source` stays `"config"` whenever a valid number was supplied (even if
 * clamped to the max), and drops to `"default"` only when the value is absent,
 * non-numeric, or below the min.
 */
function resolveConfigured(
  raw: number | undefined,
): { ceiling: number; source: "default" | "config" } {
  if (typeof raw !== "number" || !Number.isFinite(raw)) {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  if (raw < FD_CEILING_MIN) {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  if (raw > FD_CEILING_MAX) {
    return { ceiling: FD_CEILING_MAX, source: "config" };
  }
  return { ceiling: Math.floor(raw), source: "config" };
}

/**
 * Read the fd ceiling for a project from `.unity-open-mcp/settings.json`.
 * Never throws: missing file, unparseable JSON, missing slice, or an invalid
 * value all fall back to {@link FD_CEILING_DEFAULT} with `source: "default"`.
 *
 * Reads fresh on every call — `resource_pressure` is invoked infrequently
 * (after heavy automation), and fresh reads let an operator change the setting
 * without restarting the MCP server.
 */
export function readFdCeiling(projectPath: string): FdCeilingResolution {
  let path: string;
  try {
    path = settingsFilePath(projectPath);
  } catch {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  if (!existsSync(path)) {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  let parsed: ProjectSettingsFile;
  try {
    parsed = JSON.parse(raw) as ProjectSettingsFile;
  } catch {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  if (!parsed || typeof parsed !== "object") {
    return { ceiling: FD_CEILING_DEFAULT, source: "default" };
  }
  return resolveConfigured(parsed.resourcePressure?.fdCeiling);
}
