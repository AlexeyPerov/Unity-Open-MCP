# Input simulation

Activate the `input-simulation` group. Pointer, 3D pointer, probe, step and text
entry require uGUI; touch and gesture compile in every project (they route to whichever input
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

A refusal is a failed call (`isError: true`, `mutation.success: false`); the
`error` object below is `mutation.error`, and `agentNextSteps` is the envelope's.

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
  scroll view is tapped on its visible part); when one of a control's label texts
  matches, the control is tapped where that text shows inside it (a price label
  at the right of a shop row is tapped there, not at the row's centre).
- A plain text is the element acted on. With the default `routing: "dispatch"`
  the events go to the text's GameObject and bubble only to its ancestors, so a
  label laid over a button as a **sibling** (not a child) does not press it
  (`hasHandler: false`, `blockedBy` names the button); use
  `routing: "raycast_real"` there, which taps whatever the input module hits at
  that point.
- `text_match`: `exact` (default when omitted) compares after normalisation —
  trimmed, whitespace collapsed, rich-text tags removed (a `<br>` reads as a
  space), case-insensitive — so `" BUY "` matches `<b>Buy</b>`; `contains` is a
  case-insensitive substring; `regex` is a .NET regex, case-insensitive, matched
  against the normalised text. A regex is limited to 100 ms per text and 2 s per
  screen; past either it refuses with `regex_timeout` (naming the pattern and the
  text) rather than reading the text as a non-match.
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
  (`conflicting_targets`). `under` / `index` / `text_match` without
  `target_text` → `invalid_arguments`.
- Gestures: with `coords: "target"` a keyframe names `target` or `target_text`
  (with its own `under`, `index`, `text_match`); `x`/`y` stay pixel offsets.
  Every keyframe resolves when the gesture starts, against one read of the
  screen as it is then — a label that only appears mid-gesture is
  `target_not_found`; tap it with a separate call after the screen shows it.
- The result reports the resolution as `textTarget` (`query`, `match`, `text`,
  `path`, `id`, `role`, `index`, `matches`, `point`); gestures list
  `textTargets` (`finger`, `keyframe`, `target`) once per distinct label query
  (text, `text_match`, `under`, `index`), at its first keyframe — at most 32;
  `textTargetsOmitted` counts the rest.
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
start one directly. A gesture slower than its schedule (for example
`time: "gameplay"` at a low time scale) is not cancelled: it continues as a job
under its operation id and the call answers `status: "running"` with that
`job_id` and `eta_ms`. One with no progress for 15 s (a paused Editor, `gameplay`
time at `Time.timeScale` 0) ends as `gesture_stalled` (`error.reason` says which)
with every finger released.
`operation_id` is an optional idempotency handle: a retried call with the same id
returns the same operation (for a gesture that runs as a job, the same job). One input gesture runs at a time
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
is respected. `interactable` and `blockedBy` describe the target at press time;
when the dispatch itself changes that (a button that hides its own panel), the
result adds `interactableAfter` with the post-click state, so a working tap is
not read as a disabled button. `hover` sends enter only; `hover_exit` explicitly ends it.

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
reads the Input System but only legacy touches were injected.

A click (`click`, `double_click`) is checked, not inferred from the
pre-injection raycast: `clickHandler` is the object that should receive it,
`pressedObject` what the module pressed after the press frame (a
`PointerInputModule` such as `StandaloneInputModule`), and `clicksObserved` the
`onClick` calls of a uGUI `Button` handler during the injected frames.
`clickDelivered` is `true` / `false` when one of them decides it and `null` when
none can (no Button handler, a module whose press record is not readable). On
`false` the answer adds `eventSystem` (`current`, `scene`, `enabled`,
`isFocused`, `module`, `moduleEnabled`, `activeEventSystems`) and a note to retry
with `routing: "dispatch"`; a module raycast that hit something other than the
pre-injection `topHit` is named in `notes` too. Supported actions:
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

## Text entry

`unity_open_mcp_inputsim_text` types into a uGUI `InputField` or a TextMeshPro
`TMP_InputField` (TextMeshPro fields when TextMeshPro is installed) the way a
player with a keyboard does: character by character through the field's own
public key path (`ProcessEvent` with a KeyDown carrying the character). So the
field decides what goes in — its `onValidateInput`, character validation and
content type (integer, decimal, alphanumeric, name, email, password, PIN), its
`characterLimit`, its read-only flag and its line type (a single-line field drops
`\n`, a multi-line one keeps it; uGUI also drops characters its font lacks) — and
`onValueChanged` fires once per accepted character.

| Argument | |
|---|---|
| `text` | What to type (≤ 1000 characters); required unless `mode: "clear"` |
| `mode` | `replace` (default) empties the field first (`text = ""`, one `onValueChanged("")`, as select-all + delete) and types; `append` types at the end; `clear` only empties it |
| `end` | `end_edit` (default) sends `onEndEdit` — what tapping away does; `submit` sends `onSubmit`, then `onEndEdit` — what Enter does in a single-line field; `none` leaves the edit open |
| `target_text` (+ `under`, `index`, `text_match`), `object_id`, `target` | The field. Its label in `ui_snapshot` (role `input`) is its placeholder or current text; a label / placeholder child resolves to its field, and so does a container with exactly one field below it |
| `secret` | The text is a secret: the answer gives lengths only and a playtest session records `[redacted N chars]` |

No selection is used (a TextMeshPro selection resolves through the text mesh, which
an unfocused field may not have built), and focus is not emulated: activation is
deferred a frame and would select all and leave the field focused. When the game
reacts to selecting the field (`OnSelect`, a hint), tap it with `inputsim_pointer`
first — typing then works the same, and a focused field ends its edit through its
own `DeactivateInputField` (`wasFocused: true`). Either way the field's label is
redrawn after typing and after the edit ends, so a screenshot shows the text. A
phone's on-screen keyboard never appears in the Editor.

```json
{
  "status": "ok", "action": "append",
  "field": { "id": "-29608", "path": "ExploreFixture/ExploreCanvas/Profile/NicknameField", "kind": "ugui",
             "contentType": "standard", "lineType": "single_line", "characterLimit": 12,
             "readOnly": false, "interactable": true, "focused": false },
  "wasFocused": false, "masked": false, "textBefore": "Alex", "textAfter": "Alex12345678",
  "typed": { "requested": 10, "accepted": 8, "transformed": 0, "rejected": 2,
             "rejectedAt": [ { "index": 8, "reason": "limit" }, { "index": 9, "reason": "limit" } ] },
  "events": { "value_changed": 8, "end_edit": true, "submit": true },
  "notes": [ "2 of 10 character(s) were refused by the field (character limit), as they would be for a player." ]
}
```

`typed.transformed` counts characters the field changed on the way in (an
`onValidateInput` that upper-cases, a mask); `rejectedAt[].reason` is `limit` (the
field was full) or `validation` (its validation, content type, line type or font
refused it). `events.value_changed` is counted with a temporary listener. A
listener that throws is reported as `exception` (`type`, `message`) and logged to
the console as the game's own would be; the text typed so far stays. Password and
PIN fields (content type or input type) answer `masked: true` with `lengthBefore`
/ `lengthAfter` instead of the text. The call's `logs[]` carry what the field's
listeners logged.

Refusals: `play_mode_required`, `not_a_text_field` (find fields with
`ui_snapshot role=input`), `field_inactive`, `field_not_interactable` (disabled, or
a `CanvasGroup` blocks it), `field_read_only`, `field_misconfigured` (no text
component or font), `conflicting_targets`, `ambiguous_target`, `target_not_found`,
`object_not_found`. The field refusals carry the `field` block.

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
