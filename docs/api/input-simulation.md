# Input simulation

Activate the `input-simulation` group. Pointer, 3D pointer, probe, and step require
uGUI; keyboard and touch require the Input System package. These tools write no
assets and are gate-free. All injection and stepping tools require play mode;
`inputsim_probe` also works in edit mode.

## Pointer delivery and results

`inputsim_pointer` dispatches uGUI events through `ExecuteEvents`. Click sends
pointerDown, pointerUp, then pointerClick; double-click sends two sequences with
click counts 1 and 2. Drag sends pointerDown, initializePotentialDrag, beginDrag,
interpolated drag events, pointerUp, drop, then endDrag. Dragging remains true
through drop and endDrag. Raycasts carry the event camera for camera/world canvases;
press position, pointer ID, press target, drag target, and click fields are populated.

`dispatched` lists events received by a handler. `dropLanded` is true only when an
active `IDropHandler` receives the drop, and `dropTarget` names that receiver,
including an ancestor reached by event bubbling. Empty space and objects without
a drop handler report false. This confirms delivery, not application acceptance.

Named-target dispatch can bypass occlusion. Inspect `interactable`, `blockedBy`,
and `hasHandler`: a successful dispatch does not establish that a human could click
the target. Inactive objects, disabled Selectables, and non-interactable or
non-raycastable CanvasGroup chains report non-interactable; `ignoreParentGroups`
is respected. `hover` sends enter only; `hover_exit` explicitly ends it.

Pointer selection precedence is `object_id`, then target, then screen coordinates.
Names and suffix paths must be unique; an exact root-anchored path wins. Ambiguity
returns candidate paths. RectTransform targeting uses its visual center, not its
pivot. Drag rejects simultaneous target and coordinate forms for an endpoint.
`drag_steps` is bounded to 1–100. Probe lists interactables with paging; edit-mode
occlusion is unknown (reported false).

## Keyboard and touch frames

`inputsim_key` supports down, up, tap, and hold. `up` releases the named key and
explicit modifier flags, preserving other held keys. Key names use
`UnityEngine.InputSystem.Key`; letters and digits are also accepted.

For gameplay polling, use tap/hold with `advance_frames: 1` or more. An input update inserted immediately before gameplay Update consumes the queued press, so Update observes both held
state and `wasPressedThisFrame`. An eager manual input update before stepping would
consume that edge too early. The tool temporarily selects manual Input System
updates and inserts a player-loop callback, restoring both settings and the loop
in `finally`. This guarantees Update polling; it does not promise FixedUpdate
polling or hardware-event timing. At zero frames events are processed immediately for
callbacks, with no gameplay Update between press and release. Separate down/step/up
calls work for held-state polling but do not promise an unconsumed press edge.

Touch tap advances between Began and Ended. Swipe advances after Began and each
Moved phase, preserving per-frame positions and delta. `steps` is bounded to
1–100 and `advance_frames` to 0–60. At zero frames the sequence is processed within
one dispatch. Target names/paths use ambiguity detection and root-path precedence;
target wins over coordinates at both swipe endpoints. Touch has no `object_id`
parameter. Single-finger input is the verified path; other fingers are best-effort.

`duration_ms` is descriptive metadata: it neither sleeps nor guarantees a timed
Hold interaction. Such interactions depend on elapsed Input System time.

## Frame advance and supported boundary

`inputsim_step` calls `EditorApplication.Step` up to 60 times and restores the
initial pause state. Live validation on Unity 6000.3 verifies inline player-loop
advancement and Update callbacks; fixed updates depend on the simulation clock,
and a frame count does not promise a particular elapsed duration or render output.
The response includes `framesAdvanced`, `initialPaused`, `steppedPaused`, and
`pausedRestored`. Zero frames performs no Step. A positive `settle_ms` requests one
extra `QueuePlayerLoopUpdate`; it does not wait that many milliseconds.

The keyboard/touch tools do not inject legacy `UnityEngine.Input` state.
`inputsim_pointer3d` raycasts 3D colliders and sends supported `OnMouse*` messages;
it is not hardware mouse injection, arbitrary legacy-axis simulation, or a 2D
physics pointer. uGUI drag dispatch is single-frame; step afterward for animation.
