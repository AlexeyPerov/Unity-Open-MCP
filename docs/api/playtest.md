# Playtest tools

Activate the `playtest` group (with `input-simulation` to drive input —
`manage_tools(action: "activate_for", intent: "playtest …")` brings both). These
tools let an agent read and play the running game the way a tester does: put the
game on a target device or resolution, read what the screen says, tap a control
by its label, wait for the game to get somewhere, capture what it shows over time,
check the result — put the player's save back afterwards, and record the whole
session as a report for a human. The workflow is in the
[Playtest guide](../playtest-guide.md).

| Tool | Does | Compiles when |
|---|---|---|
| `unity_open_mcp_ui_snapshot` | Lists the uGUI on screen as a flat accessibility tree with text, visibility and diagnostics | `com.unity.ugui` present; TextMeshPro text when TextMeshPro is installed |
| `unity_open_mcp_game_view` | Reads or sets the Game view resolution or a Device Simulator device + orientation, and reports the screen, safe area and notch insets the game sees | always (Unity 6; other Editors answer `unsupported_editor_version`) |
| `unity_open_mcp_playtest_wait` | Waits in play mode until a condition holds (a log line, a UI query, a GameObject state, the project's own read-only predicate, frames, ms — or all / any of them), with a timeout, an optional time scale for the wait and an early stop on game exceptions | always |
| `unity_senses_capture_series` | Captures the composed game frame at scheduled play-mode frames (relative to the call or a condition), with an optional crop, per-frame luminance / diff metric and a labelled contact sheet | always (Unity 6; reads the play-mode view) |
| `unity_open_mcp_player_state` | Snapshots, diffs and restores the player state a session changes — PlayerPrefs, `persistentDataPath` files, opt-in EditorPrefs — in a store outside the project; restore is edit-mode only, a dry run unless confirmed, and gated | always (macOS verified; Windows and Linux readers untested in the field) |
| `unity_open_mcp_playtest_session` | Records a playtest for a human: the agent's input / wait / capture / project-command calls with their frames and console errors, step verdicts, the state diff — `end` writes a Markdown report | always (server-local; no Unity code) |

`inputsim_gesture` (in `input-simulation`) takes `capture_at_ms`: a tap and the
frames after it are captured in the same scheduled operation; see
[Tap and capture what follows](#tap-and-capture-what-follows).

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

## `unity_open_mcp_playtest_wait`

Waits in play mode until a condition holds — use it instead of a loop of calls
that each race the game. The bridge starts the wait on its shared play-mode
scheduler (a player-loop system at the start of `EarlyUpdate`) and evaluates the
condition there every `poll_every_frames` frames; the call returns the terminal
result. Play-mode only (`play_mode_required` otherwise); gate-free; refused inside
`batch_execute`.

### Conditions

A condition is an object with exactly one key:

| Key | Holds when | Notes |
|---|---|---|
| `log` `{pattern, level}` | A console message logged after the wait started matches the .NET regex `pattern` (case-sensitive) | `level`: `any` (default), `log`, `warning`, `error` (Error, Exception, Assert), `exception`. Latches once seen; reports the frame the message was logged in. Main-thread messages |
| `ui` `{text_contains, path, visible, matches}` | A query over the `ui_snapshot` screen model matches — or, with `matches: false`, stops matching (a popup closed) | Needs `text_contains` and/or `path`. `text_contains` is normalised and case-insensitive over a node's display text (a control's label; a label counts once, as its control); `path` is a node path or trailing path suffix; `visible: true` (default) counts on-screen nodes, `false` nodes that exist but are hidden. Requires `com.unity.ugui` (`ui_unavailable` otherwise) |
| `object` `{path \| object_id, state}` | A GameObject is `exists` (default), `active` (`activeInHierarchy`), `inactive` or `destroyed` | Loaded scenes plus `DontDestroyOnLoad`, inactive objects included. `path` is a hierarchy path or a bare name; `object_id` an instance id (e.g. a `ui_snapshot` id). Once found the object is tracked: `destroyed` means that object was destroyed |
| `project_command` `{id, args, field}` | A synchronous, read-only [project command](project-commands.md) returns JSON `true` — or, with `field`, an object whose top-level `field` is `true` | The project's own predicate ("is the wave over"), called at every poll. Arguments are validated once, at start. Mutating or async commands answer `command_not_allowed`; an unknown id `command_unavailable`. There is no arbitrary C# predicate |
| `frames` N | N frames have run since the start | |
| `ms` N | N ms have passed in the call's `time` base | `time: gameplay` (default) is the game's clock (`Time.time`, so `time_scale` speeds it up); `realtime` is unscaled |
| `all` / `any` `[leaves]` | Every / one of the leaves holds at the same poll | One level deep (leaves only), 1–16 leaves |

Frames are reported precisely. A poll at the start of frame F+1 sees the state the
game left after frame F, so a state condition (`ui`, `object`, `project_command`)
that turns true there is reported as `frame: F` with `observed_frame: F+1`; a
`log` reports the frame the message was logged in; `frames` / `ms` the frame they
elapsed at. `play_time` is that frame's `Time.time`. With `poll_every_frames` N
the change happened within the N frames before `observed_frame`.

### Arguments

| Argument | Default | Meaning |
|---|---|---|
| `condition` | required | See above |
| `timeout_ms` | 30000 (max 600000) | Wall-clock limit. On expiry the call fails with `timed_out` and every leaf's last observed state |
| `poll_every_frames` | 1 (max 600) | Evaluate the condition every N frames; logs and exceptions are captured every frame regardless |
| `time_scale` | — | `Time.timeScale` for the wait only (> 0, ≤ 100). Applied on the first frame, restored on every exit path: success, timeout, cancel, exiting play mode, a domain reload, Editor quit. If game code changed the time scale meanwhile (a pause menu), the game's value is kept and `time_scale.note` says so. One time-scaled wait at a time (`playmode_operation_busy`) |
| `time` | `gameplay` | Clock for `ms` conditions and `elapsed_ms` |
| `fail_on` | — | `["exception"]` stops at the first exception the game logs (`exception_thrown`, with message and stack); `"error"` also stops on error logs (`error_logged`) |

Waits whose `timeout_ms` is over 40 s run as a
[`jobs` play-mode operation](jobs.md#play-mode-operations): the call returns a
`job_id` to `wait` on (and `cancel`). The tool's own `timeout_ms` is forwarded as
sent — it is not the request's transport budget.

### Result

```json
{
  "status": "ok", "outcome": "fired",
  "fired": { "path": "condition.all", "type": "all", "frame": 300, "observed_frame": 301,
             "play_time": 4.9834, "elapsed_ms": 4806.69, "realtime_ms": 4806.69 },
  "condition": { "path": "condition", "type": "all", "met": true, "last": { "children": [
    { "path": "condition.all[0]", "type": "ui", "met": true, "fired": { "frame": 300, "…": "…" },
      "last": { "query": { "text_contains": "Timer done", "visible": true, "matches": true }, "count": 1,
                "first": { "path": "WaitCanvas/TimerLabel", "role": "text", "text": "Timer done", "visibility": "on_screen" } } },
    { "path": "condition.all[1]", "type": "object", "met": true, "fired": { "frame": 180, "…": "…" },
      "last": { "state": "destroyed", "found": false, "ever_found": true, "resolved_path": "WaitCanvas/WaitFixtureEnemy" } },
    { "path": "condition.all[2]", "type": "project_command", "met": true,
      "last": { "id": "project.demo.wave_cleared", "field": "cleared", "calls": 291, "output": "{\"alive\":0,\"cleared\":true}" } } ] } },
  "time": "gameplay", "elapsed_ms": 4823.3, "realtime_ms": 4823.3, "wall_ms": 4830.1, "timeout_ms": 15000,
  "frames": { "start": 13, "end": 301, "ticks": 289, "polls": 289, "poll_every_frames": 1 },
  "time_scale": { "requested": 4, "applied": true, "previous": 1, "restored": true, "final": 1 },
  "errors": [], "errors_dropped": 0,
  "operation": { "operation_id": "…", "kind": "unity_open_mcp_playtest_wait", "state": "succeeded", "…": "…" }
}
```

`fired` names the condition that fired (an `any` names its leaf, e.g.
`condition.any[1]`). `condition` repeats the tree with every leaf's `met`, `fired`
and `last` observed state — on a timeout this is what the game showed. `errors`
lists console errors and exceptions logged meanwhile (at most 20, first stack line;
`errors_dropped` counts the rest). With `fail_on`, `exception` carries the type,
message, full stack (≤ 4000 characters), frame and play time. `time_scale` appears
when one was requested.

## `unity_senses_capture_series`

Captures the composed game frame — every camera plus Screen Space Overlay UI,
the frame `unity_senses_screenshot` `view: "composed"` returns in play mode, at
native size by default — at scheduled play-mode frames, inside the player loop,
without a round trip between frames. A sub-second tween or a "breathing" effect is
caught as a sequence instead of whatever two one-shot captures happen to hit.
Play-mode only; needs a rendering Game view or Device Simulator
(`render_target_unavailable` otherwise); Unity 6 (the play-mode view is read
through the Game view adapter, `unsupported_editor_version` elsewhere); refused
inside `batch_execute`. Group `playtest`; live-only like every `unity_senses_*` tool.

| Argument | Meaning |
|---|---|
| `at_ms` `[…]` / `every_ms` + `duration_ms` / `at_frames` `[…]` | The schedule — exactly one form; at most 120 captures. `at_ms` / `every_ms` count the `time` base (`gameplay` default, `realtime`); `at_frames` counts frames |
| `start_on` | Start the schedule when a condition holds (the `playtest_wait` shapes). Offset 0 is the frame that made it true. `start_timeout_ms` (default 30000) bounds it: `timed_out`, nothing captured |
| `region` `[x, y, w, h]` | Crop every capture; screen pixels, bottom-left origin — a `ui_snapshot` rect can be passed as is |
| `scale` | 0.05–1; shrink every capture (1 = native, the default) |
| `metric` | `luminance` = mean Rec. 709 luminance (0..1) per capture; `diff` adds the mean absolute luminance change and the fraction of pixels that changed by more than 10/255 against the previous capture |
| `metric_region` `[x, y, w, h]` | Where the metric is computed (screen pixels; default the captured area) |
| `contact_sheet` | Also write one tiled PNG of every capture, each labelled `#index F<frame> +<elapsed>MS` |
| `poll_every_frames` | Evaluate `start_on` every N frames |

Timing: the scheduler ticks at the start of a frame, when the view holds the frame
the game rendered last. A capture scheduled at `t` is the first frame whose offset
from the start reached `t`, read on the next tick and labelled with that frame —
the image and its `frame` / `play_time` / `elapsed_ms` always describe the same
frame. `unchanged: true` marks a capture whose pixels equal the previous one (a
static screen, or a hidden Game view tab, which does not render). Pixels are read
back at capture time; PNG encoding runs after the last capture, so encoding never
delays a scheduled frame. At most 512 MB of frames are held before encoding; a
capture over that budget is reported `dropped: "memory_budget"` (use `scale` or
`region`). Files go to `~/.unity-open-mcp/screenshots/series-<stamp>-<id>/`.

```json
{
  "status": "ok", "outcome": "completed",
  "series": {
    "schedule": "at_ms", "time": "gameplay", "requested": 4, "captured": 4,
    "base": { "frame": 60, "play_time": 0.9877 },
    "folder": "…/screenshots/series-20261028-165339-498-27e97633", "native": [2422, 1483],
    "frames": [
      { "index": 0, "at_ms": 0, "frame": 60, "play_time": 0.9877, "elapsed_ms": 0, "file": "…/frame-000-f60-0ms.png",
        "width": 2422, "height": 1483, "metric": { "luminance": 0.138 } },
      { "index": 1, "at_ms": 100, "frame": 66, "play_time": 1.1, "elapsed_ms": 112.28, "file": "…/frame-001-f66-112ms.png",
        "width": 2422, "height": 1483, "metric": { "luminance": 0.1488, "diff": 0.0182, "changed": 0.0486 } }
    ],
    "contact_sheet": { "file": "…/contact-sheet.png", "width": 1310, "height": 228, "columns": 4 }
  },
  "start_on": { "fired": { "path": "start_on", "type": "log", "frame": 60, "observed_frame": 61, "…": "…" }, "condition": { "…": "…" } },
  "capturedAt": { "mode": "game", "size": "Free Aspect", "orientation": "landscape", "screen": [2422, 1483], "playing": true },
  "errors": []
}
```

Series estimated over 40 s (start_on wait + schedule + encoding) run as a
[`jobs` play-mode operation](jobs.md#play-mode-operations). Cancelling or exiting
play mode keeps the frames already captured (they are encoded during cleanup); a
domain reload drops them.

### Tap and capture what follows

`unity_open_mcp_inputsim_gesture` `capture_at_ms: [0, 100, 200, 300]` captures the
composed frame at those offsets from the gesture start (the gesture's `time` base)
inside the same scheduled operation, so a tap and the frames after it come back
from one call under `captures` (same frame fields as a series, native size, at most
30). The call returns when both the gesture and the last capture are done; the
gesture's input resource is held until then.

### Recipes

- Wait for a timer instead of polling:
  `playtest_wait {"condition":{"log":{"pattern":"Wave \\d+ cleared"}},"timeout_ms":60000}`.
- Fast-forward: add `"time_scale": 4`; the result's `time_scale.restored` confirms the game is back at its own speed.
- A popup closed: `{"ui":{"text_contains":"Reward","matches":false}}`.
- Stop on a crash: `"fail_on": ["exception"]` — read `exception.stack`.
- An animation: `capture_series {"at_ms":[0,100,200,300],"start_on":{"log":{"pattern":"Popup opening"}},"contact_sheet":true}`, then open the contact sheet.
- A breathing artifact: `capture_series {"every_ms":500,"duration_ms":15000,"scale":0.25,"metric":"diff","metric_region":[…]}` and read `metric.luminance` over time.

## `unity_open_mcp_player_state`

A live playtest plays against the developer's own save: it spends currency, pays
first-clear rewards, moves progress. `player_state` captures that state before a
session, shows what the session changed, and puts it back.

> **Restore overwrites the current state.** `restore` with `confirm: true` deletes
> every PlayerPrefs key and writes the snapshot's keys back, and makes
> `persistentDataPath` an exact copy of the snapshot: **files that are not in the
> snapshot are deleted.** Opted-in EditorPrefs are overwritten under their
> prefixes. Call `restore` without `confirm` first and read what it would delete.

**What is captured.**

- **PlayerPrefs** — every key with its type (int, float, string). Unity has no API
  that lists PlayerPrefs keys, so the bridge reads the store the Editor persists
  them to. The Editor keeps PlayerPrefs in memory and writes the store only on
  `PlayerPrefs.Save()` or quit, so every read starts with `PlayerPrefs.Save()`:
  the snapshot sees what the game set even if it never saved (the same flush Unity
  does on quit; values do not change). A key stored with a type PlayerPrefs cannot
  hold is listed under `unsupported` and is not restored.
- **Files** — `Application.persistentDataPath`, with size and SHA-256 per file and
  the folder list (empty folders included). Symbolic links are skipped and listed.
  Capped at `max_files_mb` (default 64, at most 1024) and 5000 files after
  excludes; over the cap the snapshot refuses `persistent_data_too_large` with the
  largest files and writes nothing.
- **EditorPrefs** — only with `editor_prefs_prefixes` (each ≥ 3 characters): keys
  starting with one of them. Off by default because EditorPrefs are shared by
  every project on the machine. The Editor caches EditorPrefs too; the bridge
  flushes them through the Editor's internal `EditorPrefs.Sync` (Unity 6) and
  refuses `editor_internals_unavailable` where it does not exist rather than read
  a stale store.

**Excluded keys and paths are outside the sandbox**: not captured, not diffed and
never touched by restore (they keep their current values / files). Defaults:
keys `unity.player_session*` and `unity_connect.*` (Unity's own per-session
bookkeeping, which changes on every play), paths `Unity/**` (Unity's analytics
folder) and `TestResults.xml` (the Test Runner's last results). `exclude` (path
globs: `**` any depth, `*` / `?` within a name; a pattern without `/` matches a
file or folder name at any depth) and `exclude_keys` (`*` / `?` wildcards) add to
them; the snapshot records its excludes and every later diff / restore uses them.

**Where snapshots live** — outside the project, in the platform app-data folder:
`<app data>/unity-open-mcp/player-state/<company>/<product>/<name>/` with
`snapshot.json` (metadata, typed prefs, file manifest) and `files/` (the copy).
`<app data>` is `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on
Windows, `$XDG_DATA_HOME` or `~/.local/share` on Linux. Company and product are
the open project's Player Settings.

| Platform | PlayerPrefs store read | EditorPrefs store | Status |
|---|---|---|---|
| macOS | preferences domain `unity.<company>.<product>` (`~/Library/Preferences/unity.<company>.<product>.plist`) through `defaults export` | `com.unity3d.UnityEditor5.x` | verified |
| Windows | registry `HKCU\Software\Unity\UnityEditor\<company>\<product>`; value names `<key>_h<hash>`, ints `REG_DWORD`, floats an 8-byte double under a `REG_DWORD` tag, strings UTF-8 `REG_BINARY` | `HKCU\Software\Unity Technologies\Unity Editor 5.x` | decoders unit-tested; not yet verified on a Windows Editor |
| Linux | `~/.config/unity3d/<company>/<product>/prefs` (XML, strings base64) | `~/.local/share/unity3d/prefs` | untested |

### Actions

| Action | Arguments | Does |
|---|---|---|
| `snapshot` | `name`, `editor_prefs_prefixes`, `exclude`, `exclude_keys`, `max_files_mb`, `overwrite` | Captures the open project's state into `name` (`snapshot_exists` unless `overwrite`). Edit or play mode; writes nothing in the project |
| `diff` | `name`, `against`, `limit` | `name` → the current state (`to.current`, flushed first), or → snapshot `against` |
| `restore` | `name`, `confirm`, `limit` | Edit mode only. Without `confirm` (the default): a dry run returning the diff it would apply. With `confirm: true`: applies it, re-reads everything and reports `verified` |
| `list` | — | The snapshots of this project's store: name, `created_at`, prefs / files counts, bytes, EditorPrefs prefixes |
| `delete` | `name` | Removes one snapshot |

`company` / `product` address another project's store for `diff`, `restore`,
`list` and `delete`. The current state is always the open project's, so a snapshot
of another project compares only with another snapshot (`against`), and `restore`
refuses it (`snapshot_project_mismatch`) — whether it lives in another project's
store or its metadata names another company / product. Names are 1–64 of
`[A-Za-z0-9._-]`, starting with a letter or digit.

### Diff

```json
{
  "status": "ok", "action": "diff",
  "from": { "snapshot": "before", "created_at": "2026-10-28T14:51:12.481Z", "company": "DefaultCompany", "product": "Unity Open MCP Demo", "path": "…" },
  "to": { "current": true, "mode": "play", "flushed": true },
  "identical": false,
  "prefs": {
    "added": [],
    "removed": [],
    "changed": [
      { "key": "fixture.coins", "from": { "type": "int", "value": 100 }, "to": { "type": "int", "value": 150 } },
      { "key": "fixture.last_level", "from": { "type": "string", "value": "level_1" }, "to": { "type": "string", "value": "level_3" } },
      { "key": "fixture.volume", "from": { "type": "float", "value": 0.25 }, "to": { "type": "float", "value": 0.75 } }
    ],
    "counts": { "added": 0, "removed": 0, "changed": 3, "unchanged": 5 }, "truncated": 0
  },
  "editor_prefs": null,
  "files": {
    "added": [ { "path": "fixture_save.json", "size": 43, "sha256": "0eaae594…" } ],
    "removed": [], "changed": [], "dirs_added": [], "dirs_removed": [],
    "counts": { "added": 1, "removed": 0, "changed": 0, "unchanged": 0 }, "truncated": 0
  }
}
```

`added` = only in `to`, `removed` = only in `from`, `changed` = both, different
(a type change counts). Ints and floats are shown; strings up to 256 characters
are shown, longer ones as `length` + `sha256`; files as `size` + `sha256` (never
their content). `limit` (default 100, at most 1000) bounds each list; `counts` are
always complete and `truncated` says how many entries were left out.
`editor_prefs` is `null` unless the snapshot opted in (then it carries `prefixes`).

### Restore

`restore` reads the current state and diffs it **to** the snapshot — current →
snapshot, so `removed` is what restore deletes. The dry run returns that diff with
**every** removal listed (`prefs.removed`, `editor_prefs.removed`, `files.removed`,
`files.dirs_removed` are never truncated), `unsupported_deleted` (current keys of
a type PlayerPrefs cannot hold — `DeleteAll` removes them for good), a `warning`
that spells out the overwrite and the number of files it removes, and changes
nothing.

With `confirm: true` it first checks every stored file against the manifest
(`snapshot_corrupt`, nothing changed, on a mismatch), then:

1. PlayerPrefs through the PlayerPrefs API — `PlayerPrefs.DeleteAll()`, typed
   `Set*` for every snapshot key, the excluded keys' current values written back,
   `PlayerPrefs.Save()` — so the Editor's in-memory cache (what the next play
   session reads) and the store agree. The store is never written behind Unity's
   back.
2. EditorPrefs (only if the snapshot opted in) — keys under the snapshot's
   prefixes: the ones not in the snapshot deleted, the others set. Never
   `EditorPrefs.DeleteAll()`.
3. Files — `persistentDataPath` mirrored exactly: snapshot files written where
   missing or different, files not in the snapshot deleted, folders not in the
   snapshot removed when empty (`dirs_kept_not_empty` otherwise); excluded paths
   untouched.

It then re-reads everything and reports `verified: true`, or `verified: false`
with the `residual` diff. A file operation that fails leaves a partial restore:
`restore_incomplete` with `errors` and the output. Refusals: `edit_mode_required`
in play mode (dry run included — use `diff` while the game runs),
`snapshot_project_mismatch`, `snapshot_not_found`, `snapshot_unreadable`.

**Gate and scope.** Only `restore` with `confirm: true` writes state, and the
bridge classifies it as a mutation per request: it runs the normal gate (checkpoint
→ restore → validate → delta) and is recorded in the gate history and the opt-in
audit log; every other action — and the dry run — is read-only for the project
(`effectiveReadOnly: true`, no gate, no scope). Restore writes no project asset, so
its declared scope is `ProjectSettings/ProjectSettings.asset`, the asset holding
the company / product identity the PlayerPrefs store, `persistentDataPath` and the
snapshot are bound to. The MCP schema supplies it as the default `paths_hint` (a
direct bridge caller passes it, like any mutation); a caller's own `paths_hint`
wins. The gate's delta therefore confirms that no project asset changed. Like
every mutation, an applied restore is refused with `job_busy` while a project job
runs. Refusals come back as a failed mutation (`mutation.success: false`, the code
in `mutation.error`, what to do in `agentNextSteps`).

### Sandbox a playtest

Prefer the project's own isolation when it has one. A project can register
[project commands](project-commands.md) `playtest.begin` / `playtest.end` that
switch the game to an isolated save of its own (a test profile, a separate
storage scope) and back; that covers state the bridge cannot see (cloud saves, a
custom store, a server). Look for them first:

1. `project_commands(action: "list", query: "playtest")` — when `playtest.begin`
   and `playtest.end` exist, invoke `playtest.begin` before playing and
   `playtest.end` after. The demo's `project.demo.playtest.*` commands are a
   copyable example ([Playtest guide](../playtest-guide.md#project-commands-a-game-should-expose)).
2. Otherwise, in edit mode: `player_state {"action":"snapshot","name":"before"}`
   (add `editor_prefs_prefixes` for the project's own dev toggles).
3. Play the session; `player_state {"action":"diff","name":"before"}` (in play mode
   or after) lists what it changed — part of the playtest report.
4. Stop play mode; `player_state {"action":"restore","name":"before"}` — read the
   dry run's `prefs.removed` / `files.removed`.
5. `player_state {"action":"restore","name":"before","confirm":true}`; check
   `verified: true`. `delete` the snapshot when done.

## `unity_open_mcp_playtest_session`

Records a playtest so a human can review it without replaying the agent's
context: what was done, the frame it showed, which step caused which error, and
the verdict. The workflow around it is the [Playtest guide](../playtest-guide.md).

A local tool: the MCP server sees every call, so it keeps the log; the bridge only
writes the frames (the capture tools' PNGs). Gate-free; it never changes another
tool's result and never fails one.

| `action` | Arguments | Does |
|---|---|---|
| `start` | `name`, `device`, `snapshot` or `use_snapshot` | Opens a session for the calling agent and starts recording. `device` is a free-text summary for the report; the current Game view / Simulator state (`game_view get`) is recorded as well. `snapshot: true` takes a [`player_state`](#unity_open_mcp_player_state) snapshot (`playtest-<session>`) now — the start fails `snapshot_failed` rather than run unisolated; `use_snapshot` names an existing one. Either is diffed against the state at `end` |
| `mark` | `label`, `verdict` (`ok` default, `bug`, `suspicious`), `note` | Closes a step: the recorded calls since the previous mark belong to it. Answers `since_last_mark` (`calls`, `errors`, `captures`, `failed`) — errors there are findings |
| `status` | — | Your open session (counts so far), plus `orphaned` sessions of this project whose MCP server process is gone |
| `end` | `note`, `diff` (default true), `session` | Writes the report and closes the session; returns the report path and a compact `summary` |
| `resume` | `session` | Re-attaches an orphaned session after an MCP server restart; recording continues, numbering continues |
| `list` | `limit` (default 10, ≤ 50) | This project's sessions, newest first: `open`, `open_other_agent`, `open_other_process`, `orphaned` or `ended`, with the report path |

### What is recorded

While a session is open, every call **by its owner** in these categories is
appended to the log when it ends:

| Category | Tools |
|---|---|
| input | `inputsim_touch`, `inputsim_pointer`, `inputsim_pointer3d`, `inputsim_key`, `inputsim_gesture`, `inputsim_step` |
| wait | `playtest_wait` |
| capture | `unity_senses_screenshot` (and `_window` / `_camera`), `capture_inline`, `capture_series`, `visual_compare` |
| command | `project_commands` `invoke`; `jobs start` of a project command or a play-mode operation, and the job's terminal result when `jobs wait` / `status` sees it |
| control | `editor_set_state`, `game_view` `set_resolution` / `set_device` |

Reads (`ui_snapshot`, `inputsim_probe`, `read_console`, …) are not recorded; a
call reached through `manage_tools(action: "invoke")` is recorded as the inner
tool; steps nested in a `batch_execute` are not recorded one by one. Each entry holds the tool, the arguments as sent (top-level values equal to
their schema default are dropped; secrets redacted), status (`ok`, `error` with its
code, `job_started`), duration, a short result summary (a wait's `fired`, a
capture's `capturedAt`, a project command's result), the frames it wrote, and the
console errors / exceptions logged during it.

**Owner.** The owner is the identity [`jobs`](jobs.md) use: project, agent id
(`_meta.agentId`, else the MCP server process's own id) and a per-request port
override. One open session per owner — a second `start` answers
`session_already_open`. Other owners' calls (another MCP server process, another
`_meta.agentId`) are never recorded into it; they can open their own.

**Errors.** Console errors, exceptions and asserts come from the bridge event
stream (with stacks) and are matched to calls by time: inside a call's window (±
50 ms) → **during** that call; between two recorded calls → **after** the earlier
one (a tap's consequence logged frames later). The call's own `logs[]` and a wait's
or series' `errors` fill in what the stream missed — it reconnects after a domain
reload; such entries have no stack. The same message from both sources is one
error.

**Frames.** File paths a capture reports (`filePath`, a series' frames and contact
sheet) are hard-linked — or copied — into the session's `report/frames/` when the
call ends; `capture_inline` images are written there from the response. Full-size
frames stay files; the report shows downscaled thumbnails.

**Secrets.** Before anything is written: a property whose name reads as a secret
(`password`, `token`, `api_key`, `authorization`, `client_secret`, `session_id`, …)
is replaced with `[redacted]` at any depth; inside free text (notes, console
messages, stacks, results) `Bearer …`, `password=…` and `"token": "…"`-style
fragments, the bridge auth token and secret-named environment variable values are
masked; a PlayerPrefs diff entry whose key reads as a secret keeps its type, not
its value.

### Storage

A deliberate on-disk record (not a cache), outside the project, next to the
player-state snapshots:

```text
<app data>/unity-open-mcp/playtest-sessions/<company>/<product>/<session>/
  session.json        owner, server pid, device, snapshot, state, report path
  log.jsonl           one JSON line per event, appended as it happens
  report/
    report.md         written by end
    frames/           full-size captures
    thumbs/           downscaled previews (written by end)
    state-diff.json   the full player-state diff (when a snapshot was used)
```

`<app data>` is `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on
Windows, `$XDG_DATA_HOME` or `~/.local/share` on Linux; company / product are the
open project's Player Settings. Session ids are `<UTC yyyyMMdd-HHmmss>-<name
slug>-<4 hex>`. Nothing deletes sessions; remove old folders by hand.

**Domain reloads and restarts.** Each event is appended with one open-write-close,
so the log survives a domain reload (the server is not in Unity) and an MCP server
restart. A call is logged twice — `call_started` before dispatch, `call` after —
so a call that was in flight when the server process stopped is reported as
**lost** with its arguments. After a restart the new server's agent id differs:
`status` lists the session under `orphaned`; `end` or `resume` with `session:
<id>` adopts it (refused with `session_owned_by_other_agent` while the owning
process is alive). A session opened with a stable `_meta.agentId` is the same
owner after a restart.

### Report

`end` waits briefly for in-flight stream events, diffs the snapshot (when there is
one), and writes `report.md`:

- a header: session, project, start / end, device, snapshot, the project isolation
  hook seen (`*.playtest.begin` / `*.playtest.end` commands), totals and verdicts;
- **Findings**: `bug` / `suspicious` steps with notes, each distinct error with its
  first occurrence (step, call, during / after), failed calls, lost calls;
- **Steps**: per mark, a table of its calls (time, tool, arguments, result,
  duration), the errors it caused and its frames — a series shows its contact
  sheet, other captures a thumbnail, the rest as links;
- **Errors**: one entry per message, with the first occurrence and its stack;
- **State diff**: PlayerPrefs / EditorPrefs added / removed / changed and
  `persistentDataPath` files, from the session's snapshot to the state at `end`;
- **Editor events**: play-mode transitions, domain reloads and event-stream
  reconnects seen while recording.

```json
{
  "status": "ok", "action": "end", "session": "20261028-155842-demo-smoke-6065",
  "report": "…/playtest-sessions/DefaultCompany/Unity Open MCP Demo/20261028-155842-demo-smoke-6065/report/report.md",
  "summary": {
    "steps": 6, "calls": 12, "failed_calls": 0, "captures": 9, "thumbnails": 3, "duration_ms": 16451,
    "verdicts": { "ok": 4, "bug": 1, "suspicious": 1 }, "errors": { "unique": 1, "total": 1 },
    "findings": [ { "step": 6, "label": "Tap Report Bug", "verdict": "bug", "note": "Tapping Report Bug throws InvalidOperationException." } ],
    "top_errors": [ { "type": "exception", "message": "InvalidOperationException: [PlaytestFixture] Deliberate exception from the Report Bug button",
                      "first_step": 6, "first_call": 11, "relation": "during", "count": 1 } ],
    "lost_in_flight": [], "state_diff": { "identical": false, "prefs": { "added": 3, "removed": 0, "changed": 0, "unchanged": 5 } },
    "hooks": { "begin": "project.demo.playtest.begin" }
  },
  "agentNextSteps": [ "Hand the report to the human: …/report.md", "…" ]
}
```

**Limits** per session: 500 recorded calls (later ones are counted, not logged),
200 marks, 300 frames or 512 MB of frames (later ones are referenced by their
source path), 60 thumbnails (≤ 320 px, a contact sheet ≤ 960 px; 20 s of decoding),
20 errors per call, 2,000 characters of arguments per call.

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
| `batch_step_requires_server_poll` | `game_view` set_*, `playtest_wait` or `capture_series` inside `batch_execute` |
| `play_mode_required` | `playtest_wait` / `capture_series` in edit mode |
| `timed_out` | `playtest_wait`: the condition did not hold within `timeout_ms` (carries `condition` with every leaf's last state); `capture_series`: `start_on` did not hold within `start_timeout_ms` |
| `exception_thrown` / `error_logged` | `playtest_wait` with `fail_on`: the game logged an exception / error (carries `exception`) |
| `condition_error` | A leaf cannot be evaluated (a project command threw) |
| `command_unavailable` / `command_not_allowed` | `project_command`: unknown or disabled id / a mutating or async command |
| `ui_unavailable` | A `ui` condition without uGUI compiled in |
| `render_target_unavailable` | `capture_series` / gesture `capture_at_ms`: the play-mode view has not rendered (no Game view / Simulator open, or its tab is hidden) |
| `capture_failed` | `capture_series`: the first capture could not be read |
| `playmode_operation_busy` | A second time-scaled wait, or a second capture series, while one runs |
| `wait_stalled` / `capture_series_stalled` | The operation did not finish inside the call's inline window (a paused Editor); it was cancelled — run it as a job |
| `edit_mode_required` | `player_state` `restore` (dry run included) while the Editor is playing |
| `snapshot_exists` | `player_state` `snapshot` to a name that exists, without `overwrite` |
| `snapshot_not_found` / `snapshot_unreadable` | `player_state`: no such snapshot in the addressed store (`available` lists the names) / its `snapshot.json` cannot be read |
| `snapshot_project_mismatch` | `player_state` `restore` (or `diff` against the current state) of a snapshot whose company / product differ from the open project |
| `snapshot_corrupt` | `player_state` `restore confirm:true`: a stored file no longer matches the manifest; nothing was changed |
| `persistent_data_too_large` | `player_state` `snapshot`: `persistentDataPath` exceeds `max_files_mb` or 5000 files after excludes (`largest` lists the biggest files); nothing written |
| `prefs_store_unreadable` | `player_state`: the platform PlayerPrefs / EditorPrefs store could not be read (`store` names it) |
| `editor_internals_unavailable` | `player_state` with EditorPrefs on an Editor without the internal EditorPrefs flush |
| `restore_incomplete` | `player_state` `restore confirm:true`: some file operations failed; the state is partly restored (`errors`, `residual`) |
| `session_already_open` | `playtest_session` `start` while this agent has an open session (`session` names it), or `mark` / `end` / `resume` naming a different session than the open one |
| `no_open_session` | `playtest_session` `mark` / `end` without an open session (`orphaned` lists sessions a stopped server left open) |
| `session_not_found` / `session_ended` | `playtest_session`: no such session for this project / it already ended (`report`) |
| `session_owned_by_other_agent` | `playtest_session` `end` / `resume` / `mark` of a session another live agent or MCP server process owns |
| `snapshot_failed` | `playtest_session` `start` with `snapshot: true`: the player-state snapshot failed (`snapshot_error`); no session was started |
| `identity_unavailable` | `playtest_session`: neither the Editor nor `ProjectSettings/ProjectSettings.asset` gave the company / product |
| `limit_reached` | `playtest_session` `mark` beyond 200 marks |
