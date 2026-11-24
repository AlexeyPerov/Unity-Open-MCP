# Playtest tools

Activate the `playtest` group (with `input-simulation` to drive input —
`manage_tools(action: "activate_for", intent: "playtest …")` brings both). These
tools let an agent read and play the running game the way a tester does: put the
game on a target device or resolution, read what the screen says, tap a control
by its label, wait for the game to get somewhere, trace values over frames,
capture what it shows over time, check the result — put the player's save back
afterwards, and record the whole
session as a report for a human. The workflow is in the
[Playtest guide](../playtest-guide.md).

| Tool | Does | Compiles when |
|---|---|---|
| `unity_open_mcp_ui_snapshot` | Lists the uGUI on screen as a flat accessibility tree with text, visibility and diagnostics | `com.unity.ugui` present; TextMeshPro text when TextMeshPro is installed |
| `unity_open_mcp_game_view` | Reads or sets the Game view resolution or a Device Simulator device + orientation, and reports the screen, safe area and notch insets the game sees | always (Unity 6; other Editors answer `unsupported_editor_version`) |
| `unity_open_mcp_playtest_wait` | Waits in play mode until a condition holds (a log line, a UI query, a GameObject state, the project's own read-only predicate, frames, ms — or all / any of them), with a timeout, an optional time scale for the wait and an early stop on game exceptions | always |
| `unity_open_mcp_playtest_probe` | Samples a value every N play-mode frames — a field / property path or a read-only C# expression — into a bounded buffer the bridge owns, read incrementally by cursor; ends on a condition, a sample cap, repeated errors, an over-budget sample, stop or play-mode exit | always |
| `unity_senses_capture_series` | Captures the composed game frame at scheduled play-mode frames (relative to the call or a condition), with an optional crop, per-frame luminance / diff metric and a labelled contact sheet | always (Unity 6; reads the play-mode view) |
| `unity_open_mcp_player_state` | Snapshots, diffs and restores the player state a session changes — PlayerPrefs, `persistentDataPath` files, opt-in EditorPrefs — in a store outside the project; restore is edit-mode only, a dry run unless confirmed, and gated | always (macOS verified; Windows and Linux readers untested in the field) |
| `unity_open_mcp_playtest_explore` | Explores the running game's UI on its own as a job: taps every untried button / toggle of each screen (never money, destructive or leaving controls), walks back or restarts, captures each new screen and files findings — overflow, safe area, overlaps, placeholder text, touch size, dead buttons, dead ends, console errors — into the open session | `com.unity.ugui` (server-local crawl over `ui_snapshot` and `inputsim_pointer`) |
| `unity_open_mcp_playtest_devices` | Checks the game on several Device Simulator devices (× orientation) as a job — the current screen's detectors and a frame per device, or a crawl per device — groups the findings by the devices that show them and puts the Game view back | Unity 6 Editor (Device Simulator); `com.unity.ugui` for the checks (server-local) |
| `unity_open_mcp_playtest_replay` | Replays a scenario — a session's own steps or a crawl, exported as a JSON file in the project — as a job with no model: targets by path and label, the expected screen after each step, console errors and new findings, optionally each screen as a picture against per-device frames; pass / fail per step, drift for tolerated changes, retries for flakiness, `record` for the baseline | `com.unity.ugui` (server-local, over the same tools an agent uses) |
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
| `visible`, `visibility`, `reason` | see below; `visible` is `visibility == "on_screen"`; `reason` (only when set) explains a visibility the rect does not: `no_camera` |
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
| `transparent` | Effective alpha ≈ 0: the `CanvasGroup` alpha chain (`ignoreParentGroups` restarts it), a disabled `Canvas` above, or a text/image whose own alpha is 0 (color alpha × `CanvasRenderer` alpha, so a `CrossFadeAlpha` fade counts) |
| `offscreen` | The rect does not intersect the screen, or is behind the canvas camera, or a World Space canvas has no camera to project it (no event camera and no `Camera.main`; the node carries `reason: "no_camera"` and a zero rect) |
| `clipped` | On screen but fully outside an enclosing `RectMask2D` / `Mask` (an override-sorting canvas stops clipping from above, as in uGUI) |
| `on_screen` | At least partly visible |

The screen is the main Game view's pixel size (the simulated device screen when
the Device Simulator is active). A Screen Space - Camera canvas without a camera
is measured as Overlay, as Unity draws it. `safe_area` is the safe area the game sees: the
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
| `placeholder` | only with `placeholder_pattern` | Texts matching the regex (.NET, case-sensitive) — e.g. unresolved localisation keys such as `^[*#{].*[*#}]$`. There is no built-in pattern. The pattern gets 100 ms per text and 2 s per snapshot; past either the call refuses with `regex_timeout` (naming the pattern and the text) |
| `small_touch_targets` | only with `min_touch_size` | Visible controls whose rect is narrower or shorter than `min_touch_size` screen pixels; reports `dpi` so the caller can convert from dp (`px = dp × dpi / 160`) |
| `outside_safe_area` | always | Visible controls, and visible texts that are not part of a control's label, whose on-screen part reaches outside the safe area (under a notch, a rounded corner, the home indicator). Items add `role`, `text`, `rect` and `edges` (`top`, `bottom`, `left`, `right`); the list carries `safe_area`. Empty when the safe area is the whole screen — in the plain Game view, so check on a Device Simulator device (`game_view set_device`) |
| `overlapping_controls` | always | Pairs of visible interactable controls, neither inside the other in the hierarchy, whose visible rects overlap by at least `threshold` (0.25) of the smaller one — short of containing it, which is layering (a button on a full-screen click catcher). In play mode a control covered by something in front of it (a popup's dimmer) is left out. Items name the first control (`id`, `path`, `text`) and the other (`with_id`, `with`, `with_text`), with the `overlap` fraction |

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
| `scale`, `focused`, `low_resolution_aspect_ratios` | View zoom / Simulator preview scale, Editor focus, the Game view's low-resolution setting (`scale` and `low_resolution_aspect_ratios` are left out on an Editor that does not expose them) |
| `visible`, `warning` | Whether the view is the front tab of its dock area (left out when the Editor does not expose it). Only the front tab renders: in play mode a hidden view adds a `warning` — `render_texture` is the last frame it drew and `screen` its configured size, not what the running game sees |
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
  new size for two consecutive frames (`settle.frames`, `settle.observed`); a
  view behind another tab never counts (`observed.visible: false`), since its
  render target is a stale frame.
  `settle_timeout_ms` (default 5000, max 20000) bounds the wait; then the call
  answers `not_settled` (`state: "timeout"`) with what the game last reported. A paused Editor
  answers `not_settled` at once (`state: "paused"`) — unpause or step frames,
  then call `get`.
- **Clamped size** — a fixed size the Editor clamps (GPU texture limit, 8K cap)
  is still applied: the game renders at the clamped size, the call settles on
  it as above (`expected` is the clamped size), and the response carries
  `clamped` `{requested, render, note}`.

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
| `probe` `{id, state}` | A [frame probe](#unity_open_mcp_playtest_probe) is in `state` | `ended` (default) holds once it stopped for any reason; or one of `running`, `until_met`, `max_samples_reached`, `stopped`, `play_mode_exited`, `error`. The probe must exist when the wait starts (`probe_not_found`) |
| `frames` N | N frames have run since the start | |
| `ms` N | N ms have passed in the call's `time` base | `time: gameplay` (default) is the game's clock (`Time.time`, so `time_scale` speeds it up); `realtime` is the wall clock (a hitch counts in full) |
| `all` / `any` `[conditions]` | Every / one of the children holds at the same poll | 1–16 children each. A child may itself be `all` / `any` over leaves — one nested level, e.g. `{"any":[{"all":[{"object":{…}},{"ms":800}]},{"ms":5000}]}` ("the window is open and 800 ms passed, or give up after 5 s"); at most 32 leaves in the whole tree |

Frames are reported precisely. A poll at the start of frame F+1 sees the state the
game left after frame F, so a state condition (`ui`, `object`, `project_command`, `probe`)
that turns true there is reported as `frame: F` with `observed_frame: F+1`; a
`log` reports the frame the message was logged in; `frames` / `ms` the frame they
elapsed at. `play_time` is that frame's `Time.time`. With `poll_every_frames` N
the change happened within the N frames before `observed_frame`.

### Arguments

| Argument | Default | Meaning |
|---|---|---|
| `condition` | required | See above |
| `timeout_ms` | 30000 (max 600000) | Wall-clock limit. On expiry the call fails with `timed_out` and every leaf's last observed state; its `agentNextSteps` say to send one longer wait (over 40 000 ms it runs as a job) or a `time_scale` instead of repeating the same one |
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
`condition.any[1]`; a nested set that fired is named as the set, e.g.
`condition.any[0].all`). `condition` repeats the tree with every leaf's `met`, `fired`
and `last` observed state — on a timeout this is what the game showed. `errors`
lists console errors and exceptions logged meanwhile (at most 20, first stack line;
`errors_dropped` counts the rest). With `fail_on`, `exception` carries the type,
message, full stack (≤ 4000 characters), frame and play time. `time_scale` appears
when one was requested. `logs_dropped` appears when more than 2000 console messages
arrived between two polls, so a `log` condition did not see them all (lower
`poll_every_frames`).

## `unity_open_mcp_playtest_probe`

Traces a value over play-mode frames: the bridge samples it every `every_frames`
frames into a buffer that later calls read by cursor. Use it instead of per-frame
handlers installed from `execute_csharp` (`Application.onBeforeRender += …`) with
logs kept in `AppDomain.CurrentDomain.SetData` — those run until the next domain
reload when a call forgets to remove them. A probe is one operation on the same
play-mode scheduler `playtest_wait` runs on, so the per-frame hook is the
bridge's and every end removes it. Every action answers at once; gate-free and not
a project mutation (an expression must classify as read-only).

| Action | Does |
|---|---|
| `start` | Binds the source and starts sampling from the next frame. Play mode only (`play_mode_required`). A running `id` is refused (`probe_running`); starting a finished `id` replaces its buffer |
| `read` | Samples from `since` (a `next_cursor`) on, at most `max` (default 50, ≤ 500); returns `next_cursor` and `has_more`. Never clears the buffer |
| `stop` | Ends a running probe and returns the summary (a finished one answers its summary with `stopped: false`) |
| `list` | Every probe with state, sample and error counts, the running count, and `hooks`: the probe operations the scheduler still ticks and whether its tick is in the player loop at all |

`read`, `stop` and `list` work in edit mode too.

### Sources

Exactly one of:

- **`member`** — a field / property path read by reflection. With a target
  (`game_object_path` or `instance_id`, resolved like the typed GameObject tools,
  plus `DontDestroyOnLoad`), it starts at the target's `component_type` or at the
  GameObject itself: `"hp"`, `"stats.current"`, `"transform.position.y"`,
  `"waves[0].count"`. Private fields count (a `[SerializeField] private` field is
  the usual thing to watch); a segment may index a list or array. Without a target
  it is a static path `Namespace.Type.Member…` (`"UnityEngine.Time.frameCount"`,
  `"MyGame.Session.Instance.score"`). A name the value does not have is refused at
  start (`member_not_found`, with the members it does have); a getter that clones
  an asset (`Renderer.material(s)`, `MeshFilter.mesh`) is refused
  (`member_not_readable` — read `sharedMaterial`).
- **`expression`** — a C# expression compiled once at start through the
  `execute_csharp` compiler (default usings plus `usings`) and evaluated every
  sample. With a target, `target` is the component (`component_type`) or the
  GameObject: `"target.linearVelocity.magnitude"`. `new { hp = …, pos = … }`
  samples several values at once. It must only read: an assignment outside an
  object initializer, `++` / `--`, a `;`, a block-bodied lambda, a call of a known
  write (`SetActive`, `Destroy`, `AddComponent`, `AddForce`, `Set*`, `Clear`, …)
  and everything `execute_csharp` treats as disruptive or opaque (compile, import,
  scene / play-mode changes, members resolved by a computed name) are refused at
  start with `expression_not_read_only`; the project's C# deny patterns apply
  (`denied_by_policy`); a compile error is `compilation_error`. Like the
  classification of a snippet, this is a text check: what a project method does
  when the expression calls it is not visible to it. Each distinct expression
  loads one small assembly that stays until the next domain reload, as an
  `execute_csharp` snippet does.

### Samples

```json
{ "seq": 12, "frame": 6997, "time": 6.4241, "realtime": 11.6692, "value": {"x": 0, "y": -21.4, "z": 0} }
```

`frame` is `Time.frameCount`, `time` the gameplay clock (`Time.time`, seconds),
`realtime` `Time.realtimeSinceStartup`. A sample is read at the start of its frame,
so it shows the state the previous frame left. Values: numbers, strings (clipped at
256 characters), enums by name, vectors / quaternions / colours / rects / bounds as
small objects, Unity objects as `{objectId, type, name}`, the first 16 items of a
collection (`{"$items": […], "$count": N}` beyond), public fields of other values
three levels deep; a value whose JSON passes 512 characters becomes
`{"$truncated": true, "$type", "$chars", "preview"}`. A sample that threw carries
`error` (`"NullReferenceException: …"`, or `"MissingReferenceException: The probed
object was destroyed."` once the target is gone) instead of `value`; one that cost
more than `budget_ms` carries `cost_ms`.

### Arguments

| Argument | Default | Meaning |
|---|---|---|
| `id` | required | 1–64 of `[A-Za-z0-9_.-]` |
| `every_frames` | 1 (max 600) | Sample every N frames; the first sample is the first frame after start |
| `max_samples` | 200 (max 5000) | Buffer size; reaching it ends the probe `max_samples_reached` |
| `until` | — | A [`playtest_wait` condition](#conditions) polled at each sample; when it holds the probe ends `until_met` and `reason.fired` names the leaf and frame (the sample of that frame is kept) |
| `time` | `gameplay` | Clock for `until` `ms` conditions |
| `budget_ms` | 5 (0.1–100) | Cost of one sample (evaluation + serialisation), measured |
| `usings` | — | Extra namespaces for an expression |

### Lifecycle and caps

A probe's `state` is one of:

| State | `reason.code` | When |
|---|---|---|
| `running` | — | Sampling |
| `until_met` | `until_met` | `until` held |
| `max_samples_reached` | `max_samples_reached` | The buffer is full |
| `stopped` | `stop_requested` / `cancelled` / `domain_reload` | `stop`; a cancel of its operation through `POST /playmode-operations`; the domain is unloading |
| `play_mode_exited` | `play_mode_exited` | Play mode ended |
| `error` | `sample_errors` / `over_budget` / `until_error` / `probe_failed` | 5 samples in a row threw; 3 samples in a row cost more than `budget_ms`, or one cost 10 × `budget_ms`; the `until` condition could not be evaluated; the probe's tick failed |

Every end runs the operation's cleanup once: the scheduler stops ticking it (the
player-loop system goes when no operation is left), the `until` condition's log
hook is removed and the source lets go of its target. A probe never ends as
stalled: it waits through a paused Editor or `Time.timeScale` 0.

A finished probe's buffer stays readable — in edit mode too, after play mode ends —
until the same `id` is started again, until 32 records push the oldest finished one
out, or until a domain reload. Entering play mode normally reloads the domain, so
probes from an earlier play session are gone then (`probe_not_found`). At most 8
probes run at once (`probe_capacity`).

To wait for a probe to finish without polling `read`, use
`playtest_wait {"condition":{"probe":{"id":"hp"}}}`.

### Trace a value

1. `playtest_probe {"action":"start","id":"boss-hp","game_object_path":"Enemies/Boss","component_type":"MyGame.Health","member":"current","every_frames":5,"max_samples":600,"until":{"object":{"path":"Enemies/Boss","state":"destroyed"}}}`
2. Drive the fight (`inputsim_*`, project commands).
3. `playtest_probe {"action":"read","id":"boss-hp","since":0}`, then again with the
   returned `next_cursor` — only new samples come back.
4. `playtest_probe {"action":"stop","id":"boss-hp"}` — `summary` has the first and
   last sample, numeric `min` / `max` / `mean`, the error count and last error, and
   the sample cost (`mean_ms`, `max_ms`, `over_budget`).

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
| `at_ms` `[…]` / `every_ms` + `duration_ms` / `at_frames` `[…]` | The schedule — exactly one form; at most 120 captures. `at_ms` / `every_ms` count the `time` base (`gameplay` default; `realtime` = the wall clock); `at_frames` counts frames |
| `start_on` | Start the schedule when a condition holds (the `playtest_wait` shapes). Offset 0 is the frame that made it true; `start_on.fired` / `.condition` describe that moment (the condition is not evaluated after it). `start_timeout_ms` (default 30000) bounds it: `timed_out`, nothing captured |
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
frame. `unchanged: true` marks a capture whose every pixel equals the previous one (a
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
play mode keeps the frames already captured: cleanup writes them for at most 1 s,
and a frame it could not write is listed with `file: null` and
`dropped: "flush_budget"`. A domain reload drops them. Exiting play mode
mid-series fails the call with `play_mode_exited`; the message says how many
frames were kept, and `series.frames` lists them (the rest `captured: false`).

**Slow is not stalled.** A series slower than its schedule — `gameplay` time at a
low `Time.timeScale`, a slow frame rate — is never cancelled. The call measures
the series' pace (schedule offset reached against wall time) and, as soon as it
says the series cannot end inside the inline limit (40 s plus 10 s of grace) or
at that limit, the series continues as a job under its own operation id: the job
observes the running series, nothing restarts. The call answers
`status: "running"` with `job_id` (= `operationId`), `progress` (`clock`,
`elapsed_ms` / `expected_ms` on the schedule, `rate` = schedule ms per wall ms,
`eta_ms`), `series` with the frames captured so far (no files yet — encoding
runs after the last capture) and the running `operation` snapshot; wait on the
job for the full result (frames, files, contact sheet). A series that ends
inside the limit (for example 8 s of gameplay at `timeScale` 0.25 = 32 s) is
answered inline as usual.

```json
{
  "status": "running", "job_id": "8442db58-…", "operationId": "8442db58-…",
  "job": { "state": "running", "operation": "unity_senses_capture_series" },
  "progress": { "phase": "capturing", "clock": "gameplay", "elapsed_ms": 114, "expected_ms": 8000, "rate": 0.1, "eta_ms": 79034 },
  "series": { "schedule": "at_ms", "time": "gameplay", "requested": 1, "captured": 0, "frames": [] },
  "note": "The capture series is progressing at 0.1× real time (…); it continues as job 8442db58-… and should end in about 79 s."
}
```

**Stalled is zero progress.** The bridge ends a series that made no progress for
15 s of wall-clock time — the Editor is paused, or a `gameplay` `at_ms` /
`every_ms` schedule while `Time.timeScale` is 0 — as `capture_series_stalled`
with `error.reason` `editor_paused` / `time_scale_zero` (or `no_frames` when the
player loop does not run) and `operation.stall`. The frames captured so far are
written, the rest are `dropped: "stalled"`. A pause shorter than 15 s only
delays the series. Waiting for `start_on`, an `at_frames` schedule and encoding
move on every frame, so they never stall on `Time.timeScale` 0.

### Tap and capture what follows

`unity_open_mcp_inputsim_gesture` `capture_at_ms: [0, 100, 200, 300]` captures the
composed frame at those offsets from the gesture start (the gesture's `time` base)
inside the same scheduled operation, so a tap and the frames after it come back
from one call under `captures` (same frame fields as a series, native size, at most
30). The call returns when both the gesture and the last capture are done; the
gesture's input resource is held until then. A slow pair continues as a job like
a slow series; one with no progress for 15 s (a paused Editor, `gameplay` time at
`Time.timeScale` 0) ends as `gesture_stalled`: fingers released, the frames
captured so far kept.

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
> prefixes. Call `restore` without `confirm` first, read what it would delete, and
> confirm with the `plan` id it returns.

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
| `restore` | `name`, `confirm`, `plan`, `limit` | Edit mode only. Without `confirm` (the default): a dry run returning the diff it would apply and its `plan` id. With `confirm: true` and that `plan`: applies it, re-reads everything and reports `verified` |
| `list` | — | The snapshots of this project's store: name, `created_at`, prefs / files counts, bytes, EditorPrefs prefixes |
| `delete` | `name` | Removes one snapshot |

`company` / `product` address another project's store for `diff`, `restore`,
`list` and `delete`. The current state is always the open project's, so a snapshot
of another project compares only with another snapshot (`against`), and `restore`
refuses it (`snapshot_project_mismatch`) — whether it lives in another project's
store or its metadata names another company / product. Names are 1–64 of
`[A-Za-z0-9._-]`, starting with a letter or digit, without `..` and not ending in
`.`.

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
**every** removal and overwrite listed (the `removed` and `changed` lists of
`prefs`, `editor_prefs` and `files`, and `files.dirs_removed`, are never
truncated; `limit` bounds only `added`), `unsupported_deleted` (current keys of a
type PlayerPrefs cannot hold, excluded ones included — `DeleteAll` removes them
for good), a `warning` that spells out the overwrite and the number of files it
removes, and a `plan` id — a hash of exactly that diff — and changes nothing.

`confirm: true` needs that `plan` (`plan_required` without it): restore applies
only the plan that was reviewed, and refuses `plan_changed` when the current state
moved since the dry run (the game saved a new file, a key changed) — run the dry
run again and review it. Then, before changing anything, it refuses
`snapshot_corrupt` when a stored file no longer matches the manifest (or a
manifest path is not a plain path under `persistentDataPath`),
`persistent_path_link` when a path it would write or remove passes through a
symbolic link, and `prefs_store_unverified` when a probe key written and saved
through PlayerPrefs does not show up in the store read (the dry run's key lists
could not be trusted). Then:

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

Files not in the snapshot are removed before snapshot files are written, so a
case-only rename on a case-insensitive file system keeps the restored file. It
then re-reads everything and reports `verified: true`. A failed operation, or a
re-read state that still differs (`verified: false` with the `residual` diff, or
folders kept because they are not empty), is `restore_incomplete` with `errors`
and the full output. Refusals: `edit_mode_required` in play mode (dry run
included — use `diff` while the game runs), `snapshot_project_mismatch`,
`snapshot_not_found`, `snapshot_unreadable`.

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
   dry run's `prefs.removed` / `files.removed` and the `changed` lists, note its
   `plan`.
5. `player_state {"action":"restore","name":"before","confirm":true,"plan":"<plan>"}`;
   check `verified: true`. `delete` the snapshot when done.

## `unity_open_mcp_playtest_explore`

Explores the running game's uGUI on its own and reports what looks wrong, with no
model in the loop: the agent starts it, waits for the job and triages the
findings. It suits menu-driven and mobile UI — every screen and every button a
player can reach by tapping. The workflow is in the
[Playtest guide](../playtest-guide.md#let-the-crawler-explore).

A local tool. One call checks what the crawl needs and starts it as a
[`jobs`](jobs.md) job (operation `unity_open_mcp_playtest_explore`), answering at
once with `job_id`, `session`, `isolation` and the `budgets`; follow it with
`jobs {"action":"wait","job_id":"…","timeout_ms":30000}` until it is terminal.
`jobs cancel` stops it and keeps what it found. The crawl drives the bridge through
the same routes an agent's calls take, as the caller; do not drive the game
yourself while it runs.

**Requirements.** Your open [`playtest_session`](#unity_open_mcp_playtest_session)
(`no_open_session`), isolated from the developer's save by its player-state
snapshot (`start {snapshot:true}`) or a successful call of the project's
`*.playtest.begin` command (`not_isolated` otherwise, unless `allow_unsandboxed:
true` — the crawl taps through the game for real), and play mode
(`play_mode_required`). `jobs start` of this operation is refused: start it with
the tool.

### The crawl

1. **Settle.** `ui_snapshot` (compact, visible, 200 nodes, with the detector
   arguments) until two snapshots `settle_ms` apart show the same screen, at most
   `max_settle_ms`.
2. **Screen.** A screen is the set of its visible, uncovered controls (role +
   label + path) and visible texts, numbers masked so a ticking counter is the same
   screen; a snapshot whose set is at least `same_screen` (Jaccard) like a known
   screen's is that screen. A control covered by something in front of it (a
   popup's dimmer) is behind the screen, so a popup over a menu is a screen of its
   own. A new screen is captured once (`unity_senses_screenshot` composed, ≤ 960 px)
   and checked by the [detectors](#findings).
3. **Tap.** The next untried control of the screen: visible, interactable,
   uncovered `input` / `button` / `toggle` nodes not on the deny-list, in screen
   order — text fields first (a form is filled before it is sent: `fill_text`,
   default `Test`, typed with [`inputsim_text`](input-simulation.md#text-entry) and
   `end_edit`; a field is known by its path, so typing into it keeps the screen),
   the leaving ones (Back, Close, Cancel, OK, ×, …, or an unlabelled object named
   `*Close*` / `*Back*`) last. `inputsim_pointer {action: click, object_id}` with
   `routing` (default `dispatch`); settle; file the edge (`new_screen`,
   `known_screen`, `same_screen`).
4. **Move on.** When a screen has nothing left to tap, the shortest known path to
   a screen with untried controls is replayed one tap at a time (a hop reaching
   another screen twice gives up the path); with no path the game is restarted
   (`restart`: `play_mode` — exit and re-enter play mode, the default; `command` —
   `restart_command`, a synchronous project command such as
   `project.<owner>.playtest.boot`, with `restart_args`; `none` — stop). A control
   that is not tappable on two visits (covered, disabled, gone) is given up and
   listed as untried.

**Never tapped.** Labels matching the default deny-list — buy, purchase, pay,
subscribe, subscription, delete, remove, reset, log out, sign out, rate, review,
share, restore, quit, exit, unlock now, watch an ad / video — and price-like
labels (a currency symbol or code next to a number). `deny` adds case-insensitive
regexes matched against the label and the path; `allow` exempts exact labels or
paths (case-insensitive) from every rule; `default_deny: false` drops the built-in
list and the price check. Denied controls are listed in the result and the report.

### Arguments

| Argument | Default | |
|---|---|---|
| `max_steps` | 60 (≤ 300) | Taps — exploring and navigating |
| `max_minutes` | 10 (≤ 60) | Wall-clock budget |
| `max_screens` | 40 (≤ 200) | Distinct screens before stopping |
| `same_screen` | 0.8 (0.5–1) | Similarity at which two snapshots are one screen; raise it when distinct screens merge, lower it when one screen splits |
| `settle_ms` / `max_settle_ms` | 250 / 4000 | Pause between the settle snapshots / longest settle wait |
| `restart` / `restart_command` / `restart_args` / `max_restarts` | `play_mode` / — / — / 3 (≤ 10) | How to get unstuck (above) |
| `deny` / `allow` / `default_deny` | — / — / true | The deny-list (above) |
| `routing` | `dispatch` | `raycast_real` sends a real touch the project's input module resolves |
| `placeholder_pattern` | the default below | `""` turns the check off |
| `min_touch_dp` | 44 | 0 turns the touch-size check off |
| `capture` | true | Capture each new screen once |
| `fill_inputs` / `fill_text` | true / `Test` | Type into each visible text field once, before the buttons; what a field refused is in the step's `typed` |
| `allow_unsandboxed` | false | Crawl without a snapshot or `playtest.begin` |

### Result

The job's `result`:

```json
{
  "status": "ok", "stop_reason": "explored", "steps": 18, "restarts": 1, "duration_ms": 12568,
  "coverage": { "screens": 8, "actions_total": 17, "actions_tried": 17, "untried": 0 },
  "finding_counts": { "bug": 2, "suspicious": 4 },
  "findings": [
    { "kind": "text_overflow", "severity": "bug", "screen": "S4", "path": "ExploreFixture/ExploreCanvas/Shop/ProductName",
      "label": "Mega Bundle of Absolutely Everything Forever", "step": 5, "detail": "text needs 1069×295 px but its box is 300×70" },
    { "kind": "dead_button", "severity": "suspicious", "screen": "S5", "path": "ExploreFixture/ExploreCanvas/Settings/HelpButton",
      "label": "Help", "step": 9, "detail": "tapping 'Help' changed nothing on screen and logged nothing" }
  ],
  "screens": [ { "id": "S1", "title": "Explore Fixture", "labels": ["Play", "Shop", "Settings", "Buy Gems", "Daily"], "visits": 7,
                 "first_step": 0, "actions": 6, "tried": 6, "denied": 1, "frame": "…/screenshot-composed-….png", "frame_call": 1 } ],
  "edges": [ { "from": "S1", "action": "Play", "to": "S2", "outcome": "new_screen", "step": 1 } ],
  "denied": [ { "screen": "S1", "label": "Buy Gems", "path": "…/Menu/BuyGemsButton", "reason": "label matches the default deny-list ('Buy')" } ],
  "untried": [],
  "session": "20261123-161731-explore-fixture-4375",
  "agentNextSteps": [ "Triage the 2 bug / 4 suspicious finding(s): open the screens' frames …", "End the session …" ]
}
```

`stop_reason`: `explored` (nothing left to tap), `max_steps`, `max_time`,
`max_screens`, `no_way_back` (stuck with `restart: none` or no restarts left),
`cancelled`, `play_mode_exited`, `tool_failures` (three tool calls in a row
failed), `restart_failed`, `session_ended` (the session was ended during the
crawl). `findings` holds up to 100 (bug first; `findings_truncated` counts the
rest), `edges` 200, `denied` and `untried` 50; `navigation_failures` lists hops
that reached an unexpected screen; `notes` explain skipped checks. In the session
each tap, navigation hop and restart is one recorded call of kind `explore` (with
the console errors logged during it and the new screen's frame); the crawl ends
with one mark — `Explore: N taps, M screens (<stop reason>)`, verdict `bug` /
`suspicious` / `ok` from its findings — and the report's
[Exploration](#report) section.

Out of scope: sliders, scroll-to-reveal, world-space taps, UI Toolkit panels,
choosing what to type per field, and a model choosing taps.

### Findings

Detectors of the crawl (every new screen and tap) and of `playtest_session
check`. A finding is `{kind, severity, screen, path, label, step, detail}`; one
element is one finding whatever screen shows it (a screen-wide finding is per
screen; a console error per message).

| `kind` | Severity | When |
|---|---|---|
| `console_error` | bug | An error / exception logged while the crawler tapped |
| `text_overflow` | bug | `ui_snapshot` diagnostics `overflow` |
| `outside_safe_area` | bug | `outside_safe_area` |
| `overlapping_controls` | suspicious | `overlapping_controls` |
| `placeholder_text` | suspicious | `placeholder` — the default pattern matches `#KEY#`, `{key}`, `[KEY.part]`, `snake_case_with_three_parts`, `UPPER_SNAKE_THREE_PARTS` and `dotted.key.three` (parts starting with a letter, so `v1.2.3` is not one) |
| `small_touch_target` | suspicious | `small_touch_targets` at `min_touch_dp` (px = dp × dpi / 160) |
| `zero_size_control` | suspicious | `zero_size_interactables` |
| `dead_button` | suspicious | A button tap left the screen exactly as it was and logged nothing (toggles excluded) |
| `dead_end` | suspicious | A screen other than the first with nothing to tap that did not move on by itself |
| `navigation_failed` | suspicious | A replayed path reached another screen twice |

A control covered by something in front of it is behind the screen: its
diagnostics are left to the screen it belongs to.

## `unity_open_mcp_playtest_devices`

Puts the running game on each device of a Device Simulator list in turn and
checks it there, so a layout problem that only some devices have — the notch
over a top bar, a tall phone pushing a button off, a tablet stretching a popup —
shows up in one run with the devices named. The workflow is in the
[Playtest guide](../playtest-guide.md#check-on-several-devices).

A local tool, like [`playtest_explore`](#unity_open_mcp_playtest_explore): one call
checks the open session (`no_open_session`) and starts a [`jobs`](jobs.md) job,
answering with `job_id` and the `plan`; `jobs wait` until terminal, `jobs cancel`
stops it. Whatever happens — a cancel, a failure — the Game view or Simulator
state the run found is put back at the end (`restored`): Game view →
`set_resolution {size_label}`, Simulator → `set_device {device, orientation}`.
Unity 6 Editor only (the Device Simulator through [`game_view`](#unity_open_mcp_game_view));
another Editor fails the job with `unsupported_editor_version`.

### Which devices

`devices` names them (an exact id from `game_view list_devices`, or a substring
that matches one; an unknown or ambiguous name fails the job with
`device_not_found` listing the catalog), or `preset` picks them from the
catalog:

| `preset` | Devices |
|---|---|
| `common` (default) | the smallest phone, the tallest phone (aspect), the phone with the deepest top inset, the smallest tablet — at most four, catalog order |
| `phones` / `tablets` | by diagonal from the portrait size and dpi (≥ 7″ is a tablet) |
| `notch` | the devices with a notch or cutout |
| `all` | the whole catalog |

`max_devices` (default 8, ≤ 20) caps the list. `orientations` (default
`["portrait"]`; `portrait_upside_down`, `landscape_left`, `landscape_right`, or
`landscape` = `landscape_left`) multiplies it. A pair the device does not support,
or that does not settle, is listed under `skipped` (`orientation_unsupported`,
`not_settled`, …); a rotation the game does not follow (`orientation_applied:
false` and the size of an orientation already checked) is checked once and
skipped with `orientation_not_applied`.

### What is checked

| `screens` | Per device × orientation |
|---|---|
| `current` (default) | The screen the game shows: the [detectors](#findings) at that device's dpi (`min_touch_dp` → px per device), console errors logged meanwhile, and one composed frame (`capture`, ≤ 960 px). Edit or play mode. One recorded session call of kind `check` per pair |
| `explore` | A whole [crawl](#the-crawl) with the `explore` options (the `playtest_explore` arguments, budgets per device). The game is restarted before every device but the first (`explore.restart`; `none` crawls on from where the previous crawl ended). Needs what the crawler needs: isolation (`not_isolated`, or `allow_unsandboxed`) and play mode (`play_mode_required`). Each crawl records its own steps, an explore block and a mark naming the device |

`placeholder_pattern` and `min_touch_dp` default as for the crawler and apply to
the crawls too unless `explore` sets its own. `settle_timeout_ms` (default 10000)
bounds each device switch.

### Result

```json
{
  "status": "ok", "stop_reason": "done", "screens": "current", "duration_ms": 1824, "checked": 8,
  "devices": [ { "id": "iOS Notch Device Small (1080x2338)", "screen": [1080, 2338], "dpi": 476, "inches": 5.4, "tablet": false, "notch": true } ],
  "entries": [
    { "device": "iOS Notch Device Small (1080x2338)", "orientation": "portrait", "size": "1080x2338", "screen": [1080, 2338],
      "safe_area": [0, 97, 1080, 2096], "insets": { "top": 145, "bottom": 97, "left": 0, "right": 0 }, "dpi": 476,
      "counts": { "bug": 1, "suspicious": 1 }, "findings": [ … ], "frame": "…/screenshot-composed-….png", "frame_call": 7 }
  ],
  "matrix": [
    { "kind": "outside_safe_area", "severity": "bug", "path": "ExploreFixture/ExploreCanvas/Menu/InfoButton", "label": "i",
      "detail": "button reaches outside the safe area (top)", "on": ["iOS Notch Device Small (1080x2338) (portrait)", "iOS Notch Device Small (1080x2338) (landscape_left)"],
      "count": 2, "of": 8 }
  ],
  "finding_counts": { "bug": 2, "suspicious": 25 },
  "restored": { "restored": true, "to": { "action": "set_resolution", "size_label": "Free Aspect" } }
}
```

Findings are per device — an element's finding on two devices is two findings,
each with `device` and `orientation`; `matrix` groups them by kind and element
with the devices that show them (`on`, `count` of `of` checks), bug first, then
the widest spread. An explore entry adds `stop_reason`, `steps`, `screens` and
`actions` (tried / total). `stop_reason`: `done`, `cancelled`, `session_ended`.
The run adds a `devices` block and (for `current`) one mark to the session; the
report's **Devices** section has a row per device with its frame thumbnail, then
the findings by element, then what was skipped.

## `unity_open_mcp_playtest_replay`

Turns a flow found once — by the [crawler](#unity_open_mcp_playtest_explore) or
by the agent's own steps — into a scenario file that replays without a model
after every change: pass / fail per step, what changed on screen, and whether a
failure is flaky. The workflow is in the
[Playtest guide](../playtest-guide.md#replay-a-flow-after-a-change).

A local tool with four actions:

| `action` | Does |
|---|---|
| `export` | Writes a scenario from a playtest session (below) — synchronous |
| `list` | The project's scenarios (`Playtests/Scenarios/*.json`): steps, how many lack a baseline, the variables they need, problems |
| `show` | One scenario: its start, settings, variables and steps (target, baseline yes / no, required texts) |
| `run` (default) | Replays a scenario as a [`jobs`](jobs.md) job: answers with `job_id` and the `plan`; `jobs wait` until terminal, `jobs cancel` stops it |

`run` needs what the crawler needs: the caller's open session (`no_open_session`)
isolated by its player-state snapshot or the project's `playtest.begin`
(`not_isolated`, or `allow_unsandboxed: true`), and play mode
(`play_mode_required`) — unless the run starts by restarting play mode, which
also works from edit mode. It needs the live Editor; a headless batch run is not
supported.

### Scenarios

A scenario is one JSON file in the project, by default
`Playtests/Scenarios/<name>.json` — outside `Assets/`, so Unity does not import
it and the team versions and reviews it with the code it checks. `scenario` names
one by name, or by a project-relative `.json` path (an absolute path is read but
never written). Export and `record` write the same format a person can write or
edit by hand:

```json
{
  "format": "unity-open-mcp.playtest-scenario/1",
  "name": "profile-save",
  "description": "Set a nickname and PIN, save",
  "source": { "session": "20261123-202624-replay-fixture-afe0", "kind": "calls", "exported_at": "…" },
  "start": { "restart": "play_mode", "screen": { "title": "Explore Fixture", "signature": ["c|button|play|ExploreFixture/ExploreCanvas/Menu/PlayButton", "…"] }, "findings": [] },
  "settings": { "same_screen": 0.8, "step_timeout_ms": 5000, "routing": "dispatch", "ignore_errors": [] },
  "vars": { "pin": { "secret": true, "description": "a 4-digit PIN" } },
  "steps": [
    { "do": "tap", "target": { "path": "ExploreFixture/ExploreCanvas/Menu/SettingsButton", "text": "Settings" },
      "expect": { "screen": { "title": "Settings", "signature": ["…"] }, "findings": ["placeholder_text|…/LanguageLabel|"] } },
    { "do": "type", "target": { "path": "ExploreFixture/ExploreCanvas/Profile/PinField", "role": "input" }, "text": "${pin}", "end": "end_edit" },
    { "do": "wait", "args": { "condition": { "ui": { "text_contains": "Vibration" } }, "timeout_ms": 5000 }, "expect": { "texts": ["Vibration"] } },
    { "do": "command", "command": "project.demo.playtest.state_dump", "args": {} },
    { "do": "check", "checkpoint": "Profile saved" }
  ]
}
```

| Field | Meaning |
|---|---|
| `start.restart` | How a run (and every retry) starts: `play_mode` (exit and re-enter play mode), `command` (`start.command` + `start.args`, a synchronous project command such as `project.<owner>.playtest.boot`), `none` (the screen shown now; no retries). `start.screen` / `start.findings` are the first screen's baseline |
| `settings` | `same_screen` (0.5–1, default 0.8), `step_timeout_ms` (500–60000, default 5000), `routing` of taps (`dispatch` or `raycast_real`), `ignore_errors` (regexes on a console message's first line that do not fail a step), `visual` (compare every step as a picture — [Visual baselines](#visual-baselines)) |
| `vars` | Declared `${name}` variables, filled from the run's `vars`; `secret: true` keeps the value out of the session log |
| `steps[].do` | `tap` {`target`, `routing`}, `type` {`target`, `text`, `mode` replace / append / clear, `end` end_edit / submit / none, `secret`}, `wait` {`args`: [`playtest_wait`](#unity_open_mcp_playtest_wait) arguments, `timeout_ms` ≤ 40000}, `command` {`command`, `args`: a synchronous project command}, `input` {`tool`, `args`: another inputsim tool's arguments as sent — key, touch, gesture, step, pointer3d, a pointer drag}, `restart` (as `start` says), `check` (only looks) |
| `target` | `path` (hierarchy path as `ui_snapshot` shows it), `text` (the control's label), `role`, `index` (which of several equal matches). Never an instance id: ids change every play session (`object_id` is refused) |
| `expect` | `screen` {`title`, `signature`}: the screen after the step — the crawler's [screen signature](#the-crawl) (controls by role + label + masked path, plain texts, numbers masked); `texts`: visible texts that must be on screen (case-insensitive substring); `findings`: the [detector](#findings) findings of that screen, as keys `kind|path|…` |
| `label`, `checkpoint` | How the report names the step; the session mark it ended (export) |
| `visual` | `false` = this step is not compared as a picture |

At most 300 steps and 4 MB per file.

### Export

`export` reads a playtest session's log — `session` (default: the caller's open
session, else the project's latest ended one) — and writes `scenario` (refused
with `scenario_exists` unless `overwrite: true`). `source` is required:

| `source` | Steps | Baseline |
|---|---|---|
| `crawl` | One [crawl](#unity_open_mcp_playtest_explore) of the session (`crawl`: 1-based, default the last): its taps, typing and restarts in order. With `to_screen` (`S5`): only the shortest path of exploring taps from the start screen to that screen, each hop preceded by the typing the crawl did on its screen first | The crawl's screens: signature and snapshot findings per screen — a crawl export replays at once |
| `calls` | The agent's own recorded calls in order: `inputsim_pointer` clicks → `tap` by the path the bridge reported and the label used (`target_text` with an exact match), `inputsim_text` → `type` into the field the bridge reported, `playtest_wait` → `wait` (a timeout over 40 s is cut), project commands → `command`, `playtest_session check` → `check`, other inputsim calls → `input` (an `object_id` with a reported path becomes `target`; one without is kept with a warning). Failed calls, captures, Game view changes and the crawler's / replay's own steps are left out; entering play mode or a boot-like command (`*.boot`) before the first action becomes `start`; the `playtest.begin` / `.end` hooks are left out. Text the session redacted (a password, a PIN, `secret: true`) becomes a secret variable `secret_N` | None: run once with `record: true` |

The answer names the file (`path`), the step count, `unbaselined`, the variables,
a preview of the first steps and `warnings`.

### A run

From the scenario's start (`from: "start"`, default) or the screen shown now
(`from: "current"`), each step:

1. **Finds its target** (tap / type): snapshots until the target is on screen and
   usable, up to `step_timeout_ms`. Same masked path and label → exact; same path,
   another label → `label_changed`; same label (and role) at another path →
   `path_changed`; siblings that mask alike ("Item (2)") prefer the exact path,
   then `index`. Not found → `target_not_found` with the controls on screen
   (`candidates`); found but covered or disabled → `target_not_tappable`.
2. **Acts** through the same tools an agent uses (`inputsim_pointer` click,
   `inputsim_text`, `playtest_wait`, `project_commands invoke`, the `input` tool,
   the restart).
3. **Looks**: snapshots until the screen matches `expect.screen` and stops
   changing, or `step_timeout_ms` passes (without a baseline: until it stops
   changing, ≤ 4 s). Similarity is Jaccard over the signatures; a screen whose
   controls are the same and in the same places but relabelled (copy or
   localisation changes) also counts as the same screen (`labels_changed`).
4. **Checks**: the screen (`screen_changed`, with `similarity`, the expected and
   actual title, and the controls / texts added and removed), `expect.texts`
   (`text_missing`), console errors and exceptions logged during the step — the
   bridge event stream by time plus the responses' `logs[]`; `ignore_errors` of
   the scenario and of the call exempt them (`console_error`) — and the screen's
   detector findings against `expect.findings`: a new `bug` finding fails
   (`new_finding`), a new `suspicious` one is drift, a baseline finding gone is
   listed as fixed.

A failed step ends the attempt (the rest are `skipped`) and the failing step gets
a composed frame (`capture`: `failures` default, `steps` = every step, `none`).
Drift — `label_changed`, `path_changed`, `index_changed`, `labels_changed`,
`screen_drift` (the same screen with something added or removed),
`new_suspicious`, `fixed_finding` — passes and is listed; `strict: true` fails a
step on the first four (`drift`).

| Argument | Default | Meaning |
|---|---|---|
| `from` | `start` | `start` = restart as the scenario says; `current` = once, on the screen shown now (no restart, no retries; a start screen that does not match fails step 0 and says so) |
| `retries` | 1 (≤ 3) | Replays of a failed attempt from the start; none with `record`, `from: "current"` or `start.restart: none` |
| `record` | false | Write what this run saw — every step's screen and findings, the start screen, the targets as resolved — into the file as the new baseline, only when every step executed (no target, action, wait, command, text or console failure); screen and findings differences are changes, not failures. Excludes `strict` |
| `strict` | false | Fail on label / path / index drift and relabelled screens |
| `vars` | — | Values for the scenario's variables (`missing_vars` before anything is tapped) |
| `ignore_errors` | — | More regexes, added to `settings.ignore_errors` |
| `step_timeout_ms`, `same_screen` | the scenario's | Override its settings |
| `placeholder_pattern`, `min_touch_dp` | as for the crawler | The findings check |
| `capture` | `failures` | Frames |
| `max_minutes` | 20 (≤ 60) | Wall-clock budget for the whole run, retries included |
| `allow_unsandboxed` | false | Replay without a snapshot or `playtest.begin` (the save changes for real) |
| `visual` | the scenario's | `true` = compare every step as a picture (the scenario's `settings.visual`, else the defaults; on a record run it also turns them on in the file); `false` = skip |

### Result

```json
{
  "status": "ok", "verdict": "failed", "stop_reason": "completed", "consistent": true,
  "scenario": { "name": "explore-fixture-tour", "file": "Playtests/Scenarios/explore-fixture-tour.json", "steps": 26 },
  "attempts": [ { "attempt": 1, "passed": false, "duration_ms": 14930, "failed_step": 11, "reason": "screen_changed" },
                { "attempt": 2, "passed": false, "duration_ms": 14341, "failed_step": 11, "reason": "screen_changed" } ],
  "max_attempts": 2,
  "steps": [ { "n": 0, "do": "start", "label": "Start (restart play mode), attempt 2", "status": "passed", "similarity": 0.778, "title": "Explore Fixture", "drift": [ { "kind": "labels_changed", "detail": "…" } ], "duration_ms": 3120 }, "…" ],
  "failure": { "n": 11, "do": "tap", "label": "Tap 'Credits' on S5", "status": "failed", "reason": "screen_changed",
               "message": "the screen is 0% like the expected one ('Credits'), under 80%; now 'Settings'", "similarity": 0,
               "title": "Settings", "expected_title": "Credits",
               "diff": { "added": ["button 'credits' (Settings/CreditsButton)", "…"], "removed": ["text 'credits'", "text 'made by the demo team'"] },
               "frame": "…/screenshot-composed-….png", "frame_call": 78, "duration_ms": 5310 },
  "drift": [ { "step": 5, "kind": "label_changed", "detail": "'Shop' is now 'Store' (ExploreFixture/ExploreCanvas/Menu/ShopButton)" } ],
  "findings": { "new": [], "fixed": [] },
  "unbaselined": 0, "duration_ms": 29276, "session": "…", "agentNextSteps": [ "…" ]
}
```

`verdict`: `passed` (the first attempt passed), `flaky` (a later attempt passed;
`earlier_failures` lists the failures), `failed` (every attempt failed;
`consistent: true` when at the same step for the same reason), `recorded` (a
`record` run wrote the baseline — `baseline` {`file`, `changed_steps`}),
`cancelled`, `incomplete` (`stop_reason` `max_time` or `session_ended`). Step
`reason`: `target_not_found`, `target_not_tappable`, `action_failed`,
`wait_timed_out`, `command_failed` (also an async command), `restart_failed`,
`screen_changed`, `text_missing`, `console_error` (`errors`), `new_finding`
(`new_findings`), `drift`, `play_mode_exited`, `snapshot_failed`, `visual_changed`
(`visual`, see [Visual baselines](#visual-baselines)). `unbaselined` counts steps
that passed on execution alone.

Each step is one recorded session call of kind `replay` (a secret's typed text as
`[redacted N chars]`), with its frame and the console errors it caused; the run
adds a `replay` block and one mark (`passed` / `recorded` → ok, `flaky` →
suspicious, `failed` → bug). The report's **Replays** section has, per run, the
scenario, file, attempts, a row per step (status, screen match, diff, drift,
errors, picture check, frame thumbnail), the controls on screen instead of a
missing target, the visual differences (baseline · now · diff thumbnails with the
regions), drift, new and fixed findings, and the baseline written.

### Visual baselines

The checks above see what a screen is (its controls and texts), not how it looks.
With `settings.visual` in the scenario — or `visual: true` on a run — every step
(and the start) is also compared as a picture: a moved or resized control, a
missing icon, a wrong colour, a popup under the HUD.

- **Frames.** A composed capture (`unity_senses_screenshot view: composed`: every
  camera plus overlay canvases, as the player sees them) at `max_edge` (default
  640 px), compared in the MCP server — the bridge's `visual_compare` renders the
  main camera only.
- **Baselines** live next to the scenario: `<name>.visual/<key>/NN.png` (`00` = the
  start screen) and an `index.json` naming each frame's step. `key` is what the
  frames were taken on — platform plus the Simulator device and orientation, or
  the Game view size (`darwin-game-2422x1483`,
  `win32-ios-notch-device-small-1080x2338-portrait`) — because rendering differs
  across operating systems, GPUs and sizes. A run on a key with no baseline, or a
  step whose frame was recorded for another step label, reports the step as
  visually unbaselined; it does not fail.
- **Verdict.** A pixel differs when a channel moves more than `pixel_tolerance`
  (24); a 16-px block with fewer than 4 differing pixels is noise; a step fails
  `visual_changed` when the rest exceeds `threshold` (default 0.0002 = 0.02 % of
  the compared pixels — about 50 px at 640 × 392). UI rendering is deterministic
  (the same screen twice differs by 0 pixels), so the check is strict on purpose:
  a renamed label (~0.1 %) fails it until re-recorded.
- **Masks.** `mask`: hierarchy paths or suffixes (every visible node at or under
  them, from the step's snapshot) and normalized `[x, y, w, h]` boxes (0–1,
  top-left origin); `mask_numbers` (default true) masks visible texts with digits
  — counters, timers, scores — as the screen signature does. A step's
  `visual: false` skips it (an animated screen).
- **Animation.** A frame that does not match is captured again (up to 3 times
  within the step timeout) before the step fails; a `record` run keeps a frame
  once two captures in a row match.
- **Where.** The differing blocks are joined into regions (largest first),
  described in percent of the screen — or "across the whole screen" — and by the
  controls / texts they cover. The failing step carries `visual` {`ratio`,
  `threshold`, `regions` [{`where`, `near`}], `baseline`, `current`, `diff`}; the diff
  image is the frame in grey with changed pixels red, regions outlined yellow and
  masked areas blue.

```json
"settings": { "visual": { "threshold": 0.0002, "pixel_tolerance": 24, "max_edge": 640, "mask": ["HUD/Ad", [0, 0, 1, 0.05]], "mask_numbers": true } }
```

`record: true` writes the frames of every step for the run's key (the key's
folder is replaced as a whole) along with the rest of the baseline — only when
every step executed; `record: true, visual: true` also turns `settings.visual` on
in the file. Commit the frames with the scenario for the devices the team checks
on. The result adds `visual` {`key`, `threshold`, `max_edge`, `passed`, `failed`,
`unbaselined`, `recorded`, `skipped`} and, after a record, `baseline.visual`
{`key`, `folder`, `frames`}; `list` / `show` name the recorded keys
(`visual_keys`).

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
| `mark` | `label`, `verdict` (`ok` default, `bug`, `suspicious`), `note` | Closes a step: the recorded calls since the previous mark belong to it. Answers `since_last_mark` (`calls`, `errors`, `captures`, `failed`) — errors there are findings. A `label` over 120 characters is not refused: it is cut to 120 (ending in `…`), the full text starts the `note`, and the answer carries `labelTruncated` |
| `check` | `label`, `placeholder_pattern`, `min_touch_dp`, `capture` (default true) | Snapshots the current screen and records its [findings](#findings) — text overflow, outside the safe area, overlapping controls, placeholder-looking text (default pattern below; `""` turns it off), touch targets under `min_touch_dp` (default 44 dp; 0 turns it off; skipped when the game reports no dpi), zero-size controls — with a frame, as one call of kind `check`. Answers `findings`, `counts`, `frame`, `min_touch_px`. Edit or play mode |
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
error. A job's errors are attributed from the stream as they happen (to the call
before them); the job result's own copies of them are not counted again.

The console is shared: an error is matched to *your* calls by time, whoever caused
it. When another agent drives the same Editor during your session, its errors show
up in your report as during / after your calls — run one playtest per Editor, or
read the attribution with that in mind.

**Frames.** File paths a capture reports (`filePath`, a series' frames and contact
sheet) are hard-linked — or copied — into the session's `report/frames/` when the
call ends; `capture_inline` images are written there from the response. Full-size
frames stay files; the report shows downscaled thumbnails.

**Secrets.** Before anything is written: under a property whose name reads as a
secret (`password`, `pass`, `token`, `api_key`, `authorization`, `client_secret`,
`session_id`, `sessionKey`, …) every string is replaced with `[redacted]`, at any
depth — numbers, booleans and the shape of objects stay (a `tokens: 150` currency
is still readable); inside free text (notes, console messages, stacks, results)
`Bearer …`, `Basic …` and `Digest …` credentials, JWTs, `password=…`, `?key=…` /
`pass=…`, `--password …` flags and `"token": "…"`-style fragments, the bridge auth
token and secret-named environment variable values are masked; a PlayerPrefs diff
entry whose key reads as a secret keeps its type, not its string value.

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
**lost** with its arguments (one the ending process itself had not finished is
**still running at end**). After a restart the new server's agent id differs:
`status` lists the session under `orphaned`; `end` or `resume` with `session:
<id>` adopts it (refused with `session_owned_by_other_agent` while the owning
process is alive, even for the same agent id). A session opened with a stable
`_meta.agentId` is the same owner after a restart. Adoption continues the
numbering and the limits (frames, bytes, dropped calls, logged errors) where the
log left them.

### Report

`end` waits briefly for in-flight stream events, diffs the snapshot (when there is
one), and writes `report.md`:

- a header: session, project, start / end, device, snapshot, the project isolation
  hook seen (`*.playtest.begin` / `*.playtest.end` invoked, or started as a job that
  did not fail), totals and verdicts;
- **Findings**: `bug` / `suspicious` steps with notes, the detector findings of
  crawls and checks (bug first), each distinct error with its first occurrence
  (step, call, during / after), failed calls, lost calls and calls still running
  at end;
- **Exploration** (when a [crawl](#unity_open_mcp_playtest_explore) ran): per run
  its stop reason, taps, screens with their frame thumbnails and controls tried /
  total, the findings table, the exploring taps (from → control → to), the controls
  it never tapped (deny-list) and the ones left untried;
- **Devices** (when a [device run](#unity_open_mcp_playtest_devices) ran): per
  run a row per device × orientation (screen, insets, findings, frame thumbnail —
  or the crawl's taps / screens / controls), the findings by element with the
  devices that show them, and the skipped pairs; crawl runs are also in
  Exploration, named by device;
- **Replays** (when a [replay](#unity_open_mcp_playtest_replay) ran): per run the
  scenario and file, the attempts, a row per step (status, screen match, diff,
  drift, errors, the failing step's frame thumbnail), drift, new and fixed
  findings, the baseline written; a failed or flaky run is also a bug /
  suspicious mark under Findings;
- **Screen checks** (when `check` ran): each check's frame and findings;
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
    "lost_in_flight": [], "running_at_end": [], "state_diff": { "identical": false, "prefs": { "added": 3, "removed": 0, "changed": 0, "unchanged": 5 } },
    "hooks": { "begin": "project.demo.playtest.begin" },
    "detector_findings": { "bug": 0, "suspicious": 0, "items": [] },
    "explorations": [], "device_runs": [],
    "replays": [ { "scenario": "explore-fixture-tour", "verdict": "failed", "steps": 26, "failed_step": 11, "reason": "screen_changed" } ]
  },
  "agentNextSteps": [ "Hand the report to the human: …/report.md", "…" ]
}
```

**Limits** per session: 500 recorded calls (later ones are counted, not logged),
200 marks, 300 frames or 512 MB of frames (later ones are referenced by their
source path), 60 thumbnails (≤ 320 px, a contact sheet ≤ 960 px; 20 s of decoding),
20 errors per call, 2,000 characters of arguments per call. Console errors: one
message is logged 20 times and a session 2,000 errors; later ones are counted, not
logged (the report adds them to the ×counts), so an exception thrown every frame
cannot grow the log without bound. Errors arriving while one call's backlog (500)
is full are dropped, and the report says how many.

## Snapshot → tap by label

1. `ui_snapshot` — read the screen (compact).
2. `inputsim_pointer` / `inputsim_touch` with `target_text: "Buy"` (plus `index`
   or `under` when the label repeats) — or `inputsim_gesture` keyframes with
   `coords: "target"` and `target_text`.
3. `inputsim_step`, then `ui_snapshot` or `unity_senses_screenshot` to confirm.

## Errors

Each of these is a failed call: `isError: true`, `mutation.success: false`, and
the code, message and any detail named below in `mutation.error` (see
[tool refusals](mcp-tools.md#mutation-and-gate-contract)). Errors the MCP server
raises itself (`not_settled`, session errors) and the end of a play-mode
operation (`timed_out`, a stall, `play_mode_exited`) are a flat top-level
`error`.

| Code | When |
|---|---|
| `invalid_arguments` | `ui_snapshot`: unknown `profile` / `role`, invalid `placeholder_pattern`, `min_touch_size` ≤ 0. `game_view`: unknown action, width/height out of range or without the other, width/height together with `size_label`, an orientation the device does not support, `settle_timeout_ms` out of range |
| `under_not_found` | No node path matches `under` |
| `not_settled` | `game_view` set_*: the change was applied but the game did not report the new size (paused Editor, time-out, the view behind another tab); carries `expected`, `observed` and the applied state |
| `game_view_unavailable` | `set_resolution`: no Game view could be found or opened |
| `simulator_unavailable` | `set_device`: the Device Simulator could not be opened, or reported no screen after selecting the device |
| `native_size_unavailable` | `unity_senses_screenshot` / `capture_inline` `view: "game"` with `size: "native"`: the Game view reports no size yet |
| `safe_area_unavailable` | `unity_senses_screenshot` / `capture_inline` `overlay: "safe_area"`: no Game view or Device Simulator reports the game's screen (mode `none`) |
| `device_not_found` / `ambiguous_device` | `set_device`: no device / several devices match `device` (`candidates`) |
| `size_not_found` | `set_resolution`: no size entry has that `size_label` (`labels` lists them) |
| `unsupported_editor_version` | `game_view` on an Editor outside the verified table (Unity 6 line) |
| `editor_internals_unavailable` | `game_view`: an internal Game view / Simulator member this Editor lacks (`detected.missing`) |
| `batch_step_requires_server_poll` | `game_view` set_*, `playtest_wait` or `capture_series` inside `batch_execute` |
| `play_mode_required` | `playtest_wait` / `capture_series` / `playtest_probe` `start` / `playtest_explore` in edit mode; `playtest_replay` `run` in edit mode unless it starts by restarting play mode |
| `probe_running` / `probe_capacity` | `playtest_probe` `start`: the `id` is running / 8 probes are running |
| `probe_not_found` | `playtest_probe` `read` / `stop` of an unknown id (a domain reload drops probes); a `probe` condition naming one |
| `expression_not_read_only` | `playtest_probe` `start`: the expression writes or disrupts (`classification`: `writes`, `disruptive`, `opaque`, `savesscenes`) |
| `member_not_found` / `member_not_readable` | `playtest_probe` `start`: the member path names nothing on the value it reaches (the message lists its members) / a getter that clones an asset, or an index on a non-list |
| `type_not_found` / `component_not_found` | `playtest_probe` `start`: `component_type` or a static path's type is not loaded / the target has no such component |
| `timed_out` | `playtest_wait`: the condition did not hold within `timeout_ms` (carries `condition` with every leaf's last state); `capture_series`: `start_on` did not hold within `start_timeout_ms` (outcome `start_on_timed_out`) |
| `exception_thrown` / `error_logged` | `playtest_wait` with `fail_on`: the game logged an exception / error (carries `exception`) |
| `condition_error` | A leaf cannot be evaluated (a project command threw) |
| `command_unavailable` / `command_not_allowed` | `project_command`: unknown or disabled id / a mutating or async command |
| `ui_unavailable` | A `ui` condition without uGUI compiled in |
| `render_target_unavailable` | `capture_series` / gesture `capture_at_ms`: the play-mode view has not rendered (no Game view / Simulator open, or its tab is hidden) |
| `capture_failed` | `capture_series`: the first capture could not be read |
| `playmode_operation_busy` | A second time-scaled wait, or a second capture series, while one runs |
| `wait_stalled` / `capture_series_stalled` / `gesture_stalled` | The operation made no progress for 15 s and the bridge ended it; `error.reason`: `editor_paused`, `time_scale_zero` (a `gameplay` clock), `no_frames` (the player loop did not run) or `no_progress`. What it had is kept (a series' frames); a slow operation never ends this way — it continues as a job |
| `play_mode_exited` | Play mode ended while the operation ran: input released, time scale restored, a series' frames so far kept |
| `edit_mode_required` | `player_state` `restore` (dry run included) while the Editor is playing |
| `snapshot_exists` | `player_state` `snapshot` to a name that exists, without `overwrite` |
| `snapshot_not_found` / `snapshot_unreadable` | `player_state`: no such snapshot in the addressed store (`available` lists the names) / its `snapshot.json` cannot be read |
| `snapshot_project_mismatch` | `player_state` `restore` (or `diff` against the current state) of a snapshot whose company / product differ from the open project |
| `snapshot_corrupt` | `player_state` `restore`: a stored file no longer matches the manifest, or a manifest path leaves `persistentDataPath`; nothing was changed |
| `plan_required` / `plan_changed` | `player_state` `restore confirm:true` without the dry run's `plan` / after the current state moved since that dry run; nothing was changed |
| `persistent_path_link` | `player_state` `restore confirm:true`: a path it would write or remove passes through a symbolic link (`paths`); nothing was changed |
| `prefs_store_unverified` | `player_state` `restore confirm:true`: a probe key saved through PlayerPrefs did not appear in the store read; nothing was restored |
| `persistent_data_too_large` | `player_state` `snapshot`: `persistentDataPath` exceeds `max_files_mb` or 5000 files after excludes (`largest` lists the biggest files); nothing written |
| `prefs_store_unreadable` | `player_state`: the platform PlayerPrefs / EditorPrefs store could not be read (`store` names it) |
| `editor_internals_unavailable` | `player_state` with EditorPrefs on an Editor without the internal EditorPrefs flush |
| `restore_incomplete` | `player_state` `restore confirm:true`: some operations failed, or the re-read state still differs from the snapshot (`errors`, `residual`, `dirs_kept_not_empty`) |
| `session_already_open` | `playtest_session` `start` while this agent has an open session (`session` names it), or `mark` / `end` / `resume` naming a different session than the open one |
| `no_open_session` | `playtest_session` `mark` / `check` / `end` without an open session (`orphaned` lists sessions a stopped server left open); `playtest_explore` / `playtest_devices` / `playtest_replay` `run` without one |
| `not_isolated` | `playtest_explore` / `playtest_replay` `run`: the session has no player-state snapshot and no `*.playtest.begin` call succeeded in it (pass `allow_unsandboxed: true` to go on anyway) |
| `job_operation_unsupported` | `jobs start` of `unity_open_mcp_playtest_explore` / `unity_open_mcp_playtest_devices` / `unity_open_mcp_playtest_replay` — start them with the tool itself |
| `scenario_not_found` / `scenario_invalid` | `playtest_replay`: no file at the named path / not JSON, or not a valid scenario (`problems` lists what is wrong) |
| `scenario_exists` | `playtest_replay` `export` to a file that exists, without `overwrite` |
| `missing_vars` | `playtest_replay` `run`: the scenario uses `${name}` variables the call's `vars` do not give (`missing`) |
| `nothing_to_export` / `screen_not_reached` | `playtest_replay` `export`: the session has no calls / no crawl to replay / the crawl never tapped its way to `to_screen` |
| `no_session` | `playtest_replay` `export` without `session`, an open session or an ended one |
| `project_unknown` | `playtest_replay`: the server has no project path (scenarios live in the project) |
| `device_not_found` / `no_devices` | `playtest_devices` (job error): a `devices` name matches no or several devices (the message lists the catalog) / the preset or the Simulator lists none |
| `session_not_found` / `session_ended` | `playtest_session` (and `playtest_replay` `export` with `session`): no such session for this project / it already ended (`report`) |
| `session_owned_by_other_agent` | `playtest_session` `end` / `resume` / `mark` of a session another live agent or MCP server process owns |
| `snapshot_failed` | `playtest_session` `start` with `snapshot: true`: the player-state snapshot failed (`snapshot_error`); no session was started |
| `identity_unavailable` | `playtest_session`: neither the Editor nor `ProjectSettings/ProjectSettings.asset` gave the company / product |
| `limit_reached` | `playtest_session` `mark` beyond 200 marks |
