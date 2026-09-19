import { invoke } from "@tauri-apps/api/core";

export interface HubUpdateRelease {
  version: string;
  tagName: string;
  releaseNotesUrl: string;
  assetName: string;
  assetUrl: string;
}

export type HubUpdateState =
  | "idle"
  | "checking"
  | "skipped"
  | "throttled"
  | "upToDate"
  | "available"
  | "suppressed"
  | "error";

export interface HubUpdateStatus {
  state: Exclude<HubUpdateState, "idle" | "checking">;
  runningVersion: string;
  checkedAtEpoch: number;
  nextCheckAtEpoch: number;
  manualDownloadUrl: string;
  release?: HubUpdateRelease;
  message?: string;
}

export interface HubUpdateApplyResult {
  installerPath: string;
  installerLaunched: boolean;
  manualDownloadUrl: string;
  message: string;
}

export function checkHubUpdate(force = false): Promise<HubUpdateStatus> {
  return invoke<HubUpdateStatus>("check_hub_update", { force });
}

export function setHubUpdateNotice(
  action: "dismiss" | "remindLater"
): Promise<HubUpdateStatus> {
  return invoke<HubUpdateStatus>("set_hub_update_notice", { action });
}

export function applyHubUpdate(
  release: HubUpdateRelease
): Promise<HubUpdateApplyResult> {
  return invoke<HubUpdateApplyResult>("apply_hub_update", { release });
}

export function relaunchHub(): Promise<void> {
  return invoke<void>("relaunch_hub");
}
