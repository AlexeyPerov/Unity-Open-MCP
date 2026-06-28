# UI (uGUI) — embedded domain tools

UI (uGUI) typed tools (`unity_open_mcp_ui_*`), embedded inside the bridge.
Four tools cover the Canvas / element / layout-group / element-modify layer:

- `ui_canvas_add` — add a `Canvas` (+ `CanvasScaler` + `GraphicRaycaster`) to a
  host GameObject or as a new scene root, and ensure an `EventSystem` exists.
  Sets `render_mode` (ScreenSpaceOverlay / ScreenSpaceCamera / WorldSpace) and
  `sorting_order`.
- `ui_element_add` — add a uGUI element (`Text` / `TMP_Text` if TMP is present /
  `Image` / `Button` / `Slider` / `Toggle` / `InputField`) as a child of a
  parent RectTransform. Common fields: `text`, `color` (r,g,b,a 0-1),
  `sprite_path`.
- `ui_layout_group_add` — add a `HorizontalLayoutGroup` / `VerticalLayoutGroup`
  / `GridLayoutGroup` to a parent. Optional padding, spacing, child alignment,
  child control / force-expand flags.
- `ui_element_modify` — typed patch on a uGUI component (Selectables, Graphic
  color / raycastTarget, LayoutElement preferred sizes, Text/TMP_Text text).
  Mirrors `component_modify` shape, typed to uGUI types.

Added in M20 Plan 3 to close the UI parity gap with the competitor (AnkleBreaker
ships a full UI category).

## Compile gate

**None.** The `Canvas`, `CanvasScaler`, `GraphicRaycaster`, `Image`, `Text`,
`Button`, `Slider`, `Toggle`, `InputField`, layout groups, and `EventSystem`
types live in the built-in UI module (`UnityEngine.UI` /
`UnityEngine.EventSystems`) and are present in every Unity install, so this
domain ships ungated — no `UNITY_OPEN_MCP_EXT_UI` define and no sub-asmdef
`defineConstraints`. The owning sub-asmdef only references the bridge Editor
asmdef.

### TextMesh Pro (optional, runtime-detected)

TextMesh Pro (`com.unity.textmeshpro` / `unity.textmeshpro` on Unity 6) is
**optional**. The tools compile regardless (no compile-time TMP reference). When
an agent requests `element_type=TMP_Text` and TMP is absent, the tool returns a
structured `tmp_package_required` error — it does **not** silently fall back to
legacy `UnityEngine.UI.Text`. The presence check is a soft runtime reflection
(`TMPro.TMP_Text` / `TMPro.TextMeshProUGUI` resolved per call), so no
compile-gate define is needed.

## Tool group

All four tools belong to the `ui` group (M20 Plan 3 / T20.3.2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
