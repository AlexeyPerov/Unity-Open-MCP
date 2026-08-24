import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS, BRIDGE_MIN_TIMEOUT_MS, BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS } from "../constants.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, CONFIRM_BYPASS_BASE, makeTool } from "./schema-fragments.js";

export const executeMenu = makeTool(
  "unity_open_mcp_execute_menu",
  "Execute a Unity Editor menu item. Runs on Unity's main thread and blocks " +
    "until the menu returns, so a legitimately slow menu needs an explicit " +
    "`timeout_ms` (e.g. `Assets/Refresh` after an AssetPostprocessor version " +
    `bump reimports the whole content tree) — raised up to the ${BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS} ms ` +
    "transport cap, which is the hard ceiling this schema declares, NOT an " +
    "arbitrary value: a menu that needs longer cannot be waited out in one " +
    "call (poll unity_open_mcp_editor_status instead). A `timeout` response does NOT " +
    "mean the menu failed — it usually completed after the wait elapsed; " +
    "confirm with unity_open_mcp_editor_status or an asset probe rather than " +
    "retrying, because a blind retry of an authoring menu can double-write " +
    "assets. Prefer non-interactive APIs over menus that open a MODAL dialog " +
    "(material/scene converters, importer settings prompts): a modal blocks " +
    "the main thread for as long as it is open, wedging this and every " +
    "subsequent call, and no tool can dismiss it — an operator must close it " +
    "in the Unity UI.",
  {
    required: ["menu_path"],
        properties: {
          menu_path: {
            type: "string",
            description: "e.g. Assets/Refresh, File/Save Project",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — asset paths the menu is likely to touch. REQUIRED for a mutating menu path even with gate: \"off\" (it is the declared scope recorded in the audit trail, not only the gate's validation scope); read-only menu paths waive it. There is no whole-project fallback: name the paths you expect to change, e.g. [\"Assets/Prefabs\"]." },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default a disruptive op " + "(recompile / scene switch / menu that can disrupt the editor) is " + "refused with scene_dirty when any loaded scene has unsaved changes, " + "so Unity's native save modal never interrupts the flow. Set true to " + "proceed and accept the risk of a native save prompt." },
          confirm_bypass: { ...CONFIRM_BYPASS_BASE, description: "Bypass the deny heuristic for destructive menu paths " + "(File/Quit, File/Exit, Assets/Reimport All). Requires gate: \"off\" " + "as well — both flags must be set. The bypass is audited." },
          gate: { ...GATE_PROP },
          timeout_ms: {
            type: "integer",
            default: BRIDGE_DEFAULT_TIMEOUT_MS,
            minimum: BRIDGE_MIN_TIMEOUT_MS,
            maximum: BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS,
            description:
              "How long to wait for the menu to return. Same semantics and " +
              `hard cap (${BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS} ms) as ` +
              "execute_csharp: values above a typical MCP host request " +
              "timeout (~60s) are unreachable because the host aborts the " +
              "tools/call first, so the route clamps server-side and reports " +
              "the clamp. Raise it for a genuinely slow menu " +
              "(`Assets/Refresh` after an importer version bump reimports " +
              "the whole content tree). If the menu needs longer than the " +
              "cap, do NOT retry on timeout — the menu is still running; poll " +
              "unity_open_mcp_editor_status until it settles. Default 30000.",
          },
        },
  },
);
