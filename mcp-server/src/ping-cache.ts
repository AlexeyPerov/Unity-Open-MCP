export interface PingSnapshot {
  connected: boolean;
  projectPath: string | null;
  unityVersion: string | null;
  bridgeVersion: string;
  mode: string;
  compiling: boolean;
  isPlaying: boolean;
  asOf: string;
}

export class PingCache {
  private snapshot: PingSnapshot | null = null;

  record(body: Omit<PingSnapshot, "asOf">): void {
    this.snapshot = { ...body, asOf: new Date().toISOString() };
  }

  get(): PingSnapshot | null {
    return this.snapshot;
  }
}
