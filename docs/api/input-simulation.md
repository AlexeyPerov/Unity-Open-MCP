# Input simulation

Activate the `input-simulation` group. Pointer, 3D pointer, probe, and step require
uGUI; touch and gesture compile in every project (they route to whichever input
backend the project uses); keyboard requires the Input System package. These tools
write no assets and are gate-free. All injection and stepping tools require play
mode; `inputsim_probe` also works in edit mode. To read the screen (texts,
visibility) and tap by label, pair this group with `playtest` —
[`ui_snapshot`](playtest.md) and [target by label](#target-by-label).

## Input backends

`inputsim_touch`, `inputsim_gesture` and `inputsim_pointer` with
`routing: "raycast_real"` inject through the project's real input path. Each call
makes one routing decision and reports it as `backend`, an array:

| Backend | Reaches | Notes |
|---|---|---|
| `legacy_touch` | Legacy Input Manager: `Input.touchCount`, `Input.GetTouch`, and — through `Input.simulateMouseWithTouches` (default true) — `Input.GetMouseButton*`, `Input.mousePosition`, and therefore `StandaloneInputModule` | Single finger (id 0). Needs no Game view focus, no OS focus and no Device Simulator window. Uses the Editor's legacy touch simulation entry point behind a version-checked adapter |
| `input_system` | Input System `Touchscreen` state events (`Touchscreen.current`, `InputAction` touch bindings, `InputSystemUIInputModule`) | Needs a `Touchscreen` device. Under the default editor input behaviour the player only receives pointer input while the Game view has focus inside Unity, so the tool calls `EditorWindow.Focus()` on the Game view and reports `focusedGameView: true`. The Unity application is never raised to the OS foreground |
| `ugui_dispatch` | `ExecuteEvents` handlers only | `inputsim_pointer` with the default `routing: "dispatch"` |

`backend: "auto"` (default) injects into every enabled backend that is reachable.
Legacy touches do not reach Input System devices and Input System events do not
reach legacy `Input`, so a project with Active Input Handling **Both** gets the
same finger in both, in the same frame. A backend that is enabled but unreachable
is listed in `skipped` with a reason (`no_touchscreen`, `input_system_disabled`,
`input_system_package_missing`, `legacy_input_disabled`, `unsupported_editor_version`,
`legacy_entry_point_missing`).

| Active Input Handling | `auto` routes to |
|---|---|
| Input Manager (legacy) | `legacy_touch` |
| Input System Package | `input_system` when a `Touchscreen` exists, otherwise refused |
| Both | `legacy_touch` + `input_system` when a `Touchscreen` exists, otherwise `legacy_touch` (input_system skipped) |

`auto` never opens or focuses the Device Simulator. In the Editor there is usually
no `Touchscreen` device unless the Simulator is the active view or one was added
(`InputSystem.AddDevice<Touchscreen>()`). Opening the Simulator is an explicit
step: [`game_view set_device`](playtest.md#unity_open_mcp_game_view). With the
Simulator active, `auto` routes to both backends, and `target` / `target_text`
resolve in the simulated screen's pixels.

### Refusals

| Code | When | Response |
|---|---|---|
| `input_backend_unavailable` | No backend can reach the project's input (for example `input_system` requested in a legacy-only project, or an Input System–only project without a `Touchscreen`) | `error.detected` holds the configuration (`activeInputHandler`, Input System package version, `touchscreen`, Editor version, Simulator/Game view state, focus requirement); `error.skipped` lists each backend's reason; `agentNextSteps` says what to do |
| `legacy_multitouch_unsupported` | A finger other than id 0, or more than one finger, while `legacy_touch` is routed | Use finger 0, or `backend: "input_system"` with a `Touchscreen` |
| `unsupported_editor_version` | The Editor is outside the legacy adapter's version table (enabled: the Unity 6 line, verified on 6000.3) | 2022.3 is not enabled until verified there |
| `no_device` | `backend: "input_system"` requested explicitly without a `Touchscreen` | — |
| `playmode_operation_busy` | A gesture is still driving input | Wait for it, or cancel it |

## Target by label

`inputsim_pointer`, `inputsim_touch` and `inputsim_gesture` keyframes accept
`target_text` — the on-screen text of the element to act on — instead of a
GameObject name. It resolves over the same uGUI screen model as
[`ui_snapshot`](playtest.md) (uGUI `Text` and TextMeshPro):

- Only **visible** elements (`visibility: "on_screen"`) are candidates. A control
  matches on its label (the texts of its descendants) or on any one of those
  texts, and resolves to the control; a plain text resolves to itself. The tap
  point is the centre of the element's visible rect (so a row half-clipped by a
  scroll view is tapped on its visible part).
- `text_match`: `exact` (default when omitted) compares after normalisation —
  trimmed, whitespace collapsed, rich-text tags removed, case-insensitive —
  so `" BUY "` matches `<b>Buy</b>`; `contains` is a case-insensitive substring;
  `regex` is a .NET regex, case-insensitive, matched against the normalised text.
- Several visible matches refuse with `ambiguous_target`: `candidates` (paths) and
  `matches[]` (`index`, `path`, `id`, `role`, `text`, `visibility`, `rect`) in
  hierarchy order. Pick one with `index` or narrow with `under` (a hierarchy path
  or trailing path suffix); it never silently takes the first.
- No visible match refuses with `target_not_found`; `hidden_matches` lists
  matches that exist but are clipped, offscreen, transparent or zero-size.
  `index` past the visible matches → `index_out_of_range`; unknown `under` →
  `under_not_found`.
- `target_text` excludes the other forms for the same end: `object_id`, `target`,
  `screen_x`/`screen_y` (pointer and touch); for a pointer `drag` / touch `swipe`
  it is the start, exclusive with `from_target` / `from_x`/`from_y`
  (`conflicting_targets`). `under` / `index` without `target_text` →
  `invalid_arguments`.
- Gestures: with `coords: "target"` a keyframe names `target` or `target_text`
  (with its own `under`, `index`, `text_match`); `x`/`y` stay pixel offsets.
- The result reports the resolution as `textTarget` (`query`, `match`, `text`,
  `path`, `id`, `role`, `index`, `matches`, `point`); gestures list
  `textTargets` per finger and keyframe.
- Label targeting is compiled with uGUI; without `com.unity.ugui`, touch and
  gesture refuse `target_text` with `ui_text_unavailable`.

## Touch

`inputsim_touch` supports `tap`, `press`, `release`, and `swipe` with a `backend`
parameter. Legacy phases collapse unless a frame passes between them, so the legacy
backend always advances at least one frame between phases and one after the final
phase (`advanceFrames` reports the effective value, `framesAdvanced` the total).
Input System–only routing keeps the immediate path: at `advance_frames: 0` all
phases process within one dispatch, visible to callbacks but not to polling code.
`mouseEmulation` reports `Input.simulateMouseWithTouches` for legacy routing.
`press` without `release` keeps the finger down across calls; exiting play mode or a
domain reload releases it (Canceled).

Target names/paths use ambiguity detection and root-path precedence; target wins
over coordinates at both swipe endpoints. Touch has no `object_id` parameter; use
`target_text` ([target by label](#target-by-label)) to tap what the screen says.
`duration_ms` is descriptive metadata: it neither sleeps nor guarantees a timed
Hold interaction.

## Gestures over real frames

`inputsim_gesture` plays keyframed finger paths across real play-mode frames:

```json
{
  "fingers": [
    { "id": 0, "path": [
      { "at_ms": 0,    "x": 540, "y": 1200, "phase": "down" },
      { "at_ms": 400,  "x": 700, "y": 1100 },
      { "at_ms": 2000, "x": 700, "y": 1100, "phase": "hold" },
      { "at_ms": 2600, "x": 400, "y": 900,  "phase": "up" }
    ]}
  ],
  "coords": "screen",
  "time": "realtime"
}
```

- Phases: `down` (first keyframe, default there), `move` (default in between —
  linear interpolation from the previous keyframe), `hold` (stay at the previous
  point until `at_ms`; `x`/`y` may be omitted), `up` (last keyframe, default there —
  move to the point and lift). `at_ms` is strictly increasing per finger; one
  contact per finger; up to 10 fingers × 256 keyframes, last keyframe ≤ 300000 ms.
- `coords`: `screen` = Screen pixels, bottom-left origin (the space of
  `Input.mousePosition` — on a 2× display the Game view's pixel size, not its point
  size); `normalized` = 0..1 of the Game view size; `target` = each keyframe names a
  `target` (name or path, visual center) and `x`/`y` are pixel offsets.
- `time`: `realtime` (unscaled, per-frame step clamped at 250 ms so a hitch never
  skips a keyframe) or `gameplay` (scaled by `Time.timeScale`; a paused game does
  not advance).
- Each frame sends `Moved` when the point changed and `Stationary` otherwise, one
  phase per finger per frame. The result is reported one frame after the last
  release, so input modules and polling code have consumed it.

The bridge runs the gesture on a shared play-mode scheduler: a player-loop system
at the start of `EarlyUpdate`, before the legacy Input Manager and the Input System
update, so input injected in a tick is observed by the same frame's `Update`. The
call returns once the gesture ends. The terminal result holds `backend`,
`skipped`, `frames` (`start`, `end`, `ticks`), `elapsedMs`, `released`, a bounded
`trace` — per keyframe: `frame`, `elapsedMs`, injected `point`, `phase`, and
`uiTopHit` (top uGUI raycast hit at that point, when uGUI is installed) — and up to
20 console `errors` raised during the gesture.

**Capture what follows.** `capture_at_ms: [0, 100, 200, 300]` (at most 30
offsets, in the gesture's `time` base) captures the composed game frame at those
offsets from the gesture start inside the same scheduled operation — the
`capture_series` recorder: native size, read inside the player loop, labelled with
the frame the image shows. The call returns when the gesture and the last capture
are done; `captures.frames[]` carries `frame`, `play_time`, `elapsed_ms`, `file`
and `unchanged`. Needs a rendering Game view / Simulator
(`render_target_unavailable` otherwise) on Unity 6. See
[Tap and capture what follows](playtest.md#tap-and-capture-what-follows).

Gestures longer than 40 s (by their last keyframe or capture) return a `job_id` instead; wait
on it with [`unity_open_mcp_jobs`](jobs.md#play-mode-operations), which can also
start one directly. A gesture that outlives the inline window (for example
`time: "gameplay"` at a low time scale) is cancelled and returns `gesture_timeout`.
`operation_id` is an optional idempotency handle: a retried call with the same id
returns the same operation. One input gesture runs at a time
(`playmode_operation_busy`); `inputsim_touch` and `raycast_real` are refused
while it runs. The gesture cannot be a `batch_execute` step.

**Cancellation and cleanup.** Cancelling the job, exiting play mode, and a domain
reload all release every held finger with a `Canceled` phase — no stuck touch or
emulated mouse button. Exiting play mode ends the gesture `cancelled` with reason
`play_mode_exited` (the direct call returns that code). A reload drops the
bridge's record after releasing; the job becomes `orphaned`. When play mode is
exited mid-gesture the release is consumed by the next play session's first frame,
which observes a `Canceled` touch.

## Pointer delivery and results

`inputsim_pointer` dispatches uGUI events through `ExecuteEvents` (`routing:
"dispatch"`, default). Click sends pointerDown, pointerUp, then pointerClick;
double-click sends two sequences with click counts 1 and 2. Drag sends pointerDown,
initializePotentialDrag, beginDrag, interpolated drag events, pointerUp, drop, then
endDrag. Dragging remains true through drop and endDrag. Raycasts carry the event
camera for camera/world canvases; press position, pointer ID, press target, drag
target, and click fields are populated.

`dispatched` lists events received by a handler. `dropLanded` is true only when an
active `IDropHandler` receives the drop, and `dropTarget` names that receiver,
including an ancestor reached by event bubbling. Empty space and objects without
a drop handler report false. This confirms delivery, not application acceptance.

Named-target dispatch can bypass occlusion. Inspect `interactable`, `blockedBy`,
and `hasHandler`: a successful dispatch does not establish that a human could click
the target. Inactive objects, disabled Selectables, and non-interactable or
non-raycastable CanvasGroup chains report non-interactable; `ignoreParentGroups`
is respected. `hover` sends enter only; `hover_exit` explicitly ends it.

Pointer selection precedence is `object_id`, then target, then screen coordinates;
`target_text` is exclusive with all three ([target by label](#target-by-label)).
Names and suffix paths must be unique; an exact root-anchored path wins. Ambiguity
returns candidate paths. RectTransform targeting uses its visual center, not its
pivot. Drag rejects simultaneous target and coordinate forms for an endpoint.
`drag_steps` is bounded to 1–100. Probe lists interactables with paging; edit-mode
occlusion is unknown (reported false).

### `routing: "raycast_real"`

Instead of `ExecuteEvents`, inject a real touch at the target's screen point through
the routed backend and step frames, so the scene's own EventSystem input module
raycasts and dispatches it. Whatever is on top receives the press: a cover graphic
absorbs a tap meant for the button underneath. The response reports `routing`,
`backend`, `skipped`, `topHit` (before injection), `targetHit`, `blockedBy`,
`interactable`, `hasHandler`, `inputModule` (the active module's type),
`injected` (phases), `framesAdvanced`, and `notes` — for example when the module
reads the Input System but only legacy touches were injected. Supported actions:
`click`, `double_click`, `press`, `release`, `drag`, left button only. A bare
screen point over empty space is valid (the module decides). `backend` applies to
`raycast_real` only.

## Keyboard frames

`inputsim_key` supports down, up, tap, and hold through the Input System and reports
`backend: ["input_system"]`. `up` releases the named key and explicit modifier
flags, preserving other held keys. Key names use `UnityEngine.InputSystem.Key`;
letters and digits are also accepted. Legacy `Input.GetKey*` has no supported
Editor injection path: with Active Input Handling Both the response adds a note,
and with the Input System disabled the tool refuses `input_backend_unavailable`.

For gameplay polling, use tap/hold with `advance_frames: 1` or more. An input update inserted immediately before gameplay Update consumes the queued press, so Update observes both held
state and `wasPressedThisFrame`. An eager manual input update before stepping would
consume that edge too early. The tool temporarily selects manual Input System
updates and inserts a player-loop callback, restoring both settings and the loop
in `finally`. This guarantees Update polling; it does not promise FixedUpdate
polling or hardware-event timing. At zero frames events are processed immediately for
callbacks, with no gameplay Update between press and release. Separate down/step/up
calls work for held-state polling but do not promise an unconsumed press edge.

## Frame advance and supported boundary

`inputsim_step` calls `EditorApplication.Step` up to 60 times and restores the
initial pause state. Live validation on Unity 6000.3 verifies inline player-loop
advancement and Update callbacks; fixed updates depend on the simulation clock,
and a frame count does not promise a particular elapsed duration or render output.
The response includes `framesAdvanced`, `initialPaused`, `steppedPaused`, and
`pausedRestored`. Zero frames performs no Step. A positive `settle_ms` requests one
extra `QueuePlayerLoopUpdate`; it does not wait that many milliseconds.

Not covered: legacy keyboard (`Input.GetKey*`), legacy mouse hover without a press
(`Input.mousePosition` follows the OS cursor when no touch is active), legacy
multi-touch, and UI Toolkit runtime panels. `inputsim_pointer3d` raycasts 3D
colliders and sends supported `OnMouse*` messages; it is not hardware mouse
injection or a 2D physics pointer. uGUI drag dispatch is single-frame; step
afterward for animation.
