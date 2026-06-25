/**
 * Tauri-backed {@link ActionBackend} (phase-2 deliverable).
 *
 * Bridges the engine-neutral action runner (`@validation-suite/core` →
 * actions) to the Rust command surface (`backend.ts`). The runner stays
 * free of Tauri imports; this adapter is the only place that maps each
 * `ActionBackend` op to a `#[tauri::command]`. All side effects + path
 * sandboxing live in Rust; the adapter only shuttles params + results.
 *
 * Manifest entries recorded by `fs_*` ops are already project-relative
 * forward-slash strings (the Rust side relativizes), so they round-trip
 * through `saveStepManifest` unchanged.
 */

import type {
  ActionBackend,
  ActionContext,
  ActionResult,
  FsCopyArgs,
  FsDeleteArgs,
  FsPatchArgs,
  ManifestRef,
  McpToolArgs,
  StepManifest,
} from "@validation-suite/core";
import type { EngineProfile } from "@validation-suite/core";
import * as backend from "./backend.ts";

/**
 * Build an {@link ActionBackend} for the active project + profile. The
 * context is captured at construction (project root + fixture root +
 * profile); the runner passes a fresh context per step, but the Tauri
 * commands re-resolve the active project from app state on each call, so
 * the captured context is only used for the values the core needs
 * (placeholder expansion).
 */
export function tauriBackend(): ActionBackend {
  return {
    async fsCopy(args: FsCopyArgs, _ctx: ActionContext): Promise<ActionResult> {
      return backend.fsCopyAction(args.from, args.to);
    },

    async fsPatch(args: FsPatchArgs, _ctx: ActionContext): Promise<ActionResult> {
      return backend.fsPatchAction(
        args.path,
        args.patches,
        args.snapshotOverride !== undefined ? args.snapshotOverride : null,
      );
    },

    async fsDelete(args: FsDeleteArgs, _ctx: ActionContext): Promise<ActionResult> {
      return backend.fsDeleteAction(args.paths);
    },

    async mcpTool(args: McpToolArgs, _ctx: ActionContext): Promise<ActionResult> {
      return backend.mcpToolAction(args.tool, args.args ?? null, args.timeoutMs ?? null);
    },

    async saveManifest(manifest: StepManifest): Promise<ManifestRef> {
      return backend.saveStepManifest(manifest.scenarioId, manifest.stepId, manifest.entries);
    },

    async loadManifest(id: ManifestRef): Promise<StepManifest | null> {
      if (!id) return null;
      return backend.loadStepManifest(id);
    },

    async deleteManifest(id: ManifestRef): Promise<void> {
      if (!id) return;
      await backend.deleteStepManifest(id);
    },
  };
}

/**
 * Build an {@link ActionContext} for the runner: absolute project root +
 * resolved fixture root + the active profile. The fixture root is
 * resolved from the profile pattern by the backend (`resolve_fixture_root`).
 */
export async function buildContext(
  projectRoot: string,
  profile: EngineProfile,
  scenarioId: string,
): Promise<ActionContext> {
  const fixtureRel = await backend.resolveFixtureRoot(scenarioId);
  const fixtureRoot = joinPaths(projectRoot, fixtureRel);
  return { projectRoot, fixtureRoot, profile };
}

/** Join two path segments with a single separator (platform-agnostic). */
function joinPaths(a: string, b: string): string {
  if (!a) return b;
  if (!b) return a;
  const sep = a.includes("/") || b.includes("/") ? "/" : "/";
  const left = a.endsWith(sep) ? a.slice(0, -1) : a;
  const right = b.startsWith(sep) ? b.slice(1) : b;
  return `${left}${sep}${right}`;
}
