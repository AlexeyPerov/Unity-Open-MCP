# Unity Open MCP — Input Simulation Extension

Skill for AI agents testing a game **in the Unity Editor** by simulating input —
clicks, drags, swipes, key presses, and touches — through the `unity-open-mcp`
MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tool group is
> **hidden** from `ListTools` until the connected session activates it. The uGUI
> tools (`pointer`, `step`, `probe`, `pointer3d`) compile when `com.unity.ugui`
> is present; the keyboard/touch tools compile when `com.unity.inputsystem` is
> present. Call `capabilities` to see which half is live.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `input-simulation` tool group is activated:
  `unity_open_mcp_manage_tools(action: "activate", group: "input-simulation")`.
  Or use intent activation:
  `unity_open_mcp_manage_tools(action: "activate_for", intent: "click and drag UI to test the game")`.
- **Most tools need play mode** (`pointer`, `key`, `touch`, `step`, `pointer3d`).
  Enter it first: `unity_open_mcp_editor_set_state(state: "play")`. `probe` is
  read-only and works in edit mode too — use it to build a click plan *before*
  entering play mode.

## Tools

| Tool | Reach | Play mode? |
|---|---|---|
| `inputsim_pointer` | uGUI (`EventSystem` + `ExecuteEvents`) — click/drag/hover/submit on a named GameObject, object_id, or screen point | yes |
| `inputsim_pointer3d` | 3D / legacy — `Physics.Raycast` + `OnMouseDown`/`OnMouseUp`/`OnMouseDrag` SendMessage | yes |
| `inputsim_key` | Input System keyboard device events (`Keyboard.current`) | yes |
| `inputsim_touch` | Input System touch / swipe (`Touchscreen.current`) | yes |
| `inputsim_step` | Advance N play-mode frames (`EditorApplication.Step`) | yes |
| `inputsim_probe` | List active uGUI interactables (paths, ids, rects, interactable, occluded) | no (works in edit mode) |

All six are **gate-free** (`IsMutating=false` — input writes no assets).

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
| Legacy `UnityEngine.Input.GetKeyDown` | **not covered** — use `invoke_method` / `execute_csharp`, or `inputsim_pointer3d` for `OnMouseDown` physics gameplay |

**Why polling needs `advance_frames`:** a synchronous tool call processes down+up
in a single `InputSystem.Update()` — no `MonoBehaviour.Update` ticks in between,
so `wasPressedThisFrame` is already false by the time game code next runs. Pass
`advance_frames: 1` (or more) to pump that many player-loop frames between down
and up. Alternatively split into `down` → `inputsim_step` → `up`.

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
{ "action": "click", "object_id": -10234 }                  // inputsim_pointer
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
{ "action": "up", "key": "Space" }                          // inputsim_key (releases ALL held keys)
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

`inputsim_step` returns `{ framesAdvanced, initialPaused, finalPaused }`.

## Error codes

| Code | Meaning | Fix |
|---|---|---|
| `play_mode_required` | Called outside play mode | `editor_set_state(state: "play")` first |
| `no_event_system` | No `EventSystem` in the scene (pointer) | Add one (create via `ui_canvas_add`, or add an EventSystem GameObject) |
| `target_not_found` | `target` matches no active GameObject | Check the name/path; `GameObject.Find` only matches active objects |
| `ambiguous_target` | `target` matches >1 active GameObject (**`inputsim_pointer` only**) | Use the full path, a longer trailing segment, or `object_id` (candidates listed). `inputsim_key` / `inputsim_touch` resolve targets via raw `GameObject.Find` and do NOT detect ambiguity — see the asymmetry note below |
| `no_hit` | Screen point raycast hit nothing | Check the point is over a raycast-target element |
| `both_endpoint_forms` | Both target and screen coords supplied for one drag endpoint | Pass one form per endpoint (target wins; this error makes it explicit) |
| `no_target_or_screen_point` | Neither object_id, target, nor screen point given | Provide one |
| `no_camera` | `Camera.main` is null (pointer3d) | Tag a MainCamera in the scene |
| `no_device` | `Keyboard.current` / `Touchscreen.current` is null | Set Active Input Handling to Input System; for touch on desktop, prefer `inputsim_pointer` |
| `invalid_key` / `invalid_action` | Bad enum value | See the tool's enum |

## Limits

- **Ambiguity detection is pointer-tool-only.** `inputsim_pointer` walks active
  transforms and returns `ambiguous_target` (with candidates) when a name/path
  matches more than one GameObject. `inputsim_key` and `inputsim_touch` resolve
  their target via raw `GameObject.Find`, which returns an arbitrary first match
  with no ambiguity signal — for those tools, prefer a unique name or `object_id`
  to avoid silently hitting the wrong object. (The asmdef split puts the
  ambiguity-aware `PointerTargets` in the uGUI half, so the key/touch tools do
  not share it.)
- **uGUI drag is single-frame.** `begin/drag×N/pointerUp/drop/endDrag` dispatch
  in one frame. Use `inputsim_step` afterward to let drag-driven tweens settle.
- **`hold` duration is the number of advanced frames, not wall-clock.** Pair
  `hold` with `advance_frames` for Input System Hold *interactions*.
- **`up` releases ALL held keys** (the named key + mods + anything held by a
  prior `down`) — per-key release needs cross-call state. To release one key
  among several: prefer `tap` per key, or track held keys in game code.
- **Touch multi-touch beyond finger 0 is best-effort.** Single-finger
  tap/swipe is the verified path.
- **Probe occlusion is reported only in play mode** (needs a live EventSystem
  raycast); in edit mode `occluded` is false (unknown), not a hard failure.
