// A create-type call whose response a domain reload closed.
//
// The bridge answers `editor_reloading` with `dispatched: true` when a call ran
// but a domain reload started before its response was written (a compile the
// project had due, an asset import that requested one). For a call that
// creates an asset that error invites a retry, and a retry of a create is not
// idempotent: it fails on the existing file, or makes a second one. The asset
// path the call creates is in its arguments, so the server can tell whether it
// committed: the file was absent before the call and is on disk after it.
//
// Committed → the call's answer is a success (`status: "ok"`, `committed:
// true`, the created paths) that says the reload closed the response, so the
// tool's own result fields are missing. Not on disk → the error stays, and its
// steps say the asset was not created (safe to retry once the Editor settles).

import { existsSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { waitCall } from "./editor-state.js";

/** Create-type tools and the argument holding the asset path(s) each one creates. */
export const CREATED_ASSET_ARGS: Readonly<Record<string, string>> = {
  unity_open_mcp_scene_create: "path",
  unity_open_mcp_prefab_create: "prefab_asset_path",
  unity_open_mcp_material_create: "asset_path",
  unity_open_mcp_scriptableobject_create: "asset_path",
  unity_open_mcp_inputsystem_asset_create: "asset_path",
  unity_open_mcp_shader_graph_create: "asset_path",
  unity_open_mcp_spriteatlas_create: "asset_path",
  unity_open_mcp_timeline_create: "asset_path",
  unity_open_mcp_tilemap_create_rule_tile: "asset_path",
  unity_open_mcp_tilemap_create_tile_asset: "asset_path",
  unity_open_mcp_animation_create: "asset_paths",
  unity_open_mcp_animator_create: "asset_paths",
};

/** The project-relative asset paths a create-type call targets ([] for any other call). */
export function createdAssetPaths(toolName: string, args: Record<string, unknown>): string[] {
  const key = CREATED_ASSET_ARGS[toolName];
  if (!key) return [];
  const value = args[key];
  const list = Array.isArray(value) ? value : [value];
  return list
    .filter((p): p is string => typeof p === "string" && p.trim().length > 0)
    .map(p => p.trim().replace(/\\/g, "/"));
}

/**
 * Which of `paths` exist under the project (absolute paths are taken as they
 * are). A path rooted at neither Assets/ nor Packages/ is also looked up under
 * Assets/, where tools that take a bare `Scenes/Foo.unity` put it.
 */
export function existingPaths(projectPath: string, paths: string[], exists: (p: string) => boolean = existsSync): Set<string> {
  const candidates = (p: string): string[] => isAbsolute(p) ? [p]
    : /^(Assets|Packages)\//.test(p) ? [join(projectPath, p)]
    : [join(projectPath, p), join(projectPath, "Assets", p)];
  return new Set(paths.filter(p => candidates(p).some(exists)));
}

/**
 * The answer for a create-type call the bridge reports as cut off by a reload
 * (`editor_reloading`, `dispatched: true`), given which target paths existed
 * before the call and which exist now. Null when the result is not that case,
 * or when a target existed before the call (its presence proves nothing).
 */
export function confirmCreatedAfterReload(
  toolName: string,
  body: Record<string, any>,
  paths: string[],
  before: Set<string>,
  after: Set<string>,
): { body: Record<string, unknown>; isError: boolean } | null {
  if (paths.length === 0 || body?.error?.code !== "editor_reloading" || body.dispatched !== true) return null;
  if (paths.some(p => before.has(p))) return null;
  const generation = typeof body.compile?.generationAtStart === "number" ? body.compile.generationAtStart as number : undefined;
  const wait = waitCall("idle", generation);
  const name = toolName.replace(/^unity_open_mcp_/, "");
  const missing = paths.filter(p => !after.has(p));
  if (missing.length === 0) {
    return {
      body: {
        status: "ok",
        committed: true,
        created: paths,
        reloadClosedResponse: true,
        headline: `${name} committed: ${paths.join(", ")} ${paths.length === 1 ? "exists" : "exist"} on disk. ` +
          "A domain reload started before the bridge wrote the response, so the tool's own result fields are missing.",
        agentNextSteps: [
          `Do NOT repeat ${name}: the asset is created, and a second call would fail on it or create another.`,
          `Wait for the Editor to settle with ${wait}, then read the asset if you need what the response would have said.`,
        ],
        _route: { route: "live", outcome: "committed_before_reload" },
      },
      isError: false,
    };
  }
  return {
    body: {
      ...body,
      created: paths.filter(p => after.has(p)),
      notCreated: missing,
      agentNextSteps: [
        `${name} did not create ${missing.join(", ")} (not on disk). Wait for the Editor to settle with ${wait}, then call it again.`,
      ],
    },
    isError: true,
  };
}

/** Apply {@link confirmCreatedAfterReload} to a call result; the result itself when it does not apply. */
export function withCreatedCheck(
  result: CallToolResult,
  body: Record<string, any>,
  toolName: string,
  paths: string[],
  before: Set<string>,
  after: Set<string>,
  replaceBody: (result: CallToolResult, body: Record<string, unknown>) => CallToolResult,
): CallToolResult {
  const confirmed = confirmCreatedAfterReload(toolName, body, paths, before, after);
  return confirmed ? { ...replaceBody(result, confirmed.body), isError: confirmed.isError } : result;
}
