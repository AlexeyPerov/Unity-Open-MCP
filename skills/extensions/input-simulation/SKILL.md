# Unity Open MCP — Input Simulation Extension

Skill for AI agents testing a game **in the Unity Editor** by simulating input —
clicks, drags, swipes, key presses, and touches — through the `unity-open-mcp`
MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tool group is
> **hidden** from `ListTools` until the connected session activates it. The uGUI
> tools (`pointer`, `step`, `probe`, `pointer3d`) compile when `com.unity.ugui`
> is present; `touch` and `gesture` compile everywhere (they route to the legacy
> Input Manager and/or the Input System); `key` compiles when
> `com.unity.inputsystem` is present. Call `capabilities` to see what is live.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `input-simulation` tool group is activated:
  `unity_open_mcp_manage_tools(action: "activate", group: "input-simulation")`.
  Or use intent activation:
  `unity_open_mcp_manage_tools(action: "activate_for", intent: "click and drag UI to test the game")`.
- **Most tools need play mode** (`pointer`, `key`, `touch`, `gesture`, `step`, `pointer3d`).
  Enter it first: `unity_open_mcp_editor_set_state(state: "play")`. `probe` is
  read-only and works in edit mode too — use it to build a click plan *before*
  entering play mode.

## Tools

| Tool | Reach | Play mode? |
|---|---|---|
| `inputsim_pointer` | uGUI (`EventSystem` + `ExecuteEvents`) — click/drag/hover/submit on a named GameObject, object_id, or screen point | yes |
| `inputsim_pointer3d` | 3D / legacy — `Physics.Raycast` + `OnMouseDown`/`OnMouseUp`/`OnMouseDrag` SendMessage | yes |
| `inputsim_key` | Input System keyboard device events (`Keyboard.current`) | yes |
| `inputsim_touch` | Touch / swipe through the project's real backend(s): legacy Input Manager (`legacy_touch`) and/or Input System `Touchscreen` (`input_system`) | yes |
| `inputsim_gesture` | Keyframed finger paths played over **real** frames (hold, drag, trace for seconds) | yes |
| `inputsim_step` | Advance N play-mode frames (`EditorApplication.Step`) | yes |
| `inputsim_probe` | List active uGUI interactables (paths, ids, rects, interactable, occluded) | no (works in edit mode) |

All seven are **gate-free** (`IsMutating=false` — input writes no assets).

## Input backends (touch, gesture, `raycast_real`)

Every injecting call reports `backend` as an **array** and `skipped` backends
with a reason. `backend: "auto"` (default) injects into every enabled, reachable
backend — a project with Active Input Handling **Both** gets the same finger in
legacy `Input` and the Input System in the same frame.

- `legacy_touch` — `Input.touchCount` / `Input.GetTouch`, and (via
  `Input.simulateMouseWithTouches`, reported as `mouseEmulation`)
  `Input.GetMouseButton*` / `Input.mousePosition` / `StandaloneInputModule`.
  Finger 0 only. No Game view, OS or Simulator focus needed.
- `input_system` — `Touchscreen` state events. Needs a `Touchscreen` device
  (usually absent in the Editor); the tool focuses the Game view inside Unity
  when the Input System requires it (`focusedGameView: true`).
- Never open the Device Simulator or rewrite input settings to make a call work.
  When a check needs the Simulator (a device screen, safe area, a `Touchscreen`),
  open it explicitly with `game_view set_device` (`playtest` group) first.
  Read the refusal instead: `input_backend_unavailable` (with `error.detected`
  configuration and `agentNextSteps`), `legacy_multitouch_unsupported`,
  `unsupported_editor_version`.

## Which tool reaches which code

Unity has **four** input surfaces. Pick the tool that reaches the code your game
actually reads — **callback vs polling matters** for `key`/`touch`:

| The game reads… | Use | Polling? |
|---|---|---|
| uGUI handlers (`Button.onClick`, `IPointerClickHandler`, `IDragHandler`, `IDropHandler`) | `inputsim_pointer` | n/a (event-driven) |
| `OnMouseDown` / physics-raycast gameplay (legacy Input Manager) | `inputsim_pointer3d` | n/a (event-driven) |
| `InputAction.performed += …` (callback) | `inputsim_key` / `inputsim_touch` | callback — fires without `advance_frames` |
| `Keyboard.current[K].wasPressedThisFrame` (polling) | `inputsim_key` with `advance_frames ≥ 1` | **polling — needs frame advance** |
| `Touchscreen.current` / per-frame `primaryTouch.delta` (polling) | `inputsim_touch` with `advance_frames ≥ 1` | **polling — needs frame advance** |
| Legacy `Input.GetTouch` / `Input.touchCount` / `Input.GetMouseButton` / `Input.mousePosition` (polling) | `inputsim_touch` (taps, swipes) or `inputsim_gesture` (holds, drags over real frames) | legacy backend always advances ≥ 1 frame between phases |
| "Does a real tap at this point reach the button?" (`StandaloneInputModule`, covers, modals) | `inputsim_pointer` with `routing: "raycast_real"` | input module decides; read `topHit` / `targetHit` |
| Legacy `UnityEngine.Input.GetKeyDown`, legacy hover without a press | **not covered** — use `invoke_method` / `execute_csharp`, or `inputsim_pointer3d` for `OnMouseDown` physics gameplay |

**Why polling needs `advance_frames`:** a synchronous tool call processes down+up
in a single `InputSystem.Update()` — no `MonoBehaviour.Update` ticks in between,
so `wasPressedThisFrame` is already false by the time game code next runs. Pass
`advance_frames: 1` (or more) to pump that many player-loop frames between down
and up. An input update inside each stepped frame consumes the press so gameplay
can observe `wasPressedThisFrame`. Splitting `down` → `inputsim_step` → `up`
works for held-state polling; the immediate `down` update may consume the edge.

## Read the screen, tap by label

When you know what the player sees ("the Buy button", "Claim ×2") but not the
GameObject names, activate `playtest` too
(`manage_tools(action: "activate_for", intent: "playtest the shop screen")` brings
both groups):

```jsonc
{ }                                                          // ui_snapshot (compact)
// → nodes: [{ "path":"Canvas/Shop/Btn_03", "role":"button", "text":"Buy",
//             "rect":[40,1173,220,80], "visibility":"on_screen", "interactable":true }, ...]
//   diagnostics: overflow / zero_size_interactables (+ placeholder, small_touch_targets on request)
{ "action": "tap", "target_text": "buy" }                    // inputsim_touch (or inputsim_pointer)
// → "textTarget": { "path":"Canvas/Shop/Btn_03", "text":"Buy", "matches":1, "point":[150,1213] }
{ "frames": 10 }                                             // inputsim_step
{ "text_contains": "coins" }                                 // ui_snapshot — confirm the counter changed
```

- Exact match is the default: trimmed, whitespace collapsed, rich-text tags
  removed, case-insensitive. Use `text_match: "contains"` for a partial label,
  `"regex"` for patterns.
- Only visible elements match. Two equal labels → `ambiguous_target` with
  `matches[]` — pick with `index` or narrow with `under`; never guess. No visible
  match → `target_not_found` with `hidden_matches` (clipped / offscreen /
  transparent): scroll or open the panel first.
- Gestures: `coords: "target"` keyframes take `target_text` instead of `target`.
- A label tap still goes through the normal routing: read `blockedBy` /
  `occluded_by` (a toast or modal on top) before reporting the feature works.

## The self-sufficient testing loop

```
probe (edit mode OK) → click by object_id → step → screenshot
```

1. **Discover** — `inputsim_probe` lists every active interactable with its
   `instanceId`, `interactable` flag, and whether it is `occluded`. No guessing
   names. Works before entering play mode.
2. **Enter play mode** — `editor_set_state(state: "play")`.
3. **Interact** — `inputsim_pointer` (`object_id` from probe is unambiguous),
   `inputsim_key` / `inputsim_touch` (with `advance_frames` for polling code),
   or `inputsim_pointer3d` for world-space gameplay.
4. **Advance** — `inputsim_step(frames: N)` so tweens, animations, and polling
   code run before you look. **This is mandatory between an interaction and a
   screenshot** — without it a click that opens a panel with a 0.3s tween
   screenshots as the old panel.
5. **Verify** — `screenshot` (view: "game"), `visual_compare` (regression), or
   `read_console` for gameplay logs/errors.

### Recipe: probe → click → step → screenshot

```jsonc
// 1. Discover (edit mode or play mode).
{ }                                                          // inputsim_probe
// → { "interactables":[{ "path":"Canvas/MainMenu/StartButton",
//      "instanceId":-10234, "interactable":true, "occluded":false, ... }], ... }

// 2. Enter play mode + click by instanceId (unambiguous).
{ "state": "play" }                                          // editor_set_state
{ "action": "click", "object_id": "-10234" }                // inputsim_pointer (probe's instanceId; pass as a string, bare number also accepted)
// → { "status":"ok", "target":"Canvas/MainMenu/StartButton",
//     "interactable":true, "blockedBy":null,
//     "dispatched":["pointerDown","pointerUp","pointerClick"] }

// 3. Advance frames so the panel-open tween settles, then look.
{ "frames": 10 }                                             // inputsim_step
{ "view": "game" }                                           // screenshot
```

### Recipe: drag an item onto a slot (IDropHandler)

```jsonc
{ "action": "drag", "from_target": "Inventory/Sword", "to_target": "Hotbar/Slot3", "drag_steps": 12 }
// inputsim_pointer — fires beginDrag → drag×N → pointerUp → drop(on Slot3) → endDrag.
// Response carries dropTarget + dropLanded so you know the item reached the slot.
```

### Recipe: hold a key for polling gameplay (Input System)

```jsonc
// Polling wasPressedThisFrame needs a frame advance while the key is down.
{ "action": "tap", "key": "Space", "advance_frames": 3 }    // inputsim_key
// or split it:
{ "action": "down", "key": "Space" }                        // inputsim_key
{ "frames": 5 }                                             // inputsim_step
{ "action": "up", "key": "Space" }                          // inputsim_key (releases Space only)
```

### Recipe: drag the core control for 2 s (polled legacy input)

```jsonc
// inputsim_gesture — finger down, drag right for 0.4 s, hold 1.6 s, lift elsewhere.
{ "fingers": [{ "id": 0, "path": [
    { "at_ms": 0,    "x": 540, "y": 1200 },
    { "at_ms": 400,  "x": 700, "y": 1100 },
    { "at_ms": 2000, "phase": "hold" },
    { "at_ms": 2600, "x": 400, "y": 900 } ] }],
  "coords": "screen" }                                      // "normalized" = 0..1, "target" = per-keyframe target + pixel offset
// → waits for the end: { "status":"ok", "backend":["legacy_touch"], "frames":{...},
//     "trace":[{ "keyframe":0, "frame":1838, "point":[540,1200], "uiTopHit":null }, ...],
//     "errors":[], "released":true }
// Longer than 40 s → returns a job_id: wait with unity_open_mcp_jobs. Cancel releases the finger.
```

Read state at the trace's `frame`s (e.g. the controlled object's position) to
prove the game followed the finger. `time: "gameplay"` follows `Time.timeScale`.
One gesture at a time (`playmode_operation_busy`); not inside `batch_execute`.

### Recipe: is the button really tappable?

```jsonc
{ "action": "click", "target": "Shop/BuyButton", "routing": "raycast_real" }  // inputsim_pointer
// → { "topHit":"Shop/LoadingOverlay", "targetHit":false, "blockedBy":"Shop/LoadingOverlay",
//     "inputModule":"StandaloneInputModule", "backend":["legacy_touch"], "notes":[] }
// The overlay absorbed the tap exactly like a finger — the default dispatch routing would have
// clicked through it.
```

### Recipe: click a 3D world object (legacy Input Manager)

```jsonc
{ "action": "click", "screen_x": 540, "screen_y": 960 }     // inputsim_pointer3d
// Physics.Raycast → OnMouseDown + OnMouseUpAsButton + OnMouseUp on the hit collider.
```

## Response contract — honesty fields

`inputsim_pointer` returns:
```json
{ "status": "ok",
  "target": "Canvas/MainMenu/StartButton",
  "screenPoint": [540.0, 960.0],
  "hasHandler": true,
  "interactable": true,
  "blockedBy": null,
  "eventSystem": true,
  "dispatched": ["pointerDown", "pointerUp", "pointerClick"] }
```

- **`interactable: false`** — the target (or an ancestor `CanvasGroup`) is
  disabled. The dispatch still ran but a real player click would have no-op'd.
  Re-check game state before reporting the feature works.
- **`blockedBy: "<path>"`** — something else raycast-hits in front of the
  target's center (a modal, a loading overlay, a popup). The target-path dispatch
  still fired (occlusion-skipping is a feature for reaching hidden objects), but
  a real click would have hit the blocker. This is a trade, not a pure advantage:
  reaching hidden objects also means never noticing that a modal is blocking the UI.
- **`hasHandler: false`** — no `IPointer*Handler` on the target or ancestors;
  the dispatched events no-op'd.
- **`dropTarget` / `dropLanded`** (drag only) — where the drop actually landed.
  `dropLanded: false` means nothing received `IDropHandler` (dragged to empty
  space, or the slot has no `IDropHandler`).

`inputsim_step` returns `{ framesAdvanced, initialPaused, steppedPaused, pausedRestored }`.

## Error codes

| Code | Meaning | Fix |
|---|---|---|
| `play_mode_required` | Called outside play mode | `editor_set_state(state: "play")` first |
| `no_event_system` | No `EventSystem` in the scene (pointer) | Add one (create via `ui_canvas_add`, or add an EventSystem GameObject) |
| `target_not_found` | `target` matches no active GameObject, or `target_text` matches no visible element (`hidden_matches` lists invisible ones) | Check the name/path; for labels, make the element visible first |
| `ambiguous_target` | `target` matches >1 active GameObject (`inputsim_pointer`, `inputsim_pointer3d`, `inputsim_touch`) | Use the full path, a longer trailing segment, or `object_id` (candidates listed). `inputsim_key` takes no target |
| `no_hit` | Screen point raycast hit nothing | Check the point is over a raycast-target element |
| `both_endpoint_forms` | Both target and screen coords supplied for one drag endpoint | Pass one form per endpoint (target wins; this error makes it explicit) |
| `conflicting_targets` | `target_text` together with `object_id` / `target` / screen coords (or the drag/swipe start forms) | Pass one form |
| `index_out_of_range` / `under_not_found` | `index` past the visible label matches / `under` matches no path | Re-read `matches[]` or the snapshot paths |
| `ui_text_unavailable` | `target_text` on touch/gesture in a project without uGUI | Use `target` or coordinates |
| `no_target_or_screen_point` | Neither object_id, target, nor screen point given | Provide one |
| `no_camera` | `Camera.main` is null (pointer3d) | Tag a MainCamera in the scene |
| `no_device` | `Keyboard.current` is null, or `backend: "input_system"` without a `Touchscreen` | Use `backend: "auto"` (legacy touches when the Input Manager is on); for uGUI prefer `inputsim_pointer` |
| `input_backend_unavailable` | No backend reaches this project's input | Read `error.detected` + `agentNextSteps`; drive the behaviour via `invoke_method` / a project command |
| `legacy_multitouch_unsupported` | Finger ≠ 0 or several fingers on `legacy_touch` | One finger (id 0), or `backend: "input_system"` with a `Touchscreen` |
| `unsupported_editor_version` | Legacy injection not enabled for this Editor version | Use `routing: "dispatch"` / `invoke_method` |
| `playmode_operation_busy` | A gesture is still driving input | Wait for it or cancel its job |
| `gesture_timeout` / `play_mode_exited` | Gesture did not finish inline / play mode ended mid-gesture | Fingers were released; rerun (long ones via `jobs`) |
| `invalid_key` / `invalid_action` | Bad enum value | See the tool's enum |

## Limits

- **Ambiguity detection covers every tool that takes a `target`.** `inputsim_pointer`,
  `inputsim_pointer3d`, and `inputsim_touch` all walk active transforms and return
  `ambiguous_target` (with candidate paths) when a name/path matches more than one
  GameObject — none silently returns an arbitrary first match. (`inputsim_pointer`
  shares the uGUI half's `PointerTargets`; `inputsim_touch` / `inputsim_gesture`
  use the bridge's own resolver so they work without uGUI.) `inputsim_key`
  takes no target. When a name is shared, prefer a unique path or an `object_id`
  from `inputsim_probe` for pointer tools. Touch accepts unique paths or screen
  coordinates, not `object_id`.
- **uGUI drag is single-frame.** `begin/drag×N/pointerUp/drop/endDrag` dispatch
  in one frame. Use `inputsim_step` afterward to let drag-driven tweens settle.
- **`hold` duration is the number of advanced frames, not wall-clock.** Pair
  `hold` with `advance_frames` for held-state polling. Hold interactions still
  require actual elapsed input time; `duration_ms` does not wait.
- **`up` releases the named key and explicitly requested modifiers.** Other
  held keys survive. Use matching modifier flags to release modifiers.
- **Legacy touch is single-finger.** Multi-touch needs `backend: "input_system"`
  and a `Touchscreen` device.
- **Exiting play mid-gesture** releases the finger; the next play session's first
  frame observes that `Canceled` touch.
- **Probe occlusion is reported only in play mode** (needs a live EventSystem
  raycast); in edit mode `occluded` is false (unknown), not a hard failure.
