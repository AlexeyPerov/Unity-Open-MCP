# Unity Open MCP — UI (uGUI) Extension

Skill for AI agents driving Unity uGUI (Canvas / elements / layout groups /
element modify) in a project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in UI module (`Canvas` / `CanvasScaler` / `GraphicRaycaster` / `Image` /
> `Text` / `Button` / `Slider` / `Toggle` / `InputField` / layout groups /
> `EventSystem` from `UnityEngine.UI` + `UnityEngine.EventSystems`) — no Unity
> package install is required, and they compile into every bridge build. Its
> tool group is **hidden** from `ListTools` until the connected session
> activates it. TextMesh Pro (`TMP_Text`) is **optional** and detected at call
> time — when absent, `ui_element_add` with `element_type=TMP_Text` returns a
> structured `tmp_package_required` error (no silent legacy-`Text` fallback).

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `ui` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="ui")` before invoking
  any UI tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.
  Because uGUI is
  built-in, `capabilities` always reports the `ui` group as `available: true`
  (no `domainDefine`).

## Tool prefix

One prefix, four tools, all in the `ui` group:

- `unity_open_mcp_ui_canvas_add` — add a `Canvas` (+ `CanvasScaler` +
  `GraphicRaycaster`) to a host GameObject or as a new scene root, and ensure
  an `EventSystem` exists in the open scene(s). **Mutating**.
- `unity_open_mcp_ui_element_add` — add a uGUI element (`Text` / `TMP_Text` if
  TMP is present / `Image` / `Button` / `Slider` / `Toggle` / `InputField`) as
  a child of a parent RectTransform. **Mutating**.
- `unity_open_mcp_ui_layout_group_add` — add a `HorizontalLayoutGroup` /
  `VerticalLayoutGroup` / `GridLayoutGroup` to a parent. **Mutating**.
- `unity_open_mcp_ui_element_modify` — typed patch on a uGUI component
  (Selectables, Graphic `color` / `raycastTarget`, LayoutElement preferred
  sizes, `Text` / `TMP_Text` text). **Mutating**.

Every mutating tool requires a non-empty `paths_hint` scoped to the host /
new-root / parent scene path — the gate has no whole-project fallback.

## Canonical workflow: a button

1. **Create the canvas** — `unity_open_mcp_ui_canvas_add` with
   `render_mode: "ScreenSpaceOverlay"`, `sorting_order: 0`. When no host is
   addressed, a new scene root is created (controlled by `new_root_name`,
   defaults to `"Canvas"`). The tool ensures `CanvasScaler` +
   `GraphicRaycaster` are attached and that an `EventSystem` exists in the
   open scene(s) — these are the standard uGUI companions, so you do not need
   to add them by hand. Idempotent — re-using an existing Canvas reports
   `added:false` (companions are still ensured).
2. **Add the button** — `unity_open_mcp_ui_element_add` with
   `parent_path` pointing at the Canvas, `element_type: "Button"`,
   `element_name: "StartButton"`. The tool attaches an `Image` (background)
   and a `Button` (the Selectable). Pass `color` (`r,g,b,a` 0-1) and
   `sprite_path` (`Assets/.../*.png` Sprite) to skin the background.
3. **Add a label** — `ui_element_add` with `parent_path` pointing at the
   button GameObject, `element_type: "Text"` (or `"TMP_Text"` when TextMesh Pro
   is installed), `text: "Start"`, `color: "1,1,1,1"`.
4. **Tune via modify** — `ui_element_modify` to set typed fields:
   `component_type: "Button"`, `fields_json: "[{\"field\":\"interactable\",\"value\":true}]"`.

## TextMesh Pro (TMP_Text)

`element_type: "TMP_Text"` requires the TextMesh Pro package
(`com.unity.textmeshpro` on Unity 5.x–2023; `unity.textmeshpro` on Unity 6+).
The tools detect TMP at call time via reflection — they compile regardless. When
TMP is absent:

- `ui_element_add` with `element_type: "TMP_Text"` returns a `tmp_package_required`
  error. **The tools do not silently fall back to legacy `UnityEngine.UI.Text`** —
  a Text fallback would silently change the rendering and font pipeline.
- Install TMP via the Package Manager (or `unity_open_mcp_package_add` with
  `package_id: "com.unity.textmeshpro"`), then retry.

`ui_element_modify` accepts `component_type: "TMP_Text"` — when TMP is absent,
the tool returns `component_not_found` (same as any missing component).

## Element types

`ui_element_add` supports these `element_type` values:

| `element_type` | Components attached | Notes |
|---|---|---|
| `Text` | `Text` | Legacy uGUI text. Always available. |
| `TMP_Text` | `TextMeshProUGUI` | Requires TextMesh Pro. `tmp_package_required` when absent. |
| `Image` | `Image` | Plain graphic. `sprite_path` + `color` skin it. |
| `Button` | `Image` + `Button` | Background Image + Selectable. |
| `Slider` | `Slider` | Bare component — wire `fillRect` / `handleRect` via `ui_element_modify`. |
| `Toggle` | `Image` + `Toggle` | Background Image + Selectable; `isOn` defaults to `true`. |
| `InputField` | `InputField` | Bare component — wire `textComponent` / `placeholder` via `ui_element_modify`. |

## Layout groups

`ui_layout_group_add` adds one of:

- `HorizontalLayoutGroup` — stacks children left-to-right.
- `VerticalLayoutGroup` — stacks children top-to-bottom.
- `GridLayoutGroup` — snaps children to a grid (cell size controlled via
  `ui_element_modify`).

Common optional fields: `padding` (`left,right,top,bottom` ints),
`spacing` (`x,y` — grid uses both; HV uses `x` only), `child_alignment`
(`TextAnchor` name, default `UpperLeft`), `child_control_width` /
`child_control_height` (default `true`), `child_force_expand_width` /
`child_force_expand_height` (default `true`). The HV-only flags are ignored on
`GridLayoutGroup`. Idempotent — re-using an existing group of the same type
reports `added:false`.

## Element modify

`ui_element_modify` mirrors `component_modify`'s shape but is scoped to uGUI
types so the value conversion is purpose-built for the common fields:

- `component_type`: `Text` | `TMP_Text` | `Image` | `Button` | `Slider` |
  `Toggle` | `InputField` | `Canvas` | `CanvasScaler` | `GraphicRaycaster` |
  `HorizontalLayoutGroup` | `VerticalLayoutGroup` | `GridLayoutGroup` |
  `LayoutElement` | `Selectable` (shared base for Button / Slider / Toggle).
- `fields_json`: a JSON array of `{ "field", "value", "type"? }` entries.
  `type` is `int` | `float` | `bool` | `string` | `color` (`r,g,b,a` 0-1) |
  `vector` (`x,y` or `x,y,z`); when omitted, it is inferred from the current
  value. Unknown fields are reported as errors and the tool fails atomically —
  no partial writes.

### Common modify recipes

- Button disabled: `component_type: "Button"`, `fields_json:
  "[{\"field\":\"interactable\",\"value\":false,\"type\":\"bool\"}]"`.
- LayoutElement preferred size: `component_type: "LayoutElement"`,
  `fields_json: "[{\"field\":\"preferredWidth\",\"value\":200,\"type\":\"float\"},
  {\"field\":\"preferredHeight\",\"value\":60,\"type\":\"float\"}]"`.
- Graphic color: `component_type: "Image"`, `fields_json:
  "[{\"field\":\"color\",\"value\":\"1,0,0,1\",\"type\":\"color\"}]"`.
- Text content: `component_type: "Text"`, `fields_json:
  "[{\"field\":\"text\",\"value\":\"Hello\",\"type\":\"string\"}]"`.
- Slider range: `component_type: "Slider"`, `fields_json:
  "[{\"field\":\"minValue\",\"value\":0,\"type\":\"float\"},
  {\"field\":\"maxValue\",\"value\":100,\"type\":\"float\"},
  {\"field\":\"wholeNumbers\",\"value\":true,\"type\":\"bool\"}]"`.

## Common recipes

### Full screen-space overlay HUD

1. `ui_canvas_add` with `render_mode: "ScreenSpaceOverlay"`, `sorting_order: 10`.
2. `ui_layout_group_add` on the Canvas with `layout_type: "VerticalLayoutGroup"`,
   `child_alignment: "UpperLeft"`, `padding: "10,10,10,10"`,
   `spacing: "4,4"`.
3. Per line: `ui_element_add` with `element_type: "Text"`, `text: "<label>"`,
   `color: "1,1,1,1"`.

### World-space canvas (interactable sign)

1. `ui_canvas_add` on a 3D plane GameObject with `render_mode: "WorldSpace"`.
2. `ui_element_modify` with `component_type: "Canvas"`, `fields_json:
   "[{\"field\":\"worldCamera\",\"value\":null}]"` — or set it via
   `object_modify` after the agent resolves the camera reference. (UI pointer
   interaction on world-space canvases requires the camera + an EventSystem.)
3. `ui_element_add` for the sign's children.

### Check TMP presence before requesting TMP_Text

1. `unity_open_mcp_package_check` with `package_id: "com.unity.textmeshpro"` (or
   `unity.textmeshpro` on Unity 6).
2. If absent and you want crisp SDF text, `package_add` it first. Otherwise use
   `element_type: "Text"` (legacy).

## Agent-sense pairing

- `unity_senses_screenshot` (view: `"game"`) visually confirms the canvas
  renders in the Game view. Pair with a Play Mode check to verify Selectables
  receive pointer events (requires the ensured `EventSystem`).
- `unity_open_mcp_component_get` reads the raw uGUI component fields after a
  mutate (complementary to the structured element state returned by the UI
  tools).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `ui_canvas_add` | yes | editor_settle | Idempotent — re-using reports `added:false`. Ensures CanvasScaler + GraphicRaycaster + EventSystem. |
| `ui_element_add` | yes | editor_settle | Parent must exist. `TMP_Text` requires TextMesh Pro. |
| `ui_layout_group_add` | yes | editor_settle | Idempotent per type. HV-only flags ignored on GridLayoutGroup. |
| `ui_element_modify` | yes | editor_settle | Typed field patch; atomic on conversion failure. |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). For `ui_element_add`, address the **parent**
by `parent_instance_id` > `parent_path` > `parent_name` — the parent must
exist (uGUI elements live under a Canvas in the hierarchy). Every mutating
tool requires a non-empty `paths_hint` scoped to the host / new-root / parent
scene path — the gate has no whole-project fallback.
