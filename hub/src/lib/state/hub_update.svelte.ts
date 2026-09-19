import {
  applyHubUpdate,
  checkHubUpdate,
  setHubUpdateNotice,
  type HubUpdateApplyResult,
  type HubUpdateRelease,
  type HubUpdateState,
  type HubUpdateStatus,
} from "$lib/services/hub_update";

class HubUpdateStore {
  state = $state<HubUpdateState>("idle");
  status = $state<HubUpdateStatus | null>(null);
  applying = $state(false);
  applyResult = $state<HubUpdateApplyResult | null>(null);
  error = $state<string | null>(null);

  async check(force = false): Promise<void> {
    if (this.state === "checking") return;
    this.state = "checking";
    this.error = null;
    try {
      const status = await checkHubUpdate(force);
      this.status = status;
      this.state = status.state;
      if (status.state === "error" && status.message) {
        this.error = status.message;
      }
    } catch (error) {
      this.state = "error";
      this.error = error instanceof Error ? error.message : String(error);
    }
  }

  async suppress(action: "dismiss" | "remindLater"): Promise<void> {
    this.error = null;
    try {
      const status = await setHubUpdateNotice(action);
      this.status = status;
      this.state = status.state;
    } catch (error) {
      this.error = error instanceof Error ? error.message : String(error);
    }
  }

  async apply(release: HubUpdateRelease): Promise<void> {
    if (this.applying) return;
    this.applying = true;
    this.applyResult = null;
    this.error = null;
    try {
      this.applyResult = await applyHubUpdate(release);
    } catch (error) {
      this.error = error instanceof Error ? error.message : String(error);
    } finally {
      this.applying = false;
    }
  }

  clearApplyResult(): void {
    this.applyResult = null;
  }
}

export const hubUpdateStore = new HubUpdateStore();
