# Playtest tools

Activate the `playtest` group (with `input-simulation` to drive input —
`manage_tools(action: "activate_for", intent: "playtest …")` brings both). These
tools let an agent read and play the running game the way a tester does: put the
game on a target device or resolution, read what the screen says, tap a control
by its label, and check the result.

| Tool | Does | Compiles when |
|---|---|---|
| `unity_open_mcp_ui_snapshot` | Lists the uGUI on screen as a flat accessibility tree with text, visibility and diagnostics | `com.unity.ugui` present; TextMeshPro text when TextMeshPro is installed |
| `unity_open_mcp_game_view` | Reads or sets the Game view resolution or a Device Simulator device + orientation, and reports the screen, safe area and notch insets the game sees | always (Unity 6; other Editors answer `unsupported_editor_version`) |

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

The screen is the main Game view's pixel size (the simulated device screen when
the Device Simulator is active). `safe_area` is the safe area the game sees: the
Device Simulator's for the selected device and orientation, otherwise the whole
view (the Game view has no safe area). `dpi` is `Screen.dpi`.

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

## `unity_open_mcp_game_view`

Puts the game on the resolution or device a check needs. Unity 6 only: the Game
view and the Device Simulator are internal Editor types, reached through one
version-checked adapter; on another Editor every action answers
`unsupported_editor_version`, and a missing internal member answers
`editor_internals_unavailable` naming it.

It is an Editor UI change, not a project asset mutation, so it is gate-free. The
**only persistent effect** is a custom Game view size entry named
`Open MCP <w>x<h>` in the Editor's Game view size list (stored in the Editor
preferences, so other projects on the machine see it too): it is created the
first time that size is requested, reused by name afterwards, and never deleted.
Nothing is restored implicitly — call `get` first and set the old state back
explicitly (`set_resolution` with `size_label` restores a size such as
"Free Aspect"; `set_resolution` also switches the Simulator back to the Game view).

| `action` | Arguments | Does |
|---|---|---|
| `get` | — | Reports the current view and what the game sees |
| `set_resolution` | `width` + `height` (16–8192 px), or `size_label` | Selects the fixed size through the `Open MCP <w>x<h>` entry, or an existing entry by its label (unknown → `size_not_found` with the labels). Swaps the Simulator tab back to the Game view when needed |
| `set_device` | `device`, `orientation` (default `portrait`) | The only action that opens the Device Simulator: swaps the Game view tab for the Simulator (or opens one), selects the device and rotates it. Input tools never open the Simulator themselves |
| `list_devices` | `query`, `page_size` (default 25, max 100), `cursor` | The Simulator's device catalog |

Both set actions bring the Game view / Simulator tab to the front inside Unity
(`brought_to_front`): a docked tab that is not in front does not render, and the
game then falls back to 640×480. The Unity application itself is not raised.

`device` is an id from `list_devices` (exact, case-insensitive) or a substring
that matches exactly one device; several matches answer `ambiguous_device` with
`candidates`, none `device_not_found`. Rotating the device changes the game's
orientation only when the project allows it (Default Orientation /
auto-rotation, or `Screen.orientation` set by game code) — as on a phone;
`orientation_applied: false` plus a `note` say when the game stayed in another
orientation.

```json
{
  "status": "ok", "action": "set_device", "mode": "simulator", "playing": true, "paused": false,
  "focused": true,
  "window": { "logical": [1211, 762.5], "pixels": [2422, 1525], "backing_scale": 2 },
  "device": { "id": "iOS Notch Device (1170x2532)", "physical": [1170, 2532], "dpi": 460, "rotation": 0, "screens": 1 },
  "scale": 0.27, "render_texture": [1170, 2532],
  "screen": { "width": 1170, "height": 2532, "aspect": "195:422",
              "safe_area": [0, 102, 1170, 2289], "insets": { "top": 141, "bottom": 102, "left": 0, "right": 0 },
              "cutouts": [], "dpi": 460, "orientation": "portrait" },
  "requested": { "device": "iOS Notch Device (1170x2532)", "orientation": "portrait" },
  "orientation_applied": true, "switched": "game_view_to_simulator", "brought_to_front": false,
  "expected": [1170, 2532], "settled": true,
  "settle": { "state": "settled", "frames": 3, "observed": { "screen": [1170, 2532], "render_texture": [1170, 2532], "frame": 3419 } }
}
```

| Field | Meaning |
|---|---|
| `mode` | `game` (Game view), `simulator` (Device Simulator), `none` (no play-mode view open) |
| `window` | The view's window in logical points and in pixels (`backing_scale` = pixels per point on HiDPI displays) |
| `size` | Game view only: the selected entry (`index`, `label`, `type` `free_aspect` / `aspect_ratio` / `fixed_resolution`, `custom`, `width`/`height`) |
| `device` | Simulator only: device id, physical portrait size, dpi, rotation in degrees |
| `scale`, `focused`, `low_resolution_aspect_ratios` | View zoom / Simulator preview scale, Editor focus, the Game view's low-resolution setting |
| `render_texture` | Size of the frame the view last rendered (`null` before it first renders) |
| `screen` | What the game sees: `Screen.width`/`height`, reduced `aspect`, `safe_area` `[x, y, w, h]` (bottom-left origin), `insets` (the unsafe band per edge — a notch is `insets.top` in portrait), `cutouts`, `dpi`, `orientation` (`portrait` / `landscape` in the Game view) |
| `entry` | `set_resolution`: the entry used, `created`, `persistent` |

Each `list_devices` entry carries `id`, `screen` (portrait pixels), `dpi`,
`portrait_safe_area`, `insets`, `cutouts` (count), `notch` (a top inset or a
cutout in portrait) and `orientations`.

### When a set call returns

Editor code that reads `Screen.width` sees whichever Editor panel is current,
not the game, and a running game picks up a new size only on a later frame. So:

- **Edit mode** — the change applies synchronously (`settle.state: "settled"`,
  `source: "edit_mode_view"`).
- **Play mode** — the bridge watches `Screen.width`/`height` inside the player
  loop and the view's render target, and the call returns once both report the
  new size for two consecutive frames (`settle.frames`, `settle.observed`).
  `settle_timeout_ms` (default 5000, max 20000) bounds the wait; then the call
  answers `not_settled` with what the game last reported. A paused Editor
  answers `not_settled` at once (`state: "paused"`) — unpause or step frames,
  then call `get`. A size the Editor clamps (GPU texture limit, 8K cap) answers
  `not_settled` with `clamped`.

`set_resolution` / `set_device` are refused inside `batch_execute`
(`batch_step_requires_server_poll`): no frame advances while a batch holds the
main thread. `get` and `list_devices` work in a batch.

### Capture at the device resolution

Set the device or resolution first, then capture the composed frame:

1. `game_view set_device` (or `set_resolution`) — wait for `settled: true`.
2. `unity_senses_screenshot` / `unity_senses_capture_inline` with
   `view: "composed"` and no width/height — the frame comes back at the game's
   own resolution, without Editor chrome or the Simulator's device bezel; add
   `overlay: "safe_area"` to tint the unsafe band and outline the safe area.
3. Read `capturedAt` (mode, device or size entry, orientation, screen) to label
   the frame; see
   [Composed captures](mcp-tools.md#composed-captures-unity_senses_screenshot--unity_senses_capture_inline)
   for sizes, letterboxing and the overlay.

## Snapshot → tap by label

1. `ui_snapshot` — read the screen (compact).
2. `inputsim_pointer` / `inputsim_touch` with `target_text: "Buy"` (plus `index`
   or `under` when the label repeats) — or `inputsim_gesture` keyframes with
   `coords: "target"` and `target_text`.
3. `inputsim_step`, then `ui_snapshot` or `unity_senses_screenshot` to confirm.

## Errors

| Code | When |
|---|---|
| `invalid_arguments` | `ui_snapshot`: unknown `profile` / `role`, invalid `placeholder_pattern`, `min_touch_size` ≤ 0. `game_view`: unknown action, width/height out of range or without the other, width/height together with `size_label`, an orientation the device does not support, `settle_timeout_ms` out of range |
| `under_not_found` | No node path matches `under` |
| `not_settled` | `game_view` set_*: the change was applied but the game did not report the new size (paused Editor, time-out, clamped size); carries `expected`, `observed` and the applied state |
| `device_not_found` / `ambiguous_device` | `set_device`: no device / several devices match `device` (`candidates`) |
| `size_not_found` | `set_resolution`: no size entry has that `size_label` (`labels` lists them) |
| `unsupported_editor_version` | `game_view` on an Editor outside the verified table (Unity 6 line) |
| `editor_internals_unavailable` | `game_view`: an internal Game view / Simulator member this Editor lacks (`detected.missing`) |
| `batch_step_requires_server_poll` | `game_view` set_* inside `batch_execute` |
