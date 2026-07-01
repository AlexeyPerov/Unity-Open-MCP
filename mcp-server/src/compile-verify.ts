// Compile-verify failure-code detection.
//
// Implements the `compile_noop` + `dll_stale` machine-readable failure codes
// from the reliability taxonomy. These surface when a compile-reload tool
// (assets_refresh / RequestScriptCompilation via execute_csharp / script_write
// / asmdef_* / package_*) reports success but the compiled state did not
// actually advance — the single most common silent-failure mode in the
// compile-verify loop.
//
// Both codes are PURE decisions over snapshots taken before + after the
// suspected compile. The LiveClient captures the before-snapshot (bridge tool
// inventory count + ScriptAssemblies DLL mtimes), runs the compile-reload
// tool, waits for settle, then calls detectCompileVerify() with the
// after-snapshot. When flagged, the result is annotated with a
// `_compileVerify` field (additive — never blocks a successful response) so an
// agent can branch instead of trusting a no-op success.
//
// The other compile-verify codes are emitted elsewhere:
//   - editor_instance_locked — batch-spawn.ts (M22 Plan 3)
//   - bridge_compile_failed  — live-client.ts deadBridgeResult (M20)
//   - compile_timeout        — live-client.ts waitForCompile (M20)

/**
 * A compile-verify snapshot. The `bridgeToolCount` is the size of the bridge's
 * `GET /tools` inventory (KnownTools ∪ BridgeToolRegistry); `dllMtimeMs` is the
 * newest mtime of the project's `Library/ScriptAssemblies/*.dll` set (or
 * undefined when the DLLs can't be stat'd). Either field may be undefined when
 * the snapshot could not be captured — the detector degrades gracefully.
 */
export interface CompileVerifySnapshot {
  /** Count of tools the bridge compiled in, or undefined when unreachable. */
  bridgeToolCount?: number;
  /**
   * Newest mtime (epoch ms) of Library/ScriptAssemblies/*.dll, or undefined.
   * Captured from the MCP server side via fs.statSync.
   */
  dllMtimeMs?: number;
}

/**
 * The source-edit mtime to compare DLL mtimes against. Typically the mtime of
 * the .cs file the agent just edited, or the newest file under a local
 * `packages/` source tree. Undefined when not tracking a specific edit.
 */
export interface CompileVerifyInput {
  before: CompileVerifySnapshot;
  after: CompileVerifySnapshot;
  /** Mtime (epoch ms) of the source edit the compile should have picked up. */
  sourceMtimeMs?: number;
}

export type CompileVerifyCode = "compile_noop" | "dll_stale";

export interface CompileVerifyResult {
  /** The detected code, or null when the compile advanced normally. */
  code: CompileVerifyCode | null;
  /** One-sentence agent-facing recovery hint, or null when code is null. */
  recommendation: string | null;
}

/**
 * The agent-facing recommendation strings. Kept clean of internal IDs/specs
 * paths (AGENTS.md §"No internal references in user-visible surfaces").
 */
export const COMPILE_VERIFY_RECOMMENDATIONS: Readonly<Record<CompileVerifyCode, string>> = {
  compile_noop:
    "The recompile reported success but the bridge tool registry and " +
    "ScriptAssemblies DLL mtimes did not advance — Unity performed an " +
    "incremental no-op. Do not trust the success result alone. Force a " +
    "rebuild (e.g. a no-op package_add/package_remove to nudge UPM " +
    "resolution, or operator refocus of the Editor), then re-check the " +
    "DLL mtime and the bridge tool inventory.",
  dll_stale:
    "After the recompile, Library/ScriptAssemblies/*.dll is still older " +
    "than the source edit — the new code was not compiled in. This is " +
    "expected for local packages/ source (outside Unity's Assets/ watch " +
    "root). Trigger a recompile via a no-op package_add/package_remove " +
    "or operator refocus, then verify DLL mtime > source mtime before " +
    "trusting the build.",
};

/**
 * Detect a compile no-op or stale-DLL condition from before/after snapshots.
 *
 * Decision order:
 *   1. `dll_stale` — the DLL mtime is older than the source edit mtime (the
 *      compile provably did not pick up the edit). Highest-confidence signal.
 *   2. `compile_noop` — the bridge tool inventory count is unchanged AND the
 *      DLL mtime did not advance across the before/after snapshots. Indicates
 *      an incremental no-op even when a specific source mtime isn't known.
 *   3. `null` — the compile advanced (count changed, or DLL mtime advanced, or
 *      insufficient signal to say otherwise).
 *
 * Gracefully degrades when snapshots are partial: if neither DLL mtimes nor
 * counts are available, returns null (no false positive). When only one axis
 * is available, it decides on that axis alone.
 */
export function detectCompileVerify(input: CompileVerifyInput): CompileVerifyResult {
  const { before, after, sourceMtimeMs } = input;

  // 1. dll_stale — highest confidence: the DLL is provably older than the edit.
  if (
    sourceMtimeMs !== undefined &&
    after.dllMtimeMs !== undefined &&
    after.dllMtimeMs < sourceMtimeMs
  ) {
    return {
      code: "dll_stale",
      recommendation: COMPILE_VERIFY_RECOMMENDATIONS.dll_stale,
    };
  }

  // 2. compile_noop — registry count unchanged AND dll mtime did not advance.
  //    Requires both before + after snapshots to have been captured; without a
  //    baseline we cannot claim a no-op.
  const countUnchanged =
    before.bridgeToolCount !== undefined &&
    after.bridgeToolCount !== undefined &&
    before.bridgeToolCount === after.bridgeToolCount;
  const dllDidNotAdvance =
    before.dllMtimeMs !== undefined &&
    after.dllMtimeMs !== undefined &&
    after.dllMtimeMs <= before.dllMtimeMs;

  if (countUnchanged && dllDidNotAdvance) {
    return {
      code: "compile_noop",
      recommendation: COMPILE_VERIFY_RECOMMENDATIONS.compile_noop,
    };
  }

  return { code: null, recommendation: null };
}

/**
 * Build the additive `_compileVerify` annotation for a CallToolResult body.
 * Returns `null` when there is nothing to annotate (code is null), so the
 * caller can spread it conditionally without polluting the clean-success path.
 */
export function buildCompileVerifyAnnotation(
  result: CompileVerifyResult,
): { code: CompileVerifyCode; recommendation: string } | null {
  if (result.code === null || result.recommendation === null) return null;
  return { code: result.code, recommendation: result.recommendation };
}
