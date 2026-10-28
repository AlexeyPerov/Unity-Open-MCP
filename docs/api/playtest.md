# Playtest tools

Activate the `playtest` group (with `input-simulation` to drive input —
`manage_tools(action: "activate_for", intent: "playtest …")` brings both). These
tools let an agent read and play the running game the way a tester does: read
what the screen says, tap a control by its label, and check the result.

| Tool | Does | Compiles when |
|---|---|---|
| `unity_open_mcp_ui_snapshot` | Lists the uGUI on screen as a flat accessibility tree with text, visibility and diagnostics | `com.unity.ugui` present; TextMeshPro text when TextMeshPro is installed |

Label targeting — `target_text` on `inputsim_pointer`, `inputsim_touch` and
`inputsim_gesture` keyframes — uses the same screen model; see
[Target by label](input-simulation.md#target-by-label).

`inputsim_probe` stays the cheap interactables-only listing; `ui_snapshot` is the
full screen read.

## `unity_open_mcp_ui_snapshot`

Read-only and gate-free; works in edit mode and play mode. uGUI only (UI Toolkit
runtime panels are not listed).

```json
{
  "status": "ok", "mode": "play", "profile": "compact",
  "screen": { "w": 2422, "h": 1483, "safe_area": [0, 0, 2422, 1483], "dpi": 217 },
  "textSources": ["ugui", "tmp"], "occlusion": "play_mode_raycast",
  "visibleOnly": true, "scanned": 49, "total": 14,
  "nodes": [
    { "id": "44804", "path": "SnapshotCanvas/Shop/Btn_01", "role": "button", "text": "Buy",
      "rect": [40, 1173, 220, 80], "visible": true, "visibility": "on_screen", "interactable": true },
    { "id": "44772", "path": "SnapshotCanvas/Shop/Btn_03", "role": "button", "text": "Sell",
      "rect": [540, 1073, 220, 80], "visible": true, "visibility": "on_screen", "interactable": true,
      "occluded_by": "SnapshotCanvas/Toast/Label" }
  ],
  "diagnostics": { "overflow": { "count": 1, "items": [ … ], "truncated": 0 },
                   "zero_size_interactables": { "count": 1, "items": [ … ], "truncated": 0 } },
  "pagination": { "page_size": 50, "cursor": null, "next_cursor": null, "truncated": 0 }
}
```

### Nodes

Every active `RectTransform` under a root `Canvas` is a node, listed in hierarchy
order (scene order, then sibling order).

| Field | Meaning |
|---|---|
| `id` | Instance id as a string — pass it to `inputsim_pointer.object_id` |
| `path` | Hierarchy path (plain names, as `target` accepts them; duplicates are told apart by `id`) |
| `role` | `button` (Button, Dropdown, other Selectables, click handlers), `toggle`, `slider` (Slider, Scrollbar), `input` (InputField, TextMeshPro input field), `scroll` (ScrollRect), `text`, `image`, `container` |
| `text` | Display text: rich-text tags removed, whitespace collapsed. A control's text is its **label**: the texts of its descendants that are not inside a nested control |
| `rect` | `[x, y, w, h]` in Screen pixels, bottom-left origin — the space of `Input.mousePosition` and `inputsim_*` `screen_x`/`screen_y` |
| `visible`, `visibility` | see below; `visible` is `visibility == "on_screen"` |
| `interactable` | Controls and scroll views: `Selectable` state through the `CanvasGroup` chain |
| `occluded_by` | Play mode with an EventSystem: the front-most raycast hit covering a raycast-target node at its tap point. Absent/`null` when nothing covers it or the node is not a raycast target there |
| `sprite`, `depth` | `balanced` / `full`: the Image sprite name; hierarchy depth below the root canvas |
| `alpha`, `owner`, `textSource` | `full`: effective alpha, the control a label belongs to, `ugui` / `tmp` |

Text comes from `UnityEngine.UI.Text` always and from TextMeshPro (`TMP_Text`)
when it is installed — the standalone `com.unity.textmeshpro` 3.x package or
uGUI 2.x, which ships TextMeshPro. `textSources` says which readers are compiled
in. Disabled text components are not read.

### Visibility

| `visibility` | When |
|---|---|
| `zero_size` | The screen rect is narrower or shorter than half a pixel |
| `transparent` | Effective alpha ≈ 0: the `CanvasGroup` alpha chain (`ignoreParentGroups` restarts it), a disabled `Canvas` above, or a text/image whose own color alpha is 0 |
| `offscreen` | The rect does not intersect the screen, or is behind the canvas camera |
| `clipped` | On screen but fully outside an enclosing `RectMask2D` / `Mask` (an override-sorting canvas stops clipping from above, as in uGUI) |
| `on_screen` | At least partly visible |

The screen is the main Game view's pixel size. `safe_area` is `Screen.safeArea`
when the Editor reports one that fits the Game view, otherwise the whole view.
`dpi` is `Screen.dpi`.

### Profiles, filters and paging

| `profile` | Nodes | Keys |
|---|---|---|
| `compact` (default) | visible nodes that have text or are controls / scroll views; labels folded into their control | null keys omitted |
| `balanced` | every visible node except folded labels (adds images, containers) | + `sprite`, `depth` |
| `full` | every active node in any visibility state | every key, nulls explicit |

`visible_only` defaults to `true` for `compact` / `balanced` and `false` for
`full`; pass it to override (e.g. `compact` + `visible_only: false` lists hidden
text and controls too). Filters: `text_contains` (case-insensitive, normalised),
`role` (comma-separated list), `under` (a hierarchy path or trailing path suffix;
unknown → `under_not_found`). `page_size` (default 50, max 200) and `cursor`
page the node list; `pagination.next_cursor` resumes.

Measured on a 200-node canvas: `compact` ≈ 2.7k tokens (47 visible text/control
nodes), `balanced` ≈ 5.3k (114 visible nodes), `full` ≈ 14k for all 200 nodes at
`page_size: 200` (≈ 4k per default 50-node page). `capabilities.costHints`
carries these figures.

### Diagnostics

Computed over the whole `under` scope and returned on the first page only. Each
list is `{ count, items (≤ 20), truncated }`; items carry `id`, `path`,
`visibility`.

| Key | Reported | Flags |
|---|---|---|
| `overflow` | always | Text whose content exceeds its rect: TextMeshPro preferred height (wrapping at the text area) or width (no wrapping) above the area, or `isTextOverflowing`; `UI.Text` preferred height above the rect, or preferred width with horizontal overflow. Items add `text`, `source`, `size`, `content`. Best-fit `UI.Text` is not checked |
| `zero_size_interactables` | always | Controls with a zero-size rect |
| `placeholder` | only with `placeholder_pattern` | Texts matching the regex (.NET, case-sensitive) — e.g. unresolved localisation keys such as `^[*#{].*[*#}]$`. There is no built-in pattern |
| `small_touch_targets` | only with `min_touch_size` | Visible controls whose rect is narrower or shorter than `min_touch_size` screen pixels; reports `dpi` so the caller can convert from dp (`px = dp × dpi / 160`) |

## Snapshot → tap by label

1. `ui_snapshot` — read the screen (compact).
2. `inputsim_pointer` / `inputsim_touch` with `target_text: "Buy"` (plus `index`
   or `under` when the label repeats) — or `inputsim_gesture` keyframes with
   `coords: "target"` and `target_text`.
3. `inputsim_step`, then `ui_snapshot` or `unity_senses_screenshot` to confirm.

## Errors

| Code | When |
|---|---|
| `invalid_arguments` | Unknown `profile` / `role`, invalid `placeholder_pattern`, `min_touch_size` ≤ 0 |
| `under_not_found` | No node path matches `under` |
