import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { executeCsharp } from "./execute-csharp.js";
import { invokeMethod } from "./invoke-method.js";
import { executeMenu } from "./execute-menu.js";
import { findMembers } from "./find-members.js";
import { compileCheck } from "./compile-check.js";
import { editorStatus } from "./editor-status.js";
import { validateEdit } from "./validate-edit.js";
import { checkpointCreate } from "./checkpoint-create.js";
import { delta } from "./delta.js";
import { findReferences } from "./find-references.js";
import { scanPaths } from "./scan-paths.js";
import { applyFix } from "./apply-fix.js";
import { scanAll } from "./scan-all.js";
import { baselineCreate } from "./baseline-create.js";
import { regressionCheck } from "./regression-check.js";
import { reserialize } from "./reserialize.js";
import { readAsset } from "./read-asset.js";
import { searchAssets } from "./search-assets.js";
import { listAssets } from "./list-assets.js";
import { runTests } from "./run-tests.js";
import { screenshot } from "./screenshot.js";
import { readConsole } from "./read-console.js";
import { profilerCapture } from "./profiler-capture.js";
import { profilerMemory } from "./profiler-memory.js";
import { profilerRendering } from "./profiler-rendering.js";
import { spatialQuery } from "./spatial-query.js";
import { agentCapabilities } from "./agent-capabilities.js";
import { generateSkill } from "./generate-skill.js";
import { listRules } from "./list-rules.js";
import { pullEvents } from "./pull-events.js";
import { readCompileErrors } from "./read-compile-errors.js";
// M16 Plan 1 — typed project & asset management tools.
import { assetsCreateFolder } from "./assets-create-folder.js";
import { assetsCopy } from "./assets-copy.js";
import { assetsMove } from "./assets-move.js";
import { assetsDelete } from "./assets-delete.js";
import { assetsRefresh } from "./assets-refresh.js";
import { materialCreate } from "./material-create.js";
import { materialGetProperties } from "./material-get-properties.js";
import { materialSetProperty } from "./material-set-property.js";
import { materialGetKeywords } from "./material-get-keywords.js";
import { materialSetKeyword } from "./material-set-keyword.js";
import { materialSetShader } from "./material-set-shader.js";
import { shaderListAll } from "./shader-list-all.js";
import { shaderGetData } from "./shader-get-data.js";
import { prefabInstantiate } from "./prefab-instantiate.js";
import { prefabCreate } from "./prefab-create.js";
import { prefabOpen } from "./prefab-open.js";
import { prefabClose } from "./prefab-close.js";
import { prefabSave } from "./prefab-save.js";
import { prefabApply } from "./prefab-apply.js";
import { prefabRevert } from "./prefab-revert.js";
import { prefabUnpack } from "./prefab-unpack.js";
import { prefabGetOverrides } from "./prefab-get-overrides.js";
import { prefabStatus } from "./prefab-status.js";

export const M2_TOOLS: Tool[] = [
  ping,
  executeCsharp,
  invokeMethod,
  executeMenu,
  findMembers,
  compileCheck,
];

export const M2_5_TOOLS: Tool[] = [editorStatus];

export const M3_TOOLS: Tool[] = [validateEdit, checkpointCreate, delta, findReferences, scanPaths, applyFix];

export const M5_TOOLS: Tool[] = [scanAll, baselineCreate, regressionCheck];

export const M9_TOOLS: Tool[] = [reserialize, readAsset, searchAssets, listAssets];

export const M10_TOOLS: Tool[] = [
  runTests,
  screenshot,
  readConsole,
  profilerCapture,
  profilerMemory,
  profilerRendering,
  spatialQuery,
];

export const M11_TOOLS: Tool[] = [agentCapabilities, generateSkill];

export const M12_TOOLS: Tool[] = [listRules];

export const M13_TOOLS: Tool[] = [pullEvents];

// Offline Editor.log compiler-error reader. Routed offline (no bridge, no
// Unity spawn) — the one channel that works when the bridge assembly itself
// has failed to compile.
export const M14_TOOLS: Tool[] = [readCompileErrors];

// M16 Plan 1 — Project & Asset Management typed tools. Mutating members run
// the full gate path with `paths_hint`; read-only members (shader reads,
// material property/keyword reads, prefab status/overrides) are gate-free.
export const M16_PLAN1_TOOLS: Tool[] = [
  assetsCreateFolder,
  assetsCopy,
  assetsMove,
  assetsDelete,
  assetsRefresh,
  materialCreate,
  materialGetProperties,
  materialSetProperty,
  materialGetKeywords,
  materialSetKeyword,
  materialSetShader,
  shaderListAll,
  shaderGetData,
  prefabInstantiate,
  prefabCreate,
  prefabOpen,
  prefabClose,
  prefabSave,
  prefabApply,
  prefabRevert,
  prefabUnpack,
  prefabGetOverrides,
  prefabStatus,
];

export const ALL_TOOLS: Tool[] = [
  ...M2_TOOLS,
  ...M2_5_TOOLS,
  ...M3_TOOLS,
  ...M5_TOOLS,
  ...M9_TOOLS,
  ...M10_TOOLS,
  ...M11_TOOLS,
  ...M12_TOOLS,
  ...M13_TOOLS,
  ...M14_TOOLS,
  ...M16_PLAN1_TOOLS,
];
